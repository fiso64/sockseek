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
    /// Observe a moving extent, not every transfer that happens to arrive while it
    /// is open: the newest transfers, plus the older members that make the folders
    /// among them whole, within a fixed row budget. HTTP and observations use the
    /// same live-before-durable read owner.
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
        if (scope.FolderCompletionLimit < 0) throw new ArgumentOutOfRangeException(nameof(scope.FolderCompletionLimit));
        string direction = scope.Direction.ToString();
        // A scoped read of one folder is already that folder; only the general
        // timeline extends its newest transfers to whole folders.
        bool wholeFolders = scope.IncludeGroupSummaries && scope.GroupRef is null;
        var memberLimits = (scope.ExpandedGroups ?? []).ToDictionary(
            group => new TransferGroupKeyDto(scope.Direction, group.Username, group.GroupRef),
            group => Math.Max(1, group.MemberLimit));
        Dictionary<TransferGroupKeyDto, long> fileCounts = [];
        // A folder with more members than the extent shows ends at this member.
        // Its older members cannot enter, so their changes are not this reader's.
        Dictionary<TransferGroupKeyDto, (long CreatedAt, Guid Id)> folderFloors = [];
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
        // Facts for rows this reader already holds are bounded by its extent,
        // not by the arrival buffer: a burst of newer rows must not drop them.
        Dictionary<Guid, TransferReadSnapshot> pendingHeld = [];
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
            return prefixCount < scope.Limit || selected.ContainsKey(row.TransferId)
                || !hasMore || oldest is null || key.CompareTo(oldest.Value) >= 0 || CouldJoinShownFolder(row, key);
        }
        // An older member of a folder in the extent belongs to it too, unless
        // that folder is deliberately shown only in part.
        bool CouldJoinShownFolder(TransferReadSnapshot row, (long CreatedAt, Guid Id) key)
        {
            if (!wholeFolders || row.GroupRef is not { } shownRef) return false;
            var folder = new TransferGroupKeyDto(scope.Direction, row.Username, shownRef);
            return requestedGroups.Contains(folder)
                && !(folderFloors.TryGetValue(folder, out var floor) && key.CompareTo(floor) < 0);
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
                if (selected.ContainsKey(row.TransferId))
                    liveChanges[row.TransferId] = matches ? change.Current : null;
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
                    var key = (row.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0L, row.TransferId);
                    // An older folder member this reader does not hold yet enters
                    // through the folder's own canonical read, not as an arrival.
                    bool olderMember = hasMore && oldest is { } boundary && key.CompareTo(boundary) < 0
                        && !selected.ContainsKey(row.TransferId);
                    reload |= orderChanged || removed && !terminal || !removed && !matches || olderMember;
                    if (change.Previous is { } prior)
                        pendingRows.Remove((prior.RequestedAtUtc?.ToUnixTimeMilliseconds() ?? 0L, prior.TransferId));
                    if (olderMember || !matches && !(removed && terminal)) pendingHeld.Remove(row.TransferId);
                    else if (selected.ContainsKey(row.TransferId)) pendingHeld[row.TransferId] = row;
                    else
                    {
                        pendingRows[key] = row;
                        if (pendingRows.Count > scope.Limit)
                        {
                            pendingRows.Remove(pendingRows.First().Key);
                            pendingOverflow = true;
                        }
                    }
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
                    groupsChanged = scope.IncludeGroupSummaries;
                }
                if (dirty || groupsChanged || positionChanged) changed.Writer.TryWrite(true);
            }
        }

        TransferGroupKeyDto GroupOf(TransferStateDto row) => new(scope.Direction, row.Identity.Username, row.Identity.GroupRef!);
        void RememberFileCounts(TransferGroupSummariesResponseDto? summaries)
        {
            if (summaries?.RetainedCoverage.State != TransferRetainedCoverageState.Available) return;
            fileCounts = summaries.Items.ToDictionary(summary => summary.Key, summary => summary.FileCount);
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
                    rowUpdates = pendingRows.Values.Concat(pendingHeld.Values).ToArray();
                    pendingRows = [];
                    pendingHeld = [];
                    previous = selected;
                    progress = liveChanges;
                    liveChanges = [];
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
                    RememberFileCounts(summaries);
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
                    var page = await ReadObservedTransferPageAsync(scope, cancellationToken).ConfigureAwait(false);
                    found = page.Items.ToDictionary(row => row.TransferId);
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
                var nextGroups = scope.IncludeGroupSummaries ? TransferGroups(windowRows) : [];
                // Totals say how many members a folder has, which decides whether
                // it continues beyond the newest transfers.
                TransferGroupSummariesResponseDto? groupSummaries = scope.IncludeGroupSummaries
                    && (first || refreshGroups || !nextGroups.SetEquals(requestedGroups))
                    ? await ReadRequestedTransferGroupSummariesAsync(nextGroups, cancellationToken).ConfigureAwait(false)
                    : null;
                RememberFileCounts(groupSummaries);
                Dictionary<TransferGroupKeyDto, (long CreatedAt, Guid Id)> nextFloors = [];
                if (wholeFolders && window.NextCursor is not null)
                {
                    HashSet<Guid>? prefixIds = null;
                    ILookup<TransferGroupKeyDto, TransferStateDto>? held = null;
                    // Extra rows are spent newest folder first, and only on folders
                    // they make whole, so the extent has a fixed upper size.
                    long budget = scope.FolderCompletionLimit;
                    foreach (var folder in windowRows.Where(row => row.Identity.GroupRef is not null).GroupBy(GroupOf).ToArray())
                    {
                        int shown = folder.Count();
                        var last = folder.Last();
                        bool counted = fileCounts.TryGetValue(folder.Key, out long fileCount);
                        bool expanded = memberLimits.TryGetValue(folder.Key, out int memberLimit);
                        // An explicit request is the reader's own bound. Otherwise
                        // complete the folder if its missing members fit the budget;
                        // without totals, one more than the budget shows whether they do.
                        long wanted = expanded ? (counted ? Math.Min(memberLimit, fileCount) : memberLimit) - shown
                            : counted ? fileCount - shown : budget + 1;
                        if (wanted > 0 && (expanded || counted && wanted <= budget || !counted && budget > 0))
                        {
                            prefixIds ??= windowRows.Select(row => row.TransferId).ToHashSet();
                            held ??= found.Values.Where(row => !prefixIds.Contains(row.TransferId) && row.Identity.GroupRef is not null)
                                .ToLookup(GroupOf);
                            IReadOnlyList<TransferStateDto> olderMembers = held[folder.Key]
                                .OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.TransferId)
                                .Take((int)wanted).ToArray();
                            // Rows that just left the newest transfers are already here.
                            // Read the folder itself for a fresh extent, or when its
                            // total says members are missing.
                            if (readHistory || counted && olderMembers.Count < wanted)
                            {
                                var tail = await GetTransfersAsync(new TransferQuery(Cursor: window.NextCursor, Limit: (int)wanted,
                                    Direction: direction, Username: folder.Key.Username, GroupRef: folder.Key.GroupRef),
                                    cancellationToken).ConfigureAwait(false);
                                if (tail.RetainedCoverage.State != TransferRetainedCoverageState.Available)
                                    coverage = tail.RetainedCoverage;
                                olderMembers = tail.Items;
                            }
                            bool whole = counted || olderMembers.Count <= budget;
                            if (expanded || whole)
                            {
                                if (!expanded) budget -= olderMembers.Count;
                                foreach (var member in olderMembers)
                                    if (prefixIds.Add(member.TransferId)) { windowRows.Add(member); shown++; last = member; }
                                if (!expanded && !counted) continue;
                            }
                        }
                        // Only a folder with more members than it shows has a floor.
                        if (!counted || fileCount > shown)
                            nextFloors[folder.Key] = (last.CreatedAtUtc.ToUnixTimeMilliseconds(), last.TransferId);
                    }
                }
                Guid[] ids = windowRows.Select(row => row.TransferId).ToArray();
                var members = ids.ToHashSet();
                var removed = revisions.Keys.Where(id => !members.Contains(id)).ToArray();
                var updates = windowRows.Where(row => !revisions.TryGetValue(row.TransferId, out long revision)
                    || row.Revision != revision).ToArray();
                bool membershipChanged = first || previousCursor != window.NextCursor || !ids.SequenceEqual(previousIds);
                bool refreshLate;
                lock (gate)
                {
                    selected = windowRows.ToDictionary(row => row.TransferId);
                    requestedGroups = nextGroups;
                    folderFloors = nextFloors;
                    prefixCount = window.Items.Count;
                    hasMore = window.NextCursor is not null;
                    oldest = window.Items.Count == 0 ? null
                        : (window.Items[^1].CreatedAtUtc.ToUnixTimeMilliseconds(), window.Items[^1].TransferId);
                    // A mutation during the read must not be advertised as an
                    // interval with unchanged summaries. Leave its pending flags
                    // for the next flush as well; that read may be newer still.
                    refreshLate = groupsChanged && groupSummaries is null;
                    initializing = false;
                    observedCoverage = coverage;
                }
                if (refreshLate)
                {
                    groupSummaries = await ReadRequestedTransferGroupSummariesAsync(requestedGroups, cancellationToken).ConfigureAwait(false);
                    RememberFileCounts(groupSummaries);
                }
                refreshGroups = groupSummaries is not null;
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
            && before.IsTerminal == after.IsTerminal && before.Phase == after.Phase
            && before.TerminalOutcome == after.TerminalOutcome && before.File?.Size == after.File?.Size
            && before.TotalBytes == after.TotalBytes
            && before.BytesTransferred == after.BytesTransferred
            && before.BytesPerSecond == after.BytesPerSecond;
    }

    private async Task<TransferTimelinePageDto> ReadObservedTransferPageAsync(
        TransferTimelineScopeDto scope, CancellationToken cancellationToken)
    {
        using var activity = TransferReads.StartActivity("timeline.page");
        activity?.SetTag("transfer.count", (long)scope.Limit);
        try
        {
            var position = live.GetTransferReadPosition();
            activity?.SetTag("read.epoch", position.Epoch);
            activity?.SetTag("read.sequence", position.Sequence);
            var query = new TransferQuery(Limit: scope.Limit, Direction: scope.Direction.ToString(),
                Username: scope.Username, GroupRef: scope.GroupRef);
            var page = await GetTransfersAsync(query, cancellationToken).ConfigureAwait(false);
            return page with { ReadPosition = position };
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }
}
