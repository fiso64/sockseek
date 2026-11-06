using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Jobs;

namespace Sockseek.Core.Transfers.Downloads.Reporting;

internal sealed class AutoProfileWorkflowReporter
{
    private readonly DownloadEvents events;
    private readonly ConcurrentDictionary<Guid, AutoProfileWorkflowLogState> stateByWorkflow = new();

    public AutoProfileWorkflowReporter(DownloadEvents events)
    {
        this.events = events;
    }

    internal int RetainedWorkflowCount => stateByWorkflow.Count;

    public void ObservePreparedRoot(Job preparedRoot)
    {
        // Auto-profile logs are edge-triggered per workflow: announce profile names the
        // first time they appear on a real job, then leave detailed counts to debug logs.
        var state = stateByWorkflow.GetOrAdd(
            preparedRoot.WorkflowId,
            _ => new AutoProfileWorkflowLogState());
        var newlyObservedProfiles = new List<string>();
        Job? firstTriggeringJob = null;

        lock (state.Gate)
        {
            foreach (var job in EnumerateAutoProfileLogJobs(preparedRoot))
            {
                var kindLabel = AutoProfileLogKind(job);
                if (kindLabel == null)
                    continue;

                var profiles = job.Config?.AppliedAutoProfiles
                    .Where(profile => !string.IsNullOrWhiteSpace(profile))
                    .OrderBy(profile => profile, StringComparer.Ordinal)
                    .ToList();
                if (profiles == null || profiles.Count == 0)
                    continue;

                if (!state.CountedJobIds.Add(job.Id))
                    continue;

                foreach (var profile in profiles)
                {
                    if (state.ObservedProfileNames.Add(profile))
                    {
                        newlyObservedProfiles.Add(profile);
                        firstTriggeringJob ??= job;
                    }

                    if (!state.CountsByProfileAndKind.TryGetValue(profile, out var countsByKind))
                    {
                        countsByKind = new Dictionary<string, int>(StringComparer.Ordinal);
                        state.CountsByProfileAndKind[profile] = countsByKind;
                    }

                    countsByKind[kindLabel] = countsByKind.GetValueOrDefault(kindLabel) + 1;
                }
            }
        }

        if (newlyObservedProfiles.Count > 0)
        {
            var message = $"Auto profiles active: {string.Join(", ", newlyObservedProfiles)}";
            events.RaiseWorkflowMessage(
                preparedRoot.WorkflowId,
                LogLevel.Information,
                null,
                message);

            if (firstTriggeringJob != null)
                events.RaiseJobMessage(firstTriggeringJob, LogLevel.Debug, null, message);
        }
    }

    public void EmitFinalSummary(Job rootJob)
        => EmitFinalSummary(rootJob.WorkflowId);

    public void EmitFinalSummary(Guid workflowId)
    {
        if (!stateByWorkflow.TryRemove(workflowId, out var state))
            return;

        string? summary;
        lock (state.Gate)
        {
            if (state.FinalSummaryEmitted || state.CountsByProfileAndKind.Count == 0)
                return;

            state.FinalSummaryEmitted = true;
            summary = FormatAutoProfileSummary(state.CountsByProfileAndKind);
        }

        if (!string.IsNullOrWhiteSpace(summary))
            events.RaiseWorkflowMessage(workflowId, LogLevel.Debug, null, $"Auto profiles applied: {summary}");
    }

    public void Retire(Guid workflowId)
        => stateByWorkflow.TryRemove(workflowId, out _);

    private static IEnumerable<Job> EnumerateAutoProfileLogJobs(Job root)
    {
        yield return root;

        switch (root)
        {
            case JobList list:
                foreach (var child in list.Jobs)
                {
                    foreach (var descendant in EnumerateAutoProfileLogJobs(child))
                        yield return descendant;
                }
                break;

            case ExtractJob { Result: { } result }:
                foreach (var descendant in EnumerateAutoProfileLogJobs(result))
                    yield return descendant;
                break;
        }
    }

    private static string? AutoProfileLogKind(Job job) => job switch
    {
        SongJob => "song",
        AlbumJob => "album",
        AggregateJob => "aggregate",
        AlbumAggregateJob => "album aggregate",
        SearchJob => "search",
        _ => null,
    };

    private static string FormatAutoProfileSummary(Dictionary<string, Dictionary<string, int>> countsByProfileAndKind)
        => string.Join(", ",
            countsByProfileAndKind
                .OrderBy(profile => profile.Key, StringComparer.Ordinal)
                .Select(profile =>
                    $"{profile.Key} ({string.Join(", ", profile.Value.OrderBy(kind => kind.Key, StringComparer.Ordinal).Select(FormatAutoProfileKindCount))})"));

    private static string FormatAutoProfileKindCount(KeyValuePair<string, int> count)
        => $"{count.Value} {PluralizeAutoProfileKind(count.Key, count.Value)}";

    private static string PluralizeAutoProfileKind(string kind, int count)
        => count == 1
            ? kind
            : kind switch
            {
                "album aggregate" => "album aggregates",
                "search" => "searches",
                _ => $"{kind}s",
            };

    private sealed class AutoProfileWorkflowLogState
    {
        public object Gate { get; } = new();
        public HashSet<Guid> CountedJobIds { get; } = [];
        public HashSet<string> ObservedProfileNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, int>> CountsByProfileAndKind { get; } = new(StringComparer.Ordinal);
        public bool FinalSummaryEmitted { get; set; }
    }
}
