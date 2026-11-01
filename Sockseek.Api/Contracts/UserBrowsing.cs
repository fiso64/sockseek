using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<UserBrowseState>))]
public enum UserBrowseState
{
    [JsonStringEnumMemberName("queued")]
    Queued,
    [JsonStringEnumMemberName("running")]
    Running,
    [JsonStringEnumMemberName("complete")]
    Complete,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter<UserBrowsePhase>))]
public enum UserBrowsePhase
{
    [JsonStringEnumMemberName("waiting-for-peer")]
    WaitingForPeer,
    [JsonStringEnumMemberName("receiving")]
    Receiving,
    [JsonStringEnumMemberName("indexing")]
    Indexing,
    [JsonStringEnumMemberName("ready")]
    Ready,
}

[JsonConverter(typeof(JsonStringEnumConverter<ShareVisibility>))]
public enum ShareVisibility
{
    [JsonStringEnumMemberName("public")]
    Public,
    [JsonStringEnumMemberName("locked")]
    Locked,
    [JsonStringEnumMemberName("mixed")]
    Mixed,
}

public sealed record StartUserBrowseRequestDto(bool Refresh = false);

public sealed record UserBrowseDto(
    Guid BrowseId,
    string Username,
    UserBrowseState State,
    UserBrowsePhase Phase,
    long CompressedBytesReceived,
    long? CompressedBytesExpected,
    long DirectoryCount,
    long FileCount,
    long TotalFileBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    ApiErrorDto? Failure,
    long Revision);

public sealed record PageDto<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public sealed record BrowseDirectoryEntryDto(
    long DirectoryId,
    long? ParentId,
    string Name,
    string DisplayPath,
    ShareVisibility Visibility,
    bool IsSynthetic,
    long DirectDirectoryCount,
    long DirectFileCount,
    long RecursiveFileCount,
    long RecursiveFileBytes,
    long LockedDescendantCount,
    bool HasChildren,
    long? TotalRecursiveFileCount = null,
    long? TotalRecursiveFileBytes = null);

public sealed record BrowseFileEntryDto(
    long FileId,
    long DirectoryId,
    ShareVisibility Visibility,
    FileMetadataDto File);

[JsonConverter(typeof(JsonStringEnumConverter<BrowseSearchEntryKind>))]
public enum BrowseSearchEntryKind
{
    [JsonStringEnumMemberName("directory")]
    Directory,
    [JsonStringEnumMemberName("file")]
    File,
}

/// <summary>
/// One flat mixed-search row. <see cref="EntryId"/> is a directory ref for a
/// directory row and a file ref for a file row. <see cref="DisplayPath"/>
/// supplies bounded ancestor context without recursively embedding a tree.
/// </summary>
public sealed record BrowseSearchEntryDto(
    BrowseSearchEntryKind Kind,
    long EntryId,
    long DirectoryId,
    long? ParentDirectoryId,
    string Name,
    string DisplayPath,
    ShareVisibility Visibility,
    long PublicMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingFileCount,
    long LockedMatchingBytes,
    long? FileSize,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    long? PublicSubtreeFileCount = null,
    long? PublicSubtreeBytes = null,
    long? TotalSubtreeFileCount = null,
    long? TotalSubtreeBytes = null);

/// <summary>
/// A cursor page over one immutable browse revision. Totals describe the whole
/// filtered artifact and are therefore independent of the current page.
/// </summary>
public sealed record BrowseSearchPageDto(
    Guid BrowseId,
    long BrowseRevision,
    string Query,
    IReadOnlyList<BrowseSearchEntryDto> Items,
    long PublicMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingFileCount,
    long LockedMatchingBytes,
    string? NextCursor)
{
    public long MatchingFileCount
        => checked(PublicMatchingFileCount + LockedMatchingFileCount);

    public long MatchingBytes
        => PublicMatchingBytes > long.MaxValue - LockedMatchingBytes
            ? long.MaxValue
            : PublicMatchingBytes + LockedMatchingBytes;
}
