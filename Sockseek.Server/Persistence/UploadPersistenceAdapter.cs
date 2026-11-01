using Sockseek.Core.Transfers.Uploads;
using Sockseek.Persistence.Write;

namespace Sockseek.Server.Persistence;

/// <summary>
/// Non-blocking projection of daemon-owned uploads into the generic transfer
/// history writer. Terminal mutations contain a complete transfer and optional
/// attempt so they remain valid if earlier projection updates were dropped.
/// </summary>
public sealed class UploadPersistenceAdapter
{
    private readonly Guid runtimeId;
    private readonly IPersistenceMutationSink sink;
    private readonly PersistenceHandoffTracker? handoffs;
    private readonly object gate = new();
    private readonly HashSet<Guid> persistedAttempts = [];
    private long sequence;

    public UploadPersistenceAdapter(Guid runtimeId, IPersistenceMutationSink sink)
        : this(runtimeId, sink, handoffs: null)
    {
    }

    internal UploadPersistenceAdapter(
        Guid runtimeId,
        IPersistenceMutationSink sink,
        PersistenceHandoffTracker? handoffs)
    {
        if (runtimeId == Guid.Empty)
            throw new ArgumentException("A non-empty runtime ID is required.", nameof(runtimeId));
        this.runtimeId = runtimeId;
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.handoffs = handoffs;
    }

    public void Attach(UploadCoordinator uploads)
    {
        uploads.TransferChanged += OnTransferChanged;
        foreach (UploadTransferSnapshot snapshot in uploads.Snapshot())
            OnTransferChanged(snapshot);
    }

    public void Detach(UploadCoordinator uploads)
        => uploads.TransferChanged -= OnTransferChanged;

    private void OnTransferChanged(UploadTransferSnapshot snapshot)
    {
        bool terminal = snapshot.State is UploadTransferState.Completed
            or UploadTransferState.Cancelled
            or UploadTransferState.Failed
            or UploadTransferState.Interrupted;
        long next = Interlocked.Increment(ref sequence);
        DateTimeOffset occurredAt = snapshot.FinishedAtUtc
            ?? snapshot.LastProgressAtUtc
            ?? snapshot.Attempt?.StartedAtUtc
            ?? snapshot.RequestedAtUtc;
        var transfer = ToTransfer(
            snapshot,
            next,
            occurredAt,
            terminal
                ? PersistenceMutationPriority.Terminal
                : snapshot.State == UploadTransferState.Queued
                    ? PersistenceMutationPriority.Structural
                    : snapshot.State == UploadTransferState.InProgress
                        ? PersistenceMutationPriority.Progress
                        : PersistenceMutationPriority.Ordinary);

        if (terminal)
        {
            TransferAttemptPersistenceMutation? attempt = snapshot.Attempt is null
                ? null
                : ToAttempt(snapshot, snapshot.Attempt, next, terminal: true);
            handoffs?.BeginTransferTerminal(snapshot.TransferId, snapshot.Revision);
            bool accepted = sink.TryEnqueue(new TransferTerminalPersistenceMutation(
                transfer,
                attempt,
                OwningJob: null));
            if (!accepted)
                handoffs?.FailTransferTerminalAdmission(snapshot.TransferId, snapshot.Revision);
            lock (gate)
                persistedAttempts.Remove(snapshot.TransferId);
            return;
        }

        sink.TryEnqueue(transfer);
        if (snapshot.Attempt is null)
            return;

        bool first;
        lock (gate)
            first = persistedAttempts.Add(snapshot.TransferId);
        if (first && !sink.TryEnqueue(ToAttempt(snapshot, snapshot.Attempt, next, terminal: false)))
        {
            lock (gate)
                persistedAttempts.Remove(snapshot.TransferId);
        }
    }

    private TransferPersistenceMutation ToTransfer(
        UploadTransferSnapshot snapshot,
        long mutationSequence,
        DateTimeOffset occurredAt,
        PersistenceMutationPriority priority)
        => new(
            runtimeId,
            mutationSequence,
            occurredAt,
            snapshot.TransferId,
            snapshot.Revision,
            priority,
            JobId: null,
            WorkflowId: null,
            Direction: "Upload",
            Source: "SoulseekPeer",
            snapshot.Username,
            snapshot.RemotePath,
            LocalPath: null,
            snapshot.State.ToString(),
            TerminalOutcome(snapshot.State),
            snapshot.SizeBytes,
            snapshot.BytesTransferred,
            snapshot.Attempt?.Number ?? 0,
            snapshot.FailureReason.ToString(),
            FailureMessage: null,
            CancellationSource: snapshot.CancellationSource.ToString(),
            RequestedAtUtc: snapshot.RequestedAtUtc,
            StartedAtUtc: snapshot.Attempt?.StartedAtUtc,
            LastProgressAtUtc: snapshot.LastProgressAtUtc,
            BytesPerSecond: checked((long)Math.Max(0, snapshot.BytesPerSecond)),
            File: snapshot.File,
            GroupRef: snapshot.GroupRef,
            GroupDisplayPath: snapshot.GroupDisplayPath,
            AccountingObservations: AccountingObservations(snapshot, occurredAt),
            CompletedAtUtc: snapshot.FinishedAtUtc);

    private TransferAttemptPersistenceMutation ToAttempt(
        UploadTransferSnapshot transfer,
        UploadAttemptSnapshot attempt,
        long mutationSequence,
        bool terminal)
        => new(
            runtimeId,
            mutationSequence,
            terminal
                ? attempt.FinishedAtUtc ?? transfer.FinishedAtUtc ?? DateTimeOffset.UtcNow
                : attempt.StartedAtUtc,
            attempt.AttemptId,
            transfer.Revision,
            terminal
                ? PersistenceMutationPriority.Terminal
                : PersistenceMutationPriority.Structural,
            transfer.TransferId,
            attempt.Number,
            "SoulseekPeer",
            terminal ? AttemptState(transfer.State) : "Started",
            transfer.Username,
            transfer.RemotePath,
            OutputPath: null,
            transfer.FailureReason.ToString(),
            FailureMessage: null,
            Direction: "Upload",
            GroupRef: transfer.GroupRef,
            GroupDisplayPath: transfer.GroupDisplayPath,
            AccountingObservations: AccountingObservations(
                transfer,
                terminal
                    ? attempt.FinishedAtUtc ?? transfer.FinishedAtUtc ?? DateTimeOffset.UtcNow
                    : attempt.StartedAtUtc));

    private static IReadOnlyList<TransferAccountingObservation>? AccountingObservations(
        UploadTransferSnapshot transfer,
        DateTimeOffset occurredAt)
        => transfer.Attempt is null
            ? null
            :
            [
                new TransferAccountingObservation(
                    transfer.Attempt.AttemptId,
                    transfer.Revision,
                    occurredAt,
                    Math.Max(0, transfer.Attempt.BytesTransferred)),
            ];

    private static string TerminalOutcome(UploadTransferState state)
        => state switch
        {
            UploadTransferState.Completed => "Succeeded",
            UploadTransferState.Cancelled => "Cancelled",
            UploadTransferState.Failed => "Failed",
            UploadTransferState.Interrupted => "Interrupted",
            _ => "None",
        };

    private static string AttemptState(UploadTransferState state)
        => state switch
        {
            UploadTransferState.Completed => "Completed",
            UploadTransferState.Cancelled => "Cancelled",
            UploadTransferState.Interrupted => "Interrupted",
            _ => "Failed",
        };
}
