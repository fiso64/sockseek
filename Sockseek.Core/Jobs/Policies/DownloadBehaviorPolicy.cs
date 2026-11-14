using Sockseek.Core;
using Sockseek.Core.Jobs;

namespace Sockseek.Core.Models;

public sealed record DownloadBehaviorPolicy
{
    public DownloadBehavior Default { get; init; } = DownloadBehavior.Automatic;
    public DownloadBehavior? Song { get; init; }
    public DownloadBehavior? Album { get; init; }
    public DownloadBehavior? SongGroups { get; init; }
    public DownloadBehavior? AlbumGroups { get; init; }

    public DownloadBehavior For(Job job)
        => job switch
        {
            SongJob => Song ?? Default,
            AlbumJob => Album ?? Default,
            SongGroupsJob => SongGroups ?? Default,
            AlbumGroupsJob => AlbumGroups ?? Default,
            _ => Default,
        };
}
