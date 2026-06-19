namespace Tests.ProgressReporterTests;

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
    RunningOnComplete,
    Extracting,
    RunningChildren,
    AwaitingSelection,
}
