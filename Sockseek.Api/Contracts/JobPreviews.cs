using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<JobPreviewState>))]
public enum JobPreviewState
{
    Planning,
    Ready,
    PartiallyReady,
    Failed,
    Committing,
    Committed,
    Expired,
}

public sealed record CreateJobPreviewRequestDto(
    JobDraftDto Job,
    SubmissionOptionsDto? Options = null);

public sealed record JobPreviewSummaryDto(
    Guid PreviewId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    JobPreviewState State,
    long Revision,
    int NodeCount,
    int ReadyNodeCount,
    int FailedNodeCount,
    int SelectableNodeCount,
    Guid? CommittedSubmissionId = null);

public sealed record JobPreviewNodeDto(
    string Ref,
    string? ParentRef,
    ServerJobRole Role,
    bool IsReady,
    bool IsSelectable,
    ServerJobKind Kind,
    string? ItemName,
    string? QueryText,
    int DirectChildCount,
    IReadOnlyList<AppliedProfileDto> AppliedProfiles,
    SongQueryDto? SongQuery = null,
    AlbumQueryDto? AlbumQuery = null,
    string? FailureCode = null,
    string? FailureMessage = null,
    int SelectableSubtreeCount = 0,
    string? SourceDetail = null,
    string? SourceLabel = null,
    string? ExtractorInfo = null,
    string? InputType = null);

public sealed record CreateJobPreviewResponseDto(JobPreviewSummaryDto Preview);

public sealed record CommitJobPreviewRequestDto(
    long Revision,
    RefSelectionExpressionDto Selection,
    Guid IdempotencyKey,
    IReadOnlyList<JobPreviewSelectionOverrideDto>? SubtreeOverrides = null);

/// <summary>
/// Client-owned selection rule for this preview node and its descendants. The
/// most specific ancestor rule wins; otherwise Selection.Mode supplies the
/// default. When these rules are used, Selection.Refs must be empty.
/// </summary>
public sealed record JobPreviewSelectionOverrideDto(string Ref, bool Selected);

public sealed record CommitJobPreviewResponseDto(
    Guid PreviewId,
    Guid? SubmissionId,
    Guid? WorkflowId,
    int RequestedCount,
    int ResolvedCount,
    int SubmittedCount,
    int SkippedCount,
    int RejectedCount,
    IReadOnlyList<SubmissionReasonCountDto> RejectionReasons);
