using Sockseek.Api;

namespace Tests;

internal static class TestJobSummary
{
    public static JobSummaryDto Create(ServerJobKind kind = ServerJobKind.Generic, Guid jobId = default,
        Guid workflowId = default, int displayId = 0, long revision = 0,
        ServerJobLifecycleState state = ServerJobLifecycleState.Pending,
        ServerJobActivityPhase phase = ServerJobActivityPhase.None,
        ServerJobTerminalOutcome outcome = ServerJobTerminalOutcome.None,
        string? name = null, string? query = null, Guid? parentId = null)
        => new(jobId, revision,
            new(displayId, workflowId, kind, name, query, [], ServerPrintOption.None, null, ServerJobRole.UserRoot, null),
            new(state, phase, null, outcome, ServerJobSkipReason.None, null, null, false, ServerJobCancellationSource.None, []),
            new(null, null), new(parentId, null, null));
}
