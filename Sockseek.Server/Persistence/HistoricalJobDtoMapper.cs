using System.Text.Json;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

internal static class HistoricalJobDtoMapper
{
    public static async Task<SearchDefinition> SearchDefinitionAsync(
        ISubmissionStore? submissions,
        PersistedJob job,
        JobExecutionPayload? retainedPayload,
        CancellationToken cancellationToken)
    {
        SearchDefinition? retainedJobDefinition = RetainedJobPayloadCodec.Definition(retainedPayload);
        if (job.SubmissionId is Guid submissionId)
        {
            if (submissions == null)
                return retainedJobDefinition ?? throw MissingSearchDefinition(job.Id);
            PersistedSubmission? submission = await submissions.GetSubmissionAsync(
                submissionId,
                cancellationToken).ConfigureAwait(false);
            if (submission == null)
                return retainedJobDefinition ?? throw MissingSearchDefinition(job.Id);
            SubmissionSpecification specification = SubmissionSpecificationCodec.Deserialize(
                submission.SpecificationJson);
            SearchDefinition? acceptedDefinition = specification.Search
                ?? specification.Command.SearchDefinition;
            if (acceptedDefinition != null && retainedJobDefinition != null
                && !string.Equals(
                    SearchDefinitionCodec.Serialize(acceptedDefinition),
                    SearchDefinitionCodec.Serialize(retainedJobDefinition),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Historical search {job.Id} has conflicting accepted and execution definitions.");
            }

            // A directly accepted search is fully described by its immutable
            // submission. Extractors can create search children only after the
            // root was accepted; their derived execution definition therefore
            // belongs to the retained search-job record.
            return acceptedDefinition
                ?? retainedJobDefinition
                ?? throw MissingSearchDefinition(job.Id);
        }
        return retainedJobDefinition ?? throw MissingSearchDefinition(job.Id);
    }

    private static InvalidOperationException MissingSearchDefinition(Guid jobId)
        => new($"Historical search {jobId} has no retained search definition and cannot be reprojected authoritatively.");

    public static FolderSearchProjection? DefaultFolderProjection(JobExecutionPayload? payload)
        => payload is SearchJobSnapshotPayload { DefaultFolderProjection: { } projection }
            ? new(new AlbumQuery
            {
                Artist = projection.Query.Artist ?? "", Album = projection.Query.Album ?? "",
                SearchHint = projection.Query.SearchHint ?? "", URI = projection.Query.URI ?? "",
                ArtistMaybeWrong = projection.Query.ArtistMaybeWrong,
            }, projection.IncludeFiles) : null;

    public static JobSummaryDto ToSummary(PersistedJob job)
        => ToSummary(job, ReadSnapshot(job, tolerateInvalidPayload: true));

    internal static JobSummaryDto ToSummary(PersistedJob job, JobSnapshot snapshot)
    {
        SearchDefinition? searchDefinition = (snapshot.Payload as SearchJobSnapshotPayload)?.Definition;
        return new(
            job.Id,
            checked((int)job.DisplayId),
            job.WorkflowId,
            Parse(job.Kind, ServerJobKind.Generic),
            Parse(job.LifecycleState, ServerJobLifecycleState.Pending),
            Parse(job.ActivityPhase, ServerJobActivityPhase.None),
            job.ActivityUntilUtc,
            Parse(job.TerminalOutcome, ServerJobTerminalOutcome.None),
            Parse(job.SkipReason, ServerJobSkipReason.None),
            job.ItemName,
            job.QueryText,
            job.FailureReason == "None" ? null : Parse(job.FailureReason, ServerJobFailureReason.Other),
            job.FailureMessage,
            job.ParentJobId,
            job.ResultJobId,
            job.SourceJobId,
            ToNullableInt(job.DiscoveryPublicFileCount),
            ToNullableInt(job.DiscoveryLockedFileCount),
            [],
            [],
            job.FailureDetail,
            Parse(job.CancellationSource, ServerJobCancellationSource.None),
            SubmissionId: job.SubmissionId,
            Role: Enum.Parse<ServerJobRole>(job.SemanticRole),
            CreatedAtUtc: job.CreatedAtUtc,
            DiscoveryPublicFileCount: ToNullableInt(job.DiscoveryPublicFileCount),
            DiscoveryObservedPeerCount: ToNullableInt(job.DiscoveryObservedPeerCount),
            Stats: ServerSnapshotMapper.ToJobSummaryStats(snapshot),
            SearchDefaultProjection: searchDefinition?.DefaultProjection.ToServer(),
            SearchGroupResults: searchDefinition?.GroupResults,
            SourceDetail: job.SourceDetail,
            PrintOption: ((PrintOption)job.PrintOption).ToServer());
    }

    // A detail/manifest read decodes once and shares that capture between summary,
    // presentation and selected-file mapping. No cache retains database rows.
    internal static JobSnapshot ReadSnapshot(PersistedJob job, bool tolerateInvalidPayload = false)
    {
        JobExecutionPayload? payload;
        try { payload = RetainedJobPayloadCodec.Read(job); }
        catch (Exception error) when (tolerateInvalidPayload
            && error is JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            // An invalid optional payload cannot hide other rows in a listing.
            payload = new GenericJobSnapshotPayload("Retained job details are unavailable.");
        }
        // Relationships belong to the retained header, including forward-reference
        // resolution after the execution payload was captured.
        if (payload is ExtractJobSnapshotPayload extract)
            payload = extract with { ResultJobId = job.ResultJobId };
        return new(new JobSnapshotHeader(
            job.Id, checked((int)job.DisplayId), job.WorkflowId, job.SubmissionId,
            Enum.Parse<JobSemanticRole>(job.SemanticRole), job.CreatedAtUtc,
            null, null, null, null, Parse(job.Kind, JobSnapshotKind.Generic), job.Revision,
            Parse(job.LifecycleState, JobLifecycleState.Pending), Parse(job.ActivityPhase, JobActivityPhase.None),
            job.ActivityUntilUtc, Parse(job.TerminalOutcome, JobTerminalOutcome.None),
            Parse(job.SkipReason, JobSkipReason.None), Parse(job.CancellationSource, JobCancellationSource.None),
            Parse(job.FailureReason, JobFailureReason.Other), job.FailureMessage, job.FailureDetail,
            job.ItemName, job.QueryText, null, [], (PrintOption)job.PrintOption, false, job.SourceDetail),
            payload ?? new GenericJobSnapshotPayload(job.Kind));
    }

    public static JobPayloadDto ToPayload(PersistedJob job) => ToPayload(ReadSnapshot(job));

    internal static JobPayloadDto ToPayload(JobSnapshot snapshot)
        => ServerSnapshotMapper.ToJobPayload(snapshot, _ => null);

    internal static JobDetailDto ToDetail(PersistedJob job, int childCount)
    {
        var snapshot = ReadSnapshot(job);
        return new(ToSummary(job, snapshot), ToPayload(snapshot), childCount);
    }

    internal static PeerFileTargetDto? ToSelectedFileTarget(PersistedJob job)
        => ToSelectedFileTarget(ReadSnapshot(job));

    internal static PeerFileTargetDto? ToSelectedFileTarget(JobSnapshot snapshot)
    {
        PeerFileTargetDto? target = snapshot.Payload switch
        {
            RemoteFileJobSnapshotPayload file => ServerSnapshotMapper.ToPeerFileTargetDto(file.Target),
            SongJobSnapshotPayload { ResolvedTarget: { } file } => ServerSnapshotMapper.ToPeerFileTargetDto(file),
            SongJobSnapshotPayload { ExactTarget: { } file } => ServerSnapshotMapper.ToPeerFileTargetDto(file),
            SongJobSnapshotPayload { LastAttemptedCandidate: { } file } => ServerSnapshotMapper.ToPeerFileTargetDto(file),
            _ => null,
        };
        return target is { Size: < 0 } ? target with { Size = null } : target;
    }

    private static int? ToNullableInt(long? value)
        => value.HasValue ? checked((int)value.Value) : null;

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
