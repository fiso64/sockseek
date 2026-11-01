using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Jobs;

public sealed record FileSearchProjection(SongQuery Query, bool IncludeFullResults = false);
public sealed record FolderSearchProjection(
    AlbumQuery Query,
    bool IncludeFiles = false,
    bool IgnoreStringSortConditions = false,
    FolderSortMode SortMode = FolderSortMode.AlbumRanked);
public sealed record AggregateTrackProjection(SongQuery Query);
public sealed record AggregateAlbumProjection(AlbumQuery Query);

public class SearchJob : Job
{
    private readonly Lock _projectionCacheLock = new();
    private readonly Dictionary<ProjectionCacheKey, object> _incrementalProjectionStates = [];
    private readonly ConcurrentDictionary<PeerDirectoryIdentity, PeerDirectorySnapshot> retrievedDirectories = [];

    public string QueryText { get; }
    public FileSearchProjection? DefaultFileProjection { get; init; }
    public FolderSearchProjection? DefaultFolderProjection { get; init; }
    public AggregateTrackProjection? DefaultAggregateTrackProjection { get; init; }
    public AggregateAlbumProjection? DefaultAggregateAlbumProjection { get; init; }
    public bool AggregatePresentation { get; init; }

    public SearchSession Session { get; }

    public int ResultCount => Session.ResultCount;
    public int Revision => Session.Revision;
    public bool IsComplete => Session.IsComplete;
    public SearchDefinition? Definition { get; internal set; }

    public override SongQuery QueryTrack => NetworkQuery;
    protected override bool DefaultCanBeSkipped => false;

    public SongQuery NetworkQuery { get; }

    public SearchJob(string queryText, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            throw new ArgumentException("queryText is required for search jobs");

        QueryText = queryText;
        NetworkQuery = new SongQuery { Title = queryText };
        Session = new SearchSession(Id, timeProvider, QueryText);
        SearchObservationSession = Session;
    }

    public SearchJob(SongQuery query, bool includeFullResults = false, TimeProvider? timeProvider = null)
    {
        QueryText = query.ToString(noInfo: true);
        NetworkQuery = query;
        DefaultFileProjection = new FileSearchProjection(query, includeFullResults);
        DefaultAggregateTrackProjection = new AggregateTrackProjection(query);
        Session = new SearchSession(Id, timeProvider, QueryText);
        SearchObservationSession = Session;
    }

    public SearchJob(AlbumQuery query, TimeProvider? timeProvider = null)
    {
        NetworkQuery = SearchResultProjector.AlbumNetworkQuery(query);
        QueryText = NetworkQuery.ToString(noInfo: true);
        DefaultFolderProjection = new FolderSearchProjection(query);
        DefaultAggregateAlbumProjection = new AggregateAlbumProjection(query);
        Session = new SearchSession(Id, timeProvider, QueryText);
        SearchObservationSession = Session;
    }

    internal IReadOnlyCollection<(Soulseek.SearchResponse Response, Soulseek.File File)> Snapshot()
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
            () => new IncrementalNeutralFileProjectionState(
                new IncrementalResultSorter(
                    projection.Query,
                    ProjectionSettings(search),
                    userSuccessCounts,
                    useInfer: false,
                    requireFileSatisfies: !projection.IncludeFullResults)));

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

    public SearchProjectionSnapshot<AlbumFolder> GetAlbumFolders(
        FolderSearchProjection projection, SearchSettings search, bool includeLocked = false)
    {
        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("album-folders", projection, search, includeLocked: includeLocked),
            () => new IncrementalNeutralProjectionState<IncrementalAlbumFolderProjector, AlbumFolder>(
                new IncrementalAlbumFolderProjector(
                    projection.Query,
                    ProjectionSettings(search),
                    ignoreStringSortConditions: projection.IgnoreStringSortConditions,
                    sortMode: projection.SortMode),
                (projector, results) => projector.AddRange(results),
                projector => projector.Snapshot(),
                includeLocked));

        var result = state.Snapshot(this);
        ApplyRetrievedDirectories(result.Items);
        return result;
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
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool includeLocked = false)
    {
        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("aggregate-tracks", projection, search, userSuccessCounts, includeLocked),
            () => new IncrementalNeutralProjectionState<IncrementalAggregateTrackProjector, SongJob>(
                new IncrementalAggregateTrackProjector(projection.Query, ProjectionSettings(search), userSuccessCounts),
                (projector, results) => projector.AddRange(results),
                projector => projector.Snapshot(),
                includeLocked));

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

    public SearchProjectionSnapshot<AlbumJob> GetAggregateAlbums(
        AggregateAlbumProjection projection, SearchSettings search, bool includeLocked = false)
    {
        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("aggregate-albums", projection, search, includeLocked: includeLocked),
            () => new IncrementalAlbumAggregateProjectionState(projection.Query, ProjectionSettings(search), includeLocked));

        var result = state.Snapshot(this);
        ApplyRetrievedDirectories(result.Items.SelectMany(album => album.Results));
        return result;
    }

    // Retrieval belongs to the source search, independently of a projection's
    // public/locked filtering. Reuse it after cache rebuilds and between views.
    internal void RecordRetrievedDirectory(PeerDirectorySnapshot snapshot)
        => retrievedDirectories[snapshot.Identity] = snapshot;

    private void ApplyRetrievedDirectories(IEnumerable<AlbumFolder> folders)
    {
        if (retrievedDirectories.IsEmpty) return;
        foreach (AlbumFolder folder in folders)
            if (!folder.IsFullyRetrieved
                && retrievedDirectories.TryGetValue(folder.DirectoryIdentity, out var snapshot))
                Searcher.ApplyDirectorySnapshot(folder, snapshot);
    }

    private SearchSettings ProjectionSettings(SearchSettings search)
        => Config?.ResolveSearchSettings(search) ?? search;

    private TState GetOrCreateIncrementalProjectionState<TState>(
        ProjectionCacheKey key,
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

    // Read-only views include locked observations; execution projections keep
    // their public-only default so observation never grants download eligibility.
    private static List<SearchProjectionInput> RawInputs(
        IReadOnlyList<SearchRawResult> rawResults, bool includeLocked = false)
        => rawResults
            .Where(result => includeLocked || result.Visibility == SearchResultVisibility.Public)
            .Select(result => result.ProjectionInput)
            .ToList();

    private ProjectionCacheKey ProjectionKey(
        string name,
        object projection,
        SearchSettings search,
        object? supplemental = null,
        bool includeLocked = false)
        => new(name, ProjectionIdentity(projection), search, supplemental, includeLocked,
            Config?.Output.AlbumArtOnly == true, Config?.DefaultMinSharesAggregate ?? SearchSettings.DefaultMinShares);

    private static object ProjectionIdentity(object projection)
        => projection switch
        {
            FileSearchProjection value => new FileProjectionKey(
                SongKey(value.Query),
                value.IncludeFullResults),
            FolderSearchProjection value => new FolderProjectionKey(
                AlbumKey(value.Query),
                value.IgnoreStringSortConditions,
                value.SortMode),
            AggregateTrackProjection value => new AggregateTrackProjectionKey(SongKey(value.Query)),
            AggregateAlbumProjection value => new AggregateAlbumProjectionKey(AlbumKey(value.Query)),
            _ => throw new ArgumentException("Unsupported search projection type.", nameof(projection)),
        };

    private static SongQueryKey SongKey(SongQuery query)
        => new(
                query.Artist,
                query.Title,
                query.Album,
                query.URI,
                query.Length,
                query.ArtistMaybeWrong);

    private static AlbumQueryKey AlbumKey(AlbumQuery query)
        => new(
                query.Artist,
                query.Album,
                query.SearchHint,
                query.URI,
                query.ArtistMaybeWrong);

    private readonly record struct ProjectionCacheKey(
        string Name,
        object Projection,
        SearchSettings Search,
        object? Supplemental,
        bool IncludeLocked,
        bool ArtworkOnly,
        int DefaultMinShares);
    private sealed record FileProjectionKey(SongQueryKey Query, bool IncludeFullResults);
    private sealed record FolderProjectionKey(
        AlbumQueryKey Query,
        bool IgnoreStringSortConditions,
        FolderSortMode SortMode);
    private sealed record AggregateTrackProjectionKey(SongQueryKey Query);
    private sealed record AggregateAlbumProjectionKey(AlbumQueryKey Query);
    private sealed record SongQueryKey(
        string? Artist,
        string? Title,
        string? Album,
        string? Uri,
        int? Length,
        bool ArtistMaybeWrong);
    private sealed record AlbumQueryKey(
        string? Artist,
        string? Album,
        string? SearchHint,
        string? Uri,
        bool ArtistMaybeWrong);

    private sealed class IncrementalNeutralProjectionState<TProjector, TItem>
    {
        private readonly Lock gate = new();
        private readonly TProjector projector;
        private readonly Func<TProjector, IEnumerable<SearchProjectionInput>, int> addRange;
        private readonly Func<TProjector, List<TItem>> snapshot;
        private readonly bool includeLocked;
        private long lastSequence;
        private SearchProjectionSnapshot<TItem>? cachedSnapshot;

        public IncrementalNeutralProjectionState(
            TProjector projector,
            Func<TProjector, IEnumerable<SearchProjectionInput>, int> addRange,
            Func<TProjector, List<TItem>> snapshot,
            bool includeLocked)
        {
            this.projector = projector;
            this.addRange = addRange;
            this.snapshot = snapshot;
            this.includeLocked = includeLocked;
        }

        public SearchProjectionSnapshot<TItem> Snapshot(SearchJob job)
        {
            lock (gate)
            {
                var newResults = job.RawSnapshot(lastSequence);
                if (newResults.Count > 0)
                {
                    addRange(projector, RawInputs(newResults, includeLocked));
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

                var items = snapshot(projector);
                cachedSnapshot = new SearchProjectionSnapshot<TItem>(revision, items, isComplete);
                return cachedSnapshot;
            }
        }
    }

    private sealed class IncrementalNeutralFileProjectionState(IncrementalResultSorter projector)
    {
        private readonly Lock gate = new();
        private long lastSequence;
        private SearchProjectionSnapshot<FileCandidate>? cachedSnapshot;

        public SearchProjectionSnapshot<FileCandidate> Snapshot(SearchJob job)
        {
            lock (gate)
            {
                var newResults = job.RawSnapshot(lastSequence);
                if (newResults.Count > 0)
                {
                    projector.AddRange(RawInputs(newResults));
                    lastSequence = newResults[^1].Sequence;
                    cachedSnapshot = null;
                }
                if (cachedSnapshot != null
                    && cachedSnapshot.Revision == job.Revision
                    && cachedSnapshot.IsComplete == job.IsComplete)
                    return cachedSnapshot;
                cachedSnapshot = new SearchProjectionSnapshot<FileCandidate>(
                    job.Revision,
                    projector.SnapshotProjectedFiles()
                        .Select(file => file.Input.ToFileCandidate(file.ConditionFacts))
                        .ToList(),
                    job.IsComplete);
                return cachedSnapshot;
            }
        }
    }

    private sealed class IncrementalAlbumAggregateProjectionState
    {
        private readonly Lock gate = new();
        private readonly IncrementalAlbumFolderProjector albumProjector;
        private readonly IncrementalAlbumAggregateProjector aggregateProjector;
        private readonly bool includeLocked;
        private long lastSequence;
        private SearchProjectionSnapshot<AlbumJob>? cachedSnapshot;

        public IncrementalAlbumAggregateProjectionState(AlbumQuery query, SearchSettings search, bool includeLocked)
        {
            this.includeLocked = includeLocked;
            albumProjector = new IncrementalAlbumFolderProjector(
                query,
                search,
                ignoreStringSortConditions: true,
                sortMode: FolderSortMode.DeterministicUnranked);
            aggregateProjector = new IncrementalAlbumAggregateProjector(query, search);
        }

        public SearchProjectionSnapshot<AlbumJob> Snapshot(SearchJob job)
        {
            lock (gate)
            {
                var newResults = job.RawSnapshot(lastSequence);
                if (newResults.Count > 0)
                {
                    var changes = albumProjector.AddRangeAndGetChanges(RawInputs(newResults, includeLocked));
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
