using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<RefSelectionMode>))]
public enum RefSelectionMode
{
    Only,
    AllExcept,
}

/// <summary>
/// Client-owned selection over stable refs from one immutable resource revision.
/// Refs are included for Only and excluded for AllExcept.
/// </summary>
public sealed record RefSelectionExpressionDto(
    RefSelectionMode Mode,
    IReadOnlyList<string> Refs);

/// <summary>One durable accepted intent. Runtime jobs are traversed through /api/jobs?submissionId=.</summary>
public sealed record SubmissionSummaryDto(
    Guid SubmissionId,
    DateTimeOffset SubmittedAtUtc,
    Guid? RerunOfSubmissionId,
    Guid? PreviewId,
    string? ArtifactId,
    long Revision,
    int TotalJobCount,
    int UserRootJobCount,
    int ActiveJobCount,
    int FailedJobCount);

/// <summary>Safe submission metadata. The retained command/settings remain server-owned.</summary>
public sealed record SubmissionDetailDto(
    SubmissionSummaryDto Summary,
    ServerJobKind? CommandKind,
    IReadOnlyList<string> CredentialBindings);

/// <summary>A counted reason, with an optional explanation for this group of entries.</summary>
public sealed record SubmissionReasonCountDto(string Reason, long Count, string? Message = null);

public sealed record DeleteSubmissionResponseDto(
    Guid SubmissionId,
    int AffectedSubmissionCount,
    int AffectedJobCount,
    int RejectedSubmissionCount,
    IReadOnlyList<SubmissionReasonCountDto> RejectionReasons);

[JsonConverter(typeof(JsonStringEnumConverter<CompletedJobCategory>))]
public enum CompletedJobCategory { Searches, Downloads }

public sealed record DeleteCompletedJobsRequestDto([property: JsonRequired] CompletedJobCategory Category);
public sealed record DeleteCompletedJobsResponseDto(int DeletedSubmissions, int DeletedJobs, int SkippedSubmissions);
