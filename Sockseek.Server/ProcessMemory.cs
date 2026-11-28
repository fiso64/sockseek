using System.Diagnostics;
using Sockseek.Api;

namespace Sockseek.Server;

/// <summary>
/// What this process holds, read on request. Linux reports where the memory
/// comes from; other systems report only the total and its peak.
/// </summary>
internal static class ProcessMemory
{
    public static ProcessMemoryDto Read()
    {
        if (OperatingSystem.IsLinux() && (ReadLinuxProportional() ?? ReadLinuxStatus()) is { } linux) return linux;
        using var process = Process.GetCurrentProcess();
        return new ProcessMemoryDto(process.WorkingSet64, process.PeakWorkingSet64);
    }

    // The resident counters count a page once for every mapping of it, so a
    // file mapped by ten database connections shows as ten times its size.
    // The proportional figures divide each page among its mappings. The kernel
    // walks the page tables to produce them: a few milliseconds per read. It
    // keeps no peak on this basis.
    private static ProcessMemoryDto? ReadLinuxProportional()
    {
        var kilobytes = ReadKilobytes("/proc/self/smaps_rollup");
        long? Bytes(string key) => kilobytes.TryGetValue(key, out long value) ? value * 1024 : null;
        return Bytes("Pss") is { } total && Bytes("Pss_Anon") is { } heap && Bytes("Pss_Shmem") is { } runtime
            && Bytes("Pss_File") is { } files
            ? new ProcessMemoryDto(total, null, heap, runtime, files, Bytes("SwapPss")) : null;
    }

    // Kernels without the proportional breakdown still say where resident memory comes from.
    private static ProcessMemoryDto? ReadLinuxStatus()
    {
        var kilobytes = ReadKilobytes("/proc/self/status");
        long? Bytes(string key) => kilobytes.TryGetValue(key, out long value) ? value * 1024 : null;
        return Bytes("VmRSS") is { } resident
            ? new ProcessMemoryDto(resident, Bytes("VmHWM") ?? resident,
                Bytes("RssAnon"), Bytes("RssShmem"), Bytes("RssFile"), Bytes("VmSwap")) : null;
    }

    private static Dictionary<string, long> ReadKilobytes(string path)
    {
        var kilobytes = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var value = line.AsSpan(colon + 1).Trim();
                if (value.EndsWith(" kB") && long.TryParse(value[..^3], out long amount))
                    kilobytes[line[..colon]] = amount;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            kilobytes.Clear();
        }
        return kilobytes;
    }
}
