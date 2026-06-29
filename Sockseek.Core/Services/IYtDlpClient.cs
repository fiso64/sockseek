using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

public sealed record YtDlpSearchResult(int Length, string Id, string Title);

public interface IYtDlpClient
{
    Task<IReadOnlyList<YtDlpSearchResult>> SearchAsync(SongQuery query, IJobLog? log, CancellationToken ct);

    Task<string> DownloadAsync(string id, string savePathNoExt, string ytdlpArgument, IJobLog? log, CancellationToken ct);
}
