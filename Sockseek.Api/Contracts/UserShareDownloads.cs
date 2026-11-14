using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserShareDirectorySelectionDto), "directory")]
[JsonDerivedType(typeof(UserShareFileSelectionDto), "file")]
public abstract record UserShareSelectionDto;

public sealed record UserShareDirectorySelectionDto(long DirectoryId)
    : UserShareSelectionDto;

public sealed record UserShareFileSelectionDto(long FileId)
    : UserShareSelectionDto;

public sealed record StartUserShareDownloadsRequestDto(
    Guid RequestId,
    IReadOnlyList<UserShareSelectionDto> Selections,
    SubmissionOptionsDto? Options = null,
    IReadOnlyList<long>? ExcludedFileIds = null,
    IReadOnlyList<long>? ExcludedDirectoryIds = null);

public sealed record UserShareResolutionSummaryDto(
    int CanonicalDirectoryRoots,
    int StandaloneFiles,
    long TotalPublicFiles,
    long TotalPublicBytes,
    int RedundantSelectionsRemoved,
    long LockedBranchesSkipped,
    string OutputParent);

public sealed record StartUserShareDownloadsResponseDto(
    JobSummaryDto Workflow,
    UserShareResolutionSummaryDto Resolution);
