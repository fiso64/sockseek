using Sockseek.Core;
using Sockseek.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core;


/// Utility class to handle interval-based progress reports.
///
/// See: https://johnscolaro.xyz/blog/log-by-time-not-by-count
public class IntervalProgressReporter
{
    public readonly TimeSpan Interval;
    private readonly int countInterval;
    private int loggedCount = 0;
    private DateTime lastLoggedTime = new(0, DateTimeKind.Utc);

    private int downloadedCount = 0;
    private int failedCount = 0;
    private readonly int totalCount = 0;
    private readonly Lock _reportLock = new();
    private readonly DownloadEvents events;
    private readonly Guid workflowId;

    public IntervalProgressReporter(
        TimeSpan interval,
        int countInterval,
        IEnumerable<SongJob> songs,
        DownloadEvents events,
        Guid workflowId)
    {
        this.Interval      = interval;
        this.countInterval = countInterval;
        this.events = events;
        this.workflowId = workflowId;

        foreach (var song in songs)
        {
            if (song.TerminalOutcome == JobTerminalOutcome.Succeeded
                || (song.TerminalOutcome == JobTerminalOutcome.Skipped && song.SkipReason == JobSkipReason.AlreadyExists))
                downloadedCount++;
            else if (song.TerminalOutcome is JobTerminalOutcome.Failed
                or JobTerminalOutcome.Cancelled
                or JobTerminalOutcome.PartialSuccess)
                failedCount++;
            else if (song.TerminalOutcome == JobTerminalOutcome.Skipped)
                failedCount++;
            totalCount++;
        }
    }

    public void MaybeReport(SongJob song)
    {
        lock (_reportLock)
        {
            if (song.TerminalOutcome == JobTerminalOutcome.Succeeded
                || (song.TerminalOutcome == JobTerminalOutcome.Skipped && song.SkipReason == JobSkipReason.AlreadyExists))
                downloadedCount++;
            else if (song.TerminalOutcome is JobTerminalOutcome.Failed
                or JobTerminalOutcome.Cancelled
                or JobTerminalOutcome.PartialSuccess)
                failedCount++;
            else if (song.TerminalOutcome == JobTerminalOutcome.Skipped)
                failedCount++;
            else
                return;

            loggedCount++;

            var now = DateTime.UtcNow;
            var timeConditionMet  = (now - lastLoggedTime) > Interval;
            var countConditionMet = countInterval <= 0 || (loggedCount >= countInterval);

            if (timeConditionMet && countConditionMet)
            {
                lastLoggedTime = now;
                loggedCount    = 0;

                var failedStr       = failedCount > 0 ? $", Failed {failedCount}" : "";
                var percentComplete = (double)(downloadedCount + failedCount) / totalCount;
                events.RaiseWorkflowMessage(
                    workflowId,
                    LogLevel.Information,
                    null,
                    $"Downloaded {downloadedCount}{failedStr} of Total {totalCount} ({percentComplete:P})");
            }
        }
    }
}
