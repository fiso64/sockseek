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
    ServerSearchViewProjectionKind? Kind = null,
    SongQueryDto? SongQuery = null,
    AlbumQueryDto? AlbumQuery = null,
    bool IncludeFullResults = false,
    SearchSettingsPatchDto? RefinementSettings = null,
    string? FilterText = null,
    ServerSearchViewSortOrder? SortOrder = null);

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
    long SelectableOptionCount);

public sealed record SearchViewSummaryDto(
    Guid ViewId,
    Guid SourceJobId,
    ServerSearchViewProjectionKind Kind,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewRetentionState RetentionState,
    SearchViewCountersDto Counters,
    ServerSearchViewSortOrder SortOrder = ServerSearchViewSortOrder.Relevance);

public sealed record SearchViewDefinitionDto(
    Guid ViewId,
    CreateSearchViewRequestDto Projection,
    SearchSettingsPatchDto EffectiveSearchSettings);

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
    ServerSearchResultVisibility Visibility,
    ServerSearchPreferenceTier PreferenceTier,
    bool NecessaryConditionsSatisfied,
    IReadOnlyList<ServerSearchPreferenceCondition> SatisfiedPreferredConditions,
    IReadOnlyList<ServerSearchPreferenceCondition> UnsatisfiedPreferredConditions,
    string RemoteFilename,
    PeerInfoDto Peer,
    FileMetadataDto File);

public sealed record SearchViewFilePageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewFileDto> Items,
    string? NextCursor);

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
    ServerSearchPreferenceTier PreferenceTier,
    IReadOnlyList<ServerSearchPreferenceCondition> SatisfiedPreferredConditions,
    long PublicMatchingFileCount,
    long LockedMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingBytes,
    long? RetrievedFileCount,
    long? RetrievedBytes,
    SearchViewDirectoryRetrievalState RetrievalState,
    PeerInfoDto Peer,
    SearchViewFileDto BestChild,
    SearchViewDirectoryFilePreviewDto? InitialFiles = null);

public sealed record SearchViewDirectoryPageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewDirectoryDto> Items,
    string? NextCursor);

public sealed record SearchViewDirectoryFileDto(
    string Ref,
    string RelativePath,
    SearchViewFileDto File);

public sealed record SearchViewDirectoryFilePreviewDto(
    IReadOnlyList<SearchViewDirectoryFileDto> Items,
    string? NextCursor);

public sealed record SearchViewDirectoryFilePageDto(
    SearchViewRevisionDto Revision,
    PeerDirectoryRefDto Directory,
    IReadOnlyList<SearchViewDirectoryFileDto> Items,
    string? NextCursor,
    SearchViewDirectoryDto DirectorySummary);

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
    string? NextCursor);

public sealed record SearchViewTrackGroupOptionPageDto(
    SearchViewRevisionDto Revision,
    string GroupRef,
    IReadOnlyList<SearchViewFileDto> Items,
    string? NextCursor);

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
    string? NextCursor);

public sealed record SearchViewAlbumGroupOptionPageDto(
    SearchViewRevisionDto Revision,
    string GroupRef,
    IReadOnlyList<SearchViewDirectoryDto> Items,
    string? NextCursor);

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
    bool Selected);

/// <summary>Read-only effective defaults for the job kinds in a client-owned selection.</summary>
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

public sealed record CountSearchViewSelectionResponseDto(
    long Revision,
    long PublicFileCount,
    long LockedFileCount,
    long SelectedItemCount);

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
    IReadOnlyList<SubmissionReasonCountDto> RejectionReasons);
