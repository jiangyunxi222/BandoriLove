using System.IO;
using System.Text.Json;

namespace BandoriLove;

public sealed record VoiceCard
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Endpoint { get; init; } = "http://127.0.0.1:9880/";
    public string ReferenceAudio { get; init; } = "";
    public string PromptText { get; init; } = "";
    public string PromptLanguage { get; init; } = "ja";
    public string GptWeights { get; init; } = "";
    public string SovitsWeights { get; init; } = "";
    public bool Local { get; init; }
}

internal static class CardStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BandoriLove");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "characters.local.json");
    private static readonly string DevicePath = Path.Combine(DirectoryPath, "device.txt");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    internal static List<VoiceCard> Load()
    {
        var saved = File.Exists(FilePath) ? Read(FilePath) : [];
        var preset = Path.Combine(AppContext.BaseDirectory, "characters.local.json");
        return File.Exists(preset)
            ? saved.Concat(Read(preset)).GroupBy(card => card.Id).Select(group => group.Last()).ToList()
            : saved;
    }

    internal static List<VoiceCard> Read(string path) =>
        JsonSerializer.Deserialize<List<VoiceCard>>(File.ReadAllText(path), Options) ?? [];

    internal static string LoadDevice()
    {
        try
        {
            var value = File.ReadAllText(DevicePath).Trim();
            return value == "cpu" || (value.StartsWith("GPU-", StringComparison.Ordinal) && Guid.TryParse(value[4..], out _))
                ? value : "auto";
        }
        catch (IOException) { return "auto"; }
        catch (UnauthorizedAccessException) { return "auto"; }
    }

    internal static void SaveDevice(string device)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(DevicePath, device);
    }

    internal static void Save(IEnumerable<VoiceCard> cards)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = Path.Combine(DirectoryPath, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(cards, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
