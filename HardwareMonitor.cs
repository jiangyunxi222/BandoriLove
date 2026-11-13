using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace BandoriLove;

internal sealed record GpuReading(string Id, string Name, int UsedMiB, int TotalMiB, int? Load)
{
    internal string Usage => $"{(Load is int value ? $"{value}%" : "—")} · {UsedMiB / 1024.0:F1} / {TotalMiB / 1024.0:F1} GB";
}

internal sealed record DeviceChoice(string Id, string Label);
internal sealed record HardwareReading(int? Cpu, int? Ram, IReadOnlyList<GpuReading> Gpus);

internal sealed class HardwareMonitor
{
    private (ulong Idle, ulong Total)? previous;
    private static string CudaPackage => Path.Combine(EmbeddedBundle.Root, "gsv", "cuda");
    internal static bool CudaInstalled => File.Exists(Path.Combine(CudaPackage, "torch", "__init__.py"));
    internal static string CudaPath => CudaPackage;

    internal async Task<HardwareReading> SampleAsync(CancellationToken token)
    {
        int? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel.Value + user.Value;
            if (previous is { } last && total > last.Total)
                cpu = Math.Clamp((int)Math.Round(100.0 * (1 - (idle.Value - last.Idle) / (double)(total - last.Total))), 0, 100);
            previous = (idle.Value, total);
        }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        int? ram = GlobalMemoryStatusEx(ref memory) ? (int)memory.Load : null;
        return new(cpu, ram, await ReadGpusAsync(token));
    }

    private static async Task<IReadOnlyList<GpuReading>> ReadGpusAsync(CancellationToken cancellation)
    {
        var system = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        var start = new ProcessStartInfo(File.Exists(system) ? system : File.Exists(legacy) ? legacy : "nvidia-smi.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--query-gpu=uuid,name,memory.used,memory.total,utilization.gpu");
        start.ArgumentList.Add("--format=csv,noheader,nounits");
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) return []; }
        catch (System.ComponentModel.Win32Exception) { return []; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await error;
            var text = await output;
            return process.ExitCode == 0 ? ParseGpus(text) : [];
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            cancellation.ThrowIfCancellationRequested();
            return [];
        }
    }

    private static List<GpuReading> ParseGpus(string output)
    {
        var gpus = new List<GpuReading>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split(',', StringSplitOptions.TrimEntries);
            if (columns.Length != 5 || !columns[0].StartsWith("GPU-", StringComparison.Ordinal)
                || !int.TryParse(columns[2], CultureInfo.InvariantCulture, out var used)
                || !int.TryParse(columns[3], CultureInfo.InvariantCulture, out var total)) continue;
            int? load = int.TryParse(columns[4], CultureInfo.InvariantCulture, out var value) ? value : null;
            gpus.Add(new(columns[0], columns[1], used, total, load));
        }
        return gpus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low, High;
        internal readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
