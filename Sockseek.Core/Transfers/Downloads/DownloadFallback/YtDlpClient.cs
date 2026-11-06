using System.Diagnostics;
using System.Text.Json;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed class YtDlpSongDownloadFallback(IYtDlpClient client) : ISongDownloadFallback
{
    public bool CanRun(SongJob song, DownloadSettings settings)
        => settings.YtDlp.UseYtdlp;

    public async Task<JobOutcome?> TryDownloadAsync(
        SongJob song,
        DownloadSettings settings,
        FileManager organizer,
        IJobLog? log,
        CancellationToken ct,
        Action<FallbackTransferDescriptor>? transferStarting = null)
    {
        if (!CanRun(song, settings))
            return null;

        var results = await client.SearchAsync(song.Query, log, ct);
        if (results.Count == 0)
            return null;

        var result = results[0];
        var sourceName = string.IsNullOrWhiteSpace(result.Title)
            ? $"{song.Query.Artist} - {song.Query.Title}"
            : result.Title;
        var savePathNoExt = organizer.GetSavePathNoExt(sourceName + ".mp3");
        transferStarting?.Invoke(new FallbackTransferDescriptor(result.Id, sourceName, savePathNoExt));
        var downloadPath = await client.DownloadAsync(
            result.Id,
            savePathNoExt,
            settings.YtDlp.YtdlpArgument ?? "",
            log,
            ct);

        return string.IsNullOrWhiteSpace(downloadPath)
            ? null
            : JobOutcome.Done(downloadPath, downloadSource: SongDownloadSource.Fallback);
    }
}

public sealed class YtDlpClient : IYtDlpClient
{
    public async Task<IReadOnlyList<YtDlpSearchResult>> SearchAsync(SongQuery query, IJobLog? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = await YtDlpCommand.SearchAsync(query, log);
        ct.ThrowIfCancellationRequested();
        return results
            .Select(result => new YtDlpSearchResult(result.length, result.id, result.title))
            .ToList();
    }

    public async Task<string> DownloadAsync(string id, string savePathNoExt, string ytdlpArgument, IJobLog? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = await YtDlpCommand.DownloadAsync(id, savePathNoExt, ytdlpArgument, log);
        ct.ThrowIfCancellationRequested();
        return path;
    }
}

internal static class YtDlpCommand
{
    private const string DownloadPathPrefix = "sockseek-download-path:";
    private const string DownloadResultPrefix = "sockseek-download-result:";
    private const string DownloadResultTemplate =
        "[%(filepath)j,%(id)j,%(title)j,%(webpage_url)j,%(extractor)j,%(format_id)j,%(ext)j,%(duration)j]";

    public static async Task<List<(int length, string id, string title)>> SearchAsync(SongQuery query, IJobLog? log = null)
    {
        log ??= ExtractorContext.None.Log;
        string search = query.Artist.Length > 0 ? $"{query.Artist} - {query.Title}" : query.Title;
        var result = await RunAsync($"\"ytsearch3:{search}\" --dump-json", log);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp search failed with exit code {result.ExitCode}: {string.Join(Environment.NewLine, result.Stderr)}");

        List<(int, string, string)> results = [];
        foreach (var output in result.Stdout)
        {
            if (!TryParseSearchResult(output, out var parsed))
                continue;

            results.Add(parsed);
        }

        return results;
    }

    internal static bool TryParseSearchResult(string json, out (int length, string id, string title) result)
    {
        result = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!TryGetString(root, "id", out var id) || !TryGetString(root, "title", out var title))
                return false;

            var length = TryGetInt(root, "duration", out var duration)
                ? duration
                : -1;
            result = (length, id, title);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }

        static bool TryGetString(JsonElement element, string propertyName, out string value)
        {
            value = "";
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                return false;

            value = property.GetString() ?? "";
            return value.Length > 0;
        }

        static bool TryGetInt(JsonElement element, string propertyName, out int value)
        {
            value = 0;
            if (!element.TryGetProperty(propertyName, out var property))
                return false;

            if (property.ValueKind == JsonValueKind.Number)
                return property.TryGetInt32(out value)
                    || (property.TryGetDouble(out var number) && TryRound(number, out value));

            if (property.ValueKind == JsonValueKind.String)
                return int.TryParse(property.GetString(), out value);

            return false;

            static bool TryRound(double number, out int value)
            {
                value = 0;
                if (number < int.MinValue || number > int.MaxValue)
                    return false;

                value = (int)Math.Round(number);
                return true;
            }
        }
    }

    public static async Task<string> DownloadAsync(string id, string savePathNoExt, string ytdlpArgument = "", IJobLog? log = null)
    {
        log ??= ExtractorContext.None.Log;
        var arguments = BuildDownloadArguments(id, savePathNoExt, ytdlpArgument);

        var result = await RunAsync(arguments, log, StripInternalDownloadResultPrint(arguments));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp download failed with exit code {result.ExitCode}: {string.Join(Environment.NewLine, result.Stderr)}");

        var downloadResult = result.Stdout
            .Select(TryParseDownloadResult)
            .Where(download => !string.IsNullOrWhiteSpace(download?.Filepath))
            .LastOrDefault();

        var outputPath = downloadResult?.Filepath;
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new InvalidOperationException("yt-dlp did not report an output path.");

        if (!File.Exists(outputPath))
            throw new FileNotFoundException($"yt-dlp reported output path '{outputPath}', but the file does not exist.", outputPath);

        log.Info("yt-dlp downloaded: " + FormatDownloadSuccess(downloadResult!));
        log.Debug("yt-dlp download result: " + FormatDownloadResult(downloadResult!));

        return outputPath;
    }

    internal static string BuildDownloadArguments(string id, string savePathNoExt, string ytdlpArgument = "")
    {
        if (ytdlpArgument.Length == 0)
            ytdlpArgument = "\"{id}\" -f bestaudio/best -ci -o \"{savepath-noext}.%(ext)s\" -x";

        return ytdlpArgument
            .Replace("{id}", id)
            .Replace("{savepath}", savePathNoExt)
            .Replace("{savepath-noext}", savePathNoExt)
            .Replace("{savedir}", Path.GetDirectoryName(savePathNoExt))
            + $" --print \"after_move:{DownloadResultPrefix}{DownloadResultTemplate}\"";
    }

    internal static string StripInternalDownloadResultPrint(string arguments)
    {
        var suffix = $" --print \"after_move:{DownloadResultPrefix}{DownloadResultTemplate}\"";
        return arguments.EndsWith(suffix, StringComparison.Ordinal)
            ? arguments[..^suffix.Length]
            : arguments;
    }

    internal static string? TryParseDownloadPath(string line)
        => TryParseDownloadResult(line)?.Filepath;

    internal static YtDlpDownloadResult? TryParseDownloadResult(string line)
    {
        if (line.StartsWith(DownloadPathPrefix, StringComparison.Ordinal))
        {
            var rawPath = line[DownloadPathPrefix.Length..];
            try
            {
                using var pathDoc = JsonDocument.Parse(rawPath);
                return pathDoc.RootElement.ValueKind == JsonValueKind.String
                    ? new YtDlpDownloadResult(pathDoc.RootElement.GetString())
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (!line.StartsWith(DownloadResultPrefix, StringComparison.Ordinal))
            return null;

        var rawResult = line[DownloadResultPrefix.Length..];
        try
        {
            using var doc = JsonDocument.Parse(rawResult);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 8)
                return null;

            var filepath = ParseYtDlpString(root[0]);
            return string.IsNullOrWhiteSpace(filepath)
                ? null
                : new YtDlpDownloadResult(
                    filepath,
                    ParseYtDlpString(root[1]),
                    ParseYtDlpString(root[2]),
                    ParseYtDlpString(root[3]),
                    ParseYtDlpString(root[4]),
                    ParseYtDlpString(root[5]),
                    ParseYtDlpString(root[6]),
                    ParseYtDlpInt(root[7]));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed record YtDlpDownloadResult(
        string? Filepath,
        string? Id = null,
        string? Title = null,
        string? WebpageUrl = null,
        string? Extractor = null,
        string? FormatId = null,
        string? Ext = null,
        int? Duration = null);

    private static string FormatDownloadResult(YtDlpDownloadResult result)
    {
        var parts = new List<string>();
        Add("id", result.Id);
        Add("title", result.Title);
        Add("url", result.WebpageUrl);
        Add("extractor", result.Extractor);
        Add("format", result.FormatId);
        Add("ext", result.Ext);
        Add("duration", result.Duration?.ToString());
        Add("path", result.Filepath);
        return string.Join(", ", parts);

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{name}={value}");
        }
    }

    internal static string FormatDownloadSuccess(YtDlpDownloadResult result)
    {
        var source = !string.IsNullOrWhiteSpace(result.Title) && !string.IsNullOrWhiteSpace(result.Id)
            ? $"{result.Title} ({result.Id})"
            : result.Title ?? result.Id ?? "unknown item";
        return $"{source} -> {DisplayPath(result.Filepath)}";
    }

    private static string DisplayPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "<unknown path>";

        try
        {
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), fullPath);
            return IsContainedRelativePath(relative) && relative.Length < fullPath.Length
                ? relative
                : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool IsContainedRelativePath(string path)
        => path != ".."
            && !path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !path.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(path);

    private static string? ParseYtDlpString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return element.GetRawText();
    }

    private static int? ParseYtDlpInt(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt32(out var integer))
                return integer;
            if (element.TryGetDouble(out var number) && number >= int.MinValue && number <= int.MaxValue)
                return (int)Math.Round(number);
        }

        return element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private sealed record ProcessResult(int ExitCode, IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr);

    private static async Task<ProcessResult> RunAsync(string arguments, IJobLog log, string? logArguments = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        log.Info($"{process.StartInfo.FileName} {logArguments ?? process.StartInfo.Arguments}");
        process.Start();

        var stdoutTask = ReadLinesAsync(process.StandardOutput);
        var stderrTask = ReadLinesAsync(process.StandardError);
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        foreach (var line in stderr)
            log.Info(line);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<List<string>> ReadLinesAsync(TextReader reader)
    {
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            lines.Add(line);
        return lines;
    }
}
