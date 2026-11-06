using Sockseek.Core.Jobs;

namespace Sockseek.Core;

internal static class JobOutcomeCommitter
{
    public static void Commit(Job job, JobOutcome outcome)
    {
        if (!outcome.ShouldCommit)
            return;

        ApplyOutcomeDownloadPath(job, outcome);

        if (outcome.LifecycleState == JobLifecycleState.AwaitingSelection)
        {
            job.SetAwaitingSelection();
            return;
        }

        if (outcome.ActivityPhase is { } phase)
        {
            job.UpdateActivity(phase);
            return;
        }

        switch (outcome.TerminalOutcome)
        {
            case JobTerminalOutcome.Succeeded:
                if (job is SongJob song)
                    song.SetDone(outcome.DownloadPath, outcome.ChosenCandidate, outcome.DownloadSource);
                else if (job is FileDownloadJob file)
                    file.SetDone(outcome.DownloadPath);
                else if (job is DirectoryDownloadJob directory)
                    directory.SetDone(outcome.DownloadPath);
                else
                    job.SetDone();
                break;

            case JobTerminalOutcome.Failed:
                job.Fail(outcome.FailureReason, outcome.FailureMessage, outcome.FailureDetail);
                break;

            case JobTerminalOutcome.Cancelled:
                job.SetCancelled(outcome.CancellationSource, outcome.FailureMessage, outcome.FailureDetail);
                break;

            case JobTerminalOutcome.Skipped:
                if (outcome.SkipReason == JobSkipReason.AlreadyExists)
                {
                    if (job is SongJob existingSong)
                        existingSong.SetAlreadyExists(outcome.DownloadPath);
                    else if (job is FileDownloadJob existingFile)
                        existingFile.SetAlreadyExists(outcome.DownloadPath);
                    else if (job is DirectoryDownloadJob existingDirectory)
                        existingDirectory.SetAlreadyExists(outcome.DownloadPath);
                    else
                        job.SetAlreadyExists();
                }
                else
                {
                    job.SetSkipped(outcome.SkipReason, outcome.FailureReason);
                }
                break;

            case JobTerminalOutcome.PartialSuccess:
                job.SetPartialSuccess(outcome.FailureMessage, outcome.CancellationSource);
                break;
        }
    }

    private static void ApplyOutcomeDownloadPath(Job job, JobOutcome outcome)
    {
        if (!outcome.ShouldUpdateDownloadPath)
            return;

        if (job is SongJob song)
            song.DownloadPath = outcome.DownloadPath;
        else if (job is FileDownloadJob file)
            file.DownloadPath = outcome.DownloadPath;
        else if (job is DirectoryDownloadJob directory)
            directory.DownloadPath = outcome.DownloadPath;
    }
}
