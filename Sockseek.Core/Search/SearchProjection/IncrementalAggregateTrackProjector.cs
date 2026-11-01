using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

public sealed record SearchViewProjectedAggregateTrackGroup(
    int Index,
    SongQuery Query,
    int ShareCount,
    long SelectableOptionCount,
    ProjectedFileCandidate Representative,
    IReadOnlyList<ProjectedFileCandidate> NewOptions,
    SongQuery? StableQuery = null);

public sealed class IncrementalAggregateTrackProjector
{
    private readonly SongQuery query;
    private readonly SearchSettings search;
    private readonly ConcurrentDictionary<string, int> userSuccessCounts;
    private readonly SongQueryGroups<AggregateTrackBucket> buckets;
    private readonly Dictionary<string, SongQuery> inferredByFilename = new(StringComparer.Ordinal);
    private readonly HashSet<(PeerPathKey Path, SearchResultVisibility Visibility)> seen = [];

    public IncrementalAggregateTrackProjector(
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        string? filterText = null)
    {
        this.query = query;
        this.search = search;
        this.userSuccessCounts = userSuccessCounts ?? new ConcurrentDictionary<string, int>();
        string? filter = string.IsNullOrWhiteSpace(filterText) ? null : filterText.Trim();
        buckets = new(search.AggregateLengthTol,
            (key, index) => new AggregateTrackBucket(index, key, query, search, this.userSuccessCounts, filter));
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
            if (!seen.Add(seenKey))
                continue;

            if (applyRequiredConditions && !SearchResultProjector.AggregateTrackProjectionIncludes(input, query, search))
                continue;

            var bucket = GetBucket(input);
            bucket.Add(input);
            added++;
        }

        return added;
    }

    public IReadOnlyList<SearchViewProjectedAggregateTrackGroup> AddRangeForSearchView(
        IEnumerable<SearchProjectionInput> results)
    {
        var touched = new Dictionary<int, (
            AggregateTrackBucket Bucket,
            bool WasVisible,
            List<ProjectedFileCandidate> Added)>();
        foreach (SearchProjectionInput input in results)
        {
            var seenKey = (
                new PeerPathKey(input.Username, input.Filename),
                input.Visibility);
            if (!seen.Add(seenKey)
                || !SearchResultProjector.AggregateTrackProjectionIncludes(
                    input,
                    query,
                    search))
            {
                continue;
            }

            AggregateTrackBucket bucket = GetBucket(input);
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

        var changed = new List<SearchViewProjectedAggregateTrackGroup>();
        foreach (var change in touched.Values.OrderBy(value => value.Bucket.Index))
        {
            AggregateTrackBucket bucket = change.Bucket;
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

    public List<SearchViewProjectedAggregateTrackGroup> SnapshotForSearchView()
        => buckets.Values
            .Where(IsVisible)
            .Select(bucket => ProjectGroup(bucket))
            .ToList();

    internal SearchViewProjectedAggregateTrackGroup SnapshotGroupForSearchView(int index)
    {
        AggregateTrackBucket bucket = buckets.Values[index];
        if (!IsVisible(bucket))
            throw new InvalidOperationException("The aggregate track group is not visible.");
        return ProjectGroup(bucket);
    }

    private static SearchViewProjectedAggregateTrackGroup ProjectGroup(
        AggregateTrackBucket bucket,
        IReadOnlyList<ProjectedFileCandidate>? added = null)
    {
        // Existing groups publish their delta. Full ordering belongs to a
        // requested snapshot or the group's first visible publication.
        IReadOnlyList<ProjectedFileCandidate> options = added ?? bucket.SortedProjected();
        return new SearchViewProjectedAggregateTrackGroup(
            bucket.Index,
            bucket.QueryWithKnownLength(),
            bucket.ShareCount,
            bucket.PublicOptionCount,
            added == null ? options[0] : bucket.BestProjected(),
            options,
            new SongQuery(bucket.Query));
    }

    private AggregateTrackBucket GetBucket(SearchProjectionInput input)
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

    private static bool IsVisible(AggregateTrackBucket bucket) => bucket.IsVisible;

    private sealed class AggregateTrackBucket
    {
        private IncrementalResultSorter? sorter;
        private List<PendingCandidate>? pending;
        private readonly SongQuery sourceQuery;
        private readonly SearchSettings search;
        private readonly ConcurrentDictionary<string, int> userSuccessCounts;
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

        public AggregateTrackBucket(
            int index,
            SongQuery query,
            SongQuery sourceQuery,
            SearchSettings search,
            ConcurrentDictionary<string, int> userSuccessCounts,
            string? filterText)
        {
            Index = index;
            Query = query;
            this.sourceQuery = sourceQuery;
            this.search = search;
            this.userSuccessCounts = userSuccessCounts;
            this.filterText = filterText;
            matchesText = filterText is null || ContainsFilter(query.Artist) || ContainsFilter(query.Title);
        }

        private void PrepareRanking()
        {
            sorter = new IncrementalResultSorter(
                Query,
                search,
                userSuccessCounts,
                albumMode: false,
                ignoreStringSortConditions: true,
                // The containing projector owns exact identity deduplication.
                deduplicateInputs: false,
                necessaryConditionEvaluator: static _ => true);
            if (pending is null) return;
            foreach (var candidate in pending) AddRanked(candidate.Input, candidate.UserSuccessCount);
            pending = null;
        }

        public ProjectedFileCandidate? Add(SearchProjectionInput input)
        {
            firstKnownLength ??= input.Length;
            // Sharers describe discoveries, as in full aggregate grouping.
            // Workflow-local ignored peers contribute no selectable options.
            users.Add(input.Username);
            int successCount = userSuccessCounts.GetValueOrDefault(input.Username, 0);
            if (successCount <= search.IgnoreOn) return null;
            matchesText |= ContainsFilter(input.Filename);
            if (sorter is null)
            {
                // Grouping and visibility only need admitted source facts.
                // Build conditions/rank once, when this group can be read or
                // executed; replay its earlier options without losing backfill.
                if (!MeetsVisibility())
                {
                    (pending ??= []).Add(new(input, successCount));
                    return null;
                }
                PrepareRanking();
            }
            return AddRanked(input, successCount);
        }

        private ProjectedFileCandidate? AddRanked(SearchProjectionInput input, int successCount)
        {
            var projected = sorter!.AddWithUserSuccessCount(input, successCount);
            if (projected is not null && input.Visibility == SearchResultVisibility.Public) PublicOptionCount++;
            return projected;
        }

        private bool ContainsFilter(string value)
            => filterText is not null && value.Contains(filterText, StringComparison.OrdinalIgnoreCase);

        private bool MeetsVisibility()
            => matchesText && ShareCount >= search.MinSharesAggregate
                && (search.Relax || MatchesStrictQuery());

        private readonly record struct PendingCandidate(SearchProjectionInput Input, int UserSuccessCount);

        public SongQuery QueryWithKnownLength()
        {
            if (Query.Length != -1)
                return Query;

            return new SongQuery(Query) { Length = firstKnownLength ?? -1 };
        }

        // Group identity strings do not change as options arrive. Evaluate this
        // only when strict visibility is needed; Relax remains a caller policy.
        public bool MatchesStrictQuery() => strictMatch ??=
            FileConditions.StrictString(Query.Title, sourceQuery.Title, ignoreCase: true)
            && (FileConditions.StrictString(Query.Artist, sourceQuery.Artist, ignoreCase: true, boundarySkipWs: false)
                || FileConditions.StrictString(Query.Title, sourceQuery.Artist, ignoreCase: true, boundarySkipWs: false)
                    && Query.Title.ContainsInBrackets(sourceQuery.Artist, ignoreCase: true));

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
