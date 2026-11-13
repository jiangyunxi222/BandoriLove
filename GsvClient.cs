using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace BandoriLove;

internal static class GsvClient
{
    private const int MaximumAudioSize = 64 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static Uri BaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入 HTTP 或 HTTPS 的 GPT-SoVITS API 基础地址。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static void ValidateCard(VoiceCard card)
    {
        if (string.IsNullOrWhiteSpace(card.Name) || string.IsNullOrWhiteSpace(card.ReferenceAudio))
            throw new ArgumentException("请填写角色名称和推理端参考音频路径。");
        _ = BaseUri(card.Endpoint);
        if (string.IsNullOrWhiteSpace(card.GptWeights) != string.IsNullOrWhiteSpace(card.SovitsWeights))
            throw new ArgumentException("需要切换模型时，请同时填写 GPT 和 SoVITS 权重路径。");
    }

    internal static async Task<byte[]> SynthesizeAsync(VoiceCard card, string text, string language, CancellationToken cancellation)
    {
        ValidateCard(card);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var endpoint = BaseUri(card.Endpoint);
        if (!string.IsNullOrWhiteSpace(card.GptWeights))
        {
            await SetWeightsAsync(endpoint, "set_gpt_weights", card.GptWeights, "GPT 权重", token);
            await SetWeightsAsync(endpoint, "set_sovits_weights", card.SovitsWeights, "SoVITS 权重", token);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "tts"))
        {
            Content = JsonContent.Create(new
            {
                text,
                text_lang = language,
                ref_audio_path = card.ReferenceAudio,
                prompt_text = card.PromptText,
                prompt_lang = card.PromptLanguage,
                media_type = "wav",
                streaming_mode = false
            })
        };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await RequireSuccessAsync(response, "合成语音", token);
        if (response.Content.Headers.ContentLength > MaximumAudioSize)
            throw new InvalidDataException("返回音频超过 64 MB，请缩短文本后重试。");

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > MaximumAudioSize)
                throw new InvalidDataException("返回音频超过 64 MB，请缩短文本后重试。");
            output.Write(buffer, 0, count);
        }
        var audio = output.ToArray();
        if (!IsPcmWave(audio)) throw new InvalidDataException("推理端没有返回可播放的 PCM WAV，请检查 API 地址和模型。");
        return audio;
    }

    private static bool IsPcmWave(ReadOnlySpan<byte> data)
    {
        if (data.Length < 44 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8)) return false;
        var format = false;
        var samples = false;
        for (var i = 12; i <= data.Length - 8;)
        {
            var chunk = data.Slice(i, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4, 4));
            if (size > data.Length - i - 8) return false;
            if (chunk.SequenceEqual("fmt "u8))
                format = size >= 16 && BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i + 8, 2)) == 1;
            if (chunk.SequenceEqual("data"u8)) samples = size > 0;
            i += 8 + (int)size + ((int)size & 1);
        }
        return format && samples;
    }

    private static async Task SetWeightsAsync(Uri endpoint, string route, string path, string label, CancellationToken token)
    {
        using var response = await Http.GetAsync(new Uri(endpoint, route + "?weights_path=" + Uri.EscapeDataString(path)),
            HttpCompletionOption.ResponseHeadersRead, token);
        await RequireSuccessAsync(response, "载入" + label, token);
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response, string action, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var bytes = new byte[2048];
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token);
        var message = System.Text.Encoding.UTF8.GetString(bytes, 0, count);
        try
        {
            using var document = JsonDocument.Parse(message);
            var body = document.RootElement;
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out var detail)
                && detail.ValueKind == JsonValueKind.String) message = detail.GetString() ?? "";
        }
        catch (JsonException) { }
        if (message.Length > 250) message = message[..250] + "…";
        throw new HttpRequestException($"{action}失败（HTTP {(int)response.StatusCode}）{(message.Length > 0 ? $" · {message}" : "")}");
    }
}
