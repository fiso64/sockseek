using Sockseek.Api;
using Sockseek.Persistence.Write;

namespace Sockseek.Server.Persistence;

/// <summary>Writes accepted transfer facts. State and attempt ownership stay in TransferState.</summary>
internal sealed class TransferPersistence(
    Guid runtimeId, IPersistenceMutationSink sink, PersistenceHandoffTracker? handoffs = null)
{
    private long sequence;

    public void Write(TransferReadSnapshot row, DateTimeOffset occurredAt,
        PersistenceMutationPriority priority, TransferAttemptHistoryDto? finalAttempt = null,
        IReadOnlyList<TransferAccountingObservation>? accounting = null, bool metadataOnly = false)
    {
        long next = Interlocked.Increment(ref sequence);
        var mutation = new TransferPersistenceMutation(runtimeId, next, occurredAt,
            row.TransferId, row.Revision, priority, row.JobId, row.WorkflowId,
            row.Direction, row.Source, row.Username, row.RemotePath, row.LocalPath,
            row.State, row.TerminalOutcome.ToString(), row.TotalBytes, row.BytesTransferred,
            row.AttemptCount, row.StoredFailureReason, row.FailureMessage, row.StoredCancellationSource,
            row.RequestedAtUtc, row.StartedAtUtc, row.LastProgressAtUtc, row.BytesPerSecond, row.File,
            GroupRef: row.GroupRef, GroupDisplayPath: row.GroupDisplayPath,
            AccountingObservations: accounting, GroupRelativePath: row.GroupRelativePath,
            CompletedAtUtc: row.CompletedAtUtc);
        if (!row.IsTerminal)
        {
            sink.TryEnqueue(mutation);
            return;
        }
        handoffs?.BeginTransferTerminal(row.TransferId, row.Revision);
        // A final attempt and its transfer commit together. Later output-path
        // changes replace only the header and must not replay byte accounting.
        bool accepted = sink.TryEnqueue(metadataOnly ? mutation : new TransferTerminalPersistenceMutation(
            mutation, finalAttempt is null ? null : Attempt(row, finalAttempt, next, null), OwningJob: null));
        if (!accepted) handoffs?.FailTransferTerminalAdmission(row.TransferId, row.Revision);
    }

    public bool WriteAttempt(TransferReadSnapshot row, TransferAttemptHistoryDto attempt,
        IReadOnlyList<TransferAccountingObservation>? accounting = null)
        => sink.TryEnqueue(Attempt(row, attempt, Interlocked.Increment(ref sequence), accounting));

    private TransferAttemptPersistenceMutation Attempt(TransferReadSnapshot row,
        TransferAttemptHistoryDto attempt, long next, IReadOnlyList<TransferAccountingObservation>? accounting)
        => new(runtimeId, next, attempt.CompletedAtUtc ?? attempt.StartedAtUtc,
            attempt.AttemptId, attempt.Revision,
            attempt.State == "Started" ? PersistenceMutationPriority.Structural : PersistenceMutationPriority.Terminal,
            attempt.TransferId, attempt.AttemptNumber, attempt.Source, attempt.State,
            attempt.SourceUsername, attempt.SourcePath, attempt.OutputPath,
            attempt.FailureReason, attempt.FailureMessage, row.Direction,
            GroupRef: row.GroupRef, GroupDisplayPath: row.GroupDisplayPath,
            AccountingObservations: accounting, StartedAtUtc: attempt.StartedAtUtc);
}
