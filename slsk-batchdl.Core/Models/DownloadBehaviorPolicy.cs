using Sldl.Core;
using Sldl.Core.Jobs;

namespace Sldl.Core.Models;

public sealed record DownloadBehaviorPolicy
{
    public DownloadBehavior Default { get; init; } = DownloadBehavior.Automatic;
    public DownloadBehavior? Song { get; init; }
    public DownloadBehavior? Album { get; init; }
    public DownloadBehavior? Aggregate { get; init; }
    public DownloadBehavior? AlbumAggregate { get; init; }

    public DownloadBehavior For(Job job)
        => job switch
        {
            SongJob => Song ?? Default,
            AlbumJob => Album ?? Default,
            AggregateJob => Aggregate ?? Default,
            AlbumAggregateJob => AlbumAggregate ?? Default,
            _ => Default,
        };
}
