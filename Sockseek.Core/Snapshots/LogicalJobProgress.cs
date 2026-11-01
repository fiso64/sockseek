using Sockseek.Core.Jobs;

namespace Sockseek.Core.Snapshots;

/// <summary>
/// One configured member of a job list represents its executable result after
/// a successful auto-processed extraction, not the completed extraction step.
/// Both runtime snapshots and daemon projections use this rule.
/// </summary>
public static class LogicalJobProgress
{
    public readonly record struct Outcome(
        JobLifecycleState Lifecycle,
        JobTerminalOutcome Terminal,
        JobSkipReason Skip);

    public static JobOutcomeSummary SummarizeRuntime(IEnumerable<Job> members)
    {
        var summary = new JobOutcomeSummary();
        foreach (Job member in members)
        {
            Outcome outcome = FromRuntime(member);
            summary = summary.Include(outcome.Lifecycle, outcome.Terminal, outcome.Skip);
        }
        return summary;
    }

    public static Outcome FromRuntime(Job member)
    {
        HashSet<Guid>? visited = null;
        Job current = member;
        while (current is ExtractJob extract
            && extract.AutoProcessResult
            && extract.LifecycleState == JobLifecycleState.Terminal
            && extract.TerminalOutcome == JobTerminalOutcome.Succeeded)
        {
            visited ??= [];
            if (!visited.Add(current.Id))
                throw new InvalidDataException("An extraction result relationship contains a cycle.");
            if (extract.Result == null)
                return Pending;
            current = extract.Result;
        }
        return new(current.LifecycleState, current.TerminalOutcome, current.SkipReason);
    }

    public static Outcome FromSnapshot(
        JobSnapshot member,
        Func<Guid, JobSnapshot?> getJob)
    {
        HashSet<Guid>? visited = null;
        JobSnapshot current = member;
        while (current.Payload is ExtractJobSnapshotPayload extract
            && extract.AutoProcessResult
            && current.LifecycleState == JobLifecycleState.Terminal
            && current.TerminalOutcome == JobTerminalOutcome.Succeeded)
        {
            visited ??= [];
            if (!visited.Add(current.Id))
                throw new InvalidDataException("An extraction result relationship contains a cycle.");
            if (extract.ResultJobId is not Guid resultId
                || getJob(resultId) is not JobSnapshot result)
            {
                return Pending;
            }
            current = result;
        }
        return new(current.LifecycleState, current.TerminalOutcome, current.SkipReason);
    }

    private static Outcome Pending => new(
        JobLifecycleState.Pending,
        JobTerminalOutcome.None,
        JobSkipReason.None);
}
