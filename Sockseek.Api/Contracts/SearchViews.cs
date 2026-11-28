using System.Text.Json.Serialization;

namespace Sockseek.Api;

// Completeness of the available source used by this projection. A complete
// live source does not imply its asynchronous history writes have committed.
[JsonConverter(typeof(JsonStringEnumConverter<SearchViewRetentionState>))]
public enum SearchViewRetentionState
{
    Live,
    Complete,
    Incomplete,
    Interrupted,
    Pruned,
}

public sealed record CreateSearchViewRequestDto(
    SearchViewProjectionKind? Kind = null,
    SongQueryDto? SongQuery = null,
    AlbumQueryDto? AlbumQuery = null,
    bool IncludeFullResults = false,
    SearchSettingsPatchDto? RefinementSettings = null,
    string? FilterText = null,
    SearchViewSortOrder? SortOrder = null,
    // Layer view-relevant settings from these named profiles before explicit refinements.
    IReadOnlyList<string>? ProfileNames = null,
    // Explicit File Search checks remain visible across changes to the text filter.
    IReadOnlyList<FileCandidateRefDto>? KeepVisibleFiles = null);

public sealed record SearchViewCountersDto(
    long PublicFileCount,
    long LockedFileCount,
    long PublicBytes,
    long LockedBytes,
    int ObservedPeerCount,
    long ProjectedFileCount,
    long ProjectedPublicFileCount,
    long ProjectedLockedFileCount,
    long PreferredFileCount,
    long OtherFileCount,
    long TopLevelItemCount,
    long SelectableOptionCount,
    SearchPreferenceMissesDto OtherMisses);

/// <summary>
/// For each preferred condition, how many listed results outside the preferred
/// tier miss it. Properties are named after <see cref="SearchPreferenceCondition"/>.
/// </summary>
public sealed record SearchPreferenceMissesDto(
    long Format,
    long Length,
    long Bitrate,
    long SampleRate,
    long BitDepth,
    long Title,
    long Artist,
    long Album,
    long Username,
    long ExcludeLocked);

public sealed record SearchViewSummaryDto(
    Guid ViewId,
    Guid SourceJobId,
    SearchViewProjectionKind Kind,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewRetentionState RetentionState,
    SearchViewCountersDto Counters,
    SearchViewSortOrder SortOrder = SearchViewSortOrder.Relevance,
    string? FilterText = null);

public sealed record SearchViewDefinitionDto(
    Guid ViewId,
    CreateSearchViewRequestDto Projection,
    SearchSettingsPatchDto EffectiveSearchSettings,
    // Settings before manual refinements, when different layers were requested.
    SearchSettingsPatchDto? BaseSearchSettings = null);

public sealed record SearchViewRevisionLifetimeDto(long RemainingMilliseconds);

public sealed record SearchViewRevisionDto(
    Guid ViewId,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewRetentionState RetentionState,
    SearchViewCountersDto Counters);

public sealed record SearchViewFileDto(
    string Ref,
    SearchResultVisibility Visibility,
    SearchPreferenceTier PreferenceTier,
    ResultRejection NecessaryRejection,
    IReadOnlyList<SearchPreferenceCondition> SatisfiedPreferredConditions,
    IReadOnlyList<SearchPreferenceCondition> UnsatisfiedPreferredConditions,
    string RemoteFilename,
    PeerInfoDto Peer,
    FileMetadataDto File);

/// <summary>One page of a search-view listing: the revision it was read from and where the next page starts.</summary>
public interface ISearchViewPage
{
    SearchViewRevisionDto Revision { get; }
    string? NextCursor { get; }
}

public sealed record SearchViewFilePageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewFileDto> Items,
    string? NextCursor) : ISearchViewPage;

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewDirectoryVisibility>))]
public enum SearchViewDirectoryVisibility
{
    Public,
    Locked,
    Mixed,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewDirectoryRetrievalState>))]
public enum SearchViewDirectoryRetrievalState
{
    SearchResultsOnly,
    Complete,
    Incomplete,
}

/// <summary>
/// View-scoped opaque reference composed with the exact peer directory
/// identity it resolves to in the bound revision. An empty FolderPath groups
/// observed bare filenames; it cannot be used to retrieve a peer directory.
/// </summary>
public sealed record PeerDirectoryRefDto(
    string Ref,
    string Username,
    string FolderPath);

public sealed record SearchViewDirectoryDto(
    PeerDirectoryRefDto Ref,
    SearchViewDirectoryVisibility Visibility,
    SearchPreferenceTier PreferenceTier,
    IReadOnlyList<SearchPreferenceCondition> SatisfiedPreferredConditions,
    long PublicMatchingFileCount,
    long LockedMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingBytes,
    long? RetrievedFileCount,
    long? RetrievedBytes,
    SearchViewDirectoryRetrievalState RetrievalState,
    PeerInfoDto Peer,
    SearchViewFileDto BestChild,
    SearchViewDirectoryFilePreviewDto? InitialFiles = null,
    bool HasFilteredFiles = false);

public sealed record SearchViewDirectoryPageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewDirectoryDto> Items,
    string? NextCursor) : ISearchViewPage;

public sealed record SearchViewDirectoryFileDto(
    string Ref,
    string RelativePath,
    SearchViewFileDto File,
    string? DeepestFilteredFolder = null);

public sealed record SearchViewDirectoryFilePreviewDto(
    IReadOnlyList<SearchViewDirectoryFileDto> Items,
    string? NextCursor);

public sealed record SearchViewDirectoryFilePageDto(
    SearchViewRevisionDto Revision,
    PeerDirectoryRefDto Directory,
    IReadOnlyList<SearchViewDirectoryFileDto> Items,
    string? NextCursor,
    SearchViewDirectoryDto DirectorySummary) : ISearchViewPage;

public sealed record RetrieveSearchViewDirectoryRequestDto(
    long Revision,
    PeerDirectoryRefDto Directory,
    Guid IdempotencyKey);

public sealed record SearchViewTrackGroupDto(
    string Ref,
    SongQueryDto Query,
    int ShareCount,
    long SelectableOptionCount,
    long OptionCount,
    SearchViewFileDto Representative);

public sealed record SearchViewTrackGroupPageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewTrackGroupDto> Items,
    string? NextCursor) : ISearchViewPage;

/// <summary>PreviousTo or NextTo returns one neighbor of the exact option in this revision.</summary>
public sealed record SearchViewGroupOptionQuery(string? FilterText = null, string? PreviousTo = null, string? NextTo = null);

public sealed record SearchViewTrackGroupOptionPageDto(
    SearchViewRevisionDto Revision,
    string GroupRef,
    IReadOnlyList<SearchViewFileDto> Items,
    string? NextCursor,
    IReadOnlyList<int>? Positions = null) : ISearchViewPage;

public sealed record SearchViewAlbumGroupDto(
    string Ref,
    AlbumQueryDto Query,
    int ShareCount,
    long SelectableOptionCount,
    long OptionCount,
    SearchViewDirectoryDto Representative);

public sealed record SearchViewAlbumGroupPageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewAlbumGroupDto> Items,
    string? NextCursor) : ISearchViewPage;

public sealed record SearchViewAlbumGroupOptionPageDto(
    SearchViewRevisionDto Revision,
    string GroupRef,
    IReadOnlyList<SearchViewDirectoryDto> Items,
    string? NextCursor,
    IReadOnlyList<int>? Positions = null) : ISearchViewPage;

/// <summary>Reads browser-held group choices against one immutable revision; it does not save them.</summary>
public sealed record ResolveSearchViewChosenOptionsRequestDto(
    long Revision,
    IReadOnlyDictionary<string, string> ChosenOptions);

public sealed record SearchViewChosenOptionDto(
    string GroupRef,
    string OptionRef,
    SearchViewFileDto? Track,
    SearchViewDirectoryDto? Album);

public sealed record ResolveSearchViewChosenOptionsResponseDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewChosenOptionDto> Items);

public sealed record SearchViewUpdateDto(
    SearchViewSummaryDto Summary,
    bool HasNewRevision);

public sealed record CommitSearchViewSelectionRequestDto(
    long Revision,
    RefSelectionExpressionDto Selection,
    Guid IdempotencyKey,
    // Exact file refs excluded from selected whole directories, in either mode.
    // This does not expand a directory into only its projected matching files.
    IReadOnlyList<string>? ExcludedFileRefs = null,
    // Explicit files added back when their directory is not selected.
    IReadOnlyList<string>? IncludedFileRefs = null,
    // Group ref -> exact alternative ref in this revision. Absent means all candidates.
    IReadOnlyDictionary<string, string>? ChosenOptions = null,
    // Album-result group ref -> selected files in the chosen directory.
    // Omitted groups include every public file; this is commit input, not saved UI state.
    IReadOnlyDictionary<string, RefSelectionExpressionDto>? AlbumFileSelections = null,
    // Shared download-time overrides, composed independently for each selected job.
    SubmissionOptionsDto? Options = null,
    // Browser-held exact identities for exclusions hidden in a replacement view.
    // Keys are stable file refs from the same source job, not saved UI state.
    IReadOnlyDictionary<string, FileCandidateRefDto>? ExcludedFileIdentities = null,
    // If no exact option was chosen for a result group, submit only the
    // representative shown by this revision instead of every fallback source.
    bool? UseRepresentativeWhenUnchosen = null,
    IReadOnlyList<SearchViewDirectoryPathRuleDto>? DirectoryPathRules = null);

/// <summary>A browser-owned selection override for matching files below a directory-relative subfolder.</summary>
public sealed record SearchViewDirectoryPathRuleDto(
    string DirectoryRef,
    string RelativePath,
    bool Selected,
    bool IncludeFilteredFiles = false);

/// <summary>Read-only effective defaults for the job kinds in a client-owned selection.</summary>
public sealed record SearchViewLibraryMatchesRequestDto(long Revision, IReadOnlyList<string> GroupRefs, SubmissionOptionsDto? Options = null);

public sealed record ResolveSearchViewSelectionSettingsRequestDto(
    long Revision,
    RefSelectionExpressionDto Selection,
    IReadOnlyList<string>? IncludedFileRefs = null,
    IReadOnlyList<SearchViewDirectoryPathRuleDto>? DirectoryPathRules = null,
    SubmissionOptionsDto? Options = null);

public sealed record SearchViewSelectionSettingsVariantDto(
    ServerJobKind JobKind,
    EffectiveDownloadSettingsDto Settings,
    IReadOnlyList<AppliedProfileDto> AppliedProfiles);

public sealed record ResolveSearchViewSelectionSettingsResponseDto(
    long Revision,
    IReadOnlyList<SearchViewSelectionSettingsVariantDto> Variants);

/// <summary>Counts files visible in the bound revision, not files a future folder browse might discover.</summary>
public sealed record CountSearchViewSelectionRequestDto(
    long Revision,
    RefSelectionExpressionDto Selection,
    IReadOnlyList<string>? ExcludedFileRefs = null,
    IReadOnlyList<string>? IncludedFileRefs = null,
    IReadOnlyDictionary<string, string>? ChosenOptions = null,
    IReadOnlyDictionary<string, RefSelectionExpressionDto>? AlbumFileSelections = null,
    IReadOnlyDictionary<string, FileCandidateRefDto>? ExcludedFileIdentities = null,
    IReadOnlyList<SearchViewDirectoryPathRuleDto>? DirectoryPathRules = null);

/// <param name="MissingRefs">Chosen top-level refs this revision no longer holds; they count as nothing.</param>
public sealed record CountSearchViewSelectionResponseDto(
    long Revision,
    long PublicFileCount,
    long LockedFileCount,
    long SelectedItemCount,
    IReadOnlyList<string>? MissingRefs = null);

public sealed record CommitSearchViewSelectionResponseDto(
    Guid ViewId,
    long ViewRevision,
    Guid? SubmissionId,
    Guid? WorkflowId,
    long RequestedCount,
    long ResolvedCount,
    long SubmittedCount,
    long SkippedCount,
    long RejectedCount,
    IReadOnlyList<SubmissionReasonCountDto> RejectionReasons,
    // Set when the selection became exactly one job, so a client can open it
    // instead of listing the submission.
    Guid? JobId = null);
