using System.Collections.Concurrent;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Sldl.Core.Jobs;

public sealed record FileSearchProjection(SongQuery Query, bool IncludeFullResults = false);
public sealed record FolderSearchProjection(AlbumQuery Query, bool IncludeFiles = false);
public sealed record AggregateTrackProjection(SongQuery Query);
public sealed record AggregateAlbumProjection(AlbumQuery Query);

public class SearchJob : Job
{
    private readonly Lock _projectionCacheLock = new();
    private readonly Dictionary<string, (int Revision, bool IsComplete, object Value)> _projectionCache = [];
    private readonly Dictionary<string, object> _incrementalProjectionStates = [];

    public string QueryText { get; }
    public FileSearchProjection? DefaultFileProjection { get; init; }
    public FolderSearchProjection? DefaultFolderProjection { get; init; }
    public AggregateTrackProjection? DefaultAggregateTrackProjection { get; init; }
    public AggregateAlbumProjection? DefaultAggregateAlbumProjection { get; init; }

    public SearchSession Session { get; } = new();

    public int ResultCount => Session.Results.Count;
    public int Revision => Session.Revision;
    public bool IsComplete => Session.IsComplete;

    public override SongQuery QueryTrack => NetworkQuery;
    protected override bool DefaultCanBeSkipped => false;

    public SongQuery NetworkQuery => new() { Title = QueryText };

    public SearchJob(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            throw new ArgumentException("queryText is required for search jobs");

        QueryText = queryText;
    }

    public SearchJob(SongQuery query, bool includeFullResults = false)
    {
        QueryText = query.ToString(noInfo: true);
        DefaultFileProjection = new FileSearchProjection(query, includeFullResults);
        DefaultAggregateTrackProjection = new AggregateTrackProjection(query);
    }

    public SearchJob(AlbumQuery query)
    {
        QueryText = SearchResultProjector.AlbumNetworkQuery(query).ToString(noInfo: true);
        DefaultFolderProjection = new FolderSearchProjection(query);
        DefaultAggregateAlbumProjection = new AggregateAlbumProjection(query);
    }

    public IReadOnlyCollection<(Soulseek.SearchResponse Response, Soulseek.File File)> Snapshot()
        => Session.Snapshot();

    public IReadOnlyList<SearchRawResult> RawSnapshot(long afterSequence = 0)
        => Session.RawSnapshot(afterSequence);

    public IAsyncEnumerable<SearchRawResult> ReadRawResultsAsync(
        long afterSequence = 0,
        CancellationToken ct = default)
        => Session.ReadRawResultsAsync(afterSequence, ct);

    public SearchProjectionSnapshot<FileCandidate> GetSortedTrackCandidates(
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var projection = DefaultFileProjection
            ?? new FileSearchProjection(new SongQuery { Title = QueryText });
        return GetSortedTrackCandidates(projection, search, userSuccessCounts);
    }

    public SearchProjectionSnapshot<FileCandidate> GetSortedTrackCandidates(
        SongQuery query,
        bool includeFullResults,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
        => GetSortedTrackCandidates(new FileSearchProjection(query, includeFullResults), search, userSuccessCounts);

    public SearchProjectionSnapshot<FileCandidate> GetSortedTrackCandidates(
        FileSearchProjection projection,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("sorted-track", projection, search, userSuccessCounts),
            () => new IncrementalRawProjectionState<IncrementalResultSorter, FileCandidate>(
                new IncrementalResultSorter(
                    projection.Query,
                    search,
                    userSuccessCounts,
                    useInfer: false,
                    useLevenshtein: false,
                    requireFileSatisfies: !projection.IncludeFullResults),
                (projector, results) => projector.AddRange(results),
                projector => projector.Snapshot()
                    .Select(x => new FileCandidate(x.Response, x.File))
                    .ToList()));

        return state.Snapshot(this);
    }

    public SearchProjectionSnapshot<AlbumFolder> GetAlbumFolders(SearchSettings search)
    {
        if (DefaultFolderProjection == null)
            throw new InvalidOperationException("Album folder projection requires a folder projection.");

        return GetAlbumFolders(DefaultFolderProjection, search);
    }

    public SearchProjectionSnapshot<AlbumFolder> GetAlbumFolders(AlbumQuery query, SearchSettings search)
        => GetAlbumFolders(new FolderSearchProjection(query), search);

    public SearchProjectionSnapshot<AlbumFolder> GetAlbumFolders(FolderSearchProjection projection, SearchSettings search)
    {
        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("album-folders", projection, search),
            () => new IncrementalRawProjectionState<IncrementalAlbumFolderProjector, AlbumFolder>(
                new IncrementalAlbumFolderProjector(projection.Query, search),
                (projector, results) => projector.AddRange(results),
                projector => projector.Snapshot()));

        return state.Snapshot(this);
    }

    public SearchProjectionSnapshot<SongJob> GetAggregateTracks(
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var projection = DefaultAggregateTrackProjection
            ?? (DefaultFileProjection is { } fileProjection
                ? new AggregateTrackProjection(fileProjection.Query)
                : new AggregateTrackProjection(new SongQuery { Title = QueryText }));
        return GetAggregateTracks(projection, search, userSuccessCounts);
    }

    public SearchProjectionSnapshot<SongJob> GetAggregateTracks(
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
        => GetAggregateTracks(new AggregateTrackProjection(query), search, userSuccessCounts);

    public SearchProjectionSnapshot<SongJob> GetAggregateTracks(
        AggregateTrackProjection projection,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("aggregate-tracks", projection, search, userSuccessCounts),
            () => new IncrementalRawProjectionState<IncrementalAggregateTrackProjector, SongJob>(
                new IncrementalAggregateTrackProjector(projection.Query, search, userSuccessCounts),
                (projector, results) => projector.AddRange(results),
                projector => projector.Snapshot()));

        return state.Snapshot(this);
    }

    public SearchProjectionSnapshot<AlbumJob> GetAggregateAlbums(SearchSettings search)
    {
        if (DefaultAggregateAlbumProjection == null)
            throw new InvalidOperationException("Album aggregate projection requires an album projection.");

        return GetAggregateAlbums(DefaultAggregateAlbumProjection, search);
    }

    public SearchProjectionSnapshot<AlbumJob> GetAggregateAlbums(AlbumQuery query, SearchSettings search)
        => GetAggregateAlbums(new AggregateAlbumProjection(query), search);

    public SearchProjectionSnapshot<AlbumJob> GetAggregateAlbums(AggregateAlbumProjection projection, SearchSettings search)
    {
        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("aggregate-albums", projection, search),
            () => new IncrementalAlbumAggregateProjectionState(projection.Query, search));

        return state.Snapshot(this);
    }

    private TState GetOrCreateIncrementalProjectionState<TState>(
        string key,
        Func<TState> factory)
        where TState : class
    {
        lock (_projectionCacheLock)
        {
            if (_incrementalProjectionStates.TryGetValue(key, out var cached)
                && cached is TState cachedState)
                return cachedState;

            var created = factory();
            _incrementalProjectionStates[key] = created;
            return created;
        }
    }

    private static List<(Soulseek.SearchResponse Response, Soulseek.File File)> RawPairs(IReadOnlyList<SearchRawResult> rawResults)
        => rawResults
            .Select(x => (x.Response, x.File))
            .ToList();

    private static string ProjectionKey(string name, params object[] dependencies)
        => name + ":" + string.Join(':', dependencies.Select(ProjectionDependencyKey));

    private static string ProjectionDependencyKey(object dependency)
        => dependency switch
        {
            FileSearchProjection projection => string.Join('\0',
                "file",
                ProjectionDependencyKey(projection.Query),
                projection.IncludeFullResults),
            FolderSearchProjection projection => string.Join('\0',
                "folder",
                ProjectionDependencyKey(projection.Query)),
            AggregateTrackProjection projection => string.Join('\0',
                "aggregate-track",
                ProjectionDependencyKey(projection.Query)),
            AggregateAlbumProjection projection => string.Join('\0',
                "aggregate-album",
                ProjectionDependencyKey(projection.Query)),
            SongQuery query => string.Join('\0',
                "song",
                query.Artist,
                query.Title,
                query.Album,
                query.URI,
                query.Length,
                query.ArtistMaybeWrong),
            AlbumQuery query => string.Join('\0',
                "album",
                query.Artist,
                query.Album,
                query.SearchHint,
                query.URI,
                query.ArtistMaybeWrong),
            _ => dependency.GetHashCode().ToString(),
        };

    private sealed class IncrementalRawProjectionState<TProjector, TItem>
    {
        private readonly Lock gate = new();
        private readonly TProjector projector;
        private readonly Func<TProjector, IEnumerable<(Soulseek.SearchResponse Response, Soulseek.File File)>, int> addRange;
        private readonly Func<TProjector, List<TItem>> snapshot;
        private long lastSequence;
        private SearchProjectionSnapshot<TItem>? cachedSnapshot;

        public IncrementalRawProjectionState(
            TProjector projector,
            Func<TProjector, IEnumerable<(Soulseek.SearchResponse Response, Soulseek.File File)>, int> addRange,
            Func<TProjector, List<TItem>> snapshot)
        {
            this.projector = projector;
            this.addRange = addRange;
            this.snapshot = snapshot;
        }

        public SearchProjectionSnapshot<TItem> Snapshot(SearchJob job)
        {
            lock (gate)
            {
                var newResults = job.RawSnapshot(lastSequence);
                if (newResults.Count > 0)
                {
                    addRange(projector, RawPairs(newResults));
                    lastSequence = newResults[^1].Sequence;
                    cachedSnapshot = null;
                }

                int revision = job.Revision;
                bool isComplete = job.IsComplete;
                if (cachedSnapshot != null
                    && cachedSnapshot.Revision == revision
                    && cachedSnapshot.IsComplete == isComplete)
                {
                    return cachedSnapshot;
                }

                cachedSnapshot = new SearchProjectionSnapshot<TItem>(revision, snapshot(projector), isComplete);
                return cachedSnapshot;
            }
        }
    }

    private sealed class IncrementalAlbumAggregateProjectionState
    {
        private readonly Lock gate = new();
        private readonly IncrementalAlbumFolderProjector albumProjector;
        private readonly IncrementalAlbumAggregateProjector aggregateProjector;
        private long lastSequence;
        private SearchProjectionSnapshot<AlbumJob>? cachedSnapshot;

        public IncrementalAlbumAggregateProjectionState(AlbumQuery query, SearchSettings search)
        {
            albumProjector = new IncrementalAlbumFolderProjector(query, search);
            aggregateProjector = new IncrementalAlbumAggregateProjector(query, search);
        }

        public SearchProjectionSnapshot<AlbumJob> Snapshot(SearchJob job)
        {
            lock (gate)
            {
                var newResults = job.RawSnapshot(lastSequence);
                if (newResults.Count > 0)
                {
                    var changes = albumProjector.AddRangeAndGetChanges(RawPairs(newResults));
                    aggregateProjector.ApplyChanges(changes);
                    lastSequence = newResults[^1].Sequence;
                    cachedSnapshot = null;
                }

                int revision = job.Revision;
                bool isComplete = job.IsComplete;
                if (cachedSnapshot != null
                    && cachedSnapshot.Revision == revision
                    && cachedSnapshot.IsComplete == isComplete)
                {
                    return cachedSnapshot;
                }

                cachedSnapshot = new SearchProjectionSnapshot<AlbumJob>(revision, aggregateProjector.Snapshot(), isComplete);
                return cachedSnapshot;
            }
        }
    }

    public override string ToString(bool noInfo)
        => QueryText;
}
