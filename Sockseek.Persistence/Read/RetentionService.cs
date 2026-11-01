using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Storage;
using Sockseek.Persistence.Planning;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Read;

public sealed record PersistenceRetentionOptions
{
    public TimeSpan? CompletedJobHistoryAge { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan? UnsuccessfulJobHistoryAge { get; init; } = TimeSpan.FromDays(180);
    public int? MaximumRetainedJobs { get; init; } = 100_000;
    public TimeSpan? SearchResultAge { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan? TransferHistoryAge { get; init; } = TimeSpan.FromDays(90);
    public int BatchSize { get; init; } = 500;

    public void Validate()
    {
        ValidateAge(CompletedJobHistoryAge, nameof(CompletedJobHistoryAge));
        ValidateAge(UnsuccessfulJobHistoryAge, nameof(UnsuccessfulJobHistoryAge));
        if (MaximumRetainedJobs <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumRetainedJobs));
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
        // raw rows from both the age policy and the maximum job count.
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
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE search_observation_sources SET raw_search_job_id = NULL
                WHERE raw_search_job_id IN (SELECT value FROM json_each({sources}));
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
        var agedJobIds = await agedJobs
            .OrderBy(job => job.CompletedAtUtc)
            .Select(job => job.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int retainedCount = await context.Jobs.CountAsync(cancellationToken).ConfigureAwait(false);
        int excess = options.MaximumRetainedJobs.HasValue
            ? Math.Max(0, retainedCount - options.MaximumRetainedJobs.Value)
            : 0;
        int remainingExcess = Math.Max(0, excess - agedJobIds.Count);
        if (agedJobIds.Count < options.BatchSize && remainingExcess > 0)
        {
            var countIds = await context.Jobs.AsNoTracking()
                .Where(job => job.LifecycleState == "Terminal"
                    && !agedJobIds.Contains(job.Id)
                    && !context.SearchResults.Any(result => result.SearchJobId == job.Id))
                .OrderBy(job => job.CompletedAtUtc)
                .Select(job => job.Id)
                .Take(Math.Min(options.BatchSize - agedJobIds.Count, remainingExcess))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            agedJobIds.AddRange(countIds);
        }

        int prunedJobs = 0;
        if (agedJobIds.Count > 0)
            prunedJobs = await context.Jobs.Where(job => agedJobIds.Contains(job.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        await SearchObservationWriter.PruneUnreferencedAsync(
            (SqliteConnection)context.Database.GetDbConnection(),
            (SqliteTransaction)context.Database.CurrentTransaction!.GetDbTransaction(),
            searchIdsToPrune.Concat(agedJobIds).Distinct().ToArray(), cancellationToken).ConfigureAwait(false);

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
