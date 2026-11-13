using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BandoriLove;

internal sealed class EngineStartupException(string message, Exception cause) : Exception(message, cause);

internal sealed class LocalEngine : IDisposable
{
    private Process? worker;
    private string? workerDevice;
    private string? workerConfig;
    private readonly Queue<string> messages = new();
    private bool disposed;

    private Process Start(string device)
    {
        var bundle = Path.Combine(AppContext.BaseDirectory, "gsv");
        var runtime = Path.Combine(bundle, "runtime");
        var python = Path.Combine(runtime, "python.exe");
        var engine = Path.Combine(bundle, "engine");
        var config = Path.Combine(bundle, "local.json");
        if (!File.Exists(config))
            throw new InvalidOperationException("本机模型包未准备完成。需要第 2 轮 GPT 与 SoVITS 权重。");
        if (!File.Exists(python))
            throw new InvalidOperationException("本机 GPT-SoVITS Python 运行时未安装完成。");
        workerConfig = Path.Combine(Path.GetTempPath(), $"BandoriLove-gsv-{Guid.NewGuid():N}.yaml");
        File.Copy(config, workerConfig);
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = engine,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-u", Path.Combine(bundle, "local-worker.py"), workerConfig, device == "cpu" ? "cpu" : "cuda:0" })
            start.ArgumentList.Add(argument);
        start.Environment["PYTHONUTF8"] = "1";
        var sitePackages = Path.Combine(runtime, "Lib", "site-packages");
        start.Environment["PYTHONPATH"] = device == "cpu" ? sitePackages : HardwareMonitor.CudaPath + Path.PathSeparator + sitePackages;
        start.Environment["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID";
        start.Environment["CUDA_VISIBLE_DEVICES"] = device == "cpu" ? "-1" : device;
        start.Environment["PATH"] = runtime + Path.PathSeparator + start.Environment["PATH"];
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD"] = "1";
        start.Environment["OPEN_JTALK_DICT_DIR"] = Path.Combine(runtime, "open_jtalk_dic_utf_8-1.11");
        var process = new Process { StartInfo = start };
        lock (messages) messages.Clear();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (messages)
            {
                if (messages.Count >= 12) messages.Dequeue();
                messages.Enqueue(e.Data.Length > 500 ? e.Data[..500] : e.Data);
            }
        };
        try
        {
            process.Start();
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal async Task<byte[]> SynthesizeAsync(VoiceCard card, string text, string language, string device, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var bundle = Path.Combine(AppContext.BaseDirectory, "gsv");
        var reference = Path.GetFullPath(Path.Combine(bundle, card.ReferenceAudio));
        var references = Path.Combine(bundle, "references") + Path.DirectorySeparatorChar;
        if (!reference.StartsWith(references, StringComparison.OrdinalIgnoreCase) || !File.Exists(reference))
            throw new InvalidDataException("角色的本机参考音频不存在。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(12));
        var token = timeout.Token;
        var output = Path.Combine(Path.GetTempPath(), $"BandoriLove-{Guid.NewGuid():N}.wav");
        try
        {
            if (worker is null || worker.HasExited || workerDevice != device)
            {
                Stop();
                try
                {
                    worker = Start(device);
                    await ReadResponseAsync(worker, token);
                    workerDevice = device;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Stop();
                    throw new EngineStartupException(error.Message, error);
                }
            }
            var request = JsonSerializer.Serialize(new
            {
                text, text_lang = language, ref_audio_path = reference,
                prompt_text = card.PromptText, prompt_lang = card.PromptLanguage,
                output
            });
            await worker.StandardInput.WriteLineAsync(request.AsMemory(), token);
            await worker.StandardInput.FlushAsync(token);
            await ReadResponseAsync(worker, token);
            var size = new FileInfo(output).Length;
            if (size is < 44 or > 64 * 1024 * 1024) throw new InvalidDataException("本机返回的 WAV 大小无效。");
            var audio = await File.ReadAllBytesAsync(output, token);
            if (!GsvClient.IsPcmWave(audio)) throw new InvalidDataException("本机没有返回可播放的 PCM WAV。");
            return audio;
        }
        catch (OperationCanceledException) { Stop(); throw; }
        catch (IOException) { Stop(); throw; }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    private async Task ReadResponseAsync(Process process, CancellationToken cancellation)
    {
        const string prefix = "BANDORI ";
        string? line;
        do
        {
            line = await process.StandardOutput.ReadLineAsync(cancellation);
        } while (line is not null && !line.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null)
        {
            await process.WaitForExitAsync(cancellation);
            string detail;
            lock (messages) detail = messages.LastOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
            throw new IOException($"本机 GPT-SoVITS 已退出（{process.ExitCode}）。{detail}");
        }
        using var response = JsonDocument.Parse(line.AsMemory(prefix.Length));
        if (response.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetString());
    }

    private void Stop()
    {
        var process = worker;
        worker = null;
        workerDevice = null;
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            process?.Dispose();
            if (workerConfig is not null)
            {
                try { File.Delete(workerConfig); } catch (IOException) { }
                workerConfig = null;
            }
        }
    }

    public void Dispose()
    {
        disposed = true;
        Stop();
    }
}
