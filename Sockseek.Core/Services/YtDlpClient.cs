using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed class YtDlpSongDownloadFallback(IYtDlpClient client) : ISongDownloadFallback
{
    public async Task<JobOutcome?> TryDownloadAsync(
        SongJob song,
        DownloadSettings settings,
        FileManager organizer,
        IJobLog? log,
        CancellationToken ct)
    {
        if (!settings.YtDlp.UseYtdlp)
            return null;

        var results = await client.SearchAsync(song.Query, log, ct);
        var result = results.FirstOrDefault();
        if (result == null)
            return null;

        var sourceName = string.IsNullOrWhiteSpace(result.Title)
            ? $"{song.Query.Artist} - {song.Query.Title}"
            : result.Title;
        var savePathNoExt = organizer.GetSavePathNoExt(sourceName + ".mp3");
        var downloadPath = await client.DownloadAsync(
            result.Id,
            savePathNoExt,
            settings.YtDlp.YtdlpArgument ?? "",
            log,
            ct);

        return string.IsNullOrWhiteSpace(downloadPath)
            ? null
            : JobOutcome.Done(downloadPath);
    }
}

public sealed class YtDlpClient : IYtDlpClient
{
    public async Task<IReadOnlyList<YtDlpSearchResult>> SearchAsync(SongQuery query, IJobLog? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = await YouTube.YtdlpSearch(query, log);
        ct.ThrowIfCancellationRequested();
        return results
            .Select(result => new YtDlpSearchResult(result.length, result.id, result.title))
            .ToList();
    }

    public async Task<string> DownloadAsync(string id, string savePathNoExt, string ytdlpArgument, IJobLog? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = await YouTube.YtdlpDownload(id, savePathNoExt, ytdlpArgument, log);
        ct.ThrowIfCancellationRequested();
        return path;
    }
}
