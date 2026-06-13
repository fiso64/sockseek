using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using Sockseek.Api;

namespace Sockseek.Server;

public sealed class EngineStateStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Job> jobs = [];
    private readonly Dictionary<Guid, JobRecord> records = [];
    private readonly Dictionary<Guid, Guid?> parentJobIds = [];
    private readonly Dictionary<Guid, Guid> resultJobIds = [];
    private readonly Dictionary<Guid, Guid> sourceJobIds = [];
    private readonly HashSet<Guid> infrastructureFailedJobs = [];
    private readonly HashSet<Guid> executionCompletedJobs = [];
    private readonly Dictionary<Guid, TransferStates> songTransferStates = [];

    public event Action<JobSummaryDto>? JobUpserted;
    public event Action<WorkflowSummaryDto>? WorkflowUpserted;
    public event Action<SearchUpdatedDto>? SearchUpdated;

    public void AttachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered += OnJobRegistered;
        engine.Events.JobResultCreated += OnJobResultCreated;
        engine.Events.JobStateChanged += OnJobStateChanged;
        engine.Events.JobExecutionCompleted += OnJobExecutionCompleted;
        engine.Events.DownloadStarted += OnNestedSongDownloadStarted;
        engine.Events.DownloadStateChanged += OnDownloadStateChanged;
    }

    public void DetachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered -= OnJobRegistered;
        engine.Events.JobResultCreated -= OnJobResultCreated;
        engine.Events.JobStateChanged -= OnJobStateChanged;
        engine.Events.JobExecutionCompleted -= OnJobExecutionCompleted;
        engine.Events.DownloadStarted -= OnNestedSongDownloadStarted;
        engine.Events.DownloadStateChanged -= OnDownloadStateChanged;
    }

    public JobSummaryDto? GetJobSummary(Guid jobId)
    {
        lock (gate)
        {
            return records.TryGetValue(jobId, out var record)
                ? record.Summary
                : null;
        }
    }

    public TJob? GetJob<TJob>(Guid jobId)
        where TJob : Job
    {
        lock (gate)
            return jobs.TryGetValue(jobId, out var job) ? job as TJob : null;
    }

    public JobDetailDto? GetJobDetail(Guid jobId)
    {
        lock (gate)
        {
            if (jobs.TryGetValue(jobId, out var job))
                UpdateJobRecord(job);

            if (!records.TryGetValue(jobId, out var record))
                return null;

            var children = records.Values
                .Where(candidate => candidate.ParentJobId == jobId)
                .OrderBy(candidate => candidate.Summary.DisplayId)
                .ToList();

            JobPayloadDto payload = record.Payload;
            if (payload is AlbumJobPayloadDto albumPayload)
            {
                var tracks = children
                    .Select(c => jobs.TryGetValue(c.Id, out var job) ? job as SongJob : null)
                    .OfType<SongJob>()
                    .Select(s => ToSongJobPayloadDto(s, songTransferStates.TryGetValue(s.Id, out var ts) ? ts.ToString() : null))
                    .ToList();
                if (tracks.Count > 0)
                    payload = albumPayload with { Tracks = tracks };
            }

            return new JobDetailDto(record.Summary, payload, children.Select(c => c.Summary).ToList());
        }
    }

    public IReadOnlyList<JobSummaryDto> GetJobs(JobQuery query)
    {
        lock (gate)
        {
            IEnumerable<JobRecord> filtered = records.Values;

            if (query.WorkflowId.HasValue)
                filtered = filtered.Where(record => record.WorkflowId == query.WorkflowId.Value);

            if (query.Kind.HasValue)
                filtered = filtered.Where(record => record.Summary.Kind == query.Kind.Value);

            if (query.LifecycleState.HasValue)
                filtered = filtered.Where(record => record.Summary.LifecycleState == query.LifecycleState.Value);

            if (query.TerminalOutcome.HasValue)
                filtered = filtered.Where(record => record.Summary.TerminalOutcome == query.TerminalOutcome.Value);

            if (query.SkipReason.HasValue)
                filtered = filtered.Where(record => record.Summary.SkipReason == query.SkipReason.Value);

            var summaries = filtered
                .OrderBy(record => record.Summary.DisplayId)
                .Where(record => query.IncludeAll || IsDefaultRoot(record))
                .Select(record => record.Summary)
                .ToList();

            return summaries;
        }
    }

    public IReadOnlyList<WorkflowSummaryDto> GetWorkflows()
    {
        lock (gate)
        {
            return records.Values
                .GroupBy(record => record.WorkflowId)
                .OrderBy(group => group.Min(record => record.Summary.DisplayId))
                .Select(BuildWorkflowSummary)
                .ToList();
        }
    }

    public WorkflowDetailDto? GetWorkflow(Guid workflowId, bool includeAll = false)
    {
        lock (gate)
        {
            var workflowJobs = records.Values
                .Where(record => record.WorkflowId == workflowId)
                .OrderBy(record => record.Summary.DisplayId)
                .ToList();

            if (workflowJobs.Count == 0)
                return null;

            var summary = BuildWorkflowSummary(workflowJobs.GroupBy(record => record.WorkflowId).Single());
            var jobSummaries = workflowJobs
                .Where(record => includeAll || IsDefaultRoot(record))
                .Select(record => record.Summary)
                .ToList();
            return new WorkflowDetailDto(summary, jobSummaries);
        }
    }

    public WorkflowTreeDto? GetWorkflowTree(Guid workflowId)
    {
        lock (gate)
        {
            var workflowJobs = records.Values
                .Where(record => record.WorkflowId == workflowId)
                .ToList();

            if (workflowJobs.Count == 0)
                return null;

            var summary = BuildWorkflowSummary(workflowJobs.GroupBy(record => record.WorkflowId).Single());
            return new WorkflowTreeDto(summary, BuildWorkflowJobTree(workflowJobs));
        }
    }

    public ServerStatusDto GetStatistics()
    {
        lock (gate)
        {
            int totalJobCount = records.Count;
            int activeJobCount = records.Values.Count(IsActiveRecord);
            int totalWorkflowCount = records.Values.Select(record => record.WorkflowId).Distinct().Count();
            int activeWorkflowCount = records.Values
                .Where(IsActiveRecord)
                .Select(record => record.WorkflowId)
                .Distinct()
                .Count();

            return new ServerStatusDto(
                new SoulseekClientStatusDto("None", [], false),
                totalJobCount,
                activeJobCount,
                totalWorkflowCount,
                activeWorkflowCount,
                0);
        }
    }

    public void MarkActiveJobsInfrastructureFailed(string reason, string? detail = null)
    {
        List<JobSummaryDto> changedJobs;
        List<WorkflowSummaryDto> changedWorkflows;
        lock (gate)
        {
            foreach (var job in jobs.Values.Where(IsActiveJob))
            {
                job.Fail(JobFailureReason.Other, "Infrastructure failure: " + reason, detail);
                infrastructureFailedJobs.Add(job.Id);
                UpdateJobRecord(job);
            }

            changedJobs = records.Values
                .Where(record => infrastructureFailedJobs.Contains(record.Id))
                .OrderBy(record => record.Summary.DisplayId)
                .Select(record => record.Summary)
                .ToList();
            changedWorkflows = changedJobs
                .Select(job => job.WorkflowId)
                .Distinct()
                .Select(BuildWorkflowSummary)
                .ToList();
        }

        PublishJobAndWorkflowUpserts(changedJobs, changedWorkflows);
    }

    public static ServerJobKind GetJobKind(Job job) => job switch
    {
        ExtractJob => ServerJobKind.Extract,
        SearchJob => ServerJobKind.Search,
        SongJob => ServerJobKind.Song,
        AlbumJob => ServerJobKind.Album,
        JobList => ServerJobKind.JobList,
        RetrieveFolderJob => ServerJobKind.RetrieveFolder,
        AggregateJob => ServerJobKind.Aggregate,
        AlbumAggregateJob => ServerJobKind.AlbumAggregate,
        _ => ServerJobKind.Generic,
    };

    public void SetSourceJob(Guid jobId, Guid sourceJobId)
    {
        lock (gate)
        {
            sourceJobIds[jobId] = sourceJobId;
            if (jobs.TryGetValue(jobId, out var job))
                UpdateJobRecord(job);
        }
    }

    private void OnJobRegistered(Job job, Job? parent)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            jobs[job.Id] = job;
            parentJobIds[job.Id] = parent?.Id;
            summary = UpdateJobRecord(job).Summary;
            workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        }

        if (job is SearchJob searchJob)
            SubscribeToSearchJob(searchJob);

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
    }

    private void OnJobResultCreated(ExtractJob job, Job result)
    {
        List<JobSummaryDto> changedJobs = [];
        WorkflowSummaryDto? workflowSummary = null;
        lock (gate)
        {
            resultJobIds[job.Id] = result.Id;

            if (jobs.TryGetValue(job.Id, out var extractJob))
                changedJobs.Add(UpdateJobRecord(extractJob).Summary);
            if (jobs.TryGetValue(result.Id, out var resultJob))
                changedJobs.Add(UpdateJobRecord(resultJob).Summary);
            else if (job.AutoProcessResult)
            {
                jobs[result.Id] = result;
                parentJobIds[result.Id] = parentJobIds.GetValueOrDefault(job.Id);
                changedJobs.Add(UpdateJobRecord(result).Summary);
            }

            if (records.Values.Any(candidate => candidate.WorkflowId == job.WorkflowId))
                workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        }

        PublishJobAndWorkflowUpserts(changedJobs, workflowSummary != null ? [workflowSummary] : []);
    }

    private void OnJobStateChanged(Job job)
    {
        List<JobSummaryDto> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
        lock (gate)
        {
            if (IsRunningOrPending(job))
                executionCompletedJobs.Remove(job.Id);

            if (!jobs.ContainsKey(job.Id))
            {
                var containingRecords = UpdateRecordsContainingJob(job.Id);
                summaries = containingRecords.Select(record => record.Summary).ToList();
                workflowSummaries = containingRecords
                    .Select(record => record.WorkflowId)
                    .Distinct()
                    .Select(BuildWorkflowSummary)
                    .ToList();
            }
            else
            {
                var changedRecords = UpdateRecordsContainingJob(job.Id);
                changedRecords.Add(UpdateJobRecord(job));
                summaries = changedRecords
                    .DistinctBy(record => record.Id)
                    .Select(record => record.Summary)
                    .ToList();
                workflowSummaries = [BuildWorkflowSummary(job.WorkflowId)];
            }
        }

        if (summaries.Count == 0 && workflowSummaries.Count == 0)
            return;

        PublishJobAndWorkflowUpserts(summaries, workflowSummaries);
    }

    private void OnJobExecutionCompleted(Job job)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            if (!jobs.ContainsKey(job.Id))
                return;

            executionCompletedJobs.Add(job.Id);
            summary = UpdateJobRecord(job).Summary;
            workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        }

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
    }

    private void OnDownloadStateChanged(SongJob song, TransferStates state)
    {
        lock (gate)
            songTransferStates[song.Id] = state;
    }


    private void OnNestedSongDownloadStarted(SongJob song, FileCandidate _) => OnNestedSongChanged(song);

    private void OnNestedSongChanged(SongJob song)
    {
        List<JobSummaryDto> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
        lock (gate)
        {
            var containingRecords = UpdateRecordsContainingJob(song.Id);
            summaries = containingRecords.Select(record => record.Summary).ToList();
            workflowSummaries = containingRecords
                .Select(record => record.WorkflowId)
                .Distinct()
                .Select(BuildWorkflowSummary)
                .ToList();
        }

        if (summaries.Count == 0 && workflowSummaries.Count == 0)
            return;

        PublishJobAndWorkflowUpserts(summaries, workflowSummaries);
    }

    private void PublishJobAndWorkflowUpserts(
        IReadOnlyList<JobSummaryDto> jobSummaries,
        IReadOnlyList<WorkflowSummaryDto> workflowSummaries)
    {
        foreach (var summary in jobSummaries)
            JobUpserted?.Invoke(summary);

        foreach (var workflow in workflowSummaries)
            WorkflowUpserted?.Invoke(workflow);
    }

    private void SubscribeToSearchJob(SearchJob searchJob)
    {
        searchJob.Session.RawResultAdded += OnSearchRawResultAdded;
        searchJob.Session.Completed += OnSearchCompleted;
    }

    private void OnSearchRawResultAdded(SearchSession session, SearchRawResult rawResult)
    {
        SearchJob? searchJob;
        lock (gate)
        {
            searchJob = jobs.Values
                .OfType<SearchJob>()
                .FirstOrDefault(job => ReferenceEquals(job.Session, session));

            if (searchJob != null)
                UpdateJobRecord(searchJob);
        }

        if (searchJob == null)
            return;

        SearchUpdated?.Invoke(new SearchUpdatedDto(
            searchJob.Id,
            searchJob.WorkflowId,
            rawResult.Revision,
            searchJob.ResultCount,
            false));
    }

    private void OnSearchCompleted(SearchSession session)
    {
        SearchJob? searchJob;
        lock (gate)
        {
            searchJob = jobs.Values
                .OfType<SearchJob>()
                .FirstOrDefault(job => ReferenceEquals(job.Session, session));

            if (searchJob != null)
                UpdateJobRecord(searchJob);
        }

        if (searchJob == null)
            return;

        SearchUpdated?.Invoke(new SearchUpdatedDto(
            searchJob.Id,
            searchJob.WorkflowId,
            searchJob.Revision,
            searchJob.ResultCount,
            searchJob.IsComplete));
    }

    private WorkflowSummaryDto BuildWorkflowSummary(Guid workflowId)
        => BuildWorkflowSummary(records.Values.Where(record => record.WorkflowId == workflowId).GroupBy(record => record.WorkflowId).Single());

    private WorkflowSummaryDto BuildWorkflowSummary(IGrouping<Guid, JobRecord> workflow)
    {
        var workflowJobs = workflow.OrderBy(record => record.Summary.DisplayId).ToList();
        var roots = workflowJobs
            .Where(IsDefaultRoot)
            .OrderBy(record => record.Summary.DisplayId)
            .Select(record => record.Id)
            .ToList();

        string title = workflowJobs.FirstOrDefault(record => !string.IsNullOrWhiteSpace(record.Summary.ItemName))?.Summary.ItemName
            ?? workflowJobs.First().Summary.QueryText
            ?? workflowJobs.First().Summary.Kind.ToWireString();

        int active = workflowJobs.Count(IsActiveRecord);
        int failed = workflowJobs.Count(IsFailedRecord);
        int completed = workflowJobs.Count(record => !IsActiveRecord(record));

        var state = active > 0 ? ServerWorkflowState.Active
            : failed > 0 ? ServerWorkflowState.Failed
            : ServerWorkflowState.Completed;

        return new WorkflowSummaryDto(workflow.Key, title, state, roots, active, failed, completed);
    }

    private static List<WorkflowJobNodeDto> BuildWorkflowJobTree(IReadOnlyList<JobRecord> sourceRecords)
    {
        var visibleRecords = sourceRecords
            .OrderBy(record => record.Summary.DisplayId)
            .ToList();

        var visibleIds = visibleRecords.Select(record => record.Id).ToHashSet();
        var childrenByParentId = new Dictionary<Guid, List<JobRecord>>();
        var roots = new List<JobRecord>();

        foreach (var record in visibleRecords)
        {
            if (record.ParentJobId is Guid parentId && visibleIds.Contains(parentId))
            {
                if (!childrenByParentId.TryGetValue(parentId, out var children))
                {
                    children = [];
                    childrenByParentId[parentId] = children;
                }

                children.Add(record);
            }
            else
            {
                roots.Add(record);
            }
        }

        return roots
            .Select(root => BuildWorkflowJobNode(root, childrenByParentId, []))
            .ToList();
    }

    private static WorkflowJobNodeDto BuildWorkflowJobNode(
        JobRecord record,
        IReadOnlyDictionary<Guid, List<JobRecord>> childrenByParentId,
        HashSet<Guid> visited)
    {
        if (!visited.Add(record.Id))
            return new WorkflowJobNodeDto(record.Summary, []);

        var children = childrenByParentId.TryGetValue(record.Id, out var childRecords)
            ? childRecords
                .Select(child => BuildWorkflowJobNode(child, childrenByParentId, visited))
                .ToList()
            : [];

        visited.Remove(record.Id);
        return new WorkflowJobNodeDto(record.Summary, children);
    }

    private static bool IsDefaultRoot(JobRecord record)
        => record.ParentJobId == null;

    private JobRecord UpdateJobRecord(Job job)
    {
        var parentJobId = parentJobIds.GetValueOrDefault(job.Id);
        var record = new JobRecord(
            job.Id,
            job.WorkflowId,
            parentJobId,
            BuildJobSummary(job),
            BuildPayload(job));
        records[job.Id] = record;
        return record;
    }

    private List<JobRecord> UpdateRecordsContainingJob(Guid jobId)
    {
        return jobs.Values
            .Where(job => ContainsNestedJob(job, jobId))
            .Select(UpdateJobRecord)
            .ToList();
    }

    private static bool ContainsNestedJob(Job container, Guid jobId)
        => container switch
        {
            AlbumJob albumJob => albumJob.Results
                .SelectMany(folder => folder.Files)
                .Any(song => song.Id == jobId),
            AggregateJob aggregateJob => aggregateJob.Songs.Any(song => song.Id == jobId),
            JobList jobList => jobList.Jobs.Any(job => job.Id == jobId || ContainsNestedJob(job, jobId)),
            _ => false,
        };

    private JobSummaryDto BuildJobSummary(Job job)
    {
        var parentJobId = parentJobIds.GetValueOrDefault(job.Id);
        Guid? resultJobId = resultJobIds.TryGetValue(job.Id, out var resultId) ? resultId : null;
        Guid? sourceJobId = sourceJobIds.TryGetValue(job.Id, out var sourceId) ? sourceId : null;

        return new JobSummaryDto(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            GetJobKind(job),
            EffectiveServerLifecycleState(job),
            EffectiveServerActivityPhase(job),
            EffectiveActivityUntilUtc(job),
            EffectiveServerTerminalOutcome(job),
            ToServerJobSkipReason(job.SkipReason),
            job.ItemName,
            job.ToString(noInfo: true),
            ToServerFailureReason(job.FailureReason),
            job.FailureMessage,
            parentJobId,
            resultJobId,
            sourceJobId,
            job.Discovery?.ResultCount,
            job.Discovery?.LockedFileCount,
            job.Config?.AppliedAutoProfiles?.OrderBy(x => x).ToList() ?? [],
            BuildActions(job),
            job.FailureDetail,
            ToServerJobCancellationSource(job.CancellationSource));
    }

    private JobPayloadDto BuildPayload(Job job)
        => job switch
        {
            ExtractJob extractJob => new ExtractJobPayloadDto(
                extractJob.Input,
                extractJob.InputType?.ToString(),
                extractJob.Result?.Id,
                extractJob.AutoProcessResult,
                ToJobDraft(extractJob.Result, extractJob.Config)),
            SearchJob searchJob => new SearchJobPayloadDto(
                searchJob.QueryText,
                searchJob.DefaultFileProjection != null
                    ? new FileSearchProjectionRequestDto(
                        ToSongQueryDto(searchJob.DefaultFileProjection.Query),
                        searchJob.DefaultFileProjection.IncludeFullResults)
                    : null,
                searchJob.DefaultFolderProjection != null
                    ? new FolderSearchProjectionRequestDto(
                        ToAlbumQueryDto(searchJob.DefaultFolderProjection.Query),
                        searchJob.DefaultFolderProjection.IncludeFiles)
                    : null,
                searchJob.ResultCount,
                searchJob.Revision,
                searchJob.IsComplete),
            SongJob songJob => ToSongJobPayloadDto(songJob,
                songTransferStates.TryGetValue(songJob.Id, out var ts) ? ts.ToString() : null),
            AlbumJob albumJob => new AlbumJobPayloadDto(
                ToAlbumQueryDto(albumJob.Query),
                albumJob.Results.Count,
                albumJob.DownloadPath,
                albumJob.ResolvedTarget?.Username,
                albumJob.ResolvedTarget?.FolderPath,
                albumJob.ResolvedTarget?.Files.Count,
                albumJob.ResolvedTarget?.Files.Count(IsTerminalSong),
                albumJob.ResolvedTarget?.Files.Count(IsSuccessfulSong),
                albumJob.ResolvedTarget?.Files.Count(IsFailedOrSkippedSong),
                null,
                null),
            AggregateJob aggregateJob => new AggregateJobPayloadDto(
                ToSongQueryDto(aggregateJob.Query),
                aggregateJob.Songs.Count,
                aggregateJob.Songs.Count(IsTerminalSong),
                aggregateJob.Songs.Count(IsSuccessfulSong),
                aggregateJob.Songs.Count(IsFailedOrSkippedSong),
                null),
            AlbumAggregateJob albumAggregateJob => new AlbumAggregateJobPayloadDto(
                ToAlbumQueryDto(albumAggregateJob.Query),
                albumAggregateJob.Albums.Count > 0
                    ? albumAggregateJob.Albums.Count
                    : CountDescendants(albumAggregateJob.Id, ServerJobKind.Album)),
            JobList jobList => new JobListPayloadDto(
                jobList.Count,
                jobList.Jobs.Count(IsActiveJob),
                jobList.Jobs.Count(IsTerminalJob),
                jobList.Jobs.Count(IsSuccessfulJob),
                jobList.Jobs.Count(IsFailedOrSkippedJob),
                null),
            RetrieveFolderJob retrieveFolderJob => new RetrieveFolderJobPayloadDto(
                retrieveFolderJob.TargetFolder.FolderPath,
                retrieveFolderJob.TargetFolder.Username,
                retrieveFolderJob.NewFilesFoundCount,
                ToServerFolderRetrievalOutcome(retrieveFolderJob.RetrievalOutcome),
                retrieveFolderJob.RetrievalCancelled),
            _ => new GenericJobPayloadDto(job.ToString(noInfo: true))
        };

    private static JobDraftDto? ToJobDraft(Job? job, DownloadSettings? inheritedConfig = null)
        => job switch
        {
            null => null,
            ExtractJob extract => new ExtractJobDraftDto(
                extract.Input,
                extract.InputType?.ToString(),
                extract.AutoProcessResult,
                SettingsDelta(inheritedConfig, extract.Config),
                extract.ResultDownloadBehaviorPolicy != null ? ToDownloadBehaviorPolicyDto(extract.ResultDownloadBehaviorPolicy) : null,
                ToProvenanceDto(extract)),
            SearchJob search when search.DefaultFolderProjection != null =>
                new AlbumSearchJobDraftDto(
                    ToAlbumQueryDto(search.DefaultFolderProjection.Query),
                    SettingsDelta(inheritedConfig, search.Config),
                    ToProvenanceDto(search)),
            SearchJob search when search.DefaultFileProjection != null =>
                new TrackSearchJobDraftDto(
                    ToSongQueryDto(search.DefaultFileProjection.Query),
                    search.DefaultFileProjection.IncludeFullResults,
                    SettingsDelta(inheritedConfig, search.Config),
                    ToProvenanceDto(search)),
            SongJob song => new SongJobDraftDto(
                ToSongQueryDto(song.Query),
                ToDownloadBehaviorPolicyDto(song.DownloadBehaviorPolicy),
                SettingsDelta(inheritedConfig, song.Config),
                ToProvenanceDto(song)),
            AlbumJob album => new AlbumJobDraftDto(
                ToAlbumQueryDto(album.Query),
                ToDownloadBehaviorPolicyDto(album.DownloadBehaviorPolicy),
                SettingsDelta(inheritedConfig, album.Config),
                ToProvenanceDto(album)),
            AggregateJob aggregate => new AggregateJobDraftDto(
                ToSongQueryDto(aggregate.Query),
                ToDownloadBehaviorPolicyDto(aggregate.DownloadBehaviorPolicy),
                SettingsDelta(inheritedConfig, aggregate.Config),
                ToProvenanceDto(aggregate)),
            AlbumAggregateJob aggregate => new AlbumAggregateJobDraftDto(
                ToAlbumQueryDto(aggregate.Query),
                ToDownloadBehaviorPolicyDto(aggregate.DownloadBehaviorPolicy),
                SettingsDelta(inheritedConfig, aggregate.Config),
                ToProvenanceDto(aggregate)),
            JobList list => new JobListJobDraftDto(
                list.ItemName,
                list.Jobs.Select(child => ToJobDraft(child, list.Config)).OfType<JobDraftDto>().ToList(),
                SettingsDelta(inheritedConfig, list.Config),
                ToProvenanceDto(list)),
            _ => null,
        };

    private static JobProvenanceDto? ToProvenanceDto(Job job)
        => job.SourceMutation == null && job.LineNumber == 0 && job.ItemNumber == 1
            ? null
            : new JobProvenanceDto(job.ItemNumber, job.LineNumber, ToSourceMutationDto(job.SourceMutation));

    private static SourceMutationDto? ToSourceMutationDto(SourceMutation? mutation)
        => mutation == null
            ? null
            : new SourceMutationDto(
                mutation.Kind.ToString(),
                mutation.Source,
                mutation.LineNumber,
                mutation.ItemNumber,
                mutation.CsvColumnCount,
                mutation.TrackUri);

    private static DownloadSettingsPatchDto? SettingsDelta(DownloadSettings? inheritedConfig, DownloadSettings? effectiveConfig)
        => inheritedConfig != null && effectiveConfig != null
            ? DownloadSettingsPatchDtoMapper.FromDifference(inheritedConfig, effectiveConfig)
            : null;

    private static DownloadBehaviorPolicyDto ToDownloadBehaviorPolicyDto(DownloadBehaviorPolicy policy)
        => new(policy.Default, policy.Song, policy.Album, policy.Aggregate, policy.AlbumAggregate);

    private static IReadOnlyList<ResourceActionDto> BuildActions(Job job)
        => job.LifecycleState != JobLifecycleState.Terminal && job.Cts != null && !job.Cts.IsCancellationRequested
            ? [CancelAction(job.Id)]
            : [];

    private static ResourceActionDto CancelAction(Guid jobId)
        => new(ServerResourceActionKind.Cancel, "POST", $"/api/jobs/{jobId}/cancel");

    private static SongJobPayloadDto ToSongJobPayloadDto(SongJob song, string? transferState = null)
    {
        long? totalBytes = song.FileSize > 0 ? song.FileSize : song.ResolvedTarget?.File.Size;
        double? progressPercent = totalBytes > 0
            ? Math.Round((double)song.BytesTransferred / totalBytes.Value * 100, 2)
            : null;

        return new SongJobPayloadDto(
            ToSongQueryDto(song.Query),
            song.Candidates?.Count,
            song.DownloadPath,
            song.ResolvedTarget?.Username,
            song.ResolvedTarget?.Filename,
            song.ResolvedTarget?.Response.HasFreeUploadSlot,
            song.ResolvedTarget?.Response.UploadSpeed,
            song.ResolvedTarget?.File.Size,
            song.ResolvedTarget?.File.SampleRate,
            song.ResolvedTarget?.File.Extension,
            song.ResolvedTarget?.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList(),
            song.Id,
            song.DisplayId,
            null,
            ToServerJobLifecycleState(song.LifecycleState),
            ToServerJobActivityPhase(song.ActivityPhase),
            song.ActivityUntilUtc,
            ToServerJobTerminalOutcome(song.TerminalOutcome),
            ToServerJobSkipReason(song.SkipReason),
            ToServerFailureReason(song.FailureReason),
            song.FailureMessage,
            song.BytesTransferred,
            totalBytes,
            progressPercent,
            BuildActions(song),
            transferState,
            ToServerJobCancellationSource(song.CancellationSource));
    }

    private static SongQueryDto ToSongQueryDto(SongQuery query) => new(
        Optional(query.Artist),
        Optional(query.Title),
        Optional(query.Album),
        Optional(query.URI),
        Optional(query.Length),
        query.ArtistMaybeWrong);

    private static AlbumQueryDto ToAlbumQueryDto(AlbumQuery query) => new(
        Optional(query.Artist),
        Optional(query.Album),
        Optional(query.SearchHint),
        Optional(query.URI),
        query.ArtistMaybeWrong);

    private static string? Optional(string value)
        => value.Length > 0 ? value : null;

    private static int? Optional(int value)
        => value >= 0 ? value : null;

    private static FileCandidateDto ToFileCandidateDto(FileCandidate candidate) => new(
        new FileCandidateRefDto(candidate.Username, candidate.Filename),
        candidate.Username,
        candidate.Filename,
        new PeerInfoDto(candidate.Username, candidate.Response.HasFreeUploadSlot, candidate.Response.UploadSpeed),
        candidate.File.Size,
        candidate.File.BitRate,
        candidate.File.SampleRate,
        candidate.File.Length,
        candidate.File.Extension,
        candidate.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList());

    private static AlbumFolderDto ToAlbumFolderDto(AlbumFolder folder, bool includeFiles)
        => new(
            new AlbumFolderRefDto(folder.Username, folder.FolderPath),
            folder.Username,
            folder.FolderPath,
            new PeerInfoDto(
                folder.Username,
                folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.HasFreeUploadSlot,
                folder.Files.FirstOrDefault()?.ResolvedTarget?.Response.UploadSpeed),
            folder.SearchFileCount,
            folder.SearchAudioFileCount,
            includeFiles
                ? folder.Files
                    .Where(song => song.ResolvedTarget != null)
                    .Select(song => ToFileCandidateDto(song.ResolvedTarget!))
                    .ToList()
                : null,
            folder.IsFullyRetrieved);

    private JobLifecycleState EffectiveLifecycleState(Job job)
        => executionCompletedJobs.Contains(job.Id)
            && IsRunningOrPending(job)
                ? JobLifecycleState.Terminal
                : job.LifecycleState;

    private JobActivityPhase EffectiveActivityPhase(Job job)
        => EffectiveLifecycleState(job) == JobLifecycleState.Terminal
            ? JobActivityPhase.None
            : job.ActivityPhase;

    private DateTimeOffset? EffectiveActivityUntilUtc(Job job)
        => EffectiveActivityPhase(job) == JobActivityPhase.None
            ? null
            : job.ActivityUntilUtc;

    private JobTerminalOutcome EffectiveTerminalOutcome(Job job)
        => executionCompletedJobs.Contains(job.Id)
            && IsRunningOrPending(job)
                ? JobTerminalOutcome.Succeeded
                : job.TerminalOutcome;

    private ServerJobLifecycleState EffectiveServerLifecycleState(Job job)
        => ToServerJobLifecycleState(EffectiveLifecycleState(job));

    private ServerJobActivityPhase EffectiveServerActivityPhase(Job job)
        => ToServerJobActivityPhase(EffectiveActivityPhase(job));

    private ServerJobTerminalOutcome EffectiveServerTerminalOutcome(Job job)
        => ToServerJobTerminalOutcome(EffectiveTerminalOutcome(job));

    private bool IsActiveJob(Job job)
        => EffectiveLifecycleState(job) != JobLifecycleState.Terminal;

    private int CountDescendants(Guid parentId, ServerJobKind? kind = null)
    {
        var children = records.Values
            .Where(record => record.ParentJobId == parentId)
            .ToList();

        int count = children.Count(record => kind == null || record.Summary.Kind == kind);
        foreach (var child in children)
            count += CountDescendants(child.Id, kind);

        return count;
    }

    private static bool IsActiveRecord(JobRecord record)
        => record.Summary.LifecycleState != ServerJobLifecycleState.Terminal;

    private static bool IsFailedRecord(JobRecord record)
        => record.Summary.TerminalOutcome is ServerJobTerminalOutcome.Failed
            or ServerJobTerminalOutcome.Cancelled
            or ServerJobTerminalOutcome.PartialSuccess
            || (record.Summary.TerminalOutcome == ServerJobTerminalOutcome.Skipped
                && record.Summary.SkipReason != ServerJobSkipReason.AlreadyExists);

    public static ServerJobLifecycleState ToServerJobLifecycleState(JobLifecycleState state)
        => Enum.Parse<ServerJobLifecycleState>(state.ToString());

    public static ServerJobActivityPhase ToServerJobActivityPhase(JobActivityPhase phase)
        => Enum.Parse<ServerJobActivityPhase>(phase.ToString());

    public static ServerJobTerminalOutcome ToServerJobTerminalOutcome(JobTerminalOutcome outcome)
        => Enum.Parse<ServerJobTerminalOutcome>(outcome.ToString());

    public static ServerJobSkipReason ToServerJobSkipReason(JobSkipReason reason)
        => Enum.Parse<ServerJobSkipReason>(reason.ToString());

    public static ServerJobFailureReason? ToServerFailureReason(JobFailureReason reason)
        => reason == JobFailureReason.None
            ? null
            : Enum.Parse<ServerJobFailureReason>(reason.ToString());

    public static ServerJobCancellationSource ToServerJobCancellationSource(JobCancellationSource source)
        => Enum.Parse<ServerJobCancellationSource>(source.ToString());

    public static ServerFolderRetrievalOutcome ToServerFolderRetrievalOutcome(FolderRetrievalOutcome outcome)
        => Enum.Parse<ServerFolderRetrievalOutcome>(outcome.ToString());

    private static bool IsRunningOrPending(Job job)
        => job.LifecycleState is JobLifecycleState.Pending or JobLifecycleState.Running;

    private static bool IsTerminalJob(Job job)
        => job.LifecycleState == JobLifecycleState.Terminal;

    private static bool IsSuccessfulJob(Job job)
        => job.TerminalOutcome == JobTerminalOutcome.Succeeded
            || (job.TerminalOutcome == JobTerminalOutcome.Skipped && job.SkipReason == JobSkipReason.AlreadyExists);

    private static bool IsFailedOrSkippedJob(Job job)
        => job.TerminalOutcome is JobTerminalOutcome.Failed
                or JobTerminalOutcome.Cancelled
                or JobTerminalOutcome.PartialSuccess
            || (job.TerminalOutcome == JobTerminalOutcome.Skipped && job.SkipReason != JobSkipReason.AlreadyExists);

    private static bool IsTerminalSong(SongJob song)
        => song.LifecycleState == JobLifecycleState.Terminal;

    private static bool IsSuccessfulSong(SongJob song)
        => song.TerminalOutcome == JobTerminalOutcome.Succeeded
            || (song.TerminalOutcome == JobTerminalOutcome.Skipped && song.SkipReason == JobSkipReason.AlreadyExists);

    private static bool IsFailedOrSkippedSong(SongJob song)
        => song.TerminalOutcome is JobTerminalOutcome.Failed
                or JobTerminalOutcome.Cancelled
                or JobTerminalOutcome.PartialSuccess
            || (song.TerminalOutcome == JobTerminalOutcome.Skipped && song.SkipReason != JobSkipReason.AlreadyExists);

    private sealed record JobRecord(
        Guid Id,
        Guid WorkflowId,
        Guid? ParentJobId,
        JobSummaryDto Summary,
        JobPayloadDto Payload);
}
