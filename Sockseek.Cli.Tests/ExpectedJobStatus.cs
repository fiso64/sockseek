namespace Tests.Cli;

internal enum ExpectedJobStatus
{
    Pending,
    Succeeded,
    Failed,
    AlreadyExists,
    NotFoundLastTime,
    Skipped,
    Searching,
    Downloading,
    Extracting,
    RunningChildren,
    AwaitingSelection,
}
