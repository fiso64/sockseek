using Sockseek.Api;

namespace Tests;

internal static class TestJobSummary
{
    public static JobSummaryDto Create(ServerJobKind kind = ServerJobKind.Generic, Guid jobId = default,
        Guid workflowId = default, int displayId = 0, long revision = 0,
        JobLifecycleState state = JobLifecycleState.Pending,
        JobActivityPhase phase = JobActivityPhase.None,
        JobTerminalOutcome outcome = JobTerminalOutcome.None,
        string? name = null, string? query = null, Guid? parentId = null)
        => new(jobId, revision,
            new(displayId, workflowId, kind, name, query, [], ServerPrintOption.None, null, ServerJobRole.UserRoot, null),
            new(state, phase, null, outcome, JobSkipReason.None, null, null, false, JobCancellationSource.None, []),
            new(null, null), new(parentId, null, null));
}
