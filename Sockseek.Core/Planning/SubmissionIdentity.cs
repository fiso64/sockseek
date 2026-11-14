using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Planning;

public enum JobSemanticRole
{
    UserRoot,
    SemanticResult,
    Orchestration,
    ExecutionChild,
}

// Retention follows the operation's intent, independently of execution kind.
public enum JobRetention
{
    Memory,
    History,
    SearchResults,
}

public static class SubmissionIdentity
{
    public static Guid AssignAccepted(
        Job root,
        DownloadSettings effectiveSettings,
        SubmissionSourceRevision? sourceRevision = null,
        Guid? submissionId = null,
        DateTimeOffset? submittedAtUtc = null,
        Guid? rerunOfSubmissionId = null,
        Guid? previewId = null,
        string? artifactId = null)
        => AssignAccepted(
            root,
            SubmissionSpecification.Create(root, effectiveSettings, sourceRevision),
            submissionId,
            submittedAtUtc,
            rerunOfSubmissionId,
            previewId,
            artifactId);

    public static Guid AssignAccepted(
        Job root,
        SubmissionSpecification? specification,
        Guid? submissionId = null,
        DateTimeOffset? submittedAtUtc = null,
        Guid? rerunOfSubmissionId = null,
        Guid? previewId = null,
        string? artifactId = null)
    {
        Guid id = submissionId ?? Guid.NewGuid();
        DateTimeOffset created = submittedAtUtc ?? DateTimeOffset.UtcNow;
        string? specificationJson = root.Retention == JobRetention.Memory ? null
            : SubmissionSpecificationCodec.Serialize((specification
                ?? throw new ArgumentNullException(nameof(specification))).BindAcceptedJobIds(root));
        AssignTree(root, id, created, JobSemanticRole.UserRoot);
        root.SubmissionSpecificationJson = specificationJson;
        root.RerunOfSubmissionId = rerunOfSubmissionId;
        root.PreviewId = previewId;
        root.ArtifactId = artifactId;
        return id;
    }

    public static void AssignGeneratedResult(Job source, Job result)
    {
        AssignTree(
            result,
            source.SubmissionId,
            DateTimeOffset.UtcNow,
            JobSemanticRole.SemanticResult, source.Retention);
    }

    public static void AssignExecutionChild(Job parent, Job child)
    {
        child.SelectionSource = parent.SelectionSource;
        AssignTree(
            child,
            parent.SubmissionId,
            DateTimeOffset.UtcNow,
            JobSemanticRole.ExecutionChild, parent.Retention);
    }

    private static void AssignTree(
        Job job,
        Guid? submissionId,
        DateTimeOffset createdAtUtc,
        JobSemanticRole role, JobRetention? parentRetention = null)
    {
        if (parentRetention == JobRetention.Memory) job.Retention = JobRetention.Memory;
        job.SubmissionId = submissionId;
        job.CreatedAtUtc ??= createdAtUtc;
        job.SemanticRole = job switch
        {
            ExtractJob or JobList when role != JobSemanticRole.UserRoot
                => JobSemanticRole.Orchestration,
            _ => role,
        };
        switch (job)
        {
            case ExtractJob { Result: { } result }:
                AssignTree(
                    result,
                    submissionId,
                    createdAtUtc,
                    JobSemanticRole.SemanticResult, job.Retention);
                break;
            case JobList list:
                foreach (Job child in list.Jobs)
                    AssignTree(child, submissionId, createdAtUtc, JobSemanticRole.ExecutionChild, job.Retention);
                break;
            case SongGroupsJob group:
                foreach (SongJob song in group.Songs)
                    AssignTree(song, submissionId, createdAtUtc, JobSemanticRole.ExecutionChild, job.Retention);
                break;
            case AlbumGroupsJob group:
                foreach (AlbumJob album in group.Albums)
                    AssignTree(album, submissionId, createdAtUtc, JobSemanticRole.ExecutionChild, job.Retention);
                break;
            case AlbumJob album:
                foreach (SongJob song in album.TrackJobs)
                    AssignTree(song, submissionId, createdAtUtc, JobSemanticRole.ExecutionChild, job.Retention);
                break;
            case RemoteDirectoryJob directory:
                foreach (RemoteFileJob file in directory.FileJobs)
                    AssignTree(file, submissionId, createdAtUtc, JobSemanticRole.ExecutionChild, job.Retention);
                break;
        }
    }
}
