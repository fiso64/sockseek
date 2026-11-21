using Sockseek.Api;

namespace Sockseek.Cli;

internal readonly record struct DownloadRunSummary(int Succeeded, int Failed, int Skipped, Program.CliExitCode ExitCode)
{
    public static DownloadRunSummary FromJobs(IReadOnlyList<JobSummaryDto> jobs)
    {
        var directories = new HashSet<Guid>();
        var superseded = new HashSet<Guid>();
        foreach (var job in jobs)
        {
            if (job.Display.Kind is ServerJobKind.Album or ServerJobKind.RemoteDirectory)
                directories.Add(job.JobId);
            if (job.Relationships.SourceJobId is Guid source)
                superseded.Add(source);
        }

        int succeeded = 0, failed = 0, skipped = 0;
        var exitCode = Program.CliExitCode.Success;
        foreach (var job in jobs)
        {
            // The directory outcome already accounts for its files. A follow-up
            // similarly owns the outcome of the search that produced it.
            if (superseded.Contains(job.JobId)
                || job.Relationships.ParentJobId is Guid parent && directories.Contains(parent))
                continue;

            bool count = job.Display.PrintOption == ServerPrintOption.None && job.Display.Kind is not
                (ServerJobKind.Extract or ServerJobKind.JobList or ServerJobKind.RetrieveFolder
                or ServerJobKind.SongGroups or ServerJobKind.AlbumGroups);
            switch (job.Lifecycle.TerminalOutcome)
            {
                case ServerJobTerminalOutcome.Succeeded:
                case ServerJobTerminalOutcome.Skipped when job.Lifecycle.SkipReason == ServerJobSkipReason.AlreadyExists:
                    if (count) succeeded++;
                    break;
                case ServerJobTerminalOutcome.Skipped when job.Lifecycle.SkipReason == ServerJobSkipReason.Manual:
                    if (count) skipped++;
                    break;
                case ServerJobTerminalOutcome.Cancelled:
                    exitCode = Program.CliExitCode.Cancelled;
                    if (count) failed++;
                    break;
                case ServerJobTerminalOutcome.Failed:
                case ServerJobTerminalOutcome.PartialSuccess:
                case ServerJobTerminalOutcome.Skipped:
                    if (exitCode == Program.CliExitCode.Success) exitCode = Program.CliExitCode.WorkFailed;
                    if (count) failed++;
                    break;
            }
        }

        return new(succeeded, failed, skipped, exitCode);
    }

    public void Print()
    {
        if (Succeeded <= 1 && Failed == 0 && Skipped == 0)
            return;

        string skipped = Skipped > 0 ? $", {Skipped} skipped" : "";
        Printing.WriteLine();
        Printing.WriteLine($"Completed: {Succeeded} succeeded{skipped}, {Failed} failed.");
    }
}
