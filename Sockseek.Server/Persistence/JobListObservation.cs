using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sockseek.Api;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

public sealed partial class JobReadService
{
    public async Task<JobListUpdateDto> GetJobListSnapshotAsync(
        JobListScopeDto scope, ServerEventBroadcaster events, CancellationToken cancellationToken = default)
    {
        await using var reader = ObserveJobListAsync(scope, events, cancellationToken).GetAsyncEnumerator();
        if (!await reader.MoveNextAsync().ConfigureAwait(false))
            throw new InvalidOperationException("The job list did not produce an initial snapshot.");
        return reader.Current;
    }

    // Only displayed state lives here. JobState owns facts/filter semantics;
    // GetJobsAsync owns live/retained reconciliation. Progress patches neither
    // reread SQL nor rebuild the window. Archive/pruning and exits may need backfill.
    public async IAsyncEnumerable<JobListUpdateDto> ObserveJobListAsync(
        JobListScopeDto scope, ServerEventBroadcaster events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(scope.Query);
        ValidateJobPageLimit(scope.Limit);
        var gate = new Lock();
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        Dictionary<Guid, JobReadState> observed = [];
        Dictionary<Guid, (JobReadState Row, bool Matches, long Sequence)> pending = [];
        bool reload = false;
        bool hasMore = false;
        (int DisplayId, Guid Id)? boundary = null;
        void SourceChanged(JobReadChange change)
        {
            if (change.Current is not { } row) return;
            // This runs at the owner's mutation boundary. Never acquire the
            // owner gate while holding our gate in the reader below.
            bool matches = live.MatchesJobQuery(row, scope.Query);
            lock (gate)
            {
                if (scope.Query.ExcludeExtractResults && matches && row.ResultJobId is not null
                    && (!observed.TryGetValue(row.Id, out var old) || old.ResultJobId != row.ResultJobId))
                    reload = true;
                if (observed.ContainsKey(row.Id) || pending.ContainsKey(row.Id)
                    || matches)
                    pending[row.Id] = (row, matches, change.Sequence);
            }
        }
        void Invalidate()
        {
            lock (gate) reload = true;
            changed.Writer.TryWrite(true);
        }
        void Flush()
        {
            lock (gate)
                if (reload || pending.Count > 0) changed.Writer.TryWrite(true);
        }
        IEnumerable<JobReadState> Ordered(IEnumerable<JobReadState> rows)
            => scope.Query.NewestFirst ? rows.OrderByDescending(row => row.DisplayId).ThenByDescending(row => row.Id)
                : rows.OrderBy(row => row.DisplayId).ThenBy(row => row.Id);
        string? Cursor() => hasMore && boundary is { } end
            ? JobHistoryReader.EncodeCursor(end.DisplayId, end.Id, scope.Query.NewestFirst) : null;
        void SetBoundary()
        {
            var last = Ordered(observed.Values).LastOrDefault();
            boundary = last is null ? null : (last.DisplayId, last.Id);
        }

        live.JobReadChanged += SourceChanged;
        live.JobListChanged += Invalidate;
        persistence.JobsPruned += Invalidate;
        events.Flushed += Flush;
        try
        {
            var page = await GetJobsAsync(scope.Query, null, scope.Limit, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                observed = page.Items.ToDictionary(row => row.JobId, row => new JobReadState(row, 0, false));
                hasMore = page.NextCursor is not null;
                foreach (Guid id in pending.Where(pair => pair.Value.Sequence <= page.ReadSequence).Select(pair => pair.Key).ToArray())
                    pending.Remove(id);
                SetBoundary();
            }
            yield return new(observed.Values.Select(row => new JobDeltaDto(row.Id, row.Revision, Added: row.ToDto())).ToArray(),
                [], page.NextCursor, page.Items.Select(row => row.JobId).ToArray());

            while (await changed.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changed.Reader.TryRead(out _);
                Dictionary<Guid, (JobReadState Row, bool Matches, long Sequence)> updates;
                bool refresh;
                lock (gate)
                {
                    updates = pending; pending = [];
                    refresh = reload; reload = false;
                    refresh |= updates.Any(pair => !pair.Value.Matches && observed.ContainsKey(pair.Key));
                }
                CombinedJobPage? replacement = refresh
                    ? await GetJobsAsync(scope.Query, null, scope.Limit, cancellationToken).ConfigureAwait(false) : null;
                var deltas = new List<JobDeltaDto>();
                var removed = new List<Guid>();
                string? cursor;
                string? previousCursor;
                lock (gate)
                {
                    previousCursor = Cursor();
                    if (replacement is not null)
                    {
                        var ids = replacement.Items.Select(row => row.JobId).ToHashSet();
                        foreach (Guid id in observed.Keys.Where(id => !ids.Contains(id)).ToArray())
                        {
                            observed.Remove(id);
                            removed.Add(id);
                        }
                        foreach (var summary in replacement.Items)
                        {
                            var before = observed.GetValueOrDefault(summary.JobId);
                            // A canonical reload is authoritative even when a retained
                            // revision is lower than the old runtime presentation.
                            var row = new JobReadState(summary, (before?.Revision ?? 0) + 1, false);
                            var fields = before?.Difference(row) ?? JobFields.All;
                            observed[row.Id] = row;
                            if (fields != JobFields.None)
                                deltas.Add(new JobChange(row, before is null, fields).ToDto());
                        }
                        hasMore = replacement.NextCursor is not null;
                        foreach (Guid id in pending.Where(pair => pair.Value.Sequence <= replacement.ReadSequence).Select(pair => pair.Key).ToArray())
                            pending.Remove(id);
                    }
                    else
                    {
                        bool membershipChanged = false;
                        var added = new HashSet<Guid>();
                        foreach (var (row, matches, _) in updates.Values)
                        {
                            if (!matches) continue;
                            var previous = observed.GetValueOrDefault(row.Id);
                            var fields = previous?.Difference(row) ?? JobFields.All;
                            if (fields == JobFields.None) continue;
                            // Revisions here order this observation's patches; they
                            // never choose between runtime and historical authorities.
                            var current = row.WithRevision((previous?.Revision ?? 0) + 1);
                            observed[row.Id] = current;
                            if (previous is null) added.Add(row.Id);
                            deltas.Add(new JobChange(current, previous is null, fields).ToDto());
                            membershipChanged |= previous is null || previous.DisplayId != current.DisplayId;
                        }
                        if (membershipChanged)
                        {
                            foreach (var row in Ordered(observed.Values).Skip(scope.Limit).ToArray())
                            {
                                observed.Remove(row.Id);
                                if (!added.Contains(row.Id)) removed.Add(row.Id);
                                hasMore = true;
                            }
                            SetBoundary();
                            deltas.RemoveAll(delta => !observed.ContainsKey(delta.JobId));
                        }
                    }
                    if (replacement is not null) SetBoundary();
                    cursor = Cursor();
                }
                if (deltas.Count > 0 || removed.Count > 0 || replacement is not null || cursor != previousCursor)
                    yield return new(deltas, removed, cursor);
            }
        }
        finally
        {
            live.JobReadChanged -= SourceChanged;
            live.JobListChanged -= Invalidate;
            persistence.JobsPruned -= Invalidate;
            events.Flushed -= Flush;
            changed.Writer.TryComplete();
        }
    }
}
