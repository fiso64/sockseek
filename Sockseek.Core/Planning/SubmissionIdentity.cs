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
        AssignTree(root, root.WorkflowId, id, created, JobSemanticRole.UserRoot);
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
            source.WorkflowId,
            source.SubmissionId,
            DateTimeOffset.UtcNow,
            JobSemanticRole.SemanticResult, source.Retention);
    }

    public static void AssignExecutionChild(Job parent, Job child)
    {
        child.SelectionSource = parent.SelectionSource;
        AssignTree(
            child,
            parent.WorkflowId,
            parent.SubmissionId,
            DateTimeOffset.UtcNow,
            JobSemanticRole.ExecutionChild, parent.Retention);
    }

    private static void AssignTree(
        Job job,
        Guid workflowId,
        Guid? submissionId,
        DateTimeOffset createdAtUtc,
        JobSemanticRole role, JobRetention? parentRetention = null)
    {
        if (parentRetention == JobRetention.Memory) job.Retention = JobRetention.Memory;
        job.WorkflowId = workflowId;
        job.SubmissionId = submissionId;
        job.CreatedAtUtc ??= createdAtUtc;
        job.SemanticRole = job switch
        {
            ExtractJob or JobList when role != JobSemanticRole.UserRoot
                => JobSemanticRole.Orchestration,
            _ => role,
        };
        var childRole = job is ExtractJob ? JobSemanticRole.SemanticResult : JobSemanticRole.ExecutionChild;
        var children = job.Children;
        for (int i = 0; i < children.Count; i++)
            AssignTree(children[i], workflowId, submissionId, createdAtUtc, childRole, job.Retention);
    }
}
