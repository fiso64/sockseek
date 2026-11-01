using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sockseek.Api;

namespace Sockseek.Server.Persistence;

public sealed partial class HistoricalQueryFacade
{
    /// <summary>
    /// One displayed job scope owns summaries, direct membership counts and file
    /// progress. Capture before the initial history lookup, coalesce source facts,
    /// and keep final job rows through runtime retirement. Persistence handoff is
    /// invisible to this reader; reconnect starts a fresh authoritative observation.
    /// </summary>
    public async IAsyncEnumerable<JobObservationUpdateDto> ObserveJobsAsync(
        Guid jobId, IReadOnlyCollection<Guid> jobIds, ServerEventBroadcaster events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        var requested = jobIds.Append(jobId).ToHashSet();
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        var gate = new Lock();
        Dictionary<Guid, TransferReadSnapshot?> pendingTransfers = [];
        Dictionary<Guid, JobReadState> pendingJobs = [];
        Dictionary<Guid, TransferStateDto> observedTransfers = [];
        Dictionary<Guid, JobReadState> observedJobs = [];
        JobReadChange? pendingMembership = null;
        void JobChanged(JobReadChange change)
        {
            bool rowChanged = change.Current is not null && requested.Contains(change.JobId);
            bool countsChanged = change.JobId == jobId && change.ChildCount is not null;
            if (!rowChanged && !countsChanged) return;
            lock (gate)
            {
                if (rowChanged) pendingJobs[change.JobId] = change.Current!;
                if (countsChanged) pendingMembership = change;
            }
        }
        void TransferChanged(TransferReadChange change)
        {
            var row = change.Current ?? change.Previous;
            if (row is null) return;
            bool currentMatches = change.Current?.JobId is Guid job && requested.Contains(job);
            if (!currentMatches && !(change.Previous?.JobId is Guid oldJob && requested.Contains(oldJob))) return;
            lock (gate) pendingTransfers[row.TransferId] = currentMatches ? change.Current : null;
        }
        void Flush()
        {
            lock (gate)
                if (pendingMembership is not null || pendingJobs.Count > 0 || pendingTransfers.Count > 0) changed.Writer.TryWrite(true);
        }
        live.JobReadChanged += JobChanged;
        live.TransferReadChanged += TransferChanged;
        events.Flushed += Flush;
        try
        {
            var initial = live.GetJobObservationSnapshot(jobId, requested);
            observedJobs = initial.Jobs.ToDictionary(row => row.JobId);
            var missing = requested.Where(id => !observedJobs.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
                foreach (var summary in await LookupJobsAsync(new(missing), cancellationToken).ConfigureAwait(false))
                    observedJobs[summary.JobId] = new(summary, 0, false);
            var transfers = live.GetJobTransferSnapshot(requested).Select(row => row.ToDto()).ToArray();
            observedTransfers = transfers.ToDictionary(row => row.TransferId);
            int? childCount = initial.ChildCount, retrievalCount = initial.RetrievalChildCount;
            // Discard only pulses covered by an actual initial count. Retirement
            // can remove the root between subscribing and taking this snapshot.
            long membershipSequence = initial.ChildCount is null ? 0 : initial.Sequence;
            yield return new(StateDeltaDto.Empty with
            {
                Jobs = observedJobs.Values.Select(row => new JobDeltaDto(row.JobId, row.Revision, Added: row.ToDto())).ToArray(),
                Transfers = transfers.Select(row => new TransferDeltaDto(row.TransferId, row.Revision, Added: row)).ToArray(),
            }, childCount, retrievalCount);
            while (await changed.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changed.Reader.TryRead(out _);
                Dictionary<Guid, TransferReadSnapshot?> transferChanges;
                Dictionary<Guid, JobReadState> jobChanges;
                JobReadChange? membership;
                lock (gate)
                {
                    transferChanges = pendingTransfers; pendingTransfers = [];
                    jobChanges = pendingJobs; pendingJobs = [];
                    membership = pendingMembership; pendingMembership = null;
                }
                var jobs = new List<JobDeltaDto>();
                foreach (var (id, row) in jobChanges)
                {
                    observedJobs.TryGetValue(id, out var previous);
                    if (previous is not null && row.Revision <= previous.Revision) continue;
                    observedJobs[id] = row;
                    jobs.Add(new JobChange(row, previous is null, previous?.Difference(row) ?? JobFields.All).ToDto());
                }
                var rows = new List<TransferDeltaDto>();
                var removed = new List<Guid>();
                foreach (var (id, row) in transferChanges)
                {
                    observedTransfers.TryGetValue(id, out var previous);
                    if (row is null)
                    {
                        if (observedTransfers.Remove(id)) removed.Add(id);
                    }
                    else if (previous is null || row.Revision > previous.Revision)
                    {
                        var current = row.ToDto();
                        observedTransfers[id] = current;
                        rows.Add(TransferReadModel.Difference(previous, current));
                    }
                }
                bool countsChanged = false;
                if (membership is { } counts && counts.Sequence > membershipSequence)
                {
                    countsChanged = counts.ChildCount != childCount || counts.RetrievalChildCount != retrievalCount;
                    childCount = counts.ChildCount; retrievalCount = counts.RetrievalChildCount;
                    membershipSequence = counts.Sequence;
                }
                if (jobs.Count > 0 || rows.Count > 0 || removed.Count > 0 || countsChanged)
                    yield return new(StateDeltaDto.Empty with { Jobs = jobs, Transfers = rows, RemovedTransferIds = removed },
                        childCount, retrievalCount);
            }
        }
        finally
        {
            live.JobReadChanged -= JobChanged;
            live.TransferReadChanged -= TransferChanged;
            events.Flushed -= Flush;
            changed.Writer.TryComplete();
        }
    }
}
