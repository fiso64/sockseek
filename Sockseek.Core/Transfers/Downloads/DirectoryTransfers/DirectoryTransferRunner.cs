using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.Runtime;

namespace Sockseek.Core;

internal sealed record DirectoryTransferWorkItem(
    RemoteFileJob Job,
    PeerFileTarget Target,
    string OutputPath);

/// <summary>Runs already planned exact file transfers and groups child state.</summary>
internal sealed class DirectoryTransferRunner
{
    private readonly DownloadExecutionContext context;

    private readonly DownloadExecutorCoordinator downloads;

    public DirectoryTransferRunner(DownloadExecutionContext context, DownloadExecutorCoordinator downloads)
    {
        this.context = context;
        this.downloads = downloads;
    }

    public async Task<JobOutcome> Run(
        DirectoryDownloadJob directory,
        IReadOnlyList<DirectoryTransferWorkItem> work,
        DownloadSettings config)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(config);

        var attempt = directory.ActiveAttempt
            ?? throw new InvalidOperationException("A directory transfer requires an active plan attempt.");
        if (work.Count != attempt.Plan.Entries.Count)
            throw new ArgumentException("Directory work must contain one item per plan entry.", nameof(work));
        for (int index = 0; index < work.Count; index++)
        {
            if (work[index].Target.Identity != attempt.Plan.Entries[index].Target.Identity)
                throw new ArgumentException("Directory work target order must exactly match the active plan.", nameof(work));
        }

        if (attempt.ChildrenMaterialized
            && !attempt.FileJobs.SequenceEqual(work.Select(item => item.Job)))
        {
            throw new ArgumentException("Directory work jobs must be the children owned by the active attempt.", nameof(work));
        }

        if (!attempt.ChildrenMaterialized)
            directory.MaterializeDirectoryChildren(work.Select(item => item.Job));
        for (int index = 0; index < work.Count; index++)
        {
            var item = work[index];
            item.Job.WorkflowId = directory.WorkflowId;
            item.Job.Config = config;
            context.RegisterForExecution(item.Job, directory.Cts!.Token, directory, attempt.Plan.Entries[index]);
        }
        directory.BeginDirectoryTransfer();
        directory.UpdateActivity(JobActivityPhase.Downloading);

        await BoundedAsync.ForEachAsync(
            work,
            context.Runtime.ConcurrentJobLimit,
            item => RunChild(directory, item));
        return GroupOutcome(directory.FileJobs);
    }

    private async Task RunChild(
        DirectoryDownloadJob directory,
        DirectoryTransferWorkItem item)
    {
        var child = item.Job;

        try
        {
            if (directory.Retry?.RestoreOrDefer(child, item.Target) == true) return;
            await context.Runtime.WithJobSlot(child.Cts!.Token, async () =>
            {
                var outcome = await downloads.ProcessRemoteDownload(child, context.Ctx(directory),
                    directory, directory.Cts!.Token, item.OutputPath);
                JobOutcomeCommitter.Commit(child, outcome);
            });
        }
        catch (OperationCanceledException) when (child.Cts?.IsCancellationRequested == true)
        {
            var source = child.CancellationSource != JobCancellationSource.None
                ? child.CancellationSource
                : directory.CancellationSource != JobCancellationSource.None
                    ? directory.CancellationSource
                    : JobCancellationSource.ParentJob;
            JobOutcomeCommitter.Commit(child, JobOutcome.Cancelled(source));
        }
        catch (Exception ex)
        {
            JobOutcomeCommitter.Commit(child, JobOutcome.Failed(
                JobFailureReason.AllDownloadsFailed,
                ex.Message,
                Diagnostics.ExceptionText.Detail(ex)));
        }
        finally
        {
            context.Events.RaiseJobExecutionCompleted(child);
        }
    }

    internal static JobOutcome GroupOutcome(IReadOnlyList<FileDownloadJob> children)
    {
        int successful = children.Count(child => child.IsSuccessfulTerminal);
        int cancelled = children.Count(child => child.FailureReason == JobFailureReason.Cancelled);
        int unsuccessful = children.Count - successful;

        if (successful == children.Count)
            return JobOutcome.Done();
        if (successful > 0)
            return JobOutcome.PartialSuccess("Some directory files completed and some failed or were cancelled.");
        if (cancelled == children.Count)
        {
            var source = children
                .Select(child => child.CancellationSource)
                .FirstOrDefault(source => source != JobCancellationSource.None);
            return JobOutcome.Cancelled(source == JobCancellationSource.None
                ? JobCancellationSource.ParentJob
                : source);
        }

        return JobOutcome.Failed(
            JobFailureReason.ChildJobsFailed,
            unsuccessful == 1
                ? "The directory file transfer failed."
                : $"All {unsuccessful} directory file transfers failed or were skipped.");
    }
}
