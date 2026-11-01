using Sockseek.Api;
using Sockseek.Core.Planning;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

internal static class SubmissionDtoMapper
{
    public static SubmissionSummaryDto ToSummary(PersistedSubmissionSummary submission)
        => new(
            submission.Id,
            submission.SubmittedAtUtc,
            submission.RerunOfSubmissionId,
            submission.PreviewId,
            submission.ArtifactId,
            submission.Revision,
            submission.ArchivedAtUtc,
            submission.TotalJobCount,
            submission.UserRootJobCount,
            submission.ActiveJobCount,
            submission.FailedJobCount);

    public static SubmissionDetailDto ToDetail(PersistedSubmission submission)
    {
        try
        {
            SubmissionSpecification specification =
                SubmissionSpecificationCodec.Deserialize(submission.SpecificationJson);
            return new SubmissionDetailDto(
                ToSummary(submission),
                ServerSnapshotMapper.ToServerJobKind(specification.Command.Kind),
                specification.CredentialBindings);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or System.Text.Json.JsonException)
        {
            // The durable row and its jobs remain readable if a future/legacy
            // command schema is not executable by this server version.
            return new SubmissionDetailDto(ToSummary(submission), null, []);
        }
    }

    public static SubmissionArchiveResponseDto ToArchiveResponse(SubmissionArchiveResult result)
        => new(
            result.SubmissionId,
            result.Archived,
            result.AffectedSubmissionCount,
            result.AffectedJobCount,
            result.RejectedSubmissionCount,
            result.RejectionReason == null
                ? []
                : [new SubmissionReasonCountDto(result.RejectionReason, result.RejectedSubmissionCount)]);
}
