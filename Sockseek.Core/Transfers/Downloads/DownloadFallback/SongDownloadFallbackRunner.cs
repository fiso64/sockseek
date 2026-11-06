using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Events;
using Sockseek.Core.Transfers;
using Sockseek.Core.Models;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core;

internal sealed class SongDownloadFallbackRunner
{
    private readonly DownloadExecutionContext context;
    private readonly ILogger<SongDownloadFallbackRunner> logger;

    public SongDownloadFallbackRunner(DownloadExecutionContext context)
    {
        this.context = context;
        logger = context.LoggerFactory.CreateLogger<SongDownloadFallbackRunner>();
    }

    public async Task<JobOutcome?> TryRunAsync(
        SongJob song,
        DownloadSettings config,
        FileManager organizer,
        CancellationToken ct)
    {
        if (!context.SongDownloadFallback.CanRun(song, config))
            return null;

        song.UpdateActivity(JobActivityPhase.RunningFallback);
        context.Events.RaiseJobMessage(
            song,
            LogLevel.Information,
            null,
            "running fallback downloader");
        var fallbackLog = ExtractorContext.ForJob(song, context.Events).Log;
        Guid? transferId = null;
        Guid? attemptId = null;
        FallbackTransferDescriptor? descriptor = null;

        void OnTransferStarting(FallbackTransferDescriptor starting)
        {
            if (transferId != null)
                throw new InvalidOperationException("A fallback invocation may start only one logical transfer.");

            descriptor = starting;
            transferId = TransferIds.New();
            attemptId = TransferAttemptIds.New();
            context.Events.RaiseFallbackTransferStarted(
                transferId.Value,
                song,
                starting.SourceReference,
                starting.OutputPathPrefix);
            context.Events.RaiseFallbackTransferAttemptStarted(
                transferId.Value,
                attemptId.Value,
                song,
                starting.SourceReference,
                starting.OutputPathPrefix);
        }

        JobOutcome? outcome;
        try
        {
            outcome = await context.SongDownloadFallback.TryDownloadAsync(
                song,
                config,
                organizer,
                fallbackLog,
                ct,
                OnTransferStarting);
        }
        catch (Exception ex) when (transferId != null && attemptId != null && descriptor != null)
        {
            if (ex is OperationCanceledException)
            {
                context.Events.RaiseFallbackTransferAttemptCancelled(
                    transferId.Value,
                    attemptId.Value,
                    song,
                    descriptor.SourceReference,
                    descriptor.OutputPathPrefix,
                    TransferCancellationReason.Requested);
                context.Events.RaiseFallbackTransferCancelled(
                    transferId.Value,
                    song,
                    descriptor.SourceReference,
                    descriptor.OutputPathPrefix,
                    attemptCount: 1,
                    reason: TransferCancellationReason.Requested);
            }
            else
            {
                context.Events.RaiseFallbackTransferAttemptFailed(
                    transferId.Value,
                    attemptId.Value,
                    song,
                    descriptor.SourceReference,
                    descriptor.OutputPathPrefix,
                    ex);
                context.Events.RaiseFallbackTransferFailed(
                    transferId.Value,
                    song,
                    descriptor.SourceReference,
                    descriptor.OutputPathPrefix,
                    attemptCount: 1,
                    reason: TransferFailureReason.PeerFailure,
                    exception: ex);
            }

            throw;
        }

        if (transferId != null && attemptId != null && descriptor != null)
        {
            context.Events.RaiseFallbackTransferAttemptCompleted(
                transferId.Value,
                attemptId.Value,
                song,
                descriptor.SourceReference,
                descriptor.OutputPathPrefix);
        }

        if (outcome == null || !outcome.ShouldCommit)
        {
            if (transferId != null && descriptor != null)
            {
                context.Events.RaiseFallbackTransferFailed(
                    transferId.Value,
                    song,
                    descriptor.SourceReference,
                    descriptor.OutputPathPrefix,
                    attemptCount: 1,
                    reason: TransferFailureReason.Unknown,
                    exception: new IOException("Fallback transfer ended without a terminal job outcome."));
            }
            return null;
        }

        if (outcome.TerminalOutcome == JobTerminalOutcome.Succeeded)
        {
            if (transferId == null || descriptor == null || string.IsNullOrWhiteSpace(outcome.DownloadPath))
                throw new InvalidOperationException("A successful fallback must report transfer start and a produced file path.");

            context.PendingTerminalTransfers[song.Id] = new PendingTerminalTransfer(
                transferId.Value,
                AttemptCount: 1,
                Target: null,
                SourceReference: descriptor.SourceReference,
                InitialOutputPath: descriptor.OutputPathPrefix);
        }
        else if (transferId != null && descriptor != null)
        {
            context.Events.RaiseFallbackTransferFailed(
                transferId.Value,
                song,
                descriptor.SourceReference,
                descriptor.OutputPathPrefix,
                attemptCount: 1,
                reason: TransferFailureReason.Unknown,
                exception: new IOException(outcome.FailureMessage ?? "Fallback transfer failed."));
        }

        DownloadLogMessages.JobDecision(
            logger,
            song.Id,
            $"fallback-{outcome.TerminalOutcome.ToString().ToLowerInvariant()}",
            null);
        return outcome;
    }
}
