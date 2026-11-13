using System.IO;
using System.IO.Compression;
using System.Text;
using System.Diagnostics;

namespace BandoriLove;

internal sealed class EmbeddedBundle(string executable, Guid version, long archiveOffset = 0,
    long unpackedSize = 0, int extractorSize = 0, int librarySize = 0)
{
    internal static string Root { get; set; } = AppContext.BaseDirectory;
    private readonly string destination = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "BandoriLove", "packages", version.ToString("N")));
    internal bool IsReady => File.Exists(Path.Combine(destination, ".ready"));
    internal bool IsExtracting { get; private set; } = true;

    internal static EmbeddedBundle? Find()
    {
        if (Environment.ProcessPath is not { } executable) return null;
        using var stream = File.OpenRead(executable);
        if (stream.Length < 24) return null;
        Span<byte> footer = stackalloc byte[24];
        stream.Seek(-footer.Length, SeekOrigin.End);
        stream.ReadExactly(footer);
        if (footer[..8].SequenceEqual("BLBUNDLE"u8)) return new(executable, new Guid(footer[8..]));
        if (!footer[..8].SequenceEqual("BL7ZPACK"u8) || stream.Length < 48) return null;
        Span<byte> sizes = stackalloc byte[24];
        stream.Seek(-48, SeekOrigin.End);
        stream.ReadExactly(sizes);
        return new(executable, new Guid(footer[8..]), BitConverter.ToInt64(sizes[..8]),
            BitConverter.ToInt64(sizes[8..16]), BitConverter.ToInt32(sizes[16..20]), BitConverter.ToInt32(sizes[20..24]));
    }

    internal string Extract(IProgress<double> progress)
    {
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var gate = new Mutex(false, "Local\\BandoriLove-" + version.ToString("N"));
            try { gate.WaitOne(); } catch (AbandonedMutexException) { }
            try
            {
                if (IsReady) return destination;
                Directory.CreateDirectory(staging);
                using var stream = File.OpenRead(executable);
                if (archiveOffset > 0)
                {
                    Extract7Zip(stream, staging, progress);
                    Complete(staging);
                    return destination;
                }
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var size = archive.Entries.Sum(entry => entry.Length);
                CheckSpace(size);
                long complete = 0;
                var lastUpdate = 0d;
                foreach (var entry in archive.Entries)
                {
                    var filename = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!filename.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("内置模型包路径无效。");
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(filename)!);
                    entry.ExtractToFile(filename);
                    complete += entry.Length;
                    var percent = 100d * complete / size;
                    if (percent >= lastUpdate + 0.5)
                    {
                        lastUpdate = percent;
                        progress.Report(percent);
                    }
                }
                Complete(staging);
                return destination;
            }
            finally { gate.ReleaseMutex(); }
        }
        finally
        {
            IsExtracting = false;
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private void Complete(string staging)
    {
        File.WriteAllText(Path.Combine(staging, ".ready"), version.ToString("N"), Encoding.ASCII);
        Directory.Move(staging, destination);
    }

    private void CheckSpace(long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (drive.AvailableFreeSpace < bytes + 256L * 1024 * 1024)
            throw new IOException($"首次启动需要约 {bytes / 1073741824d:F1} GB 可用空间。");
    }

    private void Extract7Zip(FileStream stream, string staging, IProgress<double> progress)
    {
        var archiveLength = stream.Length - archiveOffset - extractorSize - librarySize - 48;
        if (archiveLength <= 0 || extractorSize <= 0 || librarySize <= 0 || unpackedSize <= 0)
            throw new InvalidDataException("内置模型包结构无效。");
        CheckSpace(unpackedSize + archiveLength);
        var tool = Path.Combine(staging, ".unpack");
        Directory.CreateDirectory(tool);
        stream.Position = archiveOffset + archiveLength;
        foreach (var (name, length) in new[] { ("7z.exe", extractorSize), ("7z.dll", librarySize) })
        {
            using var file = File.Create(Path.Combine(tool, name));
            CopySegment(stream, file, length);
        }
        var archive = Path.Combine(tool, "models.7z");
        stream.Position = archiveOffset;
        using (var file = File.Create(archive)) CopySegment(stream, file, archiveLength);
        var start = new ProcessStartInfo(Path.Combine(tool, "7z.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "x", "-t7z", archive, "-o" + staging, "-y", "-bso0", "-bsp1", "-bse2" })
            start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not { } line) return;
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+)%");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var value)) progress.Report(value);
        };
        process.Start();
        process.BeginOutputReadLine();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException("内置模型包解压失败。" + errors.GetAwaiter().GetResult());
        Directory.Delete(tool, recursive: true);
    }

    private static void CopySegment(Stream source, Stream destination, long remaining)
    {
        var buffer = new byte[1024 * 1024];
        while (remaining > 0)
        {
            var length = (int)Math.Min(buffer.Length, remaining);
            source.ReadExactly(buffer.AsSpan(0, length));
            destination.Write(buffer, 0, length);
            remaining -= length;
        }
    }
}
