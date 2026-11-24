using System.Diagnostics;
using Sockseek.Api;

namespace Sockseek.Server;

/// <summary>
/// What this process holds, read on request. Linux reports where resident
/// memory comes from; other systems report only the total and its peak.
/// </summary>
internal static class ProcessMemory
{
    public static ProcessMemoryDto Read()
    {
        if (OperatingSystem.IsLinux() && ReadLinuxStatus() is { } linux) return linux;
        using var process = Process.GetCurrentProcess();
        return new ProcessMemoryDto(process.WorkingSet64, process.PeakWorkingSet64);
    }

    // The kernel renders this small file on demand; reading it costs no more
    // than the request that asked for it.
    private static ProcessMemoryDto? ReadLinuxStatus()
    {
        try
        {
            var kilobytes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var value = line.AsSpan(colon + 1).Trim();
                if (value.EndsWith(" kB") && long.TryParse(value[..^3], out long amount))
                    kilobytes[line[..colon]] = amount;
            }
            long? Bytes(string key) => kilobytes.TryGetValue(key, out long value) ? value * 1024 : null;
            if (Bytes("VmRSS") is not { } resident) return null;
            return new ProcessMemoryDto(resident, Bytes("VmHWM") ?? resident,
                Bytes("RssAnon"), Bytes("RssShmem"), Bytes("RssFile"), Bytes("VmSwap"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
