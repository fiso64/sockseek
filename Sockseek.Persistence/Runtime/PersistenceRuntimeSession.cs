using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;

namespace Sockseek.Persistence.Runtime;

public sealed record PersistenceRuntimeInfo(Guid RuntimeId, DateTimeOffset StartedAtUtc, string Version);

public sealed record StartupReconciliationResult(
    PersistenceRuntimeInfo Runtime,
    int UnfinishedRuntimeCount,
    int InterruptedJobCount,
    int InterruptedTransferCount,
    int InterruptedAttemptCount,
    int InterruptedSearchCount,
    long MaximumRetainedDisplayId);

public sealed class PersistenceRuntimeSession(
    IDbContextFactory<SockseekDbContext> contextFactory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private PersistenceRuntimeInfo? current;

    public PersistenceRuntimeInfo? Current => Volatile.Read(ref current);

    public async Task<StartupReconciliationResult> StartAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        if (Current != null)
            throw new InvalidOperationException("The persistence runtime session has already started.");

        var startedAt = clock.GetUtcNow();
        long now = ToUnixMilliseconds(startedAt);
        var runtime = new PersistenceRuntimeInfo(Guid.NewGuid(), startedAt, version);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var unfinishedIds = await context.RuntimeSessions
            .AsNoTracking()
            .Where(session => session.StoppedAtUtc == null)
            .Select(session => session.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var previousRuntime = await context.RuntimeSessions
            .AsNoTracking()
            .OrderByDescending(session => session.StartedAtUtc)
            .Select(session => new { session.Id, session.ShutdownKind })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        int interruptedJobs = 0;
        int interruptedSearches = 0;

        // LastRuntimeId is a foreign key, so the replacement runtime must exist before
        // the set-based reconciliation points interrupted rows at it.
        context.RuntimeSessions.Add(new RuntimeSessionEntity
        {
            Id = runtime.RuntimeId,
            StartedAtUtc = now,
            Version = version,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (unfinishedIds.Count > 0
            || previousRuntime?.ShutdownKind is not null and not "Clean")
        {
            // An unclean stop can lose the last in-memory cumulative
            // observation even when no retained transfer row was far enough
            // along to be reconciled. Conservatively restart contiguous
            // accounting coverage here instead of inventing gap intervals.
            await context.TransferAccountingStates
                .Where(state => state.Id == 1)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(state => state.CompleteFromUtc, now)
                        .SetProperty(state => state.UpdatedAtUtc, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (unfinishedIds.Count > 0)
        {
            interruptedSearches = await context.SearchJobs
                .Where(search => !search.IsComplete && context.Jobs.Any(job =>
                    job.Id == search.JobId
                    && unfinishedIds.Contains(job.LastRuntimeId)))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(search => search.IsComplete, true)
                        .SetProperty(search => search.CompletedAtUtc, now)
                        .SetProperty(search => search.ResultPersistenceState, "Interrupted")
                        .SetProperty(search => search.Revision, search => search.Revision + 1),
                    cancellationToken)
                .ConfigureAwait(false);

            interruptedJobs = await InterruptJobsAsync(context.Jobs.Where(job => unfinishedIds.Contains(job.LastRuntimeId)),
                runtime.RuntimeId, now, "Interrupted by an unclean daemon shutdown.", cancellationToken).ConfigureAwait(false);

            if (interruptedJobs > 0)
                await context.Database.ExecuteSqlRawAsync(InterruptedDirectoryProgress.Update(currentRuntimeOnly: true),
                    [runtime.RuntimeId], cancellationToken).ConfigureAwait(false);

            await context.RuntimeSessions
                .Where(session => unfinishedIds.Contains(session.Id))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(session => session.StoppedAtUtc, now)
                        .SetProperty(session => session.ShutdownKind, "Unclean"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // A stopped publisher cannot retain an active upload, even if its final progress
        // was saved after its last terminal event. Probe the runtime/status indexes.
        var priorRuntimes = context.RuntimeSessions.Where(session => session.Id != runtime.RuntimeId).Select(session => session.Id);
        int interruptedTransfers = await context.Transfers
            .Where(transfer => priorRuntimes.Contains(transfer.LastRuntimeId) && (transfer.TerminalOutcome == "None"
                || (transfer.Direction == "Download" && transfer.TerminalOutcome == "Cancelled"
                    && transfer.CancellationSource == "DaemonShutdown")))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(transfer => transfer.LastRuntimeId, runtime.RuntimeId)
                    .SetProperty(transfer => transfer.RecoveryPending, transfer =>
                        transfer.Direction == "Download" && transfer.Source == "SoulseekPeer" && transfer.JobId != null
                        && transfer.Username != null && transfer.RemotePath != null && transfer.LocalPath != null)
                    .SetProperty(transfer => transfer.LastSequence, 0)
                    .SetProperty(transfer => transfer.State, "Interrupted")
                    .SetProperty(transfer => transfer.TerminalOutcome, "Interrupted")
                    .SetProperty(transfer => transfer.FailureReason, "None")
                    .SetProperty(transfer => transfer.FailureMessage, "The daemon stopped before this transfer finished.")
                    .SetProperty(transfer => transfer.CancellationSource, "DaemonShutdown")
                    .SetProperty(transfer => transfer.CompletedAtUtc, now)
                    .SetProperty(transfer => transfer.Revision, transfer => transfer.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);

        int interruptedAttempts = await context.TransferAttempts
            .Where(attempt => priorRuntimes.Contains(attempt.LastRuntimeId) && attempt.CompletedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(attempt => attempt.LastRuntimeId, runtime.RuntimeId)
                    .SetProperty(attempt => attempt.LastSequence, 0)
                    .SetProperty(attempt => attempt.State, "Interrupted")
                    .SetProperty(attempt => attempt.FailureReason, "None")
                    .SetProperty(attempt => attempt.CancellationSource, "DaemonShutdown")
                    .SetProperty(attempt => attempt.FailureMessage, "The daemon stopped before this transfer finished.")
                    .SetProperty(attempt => attempt.CompletedAtUtc, now)
                    .SetProperty(attempt => attempt.Revision, attempt => attempt.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);

        if (previousRuntime != null)
        {
            // A second stop can land between accepting the recovery job and its first transfer.
            // Once a new transfer exists it owns its own outcome, including a peer refusal.
            var stoppedJobs = context.Jobs.Where(job =>
                (job.LastRuntimeId == runtime.RuntimeId || job.LastRuntimeId == previousRuntime.Id)
                && (job.FailureReason == "Interrupted"
                    || (job.TerminalOutcome == "Cancelled" && job.CancellationSource == "InternalEngine")));
            await context.Transfers.Where(original => original.TerminalOutcome == "Interrupted"
                    && original.RetryJobId != null && original.RemovedAtUtc == null
                    && stoppedJobs.Any(job => job.Id == original.RetryJobId)
                    && !context.Transfers.Any(next => next.JobId == original.RetryJobId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RetryJobId, (Guid?)null)
                    .SetProperty(t => t.RecoveryPending, true)
                    .SetProperty(t => t.Revision, t => t.Revision + 1), cancellationToken);
        }

        long maximumDisplayId = await context.Jobs.MaxAsync(job => (long?)job.DisplayId, cancellationToken)
            .ConfigureAwait(false) ?? 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref current, runtime);

        return new StartupReconciliationResult(
            runtime,
            unfinishedIds.Count,
            interruptedJobs,
            interruptedTransfers,
            interruptedAttempts,
            interruptedSearches,
            maximumDisplayId);
    }

    public async Task StopAsync(string shutdownKind = "Clean", CancellationToken cancellationToken = default)
    {
        var runtime = Current ?? throw new InvalidOperationException("The persistence runtime session has not started.");
        long now = ToUnixMilliseconds(clock.GetUtcNow());

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.RuntimeSessions.SingleAsync(
            session => session.Id == runtime.RuntimeId,
            cancellationToken).ConfigureAwait(false);
        if (entity.StoppedAtUtc != null)
            throw new InvalidOperationException("The persistence runtime session is already stopped.");

        // A clean stop can precede engine registration (for example while waiting
        // for login). Keep the accepted ID and close any work the engine never finished.
        var acceptedJobs = context.Jobs.Where(job => job.LastRuntimeId == runtime.RuntimeId
            && job.Revision == 0 && job.LifecycleState == "Pending");
        await context.SearchJobs.Where(search => !search.IsComplete && acceptedJobs.Any(job => job.Id == search.JobId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(search => search.IsComplete, true)
                .SetProperty(search => search.CompletedAtUtc, now)
                .SetProperty(search => search.ResultPersistenceState, "Interrupted")
                .SetProperty(search => search.Revision, search => search.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
        await InterruptJobsAsync(acceptedJobs, runtime.RuntimeId, now,
            "The daemon stopped before this job finished.", cancellationToken).ConfigureAwait(false);
        entity.StoppedAtUtc = now;
        entity.ShutdownKind = shutdownKind;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref current, null);
    }

    private static Task<int> InterruptJobsAsync(IQueryable<JobEntity> jobs, Guid runtimeId,
        long now, string message, CancellationToken cancellationToken)
        => jobs.Where(job => job.LifecycleState != "Terminal")
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.LastRuntimeId, runtimeId)
                    .SetProperty(job => job.LastSequence, 0)
                    .SetProperty(job => job.LifecycleState, "Terminal")
                    .SetProperty(job => job.ActivityPhase, "None")
                    .SetProperty(job => job.ActivityUntilUtc, (long?)null)
                    .SetProperty(job => job.TerminalOutcome, "Failed")
                    .SetProperty(job => job.FailureReason, "Interrupted")
                    .SetProperty(job => job.FailureMessage, message)
                    .SetProperty(job => job.UpdatedAtUtc, job => job.UpdatedAtUtc > now ? job.UpdatedAtUtc : now)
                    .SetProperty(job => job.CompletedAtUtc, now)
                    .SetProperty(job => job.Revision, job => job.Revision + 1),
                cancellationToken);

    internal static long ToUnixMilliseconds(DateTimeOffset value)
        => value.ToUniversalTime().ToUnixTimeMilliseconds();
}
