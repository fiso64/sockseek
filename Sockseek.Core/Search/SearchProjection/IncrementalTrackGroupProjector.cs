using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

public sealed record SearchViewProjectedTrackGroup(
    int Index,
    SongQuery Query,
    int ShareCount,
    long SelectableOptionCount,
    ProjectedFileCandidate Representative,
    IReadOnlyList<ProjectedFileCandidate> NewOptions,
    SongQuery? StableQuery = null);

public sealed class IncrementalTrackGroupProjector
{
    private readonly SongQuery query;
    private readonly SearchSettings search;
    private readonly ConcurrentDictionary<string, int> peerFailureCounts;
    private readonly SongQueryGroups<TrackGroupBucket> buckets;
    private readonly Dictionary<string, SongQuery> inferredByFilename = new(StringComparer.Ordinal);
    private readonly HashSet<(PeerPathKey Path, SearchResultVisibility Visibility)> seen = [];

    public IncrementalTrackGroupProjector(
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? peerFailureCounts = null,
        string? filterText = null)
    {
        this.query = query;
        this.search = search;
        this.peerFailureCounts = peerFailureCounts ?? new ConcurrentDictionary<string, int>();
        string? filter = string.IsNullOrWhiteSpace(filterText) ? null : filterText.Trim();
        buckets = new(search.GroupLengthTol,
            (key, index) => new TrackGroupBucket(index, key, query, search, this.peerFailureCounts, filter));
    }

    public int Count => seen.Count;

    public void Clear()
    {
        buckets.Clear();
        inferredByFilename.Clear();
        seen.Clear();
    }

    internal int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
        => AddRange(results.Select((result, index) => SearchProjectionInput.FromLive(
            index + 1L, index + 1, result.Response, result.File, DateTimeOffset.UnixEpoch)));

    public int AddRange(IEnumerable<SearchProjectionInput> results)
        => AddRangeCore(results, applyRequiredConditions: true);

    // Automatic discovery already applied each network attempt's eligibility
    // policy, including deliberate fallback relaxation. Do not apply the original
    // conditions again; neutral Search Views still filter their raw observations.
    internal int AddAdmittedRange(IEnumerable<SearchProjectionInput> results)
        => AddRangeCore(results, applyRequiredConditions: false);

    private int AddRangeCore(IEnumerable<SearchProjectionInput> results, bool applyRequiredConditions)
    {
        int added = 0;
        foreach (var input in results)
        {
            var seenKey = (
                new PeerPathKey(input.Username, input.Filename),
                input.Visibility);
            if (!seen.Add(seenKey) || !search.NecessaryCond.VisibilitySatisfies(input.Visibility))
                continue;

            if (applyRequiredConditions && !SearchResultProjector.TrackGroupProjectionIncludes(input, query, search))
                continue;

            var bucket = GetBucket(input);
            bucket.Add(input);
            added++;
        }

        return added;
    }

    public IReadOnlyList<SearchViewProjectedTrackGroup> AddRangeForSearchView(
        IEnumerable<SearchProjectionInput> results)
    {
        var touched = new Dictionary<int, (
            TrackGroupBucket Bucket,
            bool WasVisible,
            List<ProjectedFileCandidate> Added)>();
        foreach (SearchProjectionInput input in results)
        {
            var seenKey = (
                new PeerPathKey(input.Username, input.Filename),
                input.Visibility);
            if (!seen.Add(seenKey)
                || !SearchResultProjector.TrackGroupProjectionIncludes(
                    input,
                    query,
                    search))
            {
                continue;
            }

            TrackGroupBucket bucket = GetBucket(input);
            bool wasVisible = IsVisible(bucket);
            ProjectedFileCandidate? projected = bucket.Add(input);
            // Hidden groups retain their candidates without allocating a public
            // change builder. A late match/eligible sharer still reveals them.
            if (projected is null && !wasVisible && !IsVisible(bucket))
                continue;
            if (!touched.TryGetValue(bucket.Index, out var change))
            {
                change = (bucket, wasVisible, []);
                touched.Add(bucket.Index, change);
            }
            if (projected is not null) change.Added.Add(projected);
        }

        var changed = new List<SearchViewProjectedTrackGroup>();
        foreach (var change in touched.Values.OrderBy(value => value.Bucket.Index))
        {
            TrackGroupBucket bucket = change.Bucket;
            if (!IsVisible(bucket))
                continue;
            changed.Add(ProjectGroup(bucket, change.WasVisible ? change.Added : null));
        }
        return changed;
    }

    public List<SongJob> Snapshot()
        => buckets.Values
            .Where(IsVisible)
            .OrderByDescending(x => x.ShareCount)
            .ThenBy(x => x.Index)
            .Select(x =>
            {
                var song = new SongJob(x.QueryWithKnownLength());
                song.Candidates = x.SortedCandidates();
                return song;
            })
            .ToList();

    public List<SearchViewProjectedTrackGroup> SnapshotForSearchView()
        => buckets.Values
            .Where(IsVisible)
            .Select(bucket => ProjectGroup(bucket))
            .ToList();

    internal SearchViewProjectedTrackGroup SnapshotGroupForSearchView(int index)
    {
        TrackGroupBucket bucket = buckets.Values[index];
        if (!IsVisible(bucket))
            throw new InvalidOperationException("The group track group is not visible.");
        return ProjectGroup(bucket);
    }

    private static SearchViewProjectedTrackGroup ProjectGroup(
        TrackGroupBucket bucket,
        IReadOnlyList<ProjectedFileCandidate>? added = null)
    {
        // Existing groups publish their delta. Full ordering belongs to a
        // requested snapshot or the group's first visible publication.
        IReadOnlyList<ProjectedFileCandidate> options = added ?? bucket.SortedProjected();
        return new SearchViewProjectedTrackGroup(
            bucket.Index,
            bucket.QueryWithKnownLength(),
            bucket.ShareCount,
            bucket.PublicOptionCount,
            added == null ? options[0] : bucket.BestProjected(),
            options,
            new SongQuery(bucket.Query));
    }

    private TrackGroupBucket GetBucket(SearchProjectionInput input)
    {
        // Multiple peers commonly advertise the same spelling. Inference is
        // query-dependent but not peer/duration-dependent; retain it per view.
        if (!inferredByFilename.TryGetValue(input.Filename, out var inferred))
        {
            inferred = Searcher.InferSongQuery(input.Filename, query);
            inferredByFilename.Add(input.Filename, inferred);
        }
        var key = new SongQuery(inferred) { Length = input.Length ?? -1 };
        return buckets.GetOrAdd(key);
    }

    private static bool IsVisible(TrackGroupBucket bucket) => bucket.IsVisible;

    private sealed class TrackGroupBucket
    {
        private IncrementalResultSorter? sorter;
        private List<PendingCandidate>? pending;
        private readonly SongQuery sourceQuery;
        private readonly SearchSettings search;
        private readonly ConcurrentDictionary<string, int> peerFailureCounts;
        private readonly string? filterText;
        private bool matchesText;
        private int? firstKnownLength;
        private bool? strictMatch;
        private readonly HashSet<string> users = new(StringComparer.Ordinal);

        public int Index { get; }
        public SongQuery Query { get; }
        public int ShareCount => users.Count;
        public long PublicOptionCount { get; private set; }
        public bool IsVisible
        {
            get
            {
                if (!MeetsVisibility()) return false;
                if (sorter is null && pending is not null) PrepareRanking();
                return sorter?.Count > 0;
            }
        }

        public TrackGroupBucket(
            int index,
            SongQuery query,
            SongQuery sourceQuery,
            SearchSettings search,
            ConcurrentDictionary<string, int> peerFailureCounts,
            string? filterText)
        {
            Index = index;
            Query = query;
            this.sourceQuery = sourceQuery;
            this.search = search;
            this.peerFailureCounts = peerFailureCounts;
            this.filterText = filterText;
            matchesText = filterText is null || ContainsFilter(query.Artist) || ContainsFilter(query.Title);
        }

        private void PrepareRanking()
        {
            sorter = new IncrementalResultSorter(
                Query,
                search,
                peerFailureCounts,
                albumMode: false,
                ignoreStringSortConditions: true,
                // The containing projector owns exact identity deduplication.
                deduplicateInputs: false,
                necessaryConditionEvaluator: static _ => true);
            if (pending is null) return;
            foreach (var candidate in pending) AddRanked(candidate.Input, candidate.PeerFailureCount);
            pending = null;
        }

        public ProjectedFileCandidate? Add(SearchProjectionInput input)
        {
            firstKnownLength ??= input.Length;
            // Sharers describe discoveries, as in full grouping.
            // Workflow-local ignored peers contribute no selectable options.
            users.Add(input.Username);
            int failureCount = peerFailureCounts.GetValueOrDefault(input.Username, 0);
            if (failureCount >= search.EffectiveIgnoreOn) return null;
            matchesText |= ContainsFilter(input.Filename);
            if (sorter is null)
            {
                // Grouping and visibility only need admitted source facts.
                // Build conditions/rank once, when this group can be read or
                // executed; replay its earlier options without losing backfill.
                if (!MeetsVisibility())
                {
                    (pending ??= []).Add(new(input, failureCount));
                    return null;
                }
                PrepareRanking();
            }
            return AddRanked(input, failureCount);
        }

        private ProjectedFileCandidate? AddRanked(SearchProjectionInput input, int failureCount)
        {
            var projected = sorter!.AddWithPeerFailureCount(input, failureCount);
            if (projected is not null && input.Visibility == SearchResultVisibility.Public) PublicOptionCount++;
            return projected;
        }

        private bool ContainsFilter(string value)
            => filterText is not null && value.Contains(filterText, StringComparison.OrdinalIgnoreCase);

        private bool MeetsVisibility()
            => matchesText && ShareCount >= search.EffectiveMinGroupShares
                && (search.Relax || MatchesStrictQuery());

        private readonly record struct PendingCandidate(SearchProjectionInput Input, int PeerFailureCount);

        public SongQuery QueryWithKnownLength()
        {
            if (Query.Length != -1)
                return Query;

            return new SongQuery(Query) { Length = firstKnownLength ?? -1 };
        }

        // Group identity strings do not change as options arrive. Evaluate this
        // only when strict visibility is needed; Relax remains a caller policy.
        public bool MatchesStrictQuery() => strictMatch ??= QueryMatching.GroupIdentityMatches(Query, sourceQuery);

        public List<FileCandidate> SortedCandidates()
            => sorter!.SnapshotProjectedFiles()
                .Select(file => file.Input.ToFileCandidate(file.ConditionFacts))
                .ToList();

        public List<ProjectedFileCandidate> SortedProjected()
            => sorter!.SnapshotProjectedFiles();

        public ProjectedFileCandidate BestProjected()
            => sorter!.BestProjectedFile();
    }
}
