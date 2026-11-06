using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sockseek.Api;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

public sealed partial class TransferReadService
{
    private static readonly ActivitySource TransferReads = new("Sockseek.Server.Transfers");

    /// <summary>Read the same scoped snapshot that starts a live observation.</summary>
    public async Task<TransferTimelineUpdateDto> GetTransferTimelineSnapshotAsync(
        TransferTimelineScopeDto scope, ServerEventBroadcaster events, CancellationToken cancellationToken = default)
    {
        await using var snapshot = ObserveTransferTimelineAsync(scope, events, cancellationToken).GetAsyncEnumerator();
        if (!await snapshot.MoveNextAsync().ConfigureAwait(false))
            throw new InvalidOperationException("The transfer snapshot did not produce its initial extent.");
        return snapshot.Current;
    }

    /// <summary>
    /// Observe a moving page, not every transfer that happens to arrive while it
    /// is open. HTTP and observations use the same live-before-durable read owner.
    /// The shared coalescing clock flushes facts even without a daemon packet;
    /// changed facts update the fetched window without rereading retained rows.
    /// Canonical reads handle initial membership, invalidation and backfill;
    /// requested totals refresh on relevant fact changes.
    /// All state ends with the reader's lifetime.
    /// </summary>
    public async IAsyncEnumerable<TransferTimelineUpdateDto> ObserveTransferTimelineAsync(
        TransferTimelineScopeDto scope,
        ServerEventBroadcaster events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!Enum.IsDefined(scope.Direction)) throw new ArgumentOutOfRangeException(nameof(scope.Direction));
        if (scope.Limit < 1) throw new ArgumentOutOfRangeException(nameof(scope.Limit));
        string direction = scope.Direction.ToString();
        var additionalIds = scope.AdditionalTransferIds?.ToHashSet() ?? [];
        int prefixCount = 0;
        bool hasMore = false;
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        var gate = new Lock();
        Dictionary<Guid, TransferStateDto> selected = [];
        Dictionary<Guid, TransferReadSnapshot?> liveChanges = [];
        SortedDictionary<(long CreatedAt, Guid Id), TransferReadSnapshot> pendingRows = [];
        Dictionary<Guid, TransferReadSnapshot> pendingAdditional = [];
        HashSet<Guid> changedAdditional = [];
        bool refreshAdditional = true;
        TransferRetainedCoverageDto? observedCoverage = null;
        var initialPosition = live.GetTransferReadPosition();
        long pendingRevision = initialPosition.Sequence;
        bool positionChanged = false;
        bool dirty = false;
        bool reload = false;
        bool pendingOverflow = false;
        bool groupsChanged = false;
        bool initializing = true;
        HashSet<TransferGroupKeyDto> requestedGroups = [];
        (long CreatedAt, Guid Id)? oldest = null;

        bool Matches(TransferReadSnapshot row) => row.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase)
            && (scope.Username is null || row.Username == scope.Username)
            && (scope.GroupRef is null || row.GroupRef == scope.GroupRef);
        bool CouldEnter(TransferReadSnapshot row)
        {
            var key = (row.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0L, row.TransferId);
            return prefixCount < scope.Limit || additionalIds.Contains(row.TransferId) || selected.ContainsKey(row.TransferId)
                || !hasMore || oldest is null || key.CompareTo(oldest.Value) >= 0;
        }
        bool InRequestedGroup(TransferReadSnapshot row) => Matches(row) && row.GroupRef is { } groupRef
            && (initializing || requestedGroups.Contains(new(scope.Direction, row.Username, groupRef)));
        bool HasFinalRow(TransferReadSnapshot row) => row.IsTerminal
            && selected.TryGetValue(row.TransferId, out var seen)
            && seen.Revision >= row.Revision && seen.Status.IsTerminal
            && row.RequestedAtUtc is { } requested
            && seen.CreatedAtUtc.ToUnixTimeMilliseconds() == requested.ToUnixTimeMilliseconds();
        void SourceChanged(TransferReadChange change)
        {
            // This callback runs at the fact mutation boundary. It only buffers
            // bounded facts/invalidations; it never reads state or queries SQL.
            lock (gate)
            {
                pendingRevision = change.Revision;
                if (change.Previous is null && change.Current is null)
                {
                    positionChanged = true;
                    dirty = true;
                    reload = true;
                    refreshAdditional = true;
                    groupsChanged = scope.IncludeGroupSummaries;
                    changed.Writer.TryWrite(true);
                    return;
                }
                var row = change.Current ?? change.Previous!;
                bool matches = change.Current is not null && Matches(change.Current);
                bool relevant = Matches(row) || change.Previous is { } previous && Matches(previous);
                positionChanged |= relevant || requestedGroups.Count > 0;
                bool removed = change.Current is null;
                if (scope.IncludeGroupSummaries && !SameGroupContribution(change.Previous, change.Current)
                    && ((change.Previous is { } before && InRequestedGroup(before)) || InRequestedGroup(row)))
                    groupsChanged = scope.IncludeGroupSummaries;
                if (!relevant) return;
                if (selected.ContainsKey(row.TransferId) || additionalIds.Contains(row.TransferId))
                    liveChanges[row.TransferId] = matches ? change.Current : null;
                if (additionalIds.Contains(row.TransferId)) changedAdditional.Add(row.TransferId);
                bool terminal = row.IsTerminal;
                bool orderChanged = change.Previous is { } old
                    && old.RequestedAtUtc?.ToUnixTimeMilliseconds() != row.RequestedAtUtc?.ToUnixTimeMilliseconds();
                // Eviction cannot change a final row this reader already owns.
                // No storage-tier removal is sent to the reader. A final
                // revision not yet seen still needs the normal handoff.
                bool finalAlreadySeen = removed && HasFinalRow(row);
                if (!finalAlreadySeen && CouldEnter(row)
                    && (change.Structural || terminal || orderChanged
                        || matches && !selected.ContainsKey(row.TransferId)
                        || removed && selected.ContainsKey(row.TransferId)))
                {
                    dirty = true;
                    // Lost/moved members may need an older retained successor.
                    // Arrivals and completed facts can update this window directly.
                    // Keep the newest fact for a pending arrival too: progress may
                    // precede its first publication and have no later notification.
                    // A corrected request timestamp may differ from the immutable
                    // SQL creation key; let the canonical read choose its order.
                    reload |= orderChanged || removed && !terminal || !removed && !matches;
                    if (change.Previous is { } prior)
                        pendingRows.Remove((prior.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0L, prior.TransferId));
                    if (matches || removed && terminal)
                    {
                        if (additionalIds.Contains(row.TransferId)) pendingAdditional[row.TransferId] = row;
                        else
                        {
                            var key = (row.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0L, row.TransferId);
                            pendingRows[key] = row;
                            if (pendingRows.Count > scope.Limit)
                            {
                                pendingRows.Remove(pendingRows.First().Key);
                                pendingOverflow = true;
                            }
                        }
                    }
                    else pendingAdditional.Remove(row.TransferId);
                }
            }
        }
        void Observe()
        {
            var coverage = RetainedTransferCoverage();
            lock (gate)
            {
                if (observedCoverage is not null && coverage != observedCoverage)
                {
                    dirty = true;
                    reload = true;
                    refreshAdditional = true;
                    groupsChanged = scope.IncludeGroupSummaries;
                }
                if (dirty || groupsChanged || positionChanged) changed.Writer.TryWrite(true);
            }
        }

        events.Flushed += Observe;
        live.TransferReadChanged += SourceChanged;
        try
        {
            var revisions = scope.KnownRevisions?.ToDictionary() ?? [];
            Guid[] previousIds = [];
            string? previousCursor = null;
            TransferRetainedCoverageDto? previousCoverage = null;
            bool first = true;
            Guid? previousBoundary = null;
            long publishedRevision = 0;
            while (first || await changed.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!first) changed.Reader.TryRead(out _);
                bool readRows;
                bool readHistory;
                bool knownMore;
                bool refreshGroups;
                bool publishPosition;
                long revision;
                TransferReadSnapshot[] rowUpdates;
                Dictionary<Guid, TransferStateDto> previous;
                HashSet<Guid> additionalRefresh;
                Dictionary<Guid, TransferReadSnapshot?> progress;
                lock (gate)
                {
                    readRows = first || dirty;
                    readHistory = first || reload;
                    knownMore = hasMore || pendingOverflow;
                    refreshGroups = groupsChanged;
                    publishPosition = positionChanged;
                    revision = pendingRevision;
                    positionChanged = false;
                    dirty = false;
                    reload = false;
                    pendingOverflow = false;
                    groupsChanged = false;
                    rowUpdates = pendingRows.Values.Concat(pendingAdditional.Values).ToArray();
                    pendingRows = [];
                    pendingAdditional = [];
                    previous = selected;
                    progress = liveChanges;
                    liveChanges = [];
                    additionalRefresh = refreshAdditional ? additionalIds : changedAdditional;
                    changedAdditional = [];
                    refreshAdditional = false;
                }
                if (!readRows)
                {
                    // Progress uses already-mapped facts, not history queries or
                    // a rebuilt page. Retain only the requested extent's changes.
                    var progressUpdates = new List<TransferDeltaDto>();
                    lock (gate)
                    {
                        foreach (var fact in progress.Values.OfType<TransferReadSnapshot>())
                        {
                            if (!selected.TryGetValue(fact.TransferId, out var prior) || prior.Revision >= fact.Revision)
                                continue;
                            var current = TransferReadModel.Merge(prior, fact.ToDto());
                            progressUpdates.Add(TransferReadModel.Difference(prior, current));
                            selected[fact.TransferId] = current;
                            revisions[fact.TransferId] = current.Revision;
                        }
                    }
                    var summaries = refreshGroups
                        ? await ReadRequestedTransferGroupSummariesAsync(requestedGroups, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (refreshGroups || publishPosition && revision > publishedRevision
                        || progressUpdates.Count > 0)
                    {
                        publishedRevision = Math.Max(publishedRevision, revision);
                        yield return new([], [], RetainedTransferCoverage(),
                            NextCursor: previousCursor, BoundaryTransferId: previousBoundary,
                            ReadPosition: new(initialPosition.Epoch, publishedRevision),
                            Changes: progressUpdates,
                            GroupSummaries: summaries);
                    }
                    continue;
                }
                Dictionary<Guid, TransferStateDto> found;
                TransferRetainedCoverageDto coverage;
                StateStreamPositionDto position;
                if (readHistory)
                {
                    var (page, additional) = await ReadObservedTransferPageAsync(scope, additionalIds,
                        previous, additionalRefresh, cancellationToken).ConfigureAwait(false);
                    found = page.Items.Concat(additional).ToDictionary(row => row.TransferId);
                    coverage = page.RetainedCoverage;
                    position = page.ReadPosition!;
                    knownMore = page.NextCursor is not null;
                    // With unavailable history, preserve this reader's final facts
                    // after runtime eviction, bounded by its requested extent.
                    if (coverage.State != TransferRetainedCoverageState.Available)
                        foreach (var row in previous.Values.Where(row => row.Status.IsTerminal))
                            found.TryAdd(row.TransferId, row);
                }
                else
                {
                    // A structural arrival does not invalidate unchanged retained
                    // rows. Reuse the same records already updated by progress.
                    found = previous.ToDictionary();
                    coverage = RetainedTransferCoverage();
                    position = new(initialPosition.Epoch, revision);
                }
                foreach (var changedRow in rowUpdates.Concat(progress.Values.OfType<TransferReadSnapshot>()))
                {
                    var row = changedRow.ToDto();
                    if (found.TryGetValue(row.TransferId, out var current))
                        found[row.TransferId] = TransferReadModel.Merge(current, row);
                    else if (!readHistory || coverage.State != TransferRetainedCoverageState.Available)
                        found[row.TransferId] = row;
                }
                var window = ComposeTransferTimeline(found.Values.ToArray(), [], [], scope.Limit,
                    knownMore, false, coverage);
                var windowRows = window.Items.ToList();
                var prefixIds = windowRows.Select(row => row.TransferId).ToHashSet();
                windowRows.AddRange(found.Values.Where(row => additionalIds.Contains(row.TransferId) && !prefixIds.Contains(row.TransferId))
                    .OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.TransferId));
                Guid[] ids = windowRows.Select(row => row.TransferId).ToArray();
                var members = ids.ToHashSet();
                var removed = revisions.Keys.Where(id => !members.Contains(id)).ToArray();
                var updates = windowRows.Where(row => !revisions.TryGetValue(row.TransferId, out long revision)
                    || row.Revision != revision).ToArray();
                bool membershipChanged = first || previousCursor != window.NextCursor || !ids.SequenceEqual(previousIds);
                lock (gate)
                {
                    selected = windowRows.ToDictionary(row => row.TransferId);
                    var nextGroups = scope.IncludeGroupSummaries ? TransferGroups(windowRows) : [];
                    if (scope.IncludeGroupSummaries && (first || !nextGroups.SetEquals(requestedGroups))) refreshGroups = true;
                    requestedGroups = nextGroups;
                    prefixCount = window.Items.Count;
                    hasMore = window.NextCursor is not null;
                    oldest = window.Items.Count == 0 ? null
                        : (window.Items[^1].CreatedAtUtc.ToUnixTimeMilliseconds(), window.Items[^1].TransferId);
                    // A mutation during the read must not be advertised as an
                    // interval with unchanged summaries. Leave its pending flags
                    // for the next flush as well; that read may be newer still.
                    refreshGroups |= groupsChanged;
                    initializing = false;
                    observedCoverage = coverage;
                }
                var groupSummaries = refreshGroups
                    ? await ReadRequestedTransferGroupSummariesAsync(requestedGroups, cancellationToken).ConfigureAwait(false)
                    : null;
                if (membershipChanged || updates.Length > 0 || removed.Length > 0 || refreshGroups
                    || previousCoverage != coverage || position.Sequence > publishedRevision)
                    yield return new(updates, removed, coverage,
                        first ? ids : null, window.NextCursor, window.Items.LastOrDefault()?.TransferId, position,
                        GroupSummaries: groupSummaries);
                publishedRevision = Math.Max(publishedRevision, position.Sequence);
                revisions = windowRows.ToDictionary(row => row.TransferId, row => row.Revision);
                previousIds = ids;
                previousCursor = window.NextCursor;
                previousCoverage = coverage;
                previousBoundary = window.Items.LastOrDefault()?.TransferId;
                first = false;
            }
        }
        finally
        {
            events.Flushed -= Observe;
            live.TransferReadChanged -= SourceChanged;
            changed.Writer.TryComplete();
        }

    }

    private static bool SameGroupContribution(TransferReadSnapshot? before, TransferReadSnapshot? after)
    {
        // Terminal memory eviction is a handoff, not a change to group totals.
        if (after is null) return before?.IsTerminal == true;
        return before is not null && before.Direction == after.Direction
            && before.Username == after.Username && before.GroupRef == after.GroupRef
            && before.IsTerminal == after.IsTerminal && before.State == after.State
            && before.TerminalOutcome == after.TerminalOutcome && before.File?.Size == after.File?.Size
            && before.TotalBytes == after.TotalBytes
            && before.BytesTransferred == after.BytesTransferred
            && before.BytesPerSecond == after.BytesPerSecond;
    }

    private async Task<(TransferTimelinePageDto Page, IReadOnlyList<TransferStateDto> Additional)> ReadObservedTransferPageAsync(
        TransferTimelineScopeDto scope, IReadOnlySet<Guid> additionalIds,
        IReadOnlyDictionary<Guid, TransferStateDto> previous, IReadOnlySet<Guid> refresh,
        CancellationToken cancellationToken)
    {
        using var activity = TransferReads.StartActivity("timeline.page");
        activity?.SetTag("transfer.count", (long)scope.Limit + additionalIds.Count);
        try
        {
            var position = live.GetTransferReadPosition();
            activity?.SetTag("read.epoch", position.Epoch);
            activity?.SetTag("read.sequence", position.Sequence);
            var query = new TransferQuery(Limit: scope.Limit, Direction: scope.Direction.ToString(),
                Username: scope.Username, GroupRef: scope.GroupRef);
            var page = await GetTransfersAsync(query, cancellationToken).ConfigureAwait(false);
            page = page with { ReadPosition = position };
            var missing = additionalIds.Except(page.Items.Select(row => row.TransferId)).ToHashSet();
            if (missing.Count == 0) return (page, []);
            // These are explicitly fetched identities, not a second moving
            // prefix. Unrelated arrivals do not invalidate their canonical rows.
            var readIds = missing.Where(id => refresh.Contains(id) || !previous.ContainsKey(id)).ToHashSet();
            var cached = missing.Except(readIds).Select(id => previous[id]).ToArray();
            if (readIds.Count == 0) return (page, cached);
            var additional = await GetTransfersAsync(query with { Limit = readIds.Count, TransferIds = readIds },
                cancellationToken).ConfigureAwait(false);
            return (additional.RetainedCoverage.State == TransferRetainedCoverageState.Available
                ? page : page with { RetainedCoverage = additional.RetainedCoverage }, cached.Concat(additional.Items).ToArray());
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }
}
