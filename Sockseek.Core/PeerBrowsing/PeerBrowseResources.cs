namespace Sockseek.Core.PeerBrowsing;

public enum PeerBrowseState
{
    Queued,
    Running,
    Complete,
    Failed,
    Cancelled,
}

public enum PeerBrowsePhase
{
    WaitingForPeer,
    Receiving,
    Indexing,
    Ready,
}

public enum PeerBrowseEntryVisibility
{
    Public,
    Locked,
    Mixed,
}

public sealed record PeerBrowseFailure(string Code, string Message);

public sealed record PeerBrowseResource(
    Guid BrowseId,
    string LocalAccount,
    string Username,
    PeerBrowseState State,
    PeerBrowsePhase Phase,
    long CompressedBytesReceived,
    long? CompressedBytesExpected,
    long DirectoryCount,
    long FileCount,
    long TotalFileBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset ExpiresAt,
    PeerBrowseFailure? Failure,
    long Revision);

public sealed record PeerBrowseDirectoryEntry(
    long DirectoryId,
    long? ParentId,
    string Name,
    string DisplayPath,
    PeerBrowseEntryVisibility Visibility,
    bool IsSynthetic,
    long DirectDirectoryCount,
    long DirectFileCount,
    long RecursiveFileCount,
    long RecursiveFileBytes,
    long LockedDescendantCount,
    bool HasChildren,
    long? TotalRecursiveFileCount = null,
    long? TotalRecursiveFileBytes = null);

public sealed record PeerBrowseFileAttribute(int Type, int Value);

public sealed record PeerBrowseFileEntry(
    long FileId,
    long DirectoryId,
    PeerBrowseEntryVisibility Visibility,
    string Name,
    long Size,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    IReadOnlyList<PeerBrowseFileAttribute>? Attributes);

public enum PeerBrowseSearchEntryKind
{
    Directory,
    File,
}

/// <summary>
/// One fixed-size row in the flat search projection of an immutable peer-browse
/// artifact. Directory rows summarize matching descendant files; file rows
/// summarize themselves. Exact wire identities remain owned by the artifact and
/// are addressed through the stable numeric refs.
/// </summary>
public sealed record PeerBrowseSearchEntry(
    PeerBrowseSearchEntryKind Kind,
    long EntryId,
    long DirectoryId,
    long? ParentDirectoryId,
    string Name,
    string DisplayPath,
    PeerBrowseEntryVisibility Visibility,
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

public sealed record PeerBrowseSearchPage(
    IReadOnlyList<PeerBrowseSearchEntry> Items,
    long PublicMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingFileCount,
    long LockedMatchingBytes,
    string? NextSortKey,
    PeerBrowseSearchEntryKind? NextKind,
    long? NextId);

public sealed record PeerBrowsePage<T>(
    IReadOnlyList<T> Items,
    string? NextSortKey,
    long? NextId);

public sealed record PeerBrowseResourcePage(
    IReadOnlyList<PeerBrowseResource> Items,
    DateTimeOffset? NextCreatedAt,
    Guid? NextBrowseId);

public sealed class PeerBrowseSelectionException(string message)
    : ArgumentException(message);

/// <summary>
/// Immutable ordinary-transfer plans resolved entirely from one browse artifact.
/// Compact artifact IDs never escape into the transfer model.
/// </summary>
public sealed record PeerBrowseDownloadResolution(
    IReadOnlyList<Sockseek.Core.Models.DirectoryTransferPlan> Plans,
    int CanonicalDirectoryRoots,
    int StandaloneFiles,
    long TotalPublicFiles,
    long TotalPublicBytes,
    int RedundantSelectionsRemoved,
    long LockedBranchesSkipped);
