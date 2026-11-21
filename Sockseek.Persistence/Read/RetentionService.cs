using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Read;

public sealed record PersistenceRetentionOptions
{
    public TimeSpan? CompletedJobHistoryAge { get; init; }
    public TimeSpan? UnsuccessfulJobHistoryAge { get; init; }
    public TimeSpan? SearchResultAge { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan? TransferHistoryAge { get; init; }
    public int BatchSize { get; init; } = 500;

    public void Validate()
    {
        ValidateAge(CompletedJobHistoryAge, nameof(CompletedJobHistoryAge));
        ValidateAge(UnsuccessfulJobHistoryAge, nameof(UnsuccessfulJobHistoryAge));
        ValidateAge(SearchResultAge, nameof(SearchResultAge));
        ValidateAge(TransferHistoryAge, nameof(TransferHistoryAge));
        if (BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(BatchSize));
    }

    private static void ValidateAge(TimeSpan? age, string name)
    {
        if (age <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(name);
    }
}

public sealed record RetentionResult(
    int PrunedJobs,
    int PrunedSearchResults,
    int SearchesMarkedPruned,
    int PrunedTransfers,
    int PrunedTransferAttempts);

public sealed class RetentionService(
    PersistenceInbox writes,
    PersistenceRetentionOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<RetentionResult> RunBatchAsync(CancellationToken cancellationToken = default)
        => writes.ExecuteAsync(PruneAsync, cancellationToken, background: true);

    private async Task<RetentionResult> PruneAsync(SockseekDbContext context, CancellationToken cancellationToken)
    {
        options.Validate();
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        long? completedJobCutoff = Cutoff(now, options.CompletedJobHistoryAge);
        long? unsuccessfulJobCutoff = Cutoff(now, options.UnsuccessfulJobHistoryAge);
        long? searchCutoff = Cutoff(now, options.SearchResultAge);
        long? transferCutoff = Cutoff(now, options.TransferHistoryAge);

        // Raw search-result retention is independent of job-history retention.
        // Prune eligible raw rows first, then protect every job that still owns
        // raw rows from the job age policy.
        var searchesQuery = context.SearchJobs
            .Where(search => search.ResultPersistenceState != "Pruned");
        searchesQuery = searchCutoff.HasValue
            ? searchesQuery.Where(search => context.Jobs.Any(job => job.Id == search.JobId && job.CompletedAtUtc < searchCutoff))
            : searchesQuery.Where(_ => false);
        var searchIdsToPrune = await searchesQuery
            .AsNoTracking()
            .OrderBy(search => search.CompletedAtUtc)
            .Select(search => search.JobId)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int prunedResults = 0;
        int searchesMarkedPruned = 0;
        if (searchIdsToPrune.Count > 0)
        {
            string sources = JsonSerializer.Serialize(searchIdsToPrune.Select(id => id.ToString("D").ToUpperInvariant()));
            prunedResults = await context.SearchResults.Where(result => searchIdsToPrune.Contains(result.SearchJobId))
                .CountAsync(cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlAsync($"""
                DELETE FROM search_observation_sources
                WHERE search_job_id IN (SELECT value FROM json_each({sources}));
                """, cancellationToken).ConfigureAwait(false);
            searchesMarkedPruned = await context.SearchJobs
                .Where(search => searchIdsToPrune.Contains(search.JobId))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(search => search.ResultPersistenceState, "Pruned")
                        .SetProperty(search => search.ResultsPrunedAtUtc, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var agedJobs = context.Jobs.AsNoTracking().Where(job =>
            job.LifecycleState == "Terminal"
            && !context.SearchResults.Any(result => result.SearchJobId == job.Id));
        if (completedJobCutoff.HasValue || unsuccessfulJobCutoff.HasValue)
            agedJobs = agedJobs.Where(job =>
                completedJobCutoff.HasValue && job.TerminalOutcome == "Succeeded" && job.CompletedAtUtc < completedJobCutoff
                || unsuccessfulJobCutoff.HasValue && job.TerminalOutcome != "Succeeded" && job.CompletedAtUtc < unsuccessfulJobCutoff);
        else
            agedJobs = agedJobs.Where(_ => false);
        var agedJobsToPrune = await agedJobs
            .OrderBy(job => job.CompletedAtUtc)
            .Select(job => new { job.Id, job.SubmissionId })
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int prunedJobs = 0;
        if (agedJobsToPrune.Count > 0)
        {
            var ids = agedJobsToPrune.Select(job => job.Id).ToArray();
            prunedJobs = await context.Jobs.Where(job => ids.Contains(job.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var submissions = agedJobsToPrune.Where(job => job.SubmissionId.HasValue)
                .Select(job => job.SubmissionId!.Value).Distinct().ToArray();
            await SubmissionStore.DeleteEmptyAsync(context, submissions, cancellationToken).ConfigureAwait(false);
        }

        int prunedAttempts = 0;
        int prunedTransfers = 0;
        if (transferCutoff.HasValue)
        {
            long accountingCutoff = RoundUp(
                transferCutoff.Value,
                Persistence.Write.PersistenceWriter.AccountingBucketMilliseconds);
            await context.TransferByteBuckets
                .Where(bucket => bucket.BucketStartUtc < accountingCutoff)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.TransferAccountingStates
                .Where(state => state.Id == 1 && state.CompleteFromUtc < accountingCutoff)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(state => state.CompleteFromUtc, accountingCutoff)
                        .SetProperty(state => state.UpdatedAtUtc, now),
                    cancellationToken).ConfigureAwait(false);
            var transferIds = await context.Transfers.AsNoTracking()
                .Where(transfer => transfer.TerminalOutcome != "None" && transfer.CompletedAtUtc < transferCutoff)
                .OrderBy(transfer => transfer.CompletedAtUtc)
                .Select(transfer => transfer.Id)
                .Take(options.BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (transferIds.Count > 0)
            {
                prunedAttempts = await context.TransferAttempts
                    .Where(attempt => transferIds.Contains(attempt.TransferId))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                prunedTransfers = await context.Transfers
                    .Where(transfer => transferIds.Contains(transfer.Id))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new RetentionResult(
            prunedJobs,
            prunedResults,
            searchesMarkedPruned,
            prunedTransfers,
            prunedAttempts);
    }

    private static long? Cutoff(long now, TimeSpan? age)
        => age.HasValue ? now - (long)age.Value.TotalMilliseconds : null;

    private static long RoundUp(long value, long interval)
    {
        Math.DivRem(value, interval, out long remainder);
        return remainder == 0 ? value : checked(value + interval - remainder);
    }
}
