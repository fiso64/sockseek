using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Services;
using Sockseek.Core.Planning;
using Sockseek.Core.Extractors;

namespace Sockseek.Core.Snapshots;

public static class CoreSnapshotFactory
{
    public static JobSnapshot CreateJob(Job job, long revision)
        => CreateJob(job, revision, []);

    internal static JobSnapshot CreateJob(Job job, JobStateSnapshot state)
        => CreateJob(job, state.Revision, [], state);

    public static JobReferenceSnapshot CreateJobReference(Job job, long revision)
        => new(job.Id, job.DisplayId, job.WorkflowId, GetJobKind(job), revision,
            JobTitle(job), job.EffectivePrintOption);

    public static FileCandidateSnapshot CreateFileCandidate(FileCandidate candidate)
        => new(
            candidate.Username,
            candidate.Filename,
            new PeerSnapshot(
                candidate.Username,
                candidate.HasFreeUploadSlot,
                candidate.UploadSpeed,
                candidate.QueueLength,
                candidate.ObservedAtUtc),
            candidate.Size,
            candidate.BitRate,
            candidate.BitDepth,
            candidate.SampleRate,
            candidate.Length,
            candidate.Extension,
            candidate.Attributes,
            candidate.Visibility,
            candidate.ResponseFileCount);

    public static PeerDirectoryResultSnapshot CreatePeerDirectory(PeerDirectorySnapshot directory)
        => new(
            new PeerDirectoryIdentitySnapshot(
                directory.Identity.Username,
                directory.Identity.FolderPath),
            directory.Files,
            directory.IsComplete);

    public static DirectoryTransferPlanSnapshot CreateDirectoryTransferPlan(DirectoryTransferPlan plan)
        => new(
            plan.DisplayRoot,
            SnapshotCollections.Freeze(plan.Entries.Select(entry =>
                new DirectoryTransferEntrySnapshot(
                    entry.Target,
                    SnapshotCollections.Freeze(entry.RelativeDirectoryComponents)))),
            plan.TotalKnownBytes,
            plan.RepresentsDirectorySelection,
            plan.SourceDirectory == null ? null : new PeerDirectoryIdentitySnapshot(
                plan.SourceDirectory.Username, plan.SourceDirectory.FolderPath));

    public static TransferSnapshot CreateDownloadTransfer(
        Guid transferId,
        SongJob song,
        FileCandidate candidate,
        string outputPath,
        long revision,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount)
    {
        var candidateSnapshot = CreateFileCandidate(candidate);
        return new TransferSnapshot(
            transferId,
            TransferSnapshotDirection.Download,
            TransferSnapshotSource.SoulseekPeer,
            song.Id,
            song.WorkflowId,
            revision,
            candidate.Username,
            candidate.Filename,
            outputPath,
            CandidateKey(candidateSnapshot),
            State: state,
            BytesTransferred: bytesTransferred,
            TotalBytes: totalBytes,
            AttemptCount: attemptCount,
            candidateSnapshot,
            candidate.Target,
            File: CreateTransferFileMetadata(candidateSnapshot));
    }

    public static TransferSnapshot CreateDownloadTransfer(
        Guid transferId,
        FileDownloadJob owner,
        PeerFileTarget target,
        string outputPath,
        long revision,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount)
    {
        return CreateDownloadTransfer(transferId, owner, target, outputPath, revision,
            state, bytesTransferred, totalBytes, attemptCount, DownloadCandidate(owner, target));
    }

    internal static TransferSnapshot CreateDownloadTransfer(
        Guid transferId,
        FileDownloadJob owner,
        PeerFileTarget target,
        string outputPath,
        long revision,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        FileCandidate? candidate)
    {
        if (candidate is not null && owner is SongJob candidateSong)
            return CreateDownloadTransfer(
                transferId, candidateSong, candidate, outputPath, revision, state,
                bytesTransferred, totalBytes, attemptCount);

        return new TransferSnapshot(
            transferId,
            TransferSnapshotDirection.Download,
            TransferSnapshotSource.SoulseekPeer,
            owner.Id,
            owner.WorkflowId,
            revision,
            target.Username,
            target.Filename,
            outputPath,
            CandidateKey(target.Identity),
            State: state,
            BytesTransferred: bytesTransferred,
            TotalBytes: totalBytes,
            AttemptCount: attemptCount,
            Candidate: null,
            Target: target,
            File: CreateTransferFileMetadata(target));
    }

    // Both fresh snapshots and the event owner's immutable-metadata reuse must
    // select the same exact candidate, including resolved local-cache targets.
    internal static FileCandidate? DownloadCandidate(FileDownloadJob owner, PeerFileTarget target)
    {
        var candidate = owner is SongJob song ? song.ActiveCandidate ?? song.ResolvedTarget : null;
        return candidate?.Target.Identity == target.Identity ? candidate : null;
    }

    public static TransferSnapshot CreateFallbackTransfer(
        Guid transferId,
        SongJob song,
        string? sourceReference,
        string? outputPath,
        long revision,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount)
        => new(
            transferId,
            TransferSnapshotDirection.Download,
            TransferSnapshotSource.Fallback,
            song.Id,
            song.WorkflowId,
            revision,
            Username: null,
            RemotePath: sourceReference,
            LocalPath: outputPath,
            CandidateKey: null,
            state,
            bytesTransferred,
            totalBytes,
            attemptCount,
            Candidate: null,
            Target: null);

    private static TransferFileMetadataSnapshot CreateTransferFileMetadata(
        FileCandidateSnapshot candidate)
        => new(
            Utils.GetFileNameSlsk(candidate.Filename),
            candidate.Size,
            candidate.Extension,
            candidate.BitRate,
            candidate.BitDepth,
            candidate.SampleRate,
            candidate.Length,
            candidate.Attributes);

    private static TransferFileMetadataSnapshot? CreateTransferFileMetadata(
        PeerFileTarget target)
        => target.Size is not { } size
            ? null
            : new(
                Utils.GetFileNameSlsk(target.Identity.Filename),
                size,
                target.Extension,
                target.BitRate,
                target.BitDepth,
                target.SampleRate,
                target.Length,
                target.Attributes);

    public static AlbumFolderSnapshot CreateAlbumFolder(AlbumFolder folder, bool includeFiles)
    {
        var first = folder.Files.FirstOrDefault()?.Candidate;
        return new AlbumFolderSnapshot(
            folder.Username,
            folder.FolderPath,
            new PeerSnapshot(
                folder.Username,
                first?.HasFreeUploadSlot,
                first?.UploadSpeed),
            folder.SearchFileCount,
            folder.SearchAudioFileCount,
            includeFiles
                ? SnapshotCollections.Freeze(folder.Files.Select(CreateAlbumFile))
                : SnapshotCollections.Empty<AlbumFileSnapshot>(),
            folder.IsFullyRetrieved);
    }

    public static ExceptionSnapshot CreateException(Exception exception)
        => new(exception.GetType().Name, Diagnostics.ExceptionText.Summary(exception), Diagnostics.ExceptionText.Detail(exception));

    public static JobExecutionSnapshot CreateExecution(Job job, long revision)
        => new(CreateHeader(job, revision), CreateExecutionPayload(job));

    internal static JobExecutionSnapshot CreateExecution(Job job, JobStateSnapshot state)
        => new(CreateHeader(job, state.Revision, state), CreateExecutionPayload(job));

    private static JobSnapshot CreateJob(Job job, long revision, HashSet<Guid> visited, JobStateSnapshot? state = null)
    {
        var header = CreateHeader(job, revision, state);
        if (!visited.Add(job.Id))
            return new(header, new GenericJobSnapshotPayload(job.ToString(noInfo: true)));
        try { return new(header, CreatePayload(job, visited)); }
        finally { visited.Remove(job.Id); }
    }

    private static JobSnapshotHeader CreateHeader(Job job, long revision, JobStateSnapshot? captured = null)
    {
        var state = captured ?? job.StateSnapshot;
        return new(
                job.Id,
                job.DisplayId,
                job.WorkflowId,
                job.SubmissionId,
                job.SemanticRole,
                job.CreatedAtUtc,
                job.SubmissionSpecificationJson,
                job.RerunOfSubmissionId,
                job.PreviewId,
                job.ArtifactId,
                GetJobKind(job),
                revision,
                state.LifecycleState,
                state.ActivityPhase,
                state.ActivityUntilUtc,
                state.TerminalOutcome,
                state.SkipReason,
                state.CancellationSource,
                state.FailureReason,
                state.FailureMessage,
                state.FailureDetail,
                JobTitle(job),
                job.ToString(noInfo: true),
                CreateDiscovery(job.Discovery),
                SnapshotCollections.Freeze(job.EffectiveAutoProfiles.OrderBy(x => x)),
                job.EffectivePrintOption,
                CanCancel(job, state),
                SourceDetail(job));
    }

    public static string? JobTitle(Job job)
    {
        if (job.ItemName is not null) return job.ItemName;
        if (job is ExtractJob { SourceInfo.UseResultName: true, Result.ItemName: { } title }) return title;
        return job switch
        {
            SongJob song => MusicTitle(song.Query.Artist, song.Query.Title),
            AlbumJob album => MusicTitle(album.Query.Artist, album.Query.Album),
            _ => null,
        };
    }

    private static string? MusicTitle(string artist, string title)
        => artist.Length == 0 ? title.Length == 0 ? null : title
            : title.Length == 0 ? artist : $"{artist} — {title}";

    private static string? SourceDetail(Job job) => job.SourceInfo?.Detail ?? (job switch
    {
        RetrieveFolderJob folder => folder.Directory.Username,
        ExtractJob extract when extract.InputType is { } type && type != InputType.None => $"{ExtractorRegistry.SourceName(type)} import",
        SongJob when job.SourceInputType is null && job.SemanticRole == JobSemanticRole.UserRoot => "Automatic song download",
        AlbumJob when job.SourceInputType is null && job.SemanticRole == JobSemanticRole.UserRoot => "Automatic album download",
        _ => null,
    });

    private static JobSnapshotPayload CreatePayload(Job job, HashSet<Guid> visited)
        => job switch
        {
            AlbumJob album => new AlbumJobSnapshotPayload(
                CreateAlbumQuery(album.Query),
                album.Results.Count,
                album.ResolvedTarget == null ? null : CreateAlbumFolder(album.ResolvedTarget, includeFiles: false),
                SnapshotCollections.Freeze(album.TrackJobs.Select(child => CreateJob(child, 0, visited))),
                CreateDirectoryDownloadState(album),
                album.ExecutedSearchDefinition,
                CreateDirectoryManifest(album)),
            RemoteDirectoryJob remoteDirectory => CreateRemoteDirectoryPayload(remoteDirectory, visited),
            SongGroupsJob group => new SongGroupsJobSnapshotPayload(
                CreateSongQuery(group.Query),
                SnapshotCollections.Freeze(group.Songs.Select(song => CreateJob(song, 0, visited))),
                group.ExecutedSearchDefinition,
                group.MemberOutcomes),
            JobList list => new JobListSnapshotPayload(
                list.Count,
                SnapshotCollections.Freeze(list.Jobs.Select(child => CreateJob(child, 0, visited))),
                list.MemberOutcomes),
            RetrieveFolderJob retrieve => new RetrieveFolderJobSnapshotPayload(
                new PeerDirectoryIdentitySnapshot(
                    retrieve.Directory.Username,
                    retrieve.Directory.FolderPath),
                retrieve.Result == null ? null : CreatePeerDirectory(retrieve.Result),
                retrieve.NewFilesFoundCount,
                retrieve.RetrievalOutcome,
                retrieve.RetrievalCancelled),
            _ => CreateExecutionPayload(job),
        };

    private static JobExecutionPayload CreateExecutionPayload(Job job)
        => job switch
        {
            ExtractJob extract => new ExtractJobSnapshotPayload(
                extract.Input,
                extract.InputType?.ToString(),
                extract.Result?.Id,
                extract.AutoProcessResult),
            SearchJob search => new SearchJobSnapshotPayload(
                search.QueryText,
                search.DefaultFileProjection == null
                    ? null
                    : new FileSearchProjectionSnapshot(
                        CreateSongQuery(search.DefaultFileProjection.Query),
                        search.DefaultFileProjection.IncludeFullResults),
                search.DefaultFolderProjection == null
                    ? null
                    : new FolderSearchProjectionSnapshot(
                        CreateAlbumQuery(search.DefaultFolderProjection.Query),
                        search.DefaultFolderProjection.IncludeFiles),
                search.ResultCount,
                search.Revision,
                search.IsComplete,
                search.Definition),
            SongJob song => new SongJobSnapshotPayload(
                CreateSongQuery(song.Query),
                song.Candidates?.Count,
                song.ResolvedTarget == null ? null : CreateFileCandidate(song.ResolvedTarget),
                song.ExactTarget,
                song.DownloadSource,
                CreateFileDownloadState(song),
                song.ExecutedSearchDefinition,
                song.LastAttemptedCandidate == null ? null : CreateFileCandidate(song.LastAttemptedCandidate)),
            RemoteFileJob remoteFile => new RemoteFileJobSnapshotPayload(
                remoteFile.Target,
                remoteFile.OutputPath,
                CreateFileDownloadState(remoteFile)),
            AlbumJob album => new AlbumExecutionPayload(
                CreateAlbumQuery(album.Query), album.Results.Count,
                album.ResolvedTarget == null ? null : CreateAlbumFolderSummary(album.ResolvedTarget),
                CreateDirectoryDownloadState(album), album.ExecutedSearchDefinition),
            RemoteDirectoryJob directory => CreateRemoteDirectoryExecution(directory),
            SongGroupsJob group => new SongGroupsExecutionPayload(
                CreateSongQuery(group.Query), group.ExecutedSearchDefinition, group.MemberOutcomes),
            AlbumGroupsJob group => CreateAlbumGroupsPayload(group),
            JobList list => new JobListExecutionPayload(list.MemberOutcomes),
            RetrieveFolderJob retrieve => new RetrieveFolderExecutionPayload(
                new(retrieve.Directory.Username, retrieve.Directory.FolderPath), retrieve.NewFilesFoundCount,
                retrieve.RetrievalOutcome, retrieve.RetrievalCancelled),
            _ => new GenericJobSnapshotPayload(job.ToString(noInfo: true)),
        };

    private static AlbumFolderSummarySnapshot CreateAlbumFolderSummary(AlbumFolder folder)
    {
        var first = folder.Files.FirstOrDefault()?.Candidate;
        return new(folder.Username, folder.FolderPath,
            new(folder.Username, first?.HasFreeUploadSlot, first?.UploadSpeed),
            folder.SearchFileCount, folder.SearchAudioFileCount, folder.IsFullyRetrieved);
    }

    private static RemoteDirectoryExecutionPayload CreateRemoteDirectoryExecution(RemoteDirectoryJob job)
    {
        var peer = (job.Source as RemoteDirectorySource.PeerDirectory)?.Directory;
        var sourcePlan = (job.Source as RemoteDirectorySource.Resolved)?.Plan;
        var plan = job.ActiveAttempt?.Plan;
        return new(
            peer != null ? RemoteDirectorySourceSnapshotKind.PeerDirectory : RemoteDirectorySourceSnapshotKind.Resolved,
            peer == null ? null : new(peer.Username, peer.FolderPath),
            peer?.Username ?? plan?.SourceDirectory?.Username ?? sourcePlan?.SourceDirectory?.Username
                ?? plan?.Entries.FirstOrDefault()?.Target.Username ?? sourcePlan?.Entries.FirstOrDefault()?.Target.Username,
            peer?.FolderPath ?? plan?.SourceDirectory?.FolderPath ?? sourcePlan?.SourceDirectory?.FolderPath,
            plan?.DisplayRoot ?? sourcePlan?.DisplayRoot ?? peer?.FolderPath,
            CreateDirectoryDownloadState(job));
    }

    private static AlbumGroupsJobSnapshotPayload CreateAlbumGroupsPayload(
        AlbumGroupsJob job)
    {
        return new AlbumGroupsJobSnapshotPayload(
            CreateAlbumQuery(job.Query),
            job.MemberOutcomes,
            job.ExecutedSearchDefinition);
    }

    internal static FileDownloadStateSnapshot CreateFileDownloadState(FileDownloadJob job)
        => new(job.DownloadPath, job.BytesTransferred, job.FileSize, job.ExistingFileSource, job.Reused);

    private static DirectoryDownloadStateSnapshot CreateDirectoryDownloadState(DirectoryDownloadJob job)
    {
        var progress = job.ProgressSummary;
        long plannedBytes = job.ActiveAttempt?.Plan.TotalKnownBytes ?? 0;
        return new(
            job.DirectoryState switch
            {
                DirectoryExecutionState.Unresolved => "unresolved",
                DirectoryExecutionState.Resolving => "resolving",
                DirectoryExecutionState.Planned => "planned",
                DirectoryExecutionState.Transferring => "transferring",
                _ => "unknown",
            },
            job.ActiveAttempt?.AttemptNumber,
            job.DownloadPath,
            progress.FileCount,
            progress.TerminalFileCount,
            progress.SuccessfulFileCount,
            progress.FailedFileCount,
            progress.BytesTransferred,
            plannedBytes + progress.SupplementalKnownBytes,
            (job.ActiveAttempt?.Plan.UnknownFileSizeCount ?? 0) + progress.SupplementalUnknownCount,
            progress.GetProgress(plannedBytes), job.ExistingFileSource,
            progress.SkippedFileCount, progress.DownloadedFileCount, progress.ReusedFileCount);
    }

    private static RemoteDirectoryJobSnapshotPayload CreateRemoteDirectoryPayload(
        RemoteDirectoryJob job,
        HashSet<Guid> visited)
    {
        var sourceKind = job.Source is RemoteDirectorySource.PeerDirectory
            ? RemoteDirectorySourceSnapshotKind.PeerDirectory
            : RemoteDirectorySourceSnapshotKind.Resolved;
        var directorySource = job.Source is RemoteDirectorySource.PeerDirectory peer
            ? new PeerDirectoryIdentitySnapshot(peer.Directory.Username, peer.Directory.FolderPath)
            : null;
        var resolvedPlan = job.Source is RemoteDirectorySource.Resolved resolved
            ? CreateDirectoryTransferPlan(resolved.Plan)
            : null;

        return new RemoteDirectoryJobSnapshotPayload(
            sourceKind,
            directorySource,
            resolvedPlan,
            job.ResolvedDirectory == null ? null : CreatePeerDirectory(job.ResolvedDirectory),
            CreateDirectoryManifest(job,
                job.Source is RemoteDirectorySource.Resolved resolvedSource
                    && ReferenceEquals(job.ActiveAttempt?.Plan, resolvedSource.Plan)
                        ? resolvedPlan : null),
            SnapshotCollections.Freeze(job.FileJobs.Select(child => CreateJob(child, 0, visited))),
            CreateDirectoryDownloadState(job));
    }

    private static DirectoryManifestSnapshot? CreateDirectoryManifest(
        DirectoryDownloadJob job,
        DirectoryTransferPlanSnapshot? existingPlan = null)
    {
        DirectoryTransferAttempt? attempt = job.ActiveAttempt;
        if (attempt == null)
            return null;
        int plannedChildren = attempt.ChildrenMaterialized ? attempt.FileJobs.Count : 0;
        return new DirectoryManifestSnapshot(
            attempt.AttemptNumber,
            existingPlan ?? CreateDirectoryTransferPlan(attempt.Plan),
            attempt.ChildrenMaterialized,
            SnapshotCollections.Freeze(attempt.FileJobs.Select(child => child.Id)),
            SnapshotCollections.Freeze(job.FileJobs.Skip(plannedChildren).Select(child => child.Id)),
            SnapshotCollections.Freeze(job.FileJobs.Skip(plannedChildren).Select(child => child switch
            {
                SongJob { ResolvedPeerTarget: { } target } => target,
                RemoteFileJob remote => remote.Target,
                _ => null,
            })));
    }

    private static AlbumFileSnapshot CreateAlbumFile(AlbumFile file)
        => new(CreateSongQuery(file.Query), CreateFileCandidate(file.Candidate));

    private static DiscoverySnapshot? CreateDiscovery(DiscoverySummary? discovery)
        => discovery == null
            ? null
            : new DiscoverySnapshot(
                discovery.RawResultCount,
                discovery.LockedFileCount,
                discovery.ObservedPeerCount);

    private static bool CanCancel(Job job, JobStateSnapshot state)
        => state.LifecycleState != JobLifecycleState.Terminal
            && job.Cts != null
            && !job.Cts.IsCancellationRequested;

    internal static JobSnapshotKind GetJobKind(Job job)
        => job switch
        {
            ExtractJob => JobSnapshotKind.Extract,
            SearchJob => JobSnapshotKind.Search,
            SongJob => JobSnapshotKind.Song,
            AlbumJob => JobSnapshotKind.Album,
            RemoteFileJob => JobSnapshotKind.RemoteFile,
            RemoteDirectoryJob => JobSnapshotKind.RemoteDirectory,
            SongGroupsJob => JobSnapshotKind.SongGroups,
            AlbumGroupsJob => JobSnapshotKind.AlbumGroups,
            JobList => JobSnapshotKind.JobList,
            RetrieveFolderJob => JobSnapshotKind.RetrieveFolder,
            _ => JobSnapshotKind.Generic,
        };

    private static SongQuerySnapshot CreateSongQuery(SongQuery query)
        => new(
            Optional(query.Artist),
            Optional(query.Title),
            Optional(query.Album),
            Optional(query.URI),
            Optional(query.Length),
            query.ArtistMaybeWrong);

    private static AlbumQuerySnapshot CreateAlbumQuery(AlbumQuery query)
        => new(
            Optional(query.Artist),
            Optional(query.Album),
            Optional(query.SearchHint),
            Optional(query.URI),
            query.ArtistMaybeWrong);

    private static string? Optional(string value)
        => value.Length > 0 ? value : null;

    private static int? Optional(int value)
        => value >= 0 ? value : null;

    private static string CandidateKey(FileCandidateSnapshot candidate)
        => CandidateKey(new PeerFileIdentity(candidate.Username, candidate.Filename));

    private static string CandidateKey(PeerFileIdentity identity)
        => string.Join('\0', identity.Username, identity.Filename);
}
