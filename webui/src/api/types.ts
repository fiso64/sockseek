import type { components } from './generated';

export type Schema<Name extends keyof components['schemas']> = components['schemas'][Name];

// The full-results diagnostic belongs to the CLI. Browser searches use the daemon's filtered default.
type FilteredRequest<T> = T extends { includeFullResults: boolean } ? Omit<T, 'includeFullResults'> : T;
export type SearchViewRequest = FilteredRequest<Schema<'CreateSearchViewRequestDto'>>;
export type TrackSearchRequest = FilteredRequest<Schema<'SubmitTrackSearchJobRequestDto'>>;
export type JobDraft = FilteredRequest<Schema<'JobDraftDto'>>;
export type EffectiveSettingsRequest = Omit<Schema<'ResolveEffectiveSettingsRequestDto'>, 'job'> & { job: JobDraft };

export type ApiErrorDto = Schema<'ApiErrorDto'>;
export type ServerInfoDto = Schema<'ServerInfoDto'>;
export type StateSnapshotDto = Schema<'StateSnapshotDto'>;
export type StateUpdateBatchDto = Schema<'StateUpdateBatchDto'>;
export type DaemonStateDto = Schema<'DaemonStateDto'>;
export type WorkflowStateDto = Schema<'WorkflowStateDto'>;
export type SearchStateDto = Schema<'SearchStateDto'>;
export type TransferStateDto = Schema<'TransferStateDto'>;
export type TransferTimelinePageDto = Schema<'TransferTimelinePageDto'>;
export type TransferCommandReceiptDto = Schema<'TransferCommandReceiptDto'>;
export type DashboardAnalyticsDto = Schema<'DashboardAnalyticsDto'>;
export type UserProfileDto = Schema<'UserProfileDto'>;
export type UserPresenceDto = Schema<'UserPresenceDto'>;
export type UserBrowseDto = Schema<'UserBrowseDto'>;
export type BrowseDirectoryEntryDto = Schema<'BrowseDirectoryEntryDto'>;
export type BrowseFileEntryDto = Schema<'BrowseFileEntryDto'>;
export type BrowseSearchPageDto = Schema<'BrowseSearchPageDto'>;
export type StartUserShareDownloadsResponseDto = Schema<'StartUserShareDownloadsResponseDto'>;
export type ChatTargetSnapshotDto = Schema<'ChatTargetSnapshotDto'>;
export type ConversationSummaryDto = Schema<'ConversationSummaryDto'>;
export type ChatRoomSummaryDto = Schema<'ChatRoomSummaryDto'>;
export type ChatMessageDto = Schema<'ChatMessageDto'>;
export type JobSummaryDto = Schema<'JobSummaryDto'>;
export type SearchViewSummaryDto = Schema<'SearchViewSummaryDto'>;
export type SearchViewUpdateDto = Schema<'SearchViewUpdateDto'>;
export type SearchViewFilePageDto = Schema<'SearchViewFilePageDto'>;
export type SearchViewDirectoryPageDto = Schema<'SearchViewDirectoryPageDto'>;
export type SearchViewTrackGroupPageDto = Schema<'SearchViewTrackGroupPageDto'>;
export type SearchViewAlbumGroupPageDto = Schema<'SearchViewAlbumGroupPageDto'>;
export type CommitSearchViewSelectionResponseDto = Schema<'CommitSearchViewSelectionResponseDto'>;

export type WireInteger = number | string;

export function wireInteger(value: WireInteger | null | undefined): bigint {
  return BigInt(value ?? 0);
}

export function sameWireInteger(left: WireInteger, right: WireInteger): boolean {
  return wireInteger(left) === wireInteger(right);
}
