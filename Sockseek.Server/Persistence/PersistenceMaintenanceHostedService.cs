using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Sockseek.Api;

namespace Sockseek.Server.Persistence;

public sealed class PersistenceMaintenanceHostedService(
    PersistenceCoordinator coordinator,
    IOptions<ServerOptions> serverOptions,
    ILogger<PersistenceMaintenanceHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan MaximumContinuousRetentionWork = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = serverOptions.Value.Persistence;
        if (!coordinator.IsEnabled || !options.RetentionEnabled)
            return;
        if (options.RetentionInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Persistence retention interval must be positive.");

        using var timer = new PeriodicTimer(options.RetentionInterval);
        try
        {
            // Run on startup too: a daemon restarted daily must still expire history.
            do
            {
                timer.Period = options.RetentionInterval;
                try
                {
                    var started = TimeProvider.System.GetTimestamp();
                    PersistenceRetentionResultDto? group = null;
                    bool mayHaveMore;
                    do
                    {
                        var result = await coordinator.RunRetentionAsync(stoppingToken).ConfigureAwait(false);
                        group = Add(group, result);
                        mayHaveMore = MayHaveMore(result, options.RetentionBatchSize);
                        if (mayHaveMore)
                            await Task.Yield();
                    }
                    while (mayHaveMore
                        && TimeProvider.System.GetElapsedTime(started) < MaximumContinuousRetentionWork);

                    // Yield between bounded maintenance turns, without abandoning
                    // an accumulated backlog until the next daily interval.
                    if (mayHaveMore)
                        timer.Period = TimeSpan.FromTicks(Math.Min(options.RetentionInterval.Ticks,
                            TimeSpan.FromMinutes(1).Ticks));

                    ServerLogMessages.PersistenceRetentionCompleted(
                        logger,
                        group!.PrunedJobs,
                        group.PrunedSearchResults,
                        group.PrunedTransfers,
                        group.PrunedChatMessages,
                        group.DurationMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ServerLogMessages.PersistenceRetentionFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // PeriodicTimer cancellation is the expected hosted-service stop
            // path, not a background-service failure.
        }
    }

    internal static bool MayHaveMore(
        PersistenceRetentionResultDto result,
        int batchSize)
        => result.PrunedJobs >= batchSize
            || result.SearchesMarkedPruned >= batchSize
            || result.PrunedTransfers >= batchSize
            || result.PrunedChatMessages >= batchSize;

    private static PersistenceRetentionResultDto Add(
        PersistenceRetentionResultDto? group,
        PersistenceRetentionResultDto result)
        => group is null
            ? result
            : new PersistenceRetentionResultDto(
                group.PrunedJobs + result.PrunedJobs,
                group.PrunedSearchResults + result.PrunedSearchResults,
                group.SearchesMarkedPruned + result.SearchesMarkedPruned,
                group.DurationMilliseconds + result.DurationMilliseconds,
                group.PrunedTransfers + result.PrunedTransfers,
                group.PrunedTransferAttempts + result.PrunedTransferAttempts,
                group.PrunedChatMessages + result.PrunedChatMessages);
}
