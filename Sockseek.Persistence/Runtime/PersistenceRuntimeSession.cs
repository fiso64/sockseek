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
    int InterruptedSearchCount);

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
        string? previousShutdownKind = await context.RuntimeSessions
            .AsNoTracking()
            .OrderByDescending(session => session.StartedAtUtc)
            .Select(session => session.ShutdownKind)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        int interruptedJobs = 0;
        int interruptedTransfers = 0;
        int interruptedAttempts = 0;
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
            || previousShutdownKind is not null and not "Clean")
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

            interruptedJobs = await context.Jobs
                .Where(job => unfinishedIds.Contains(job.LastRuntimeId) && job.LifecycleState != "Terminal")
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(job => job.LastRuntimeId, runtime.RuntimeId)
                        .SetProperty(job => job.LastSequence, 0)
                        .SetProperty(job => job.LifecycleState, "Terminal")
                        .SetProperty(job => job.ActivityPhase, "None")
                        .SetProperty(job => job.ActivityUntilUtc, (long?)null)
                        .SetProperty(job => job.TerminalOutcome, "Failed")
                        .SetProperty(job => job.FailureReason, "Interrupted")
                        .SetProperty(job => job.FailureMessage, "Interrupted by an unclean daemon shutdown.")
                        .SetProperty(job => job.UpdatedAtUtc, job => job.UpdatedAtUtc > now ? job.UpdatedAtUtc : now)
                        .SetProperty(job => job.CompletedAtUtc, now)
                        .SetProperty(job => job.Revision, job => job.Revision + 1),
                    cancellationToken)
                .ConfigureAwait(false);

            interruptedTransfers = await context.Transfers
                .Where(transfer => unfinishedIds.Contains(transfer.LastRuntimeId) && transfer.TerminalOutcome == "None")
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(transfer => transfer.LastRuntimeId, runtime.RuntimeId)
                        .SetProperty(transfer => transfer.LastSequence, 0)
                        .SetProperty(transfer => transfer.State, "Interrupted")
                        .SetProperty(transfer => transfer.TerminalOutcome, "Interrupted")
                        .SetProperty(transfer => transfer.FailureReason, "Interrupted")
                        .SetProperty(transfer => transfer.FailureMessage, "Interrupted by an unclean daemon shutdown.")
                        .SetProperty(transfer => transfer.CancellationSource, "DaemonShutdown")
                        .SetProperty(transfer => transfer.CompletedAtUtc, now)
                        .SetProperty(transfer => transfer.Revision, transfer => transfer.Revision + 1),
                    cancellationToken)
                .ConfigureAwait(false);

            interruptedAttempts = await context.TransferAttempts
                .Where(attempt => unfinishedIds.Contains(attempt.LastRuntimeId) && attempt.CompletedAtUtc == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(attempt => attempt.LastRuntimeId, runtime.RuntimeId)
                        .SetProperty(attempt => attempt.LastSequence, 0)
                        .SetProperty(attempt => attempt.State, "Interrupted")
                        .SetProperty(attempt => attempt.FailureReason, "Interrupted")
                        .SetProperty(attempt => attempt.FailureMessage, "Interrupted by an unclean daemon shutdown.")
                        .SetProperty(attempt => attempt.CompletedAtUtc, now)
                        .SetProperty(attempt => attempt.Revision, attempt => attempt.Revision + 1),
                    cancellationToken)
                .ConfigureAwait(false);

            await context.RuntimeSessions
                .Where(session => unfinishedIds.Contains(session.Id))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(session => session.StoppedAtUtc, now)
                        .SetProperty(session => session.ShutdownKind, "Unclean"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref current, runtime);

        return new StartupReconciliationResult(
            runtime,
            unfinishedIds.Count,
            interruptedJobs,
            interruptedTransfers,
            interruptedAttempts,
            interruptedSearches);
    }

    public async Task StopAsync(string shutdownKind = "Clean", CancellationToken cancellationToken = default)
    {
        var runtime = Current ?? throw new InvalidOperationException("The persistence runtime session has not started.");
        long now = ToUnixMilliseconds(clock.GetUtcNow());

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.RuntimeSessions.SingleAsync(
            session => session.Id == runtime.RuntimeId,
            cancellationToken).ConfigureAwait(false);
        if (entity.StoppedAtUtc != null)
            throw new InvalidOperationException("The persistence runtime session is already stopped.");

        entity.StoppedAtUtc = now;
        entity.ShutdownKind = shutdownKind;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref current, null);
    }

    internal static long ToUnixMilliseconds(DateTimeOffset value)
        => value.ToUniversalTime().ToUnixTimeMilliseconds();
}
