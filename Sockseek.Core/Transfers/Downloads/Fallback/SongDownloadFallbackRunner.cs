using Sockseek.Api;
using TransferFailureReason = Sockseek.Core.Events.TransferFailureReason;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads;
using Sockseek.Core.Events;
using Sockseek.Core.Transfers;

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

        // A new provider attempt must not inherit progress from a failed peer.
        song.BytesTransferred = 0;
        song.FileSize = null;
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
            context.Events.RaiseTransferAttemptStarted(
                TransferSubject.Fallback(transferId.Value, song, starting.SourceReference), attemptId.Value, 1,
                starting.OutputPathPrefix, starting.OutputPathPrefix, 0, 0);
        }

        JobOutcome? outcome;
        long producedBytes = 0;
        try
        {
            outcome = await context.SongDownloadFallback.TryDownloadAsync(
                song,
                config,
                organizer,
                fallbackLog,
                ct,
                OnTransferStarting);
            if (outcome is { TerminalOutcome: JobTerminalOutcome.Succeeded, DownloadPath: { } path })
                producedBytes = await context.Runtime.Files.WithCacheAsync(_ => new FileInfo(path).Length);
        }
        catch (Exception ex) when (transferId != null && attemptId != null && descriptor != null)
        {
            var subject = TransferSubject.Fallback(transferId.Value, song, descriptor.SourceReference);
            if (ex is OperationCanceledException)
            {
                context.Events.RaiseTransferAttemptCancelled(
                    subject, attemptId.Value, 1, descriptor.OutputPathPrefix, 0, 0, TransferCancellationReason.Requested);
                context.Events.RaiseTransferCancelled(
                    subject, descriptor.OutputPathPrefix, 0, 0, attemptCount: 1, TransferCancellationReason.Requested);
            }
            else
            {
                context.Events.RaiseTransferAttemptFailed(subject, attemptId.Value, 1, descriptor.OutputPathPrefix, 0, 0, ex);
                context.Events.RaiseTransferFailed(
                    subject, descriptor.OutputPathPrefix, 0, 0, attemptCount: 1, TransferFailureReason.PeerFailure, ex);
            }

            throw;
        }

        if (transferId != null && attemptId != null && descriptor != null)
        {
            context.Events.RaiseTransferAttemptCompleted(
                TransferSubject.Fallback(transferId.Value, song, descriptor.SourceReference), attemptId.Value, 1,
                descriptor.OutputPathPrefix, producedBytes, producedBytes);
        }

        if (outcome == null || !outcome.ShouldCommit)
        {
            if (transferId != null && descriptor != null)
            {
                context.Events.RaiseTransferFailed(
                    TransferSubject.Fallback(transferId.Value, song, descriptor.SourceReference), descriptor.OutputPathPrefix,
                    0, 0, attemptCount: 1, TransferFailureReason.Unknown, new IOException("Fallback transfer ended without a terminal job outcome."));
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
            context.Events.RaiseTransferFailed(
                    TransferSubject.Fallback(transferId.Value, song, descriptor.SourceReference), descriptor.OutputPathPrefix,
                    0, 0, attemptCount: 1, TransferFailureReason.Unknown, new IOException(outcome.FailureMessage ?? "Fallback transfer failed."));
        }

        DownloadLogMessages.JobDecision(
            logger,
            song.Id,
            $"fallback download: {outcome.TerminalOutcome}");
        return outcome;
    }
}
