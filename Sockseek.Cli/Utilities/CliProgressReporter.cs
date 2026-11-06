using Sockseek.Api;
using Sockseek.Core;

namespace Sockseek.Cli;

/// <summary>
/// Projects the shared daemon client store into the terminal renderer. Replicated
/// state is always replaced as one coherent render model; activity may only add a
/// temporary status label or a log message.
/// </summary>
public class CliProgressReporter
{
    private readonly CliOutputController output;
    private readonly object renderGate = new();
    private readonly Dictionary<Guid, string> activityStatuses = [];
    private readonly Dictionary<Guid, TransferMetric> transferMetrics = [];
    private readonly HashSet<Guid> albumAttemptWarnings = [];
    private Dictionary<Guid, JobSummaryDto> previousLiveJobs = [];
    private int retiredCompleted;
    private int retiredFailed;
    private bool isPaused;
    private DateTimeOffset? lastPlainRateLimit;

    private sealed class TransferMetric
    {
        public long LastBytes;
        public long LastObservedBytes = -1;
        public long LastObservedAtTicks;
        public long? SpeedBytesPerSecond;
    }

    private bool LiveMode => output.UsesLiveRendering;

    public bool UsesLiveRendering => LiveMode;

    public bool IsPaused
    {
        get => isPaused;
        set
        {
            isPaused = value;
            output.IsPaused = value;
        }
    }

    public CliProgressReporter(CliSettings cli)
        : this(cli, null)
    {
    }

    internal CliProgressReporter(CliSettings cli, CliOutputController? output)
    {
        this.output = output ?? CliOutputController.CreateDetached(cli);
    }

    public void Stop(bool printSummary = true)
        => output.StopLiveRendering(printSummary);

    public void ReportSyntheticJobFailure(
        int displayId,
        string jobType,
        string name,
        string failureReason)
    {
        string message = $"failed [{failureReason}]: {name}";
        output.WriteOutput(new CliOutputEvent.JobLog(
            new TerminalLogLine(
                TerminalLogKind.JobFailed,
                $"cli:{displayId}",
                displayId,
                jobType,
                message)));
    }

    public void ReportClientError(string message)
        => output.WriteOutput(new CliOutputEvent.ProcessLog(
            new TerminalProcessLogLine(
                LogLevel.Error,
                "cli",
                message,
                CliProcessLogPresentation.Decorated)));

    internal void Attach(ICliBackend backend)
    {
        backend.StateUpdated += update =>
        {
            if (update.Status == DaemonClientApplyStatus.Applied)
                Reconcile(backend.ClientStore, update);
        };

        backend.ActivityReceived += activity =>
            HandleActivity(backend.ClientStore, activity);
    }

    private void HandleActivity(
        DaemonClientStore store,
        ActivityEventDto activity)
    {
        var summary = activity.JobId is Guid jobId ? store.GetJob(jobId) : null;
        if (summary?.LifecycleState == ServerJobLifecycleState.Terminal
            && activity.Payload is not DiagnosticActivityDto)
        {
            return;
        }

        bool renderChanged = false;
        lock (renderGate)
        {
            switch (activity.Payload)
            {
                case JobStatusActivityDto status when summary != null:
                    activityStatuses[summary.JobId] = status.Status;
                    renderChanged = true;
                    break;
                case ExtractionStartedActivityDto when summary != null:
                    activityStatuses[summary.JobId] = "extracting";
                    renderChanged = true;
                    break;
                case DownloadAttemptFailedActivityDto failure:
                    ReportDownloadAttemptFailed(summary, failure);
                    break;
            }
        }

        if (renderChanged)
            Reconcile(store, update: null);
    }

    internal TerminalRenderState Reconcile(
        DaemonClientStore store,
        DaemonClientUpdate? update)
    {
        lock (renderGate)
        {
            var state = store.GetLiveStateView();
            var currentJobs = state.Jobs.ToDictionary(job => job.JobId);
            var relationshipContext = new Dictionary<Guid, JobSummaryDto>(
                previousLiveJobs);
            foreach (var changed in update?.ChangedJobs ?? [])
                relationshipContext[changed.JobId] = changed;
            foreach (var current in state.Jobs)
                relationshipContext[current.JobId] = current;

            RetireObservedTerminalJobs(
                currentJobs,
                relationshipContext,
                update?.ChangedJobs ?? []);

            if (update?.IsSnapshot == true)
                activityStatuses.Clear();
            else
            {
                foreach (var changed in update?.ChangedJobs ?? [])
                    activityStatuses.Remove(changed.JobId);
                RemoveMissingKeys(activityStatuses, currentJobs.Keys);
            }
            albumAttemptWarnings.RemoveWhere(parentId =>
                !currentJobs.ContainsKey(parentId));

            UpdateTransferMetrics(state.Transfers);

            var renderState = ProjectState(
                state,
                activityStatuses,
                transferMetrics,
                retiredCompleted,
                retiredFailed);

            if (state.Jobs.Any(job =>
                    ShouldStartLiveRenderingForSummary(
                        job,
                        CliJobStatusPresenter.ForSummary(job))))
            {
                output.StartLiveRenderingIfNeeded();
            }

            output.ReplaceRenderState(renderState);
            ReportSearchRateLimit(state.Daemon?.SearchRateLimitResetsAtUtc);
            previousLiveJobs = currentJobs;
            return renderState;
        }
    }

    private void RetireObservedTerminalJobs(
        IReadOnlyDictionary<Guid, JobSummaryDto> currentJobs,
        IReadOnlyDictionary<Guid, JobSummaryDto> relationshipContext,
        IReadOnlyList<JobSummaryDto> changedJobs)
    {
        var retired = previousLiveJobs.Values
            .Where(job =>
                job.LifecycleState == ServerJobLifecycleState.Terminal
                && !currentJobs.ContainsKey(job.JobId))
            .Concat(changedJobs.Where(job =>
                job.LifecycleState == ServerJobLifecycleState.Terminal
                && !currentJobs.ContainsKey(job.JobId)))
            .DistinctBy(job => job.JobId);

        foreach (var job in retired)
        {
            if (!IsCountedJob(job, relationshipContext))
                continue;

            var category = CliJobStatusPresenter.ForSummary(job).Category;
            if (category == CliJobStatusCategory.Succeeded)
                retiredCompleted++;
            else if (category == CliJobStatusCategory.Failed)
                retiredFailed++;
        }
    }

    private void UpdateTransferMetrics(IReadOnlyList<TransferStateDto> transfers)
    {
        var currentIds = transfers.Select(transfer => transfer.TransferId).ToHashSet();
        RemoveMissingKeys(transferMetrics, currentIds);

        long now = DateTimeOffset.UtcNow.UtcTicks;
        foreach (var transfer in transfers)
        {
            var metric = transferMetrics.GetValueOrDefault(transfer.TransferId);
            if (metric == null)
            {
                transferMetrics[transfer.TransferId] = new TransferMetric
                {
                    LastBytes = transfer.Progress.BytesTransferred,
                    LastObservedBytes = transfer.Progress.BytesTransferred,
                    LastObservedAtTicks = now,
                };
                continue;
            }

            long bytes = transfer.Progress.BytesTransferred;
            if (bytes == metric.LastObservedBytes)
                continue;

            long elapsed = now - metric.LastObservedAtTicks;
            if (bytes < metric.LastBytes)
            {
                metric.SpeedBytesPerSecond = null;
            }
            else if (elapsed >= TimeSpan.TicksPerMillisecond * 500)
            {
                long instant = (bytes - metric.LastBytes)
                    * TimeSpan.TicksPerSecond
                    / elapsed;
                metric.SpeedBytesPerSecond = metric.SpeedBytesPerSecond is long previous
                    ? (long)(0.4 * instant + 0.6 * previous)
                    : instant;
            }

            metric.LastBytes = bytes;
            metric.LastObservedBytes = bytes;
            metric.LastObservedAtTicks = now;
        }
    }

    internal static TerminalRenderState ProjectState(
        DaemonClientStateView state)
        => ProjectState(
            state,
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, TransferMetric>(),
            retiredCompleted: 0,
            retiredFailed: 0);

    private static TerminalRenderState ProjectState(
        DaemonClientStateView state,
        IReadOnlyDictionary<Guid, string> activityStatuses,
        IReadOnlyDictionary<Guid, TransferMetric> transferMetrics,
        int retiredCompleted,
        int retiredFailed)
    {
        var jobsById = state.Jobs.ToDictionary(job => job.JobId);
        var transfersByJob = state.Transfers
            .Where(transfer => transfer.Identity.JobId.HasValue)
            .GroupBy(transfer => transfer.Identity.JobId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(transfer => transfer.Status.IsTerminal)
                    .ThenByDescending(transfer => transfer.Revision)
                    .First());

        var records = state.Jobs
            .Where(job => IsCountedJob(job, jobsById))
            .OrderBy(job => job.DisplayId)
            .ThenBy(job => job.JobId)
            .Select(job =>
            {
                var status = CliJobStatusPresenter.ForSummary(job);
                return new TerminalJobRecord(
                    job.JobId.ToString(),
                    job.DisplayId,
                    GetJobTypeLabel(job.Kind),
                    status.Label,
                    status.Category,
                    job.ParentJobId?.ToString());
            })
            .ToList();

        var views = state.Jobs
            .Where(job => ShouldShowLiveJob(job, jobsById))
            .OrderBy(job => job.DisplayId)
            .ThenBy(job => job.JobId)
            .Select(job => ProjectJobView(
                job,
                jobsById,
                transfersByJob,
                activityStatuses,
                transferMetrics))
            .ToList();

        return new TerminalRenderState(
            records,
            views,
            retiredCompleted,
            retiredFailed);
    }

    private static JobView ProjectJobView(
        JobSummaryDto job,
        IReadOnlyDictionary<Guid, JobSummaryDto> jobsById,
        IReadOnlyDictionary<Guid, TransferStateDto> transfersByJob,
        IReadOnlyDictionary<Guid, string> activityStatuses,
        IReadOnlyDictionary<Guid, TransferMetric> transferMetrics)
    {
        transfersByJob.TryGetValue(job.JobId, out var transfer);
        var status = CliJobStatusPresenter.ForSummary(job, transfer?.Status.State);
        string state = transfer == null
            && job.LifecycleState != ServerJobLifecycleState.Terminal
            && activityStatuses.TryGetValue(job.JobId, out var activityStatus)
                ? activityStatus
                : status.Label;
        int? percent = TransferPercent(transfer);
        long? speed = transfer != null
            && transferMetrics.TryGetValue(transfer.TransferId, out var metric)
                ? metric.SpeedBytesPerSecond
                : null;

        IReadOnlyList<JobChildView> children = [];
        int? doneChildren = null;
        int? totalChildren = null;
        if (job.Kind == ServerJobKind.Album)
        {
            var songs = jobsById.Values
                .Where(child =>
                    child.ParentJobId == job.JobId
                    && child.Kind == ServerJobKind.Song)
                .OrderBy(child => child.DisplayId)
                .ToList();
            doneChildren = songs.Count(child =>
                child.LifecycleState == ServerJobLifecycleState.Terminal);
            totalChildren = songs.Count;
            children = songs
                .Where(child =>
                    child.LifecycleState != ServerJobLifecycleState.Terminal)
                .Select(child => ProjectAlbumChild(
                    child,
                    transfersByJob,
                    activityStatuses,
                    transferMetrics))
                .ToList();
            if (totalChildren > 0)
                percent = doneChildren * 100 / totalChildren;
        }

        return new JobView(
            job.JobId.ToString(),
            job.DisplayId,
            GetJobTypeLabel(job.Kind),
            DisplayName(job, transfer),
            state,
            Percent: percent,
            SpeedBytesPerSecond: speed,
            DoneChildren: doneChildren,
            TotalChildren: totalChildren,
            DiscoveryRawResultCount: job.DiscoveryRawResultCount,
            Children: children,
            ParentId: GetContainerParentId(job, jobsById));
    }

    private static JobChildView ProjectAlbumChild(
        JobSummaryDto job,
        IReadOnlyDictionary<Guid, TransferStateDto> transfersByJob,
        IReadOnlyDictionary<Guid, string> activityStatuses,
        IReadOnlyDictionary<Guid, TransferMetric> transferMetrics)
    {
        transfersByJob.TryGetValue(job.JobId, out var transfer);
        var status = CliJobStatusPresenter.ForSummary(job, transfer?.Status.State);
        string state = transfer == null
            && activityStatuses.TryGetValue(job.JobId, out var activityStatus)
                ? activityStatus
                : status.Label;
        long? speed = transfer != null
            && transferMetrics.TryGetValue(transfer.TransferId, out var metric)
                ? metric.SpeedBytesPerSecond
                : null;

        return new JobChildView(
            job.JobId.ToString(),
            job.DisplayId,
            state,
            DisplayName(job, transfer),
            TransferPercent(transfer),
            SpeedBytesPerSecond: speed);
    }

    private static bool ShouldShowLiveJob(
        JobSummaryDto job,
        IReadOnlyDictionary<Guid, JobSummaryDto> jobsById)
    {
        var status = CliJobStatusPresenter.ForSummary(job);
        if (status.IsQueued || status.IsTerminal)
            return false;
        if (IsInlineAlbumChild(job, jobsById))
            return false;
        if (IsContainerJobKind(job.Kind))
            return !IsTransparentContainer(job, jobsById);
        return !IsInfrastructureJobKind(job.Kind);
    }

    private static bool IsCountedJob(
        JobSummaryDto job,
        IReadOnlyDictionary<Guid, JobSummaryDto> jobsById)
        => !IsInfrastructureJobKind(job.Kind)
            && !IsInlineAlbumChild(job, jobsById);

    private static bool IsInlineAlbumChild(
        JobSummaryDto job,
        IReadOnlyDictionary<Guid, JobSummaryDto> jobsById)
        => job.Kind == ServerJobKind.Song
            && job.ParentJobId is Guid parentId
            && jobsById.TryGetValue(parentId, out var parent)
            && parent.Kind == ServerJobKind.Album;

    private static bool IsInfrastructureJobKind(ServerJobKind kind)
        => kind is ServerJobKind.Extract
            or ServerJobKind.JobList
            or ServerJobKind.RetrieveFolder
            or ServerJobKind.Aggregate
            or ServerJobKind.AlbumAggregate;

    private static bool IsContainerJobKind(ServerJobKind kind)
        => kind is ServerJobKind.JobList
            or ServerJobKind.Aggregate
            or ServerJobKind.AlbumAggregate
            or ServerJobKind.RemoteDirectory;

    private static bool IsTransparentContainer(
        JobSummaryDto job,
        IReadOnlyDictionary<Guid, JobSummaryDto> jobsById)
        => job.Kind == ServerJobKind.JobList
            && job.ParentJobId is Guid parentId
            && jobsById.TryGetValue(parentId, out var parent)
            && parent.Kind is ServerJobKind.Aggregate
                or ServerJobKind.AlbumAggregate;

    private static string? GetContainerParentId(
        JobSummaryDto job,
        IReadOnlyDictionary<Guid, JobSummaryDto> jobsById)
    {
        if (job.ParentJobId is not Guid parentId
            || !jobsById.TryGetValue(parentId, out var parent))
        {
            return null;
        }

        if (IsTransparentContainer(parent, jobsById)
            && parent.ParentJobId is Guid grandParentId
            && jobsById.TryGetValue(grandParentId, out var grandParent)
            && IsContainerJobKind(grandParent.Kind))
        {
            return grandParentId.ToString();
        }

        return IsContainerJobKind(parent.Kind)
            ? parentId.ToString()
            : null;
    }

    private static string DisplayName(
        JobSummaryDto job,
        TransferStateDto? transfer)
    {
        string name = job.QueryText ?? job.ItemName ?? "";
        if (transfer == null)
            return name;

        string remotePath = transfer.Identity.RemotePath ?? "";
        string remoteName = Path.GetFileName(
            remotePath.Replace('\\', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(remoteName))
            remoteName = remotePath;
        string peer = transfer.Identity.Username ?? "";
        string candidate = string.IsNullOrWhiteSpace(peer)
            ? remoteName
            : string.IsNullOrWhiteSpace(remoteName)
                ? peer
                : $"{peer}\\{remoteName}";
        if (string.IsNullOrWhiteSpace(name))
            return candidate;
        return candidate == name || string.IsNullOrWhiteSpace(candidate)
            ? name
            : $"{name}: {candidate}";
    }

    private static int? TransferPercent(TransferStateDto? transfer)
    {
        if (transfer?.Progress.TotalBytes is not > 0)
            return null;

        return (int)Math.Clamp(
            transfer.Progress.BytesTransferred * 100
                / transfer.Progress.TotalBytes,
            0,
            100);
    }

    private static string GetJobTypeLabel(ServerJobKind kind)
    {
        if (kind == ServerJobKind.RetrieveFolder)
            return "Retrieve Folder";
        if (kind == ServerJobKind.JobList)
            return "Job List";
        if (kind == ServerJobKind.AlbumAggregate)
            return "Album Aggregate";

        string kindText = kind.ToWireString();
        return $"{char.ToUpperInvariant(kindText[0])}{kindText[1..]}";
    }

    private void ReportDownloadAttemptFailed(
        JobSummaryDto? summary,
        DownloadAttemptFailedActivityDto failure)
    {
        string candidate = string.IsNullOrWhiteSpace(failure.Username)
            ? failure.RemotePath ?? ""
            : $"{failure.Username}\\{failure.RemotePath ?? ""}";
        string itemName = summary?.QueryText ?? summary?.ItemName ?? candidate;
        string display = string.IsNullOrWhiteSpace(itemName)
            || itemName == candidate
                ? candidate
                : $"{itemName}: {candidate}";
        string message =
            $"download attempt failed: {display}\n"
            + $"    Output: {failure.OutputPath}\n"
            + $"    Attempt: {failure.Attempt}/{failure.MaxAttempts}\n"
            + $"    {failure.ExceptionType}: {failure.ExceptionMessage}";
        int displayId = summary?.DisplayId ?? failure.DisplayId;
        string jobType = summary == null
            ? "Song"
            : GetJobTypeLabel(summary.Kind);

        if (summary?.ParentJobId is Guid parentId
            && !albumAttemptWarnings.Add(parentId))
        {
            return;
        }

        output.WriteOutput(new CliOutputEvent.JobLog(
            new TerminalLogLine(
                TerminalLogKind.Status,
                $"cli:{displayId}",
                displayId,
                jobType,
                message),
            LogLevel.Warning));
    }

    private void ReportSearchRateLimit(DateTimeOffset? resetsAtUtc)
    {
        if (LiveMode)
        {
            output.SetRateLimited(resetsAtUtc);
            lastPlainRateLimit = resetsAtUtc;
            return;
        }

        if (resetsAtUtc is DateTimeOffset resetsAt
            && resetsAtUtc != lastPlainRateLimit)
        {
            int seconds = Math.Max(
                0,
                (int)Math.Ceiling(
                    (resetsAt - DateTimeOffset.UtcNow).TotalSeconds));
            Printing.WriteLine(
                $"Search rate limit reached, resuming in {seconds}s",
                ConsoleColor.DarkGray);
        }

        lastPlainRateLimit = resetsAtUtc;
    }

    internal static bool ShouldShowStandaloneSummaryInLiveTable(
        JobSummaryDto summary,
        CliJobStatus status)
        => !status.IsQueued
            && !status.IsTerminal
            && !IsInfrastructureJobKind(summary.Kind);

    internal static bool ShouldShowContainerSummaryInLiveTable(
        JobSummaryDto summary,
        CliJobStatus status)
        => !status.IsQueued
            && !status.IsTerminal
            && IsContainerJobKind(summary.Kind);

    internal static bool ShouldStartLiveRenderingForSummary(
        JobSummaryDto summary,
        CliJobStatus status)
        => !IsInfrastructureJobKind(summary.Kind)
            || ShouldShowContainerSummaryInLiveTable(summary, status);

    private static void RemoveMissingKeys<TValue>(
        Dictionary<Guid, TValue> dictionary,
        IEnumerable<Guid> retainedKeys)
    {
        var retained = retainedKeys.ToHashSet();
        foreach (var key in dictionary.Keys
            .Where(key => !retained.Contains(key))
            .ToList())
        {
            dictionary.Remove(key);
        }
    }
}
