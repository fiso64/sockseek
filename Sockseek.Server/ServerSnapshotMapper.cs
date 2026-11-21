using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;

namespace Sockseek.Server;

public static class ServerSnapshotMapper
{
    public static JobSummaryDto ToJobSummary(
        JobSnapshot job,
        Guid? parentJobId = null,
        Guid? resultJobId = null,
        Guid? sourceJobId = null,
        JobLifecycleState? lifecycleState = null,
        JobActivityPhase? activityPhase = null,
        DateTimeOffset? activityUntilUtc = null,
        JobTerminalOutcome? terminalOutcome = null,
        JobSummaryStatsDto? stats = null,
        bool includeActions = true,
        (int? Public, int? Locked, int? Peers)? discoveryCounts = null,
        bool? hasFailureDetail = null)
    {
        var effectiveLifecycleState = lifecycleState ?? job.LifecycleState;
        var effectiveActivityPhase = activityPhase ?? job.ActivityPhase;
        var effectiveTerminalOutcome = terminalOutcome ?? job.TerminalOutcome;
        SearchDefinition? searchDefinition = (job.Payload as SearchJobSnapshotPayload)?.Definition;

        var counts = discoveryCounts ?? (job.Discovery?.RawResultCount, job.Discovery?.LockedFileCount,
            job.Discovery?.ObservedPeerCount);
        return new JobSummaryDto(job.Id, job.Revision,
            new(job.DisplayId, job.WorkflowId, job.Kind.ToServer(), job.ItemName, job.QueryText,
                job.AppliedProfiles.Select(profile => new AppliedProfileDto(profile.Name, profile.Source)).ToArray(),
                job.PrintOption.ToServer(), job.SubmissionId, job.SemanticRole.ToServer(), job.CreatedAtUtc,
                searchDefinition?.DefaultProjection.ToServer(), searchDefinition?.GroupResults, job.SourceDetail),
            new(effectiveLifecycleState.ToServer(), effectiveActivityPhase.ToServer(),
                effectiveActivityPhase == JobActivityPhase.None ? null : activityUntilUtc ?? job.ActivityUntilUtc,
                effectiveTerminalOutcome.ToServer(), job.SkipReason.ToServer(), job.FailureReason.ToServer(),
                job.FailureMessage, hasFailureDetail ?? !string.IsNullOrEmpty(job.FailureDetail),
                job.CancellationSource.ToServer(), includeActions ? BuildJobActions(job.Id, job.CanCancel) : []),
            new(counts.Public, counts.Locked, PublicFileCount: counts.Public, ObservedPeerCount: counts.Peers),
            new(parentJobId, resultJobId, sourceJobId), stats);
    }

    public static JobSummaryDto ToSubmittedJobSummary(Job job, Guid? sourceJobId = null)
    {
        // Acceptance needs the root's facts and counters, not a second descendant
        // graph capture before the state owner registers the planned workflow.
        var execution = CoreSnapshotFactory.CreateExecution(job, revision: 0);
        var snapshot = new JobSnapshot(execution, execution.Payload);
        return ToJobSummary(snapshot, sourceJobId: sourceJobId, stats: ToJobSummaryStats(snapshot));
    }

    public static JobPayloadDto ToJobPayload(
        JobSnapshot job,
        Func<Guid, string?> getTransferState,
        JobOutcomeSummary? liveListOutcomes = null,
        JobOutcomeSummary? liveGroupOutcomes = null,
        JobOutcomeSummary? liveAlbumGroupsOutcomes = null)
        => job.Payload switch
        {
            ExtractJobSnapshotPayload extract => new ExtractJobPayloadDto(
                extract.Input,
                extract.InputType,
                extract.ResultJobId),
            SearchJobSnapshotPayload search => new SearchJobPayloadDto(
                search.QueryText,
                search.DefaultFolderProjection != null
                    ? ServerSearchDefaultProjectionKind.Album
                    : search.DefaultFileProjection != null
                        ? ServerSearchDefaultProjectionKind.Track
                        : ServerSearchDefaultProjectionKind.GenericFile,
                search.DefaultFileProjection == null
                    ? null
                    : ToSongQueryDto(search.DefaultFileProjection.Query),
                search.DefaultFolderProjection == null
                    ? null
                    : ToAlbumQueryDto(search.DefaultFolderProjection.Query),
                search.DefaultFileProjection?.IncludeFullResults ?? false,
                search.ResultCount,
                search.Revision,
                search.IsComplete,
                search.Definition?.GroupResults ?? false),
            SongJobSnapshotPayload song => ToSongJobPayloadDto(job, song, getTransferState(job.Id)),
            AlbumJobSnapshotPayload album => new AlbumJobPayloadDto(
                ToAlbumQueryDto(album.Query),
                album.ResultCount,
                ToDirectoryDownloadStateDto(album.Directory),
                album.ResolvedTarget?.Username,
                album.ResolvedTarget?.FolderPath),
            RemoteFileJobSnapshotPayload remoteFile => new RemoteFileJobPayloadDto(
                ToPeerFileTargetDto(remoteFile.Target),
                remoteFile.OutputPath.Components,
                ToFileDownloadStateDto(remoteFile.File)),
            RemoteDirectoryJobSnapshotPayload remoteDirectory => new RemoteDirectoryJobPayloadDto(
                remoteDirectory.SourceKind == RemoteDirectorySourceSnapshotKind.PeerDirectory
                    ? RemoteDirectorySourceKindDto.PeerDirectory
                    : RemoteDirectorySourceKindDto.Resolved,
                RemoteDirectorySourceUsername(remoteDirectory),
                remoteDirectory.DirectorySource?.FolderPath
                    ?? remoteDirectory.Manifest?.Plan.SourceDirectory?.FolderPath
                    ?? remoteDirectory.ResolvedPlanSource?.SourceDirectory?.FolderPath,
                ToDirectoryDownloadStateDto(remoteDirectory.Directory),
                remoteDirectory.Manifest?.Plan.DisplayRoot ?? remoteDirectory.ResolvedPlanSource?.DisplayRoot ?? remoteDirectory.DirectorySource?.FolderPath),
            SongGroupsJobSnapshotPayload group => ToGroupPayloadDto(group, liveGroupOutcomes),
            AlbumGroupsJobSnapshotPayload albumGroups => ToAlbumGroupsPayloadDto(
                albumGroups,
                liveAlbumGroupsOutcomes),
            JobListSnapshotPayload list => ToJobListPayloadDto(list, liveListOutcomes),
            RetrieveFolderJobSnapshotPayload retrieve => new RetrieveFolderJobPayloadDto(
                retrieve.Directory.FolderPath,
                retrieve.Directory.Username,
                retrieve.NewFilesFoundCount,
                retrieve.RetrievalOutcome.ToServer(),
                retrieve.RetrievalCancelled),
            AlbumExecutionPayload album => new AlbumJobPayloadDto(
                ToAlbumQueryDto(album.Query), album.ResultCount, ToDirectoryDownloadStateDto(album.Directory),
                album.ResolvedTarget?.Username, album.ResolvedTarget?.FolderPath),
            RemoteDirectoryExecutionPayload directory => new RemoteDirectoryJobPayloadDto(
                directory.SourceKind == RemoteDirectorySourceSnapshotKind.PeerDirectory
                    ? RemoteDirectorySourceKindDto.PeerDirectory : RemoteDirectorySourceKindDto.Resolved,
                directory.SourceUsername, directory.SourceFolderPath,
                ToDirectoryDownloadStateDto(directory.Directory), directory.DisplayRoot),
            SongGroupsExecutionPayload group => new SongGroupsJobPayloadDto(
                ToSongQueryDto(group.Query), group.Outcomes.Total, group.Outcomes.Completed,
                group.Outcomes.Succeeded, group.Outcomes.Failed),
            JobListExecutionPayload list => new JobListPayloadDto(
                list.Outcomes.Total, list.Outcomes.Total - list.Outcomes.Completed,
                list.Outcomes.Completed, list.Outcomes.Succeeded, list.Outcomes.Failed),
            RetrieveFolderExecutionPayload retrieve => new RetrieveFolderJobPayloadDto(
                retrieve.Directory.FolderPath, retrieve.Directory.Username, retrieve.NewFilesFoundCount,
                retrieve.RetrievalOutcome.ToServer(), retrieve.RetrievalCancelled),
            GenericJobSnapshotPayload generic => new GenericJobPayloadDto(generic.Text),
            null => new GenericJobPayloadDto(job.QueryText ?? job.Kind.ToServer().ToWireString()),
        };

    // Summary readers need scalar counters, not a rendered detail payload. Both
    // paths use the same outcome/progress rules; targets and query DTOs are read-only work.
    public static JobSummaryStatsDto? ToJobSummaryStats(
        JobSnapshot job,
        JobOutcomeSummary? liveListOutcomes = null,
        JobOutcomeSummary? liveGroupOutcomes = null,
        JobOutcomeSummary? liveAlbumGroupsOutcomes = null)
        => job.Payload switch
        {
            SongJobSnapshotPayload song => new(CandidateCount: song.CandidateCount),
            AlbumJobSnapshotPayload album => ToDirectorySummaryStats(album.Directory, album.ResultCount),
            AlbumExecutionPayload album => ToDirectorySummaryStats(album.Directory, album.ResultCount),
            RemoteFileJobSnapshotPayload file => new(KnownBytes: file.File.FileSize ?? file.Target.Size),
            RemoteDirectoryJobSnapshotPayload directory => ToDirectorySummaryStats(directory.Directory),
            RemoteDirectoryExecutionPayload directory => ToDirectorySummaryStats(directory.Directory),
            SongGroupsJobSnapshotPayload group => MemberStats(liveGroupOutcomes ?? group.Outcomes),
            SongGroupsExecutionPayload group => MemberStats(group.Outcomes),
            AlbumGroupsJobSnapshotPayload group => AlbumGroupsStats(group, liveAlbumGroupsOutcomes),
            JobListSnapshotPayload list => MemberStats(ListOutcomes(list, liveListOutcomes)),
            JobListExecutionPayload list => MemberStats(list.Outcomes),
            RetrieveFolderJobSnapshotPayload retrieve => new(NewFilesFoundCount: retrieve.NewFilesFoundCount),
            RetrieveFolderExecutionPayload retrieve => new(NewFilesFoundCount: retrieve.NewFilesFoundCount),
            _ => null,
        };

    internal static JobSummaryStatsDto ToDirectorySummaryStats(DirectoryDownloadStateSnapshot directory, int? folders = null)
        => new(MemberCount: directory.FileCount, CompletedMemberCount: directory.TerminalFileCount,
            FailedMemberCount: directory.FailedFileCount, FolderCount: folders, FileCount: directory.FileCount,
            ProgressPercent: DirectoryProgressPercent(directory.FileCount, directory.SuccessfulFileCount,
                directory.BytesTransferred, directory.TotalKnownBytes, directory.Progress),
            SkippedMemberCount: directory.SkippedFileCount,
            DownloadedMemberCount: directory.DownloadedFileCount, ReusedMemberCount: directory.ReusedFileCount);

    private static JobSummaryStatsDto MemberStats(JobOutcomeSummary outcomes)
        => new(MemberCount: outcomes.Total, CompletedMemberCount: outcomes.Completed,
            FailedMemberCount: outcomes.Failed, SkippedMemberCount: outcomes.Skipped);

    internal static JobOutcomeSummary ListOutcomes(JobListSnapshotPayload list, JobOutcomeSummary? liveOutcomes = null)
        => liveOutcomes ?? list.LogicalOutcomes ?? list.Jobs.Aggregate(new JobOutcomeSummary(),
            (summary, job) => summary.Include(job.LifecycleState, job.TerminalOutcome, job.SkipReason));

    private static JobSummaryStatsDto AlbumGroupsStats(
        AlbumGroupsJobSnapshotPayload group, JobOutcomeSummary? registeredAlbums)
    {
        var (count, outcomes) = AlbumGroupsProgress(group, registeredAlbums);
        return new(MemberCount: count,
            CompletedMemberCount: outcomes.Completed, FailedMemberCount: outcomes.Failed, SkippedMemberCount: outcomes.Skipped);
    }

    private static (int Count, JobOutcomeSummary Outcomes) AlbumGroupsProgress(
        AlbumGroupsJobSnapshotPayload group, JobOutcomeSummary? registeredAlbums)
    {
        JobOutcomeSummary outcomes = registeredAlbums is { Total: > 0 } ? registeredAlbums.Value : group.Albums;
        return (group.Albums.Total > 0 ? group.Albums.Total : outcomes.Total, outcomes);
    }

    public static string? RemoteDirectorySourceUsername(RemoteDirectoryJobSnapshotPayload directory)
        => directory.DirectorySource?.Username
            ?? directory.Manifest?.Plan.SourceDirectory?.Username
            ?? directory.ResolvedPlanSource?.SourceDirectory?.Username
            ?? directory.Manifest?.Plan.Entries.FirstOrDefault()?.Target.Identity.Username
            ?? directory.ResolvedPlanSource?.Entries.FirstOrDefault()?.Target.Identity.Username;

    internal static JobListPayloadDto ToJobListPayloadDto(
        JobListSnapshotPayload list,
        JobOutcomeSummary? liveOutcomes = null)
    {
        JobOutcomeSummary outcomes = ListOutcomes(list, liveOutcomes);
        return new(
            list.Count,
            outcomes.Total - outcomes.Completed,
            outcomes.Completed,
            outcomes.Succeeded,
            outcomes.Failed, outcomes.Skipped);
    }

    internal static SongGroupsJobPayloadDto ToGroupPayloadDto(
        SongGroupsJobSnapshotPayload group,
        JobOutcomeSummary? liveOutcomes = null)
    {
        JobOutcomeSummary outcomes = liveOutcomes ?? group.Outcomes;
        return new(
            ToSongQueryDto(group.Query),
            outcomes.Total,
            outcomes.Completed,
            outcomes.Succeeded,
            outcomes.Failed, outcomes.Skipped);
    }

    internal static AlbumGroupsJobPayloadDto ToAlbumGroupsPayloadDto(
        AlbumGroupsJobSnapshotPayload group,
        JobOutcomeSummary? registeredAlbums = null)
    {
        var (count, outcomes) = AlbumGroupsProgress(group, registeredAlbums);
        return new(
            ToAlbumQueryDto(group.Query),
            count,
            outcomes.Completed,
            outcomes.Succeeded,
            outcomes.Failed, outcomes.Skipped);
    }

    public static SongJobPayloadDto ToSongJobPayloadDto(
        JobSnapshot job,
        SongJobSnapshotPayload song,
        string? transferState = null)
    {
        return new SongJobPayloadDto(
            ToSongQueryDto(song.Query),
            song.CandidateCount,
            ToFileDownloadStateDto(song),
            song.ResolvedTarget?.Username,
            song.ResolvedTarget?.Filename,
            song.ResolvedTarget?.Peer.HasFreeUploadSlot,
            song.ResolvedTarget?.Peer.UploadSpeed,
            song.ResolvedTarget?.Size,
            song.ResolvedTarget?.SampleRate,
            song.ResolvedTarget?.Extension,
            song.ResolvedTarget?.Attributes?.Select(ToFileAttributeDto).ToList(),
            job.Id,
            job.DisplayId,
            job.LifecycleState.ToServer(),
            job.ActivityPhase.ToServer(),
            job.ActivityUntilUtc,
            job.TerminalOutcome.ToServer(),
            job.SkipReason.ToServer(),
            job.FailureReason.ToServer(),
            job.FailureMessage,
            BuildJobActions(job.Id, job.CanCancel),
            transferState,
            job.CancellationSource.ToServer(),
            song.DownloadSource.ToServer(),
            song.ExactTarget == null ? null : ToPeerFileTargetDto(song.ExactTarget),
            ToAttemptedSongFileDto(song.LastAttemptedCandidate),
            ToFallbackSourceDto(song.Fallback));
    }

    internal static FallbackSourceDto? ToFallbackSourceDto(FallbackSource? source)
        => source == null ? null : new(source.Provider, source.Title, source.Reference);

    internal static AttemptedSongFileDto? ToAttemptedSongFileDto(FileCandidateSnapshot? candidate)
        => candidate == null ? null : new(
            ToPeerFileTargetDto(candidate),
            new PeerInfoDto(candidate.Username, candidate.Peer?.HasFreeUploadSlot,
                candidate.Peer?.UploadSpeed, candidate.Peer?.QueueLength, candidate.Peer?.ObservedAtUtc));

    public static SongQueryDto ToSongQueryDto(SongQuerySnapshot query)
        => new(query.Artist, query.Title, query.Album, query.URI, query.Length, query.ArtistMaybeWrong);

    public static SongQueryDto ToSongQueryDto(SongQuery query)
        => new(
            Optional(query.Artist),
            Optional(query.Title),
            Optional(query.Album),
            Optional(query.URI),
            Optional(query.Length),
            query.ArtistMaybeWrong);

    public static SongQueryDto ToSongQueryDto(SongQueryDefinition query)
        => new(
            Optional(query.Artist),
            Optional(query.Title),
            Optional(query.Album),
            Optional(query.Uri),
            Optional(query.Length),
            query.ArtistMaybeWrong);

    public static AlbumQueryDto ToAlbumQueryDto(AlbumQuerySnapshot query)
        => new(query.Artist, query.Album, query.SearchHint, query.URI, query.ArtistMaybeWrong);

    public static AlbumQueryDto ToAlbumQueryDto(AlbumQuery query)
        => new(
            Optional(query.Artist),
            Optional(query.Album),
            Optional(query.SearchHint),
            Optional(query.URI),
            query.ArtistMaybeWrong);

    public static AlbumQueryDto ToAlbumQueryDto(AlbumQueryDefinition query)
        => new(
            Optional(query.Artist),
            Optional(query.Album),
            Optional(query.SearchHint),
            Optional(query.Uri),
            query.ArtistMaybeWrong);

    private static string? Optional(string value)
        => value.Length == 0 ? null : value;

    private static int? Optional(int value)
        => value < 0 ? null : value;

    public static IEnumerable<Guid> NestedJobIds(JobSnapshot container)
        => DirectNestedJobs(container).SelectMany(EnumerateSubtreeIds);

    private static IEnumerable<JobSnapshot> DirectNestedJobs(JobSnapshot container)
        => container.Payload switch
        {
            AlbumJobSnapshotPayload album => album.TrackJobs,
            RemoteDirectoryJobSnapshotPayload directory => directory.FileJobs,
            SongGroupsJobSnapshotPayload group => group.Songs,
            JobListSnapshotPayload list => list.Jobs,
            _ => [],
        };

    private static IEnumerable<Guid> EnumerateSubtreeIds(JobSnapshot job)
    {
        yield return job.Id;
        foreach (JobSnapshot child in DirectNestedJobs(job))
        {
            foreach (Guid id in EnumerateSubtreeIds(child))
                yield return id;
        }
    }

    public static bool IsRunningOrPending(JobSnapshotHeader job)
        => job.LifecycleState is JobLifecycleState.Pending or JobLifecycleState.Running;

    internal static IReadOnlyList<ResourceActionDto> BuildJobActions(Guid jobId, bool canCancel)
        => canCancel
            ? [CancelAction(jobId)]
            : [];

    private static ResourceActionDto CancelAction(Guid jobId)
        => new(ServerResourceActionKind.Cancel, "POST", $"/api/jobs/{jobId}/cancel");

    public static SearchRawResultDto ToSearchRawResultDto(SearchProjectionInput input)
        => new(input.Sequence, input.Revision, input.Username, input.Filename, input.Size,
            input.BitRate, input.SampleRate, input.Length, input.Visibility.ToServer(),
            input.QueueLength, input.ObservedAtUtc);

    private static FileAttributeDto ToFileAttributeDto(FileAttributeSnapshot attribute)
        => new(attribute.Type, attribute.Value);

    internal static PeerFileTargetDto ToPeerFileTargetDto(FileCandidateSnapshot candidate)
        => new(candidate.Username, candidate.Filename, candidate.Size < 0 ? null : candidate.Size, candidate.Extension,
            candidate.BitRate, candidate.BitDepth, candidate.SampleRate, candidate.Length,
            candidate.Attributes?.Select(ToFileAttributeDto).ToArray());

    internal static PeerFileTargetDto ToPeerFileTargetDto(PeerFileTarget target)
        => new(
            target.Identity.Username,
            target.Identity.Filename,
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes?.Select(ToFileAttributeDto).ToList());

    internal static FileDownloadStateDto ToFileDownloadStateDto(SongJobSnapshotPayload song)
        => ToFileDownloadStateDto(song.File, song.File.FileSize > 0 ? song.File.FileSize
            : song.ResolvedTarget?.Size ?? song.ExactTarget?.Size ?? song.LastAttemptedCandidate?.Size);

    internal static FileDownloadStateDto ToFileDownloadStateDto(FileDownloadStateSnapshot file)
        => ToFileDownloadStateDto(file, file.FileSize);

    private static FileDownloadStateDto ToFileDownloadStateDto(FileDownloadStateSnapshot file, long? size)
        => new(file.DownloadPath, file.BytesTransferred, size,
            size > 0 ? Math.Round((double)file.BytesTransferred / size.Value * 100, 2) : null,
            ToExistingFileSource(file.ExistingFileSource), file.Reused);

    private static ServerExistingFileSource? ToExistingFileSource(ExistingFileSource? source)
        => source switch
        {
            ExistingFileSource.CompletionHistory => ServerExistingFileSource.CompletionHistory,
            ExistingFileSource.Library => ServerExistingFileSource.Library,
            ExistingFileSource.Destination => ServerExistingFileSource.Destination,
            _ => null,
        };

    private static DirectoryDownloadStateDto ToDirectoryDownloadStateDto(DirectoryDownloadStateSnapshot directory)
        => new(
            directory.Phase,
            directory.AttemptNumber,
            directory.DownloadPath,
            directory.FileCount,
            directory.TerminalFileCount,
            directory.SuccessfulFileCount,
            directory.FailedFileCount,
            directory.BytesTransferred,
            directory.TotalKnownBytes,
            DirectoryProgressPercent(directory.FileCount, directory.SuccessfulFileCount,
                directory.BytesTransferred, directory.TotalKnownBytes, directory.Progress),
            directory.UnknownFileSizeCount, ToExistingFileSource(directory.ExistingFileSource),
            directory.SkippedFileCount, directory.DownloadedFileCount, directory.ReusedFileCount);

    internal static double? DirectoryProgressPercent(
        int fileCount, int successfulFileCount, long bytesTransferred, long totalKnownBytes,
        double? progress = null)
    {
        // Already-existing files are successful without transferring bytes. Once
        // every file is successful, the directory is complete regardless of the
        // transferred/known-byte ratio.
        if (fileCount > 0 && successfulFileCount == fileCount)
            return 100;
        return totalKnownBytes > 0
            ? Math.Round((progress ?? (double)bytesTransferred / totalKnownBytes) * 100, 2)
            : null;
    }
}
