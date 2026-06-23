using System.Text.Json;
using Soulseek;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Benchmarks;

internal static class RealSearchCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task RunAsync(string[] args)
    {
        string queryText = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "artist - album";
        int minResults = IntArg(args, "--min-results", 10_000);
        int maxAttempts = IntArg(args, "--max-attempts", 5);
        int timeout = IntArg(args, "--timeout", 6_000);

        System.IO.Directory.CreateDirectory(CaptureDirectory());

        CapturePayload? best = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Console.WriteLine($"Capture attempt {attempt}/{maxAttempts}: searching \"{queryText}\"...");
            var capture = await CaptureOnceAsync(queryText, timeout);
            Console.WriteLine($"  {capture.ResultCount:n0} unique file results; {capture.LockedFilesCount:n0} locked files.");

            if (best == null || capture.ResultCount > best.ResultCount)
                best = capture;

            if (capture.ResultCount >= minResults)
                break;
        }

        if (best == null)
            throw new InvalidOperationException("No capture attempts ran.");

        string path = Path.Combine(
            CaptureDirectory(),
            $"{Slug(queryText)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{best.ResultCount}.raw.json");
        await System.IO.File.WriteAllTextAsync(path, JsonSerializer.Serialize(best, JsonOptions));

        Console.WriteLine($"Wrote {best.ResultCount:n0} result capture to {path}");
        if (best.ResultCount < minResults)
            Console.WriteLine($"Warning: best capture was below requested --min-results {minResults:n0}.");
    }

    private static async Task<CapturePayload> CaptureOnceAsync(string queryText, int timeout)
    {
        var engineSettings = new EngineSettings
        {
            UseRandomLogin = true,
            ListenPort = null,
            NoModifyShareCount = true,
        };
        using var manager = new SoulseekClientManager(engineSettings);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, timeout / 1000 + 30)));

        await manager.EnsureConnectedAndLoggedInAsync(engineSettings, cts.Token);
        var client = manager.Client ?? throw new InvalidOperationException("Soulseek client was not created.");

        var session = new SearchSession();
        var options = new SearchOptions(
            minimumResponseFileCount: 0,
            minimumPeerUploadSpeed: 0,
            searchTimeout: timeout,
            removeSingleCharacterSearchTerms: false,
            responseFilter: _ => true,
            fileFilter: _ => true);

        await client.SearchAsync(
            SearchQuery.FromText(queryText),
            responseHandler: session.AddResponse,
            options: options,
            cancellationToken: cts.Token);

        var query = new SongQuery { Artist = "artist", Album = "album", Title = "album" };
        var results = session.RawSnapshot()
            .Select(result => new CapturedResult(
                new CapturedUser(
                    result.Response.Username,
                    result.Response.UploadSpeed,
                    result.Response.HasFreeUploadSlot,
                    result.Response.QueueLength,
                    result.Response.LockedFileCount),
                new CapturedFile(
                    result.File.Filename,
                    result.File.Size,
                    result.File.Length,
                    result.File.BitRate,
                    result.File.SampleRate,
                    result.File.BitDepth)))
            .ToList();

        return new CapturePayload(
            Name: $"real-{Slug(queryText)}",
            CapturedAt: DateTimeOffset.UtcNow,
            Query: query,
            SearchTimeout: timeout,
            ResultCount: results.Count,
            LockedFilesCount: session.LockedFileCount,
            Results: results);
    }

    private static int IntArg(string[] args, string name, int defaultValue)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length
                && int.TryParse(args[i + 1], out int value))
            {
                return value;
            }

            string prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i][prefix.Length..], out value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static string CaptureDirectory()
    {
        var dir = new DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "Sockseek.sln")))
                return Path.Combine(dir.FullName, "artifacts", "search-captures");

            dir = dir.Parent;
        }

        return Path.Combine(System.IO.Directory.GetCurrentDirectory(), "artifacts", "search-captures");
    }

    private static string Slug(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }

    private record CapturePayload(
        string Name,
        DateTimeOffset CapturedAt,
        SongQuery Query,
        int SearchTimeout,
        int ResultCount,
        int LockedFilesCount,
        List<CapturedResult> Results);

    private record CapturedResult(CapturedUser User, CapturedFile File);

    private record CapturedUser(
        string Username,
        int UploadSpeed,
        bool HasFreeUploadSlot,
        int QueueLength,
        int LockedFileCount);

    private record CapturedFile(
        string Filename,
        long Size,
        int? Length,
        int? Bitrate,
        int? SampleRate,
        int? BitDepth);
}
