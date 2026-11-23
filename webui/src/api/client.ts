import { DaemonApiError, DaemonHttpClient, queryString } from './http';
import type { Schema } from './types';
import type {
  CommitSearchViewSelectionResponseDto,
  DashboardAnalyticsDto,
  EffectiveSettingsRequest,
  SearchViewRequest,
  TrackSearchRequest,
  JobSummaryDto,
  SearchViewAlbumGroupPageDto,
  SearchViewTrackGroupPageDto,
  SearchViewDirectoryPageDto,
  SearchViewFilePageDto,
  SearchViewSummaryDto,
  SearchViewUpdateDto,
  ServerInfoDto,
  StateSnapshotDto,
  TransferCommandReceiptDto,
  TransferTimelinePageDto,
  UserBrowseDto,
  UserProfileDto,
  BrowseSearchPageDto,
  StartUserShareDownloadsResponseDto,
} from './types';
import {
  albumQuery,
  networkQuery,
  searchModeFamily,
  searchSubmissionOptions,
  isGroupSearchMode,
  searchViewKind,
  modeForProjection,
  songQuery,
  type SearchDraft,
  type SearchResultMode,
} from '../domains/search/model';
import { canReplaySearch, canReplaySearchRoot } from '../domains/search/replay';

export interface SubmittedSearch {
  job: JobSummaryDto;
  view: SearchViewSummaryDto;
  draft: SearchDraft;
}

export interface CursorPage<T> {
  items: T[];
  nextCursor?: string | null;
}

export const defaultPageSize = 500;
export const nestedPageSize = __DEFAULT_NESTED_PAGE_SIZE__;
export const searchFilePageSize = 100;
export const searchDirectoryPageSize = 50;

export interface GroupOptionQuery {
  cursor?: string | null;
  limit?: number;
  filterText?: string;
  previousTo?: string;
  nextTo?: string;
}

export class DaemonApiClient {
  constructor(private readonly http = new DaemonHttpClient()) {}

  getServerInfo(signal?: AbortSignal): Promise<ServerInfoDto> {
    return this.http.get('/server/info', signal);
  }

  getDaemonSnapshot(signal?: AbortSignal, detail: Schema<'DaemonStateDetail'> = 'Full'): Promise<StateSnapshotDto> {
    return this.http.get(`/daemon/snapshot?detail=${detail}`, signal);
  }

  getJobListSnapshot(scope: Schema<'JobListScopeDto'>, signal?: AbortSignal): Promise<Schema<'JobListUpdateDto'>> {
    return this.http.post('/jobs/snapshot', scope, signal);
  }

  getJob(jobId: string, signal?: AbortSignal): Promise<Schema<'JobDetailDto'>> {
    return this.http.get(`/jobs/${encodeURIComponent(jobId)}`, signal);
  }

  getJobFailureDetail(jobId: string, signal?: AbortSignal): Promise<string> {
    return this.http.get(`/jobs/${encodeURIComponent(jobId)}/failure-detail`, signal);
  }

  getJobSummaries(jobIds: readonly string[], signal?: AbortSignal): Promise<JobSummaryDto[]> {
    return this.http.post('/jobs/lookup', { jobIds }, signal);
  }

  getDirectoryManifest(jobId: string, attemptNumber?: number | null, afterOrdinal = -1, limit = defaultPageSize, signal?: AbortSignal): Promise<Schema<'DirectoryManifestPageDto'>> {
    return this.http.get(`/jobs/${encodeURIComponent(jobId)}/directory-files${queryString({ attemptNumber, afterOrdinal, limit })}`, signal);
  }

  createJobPreview(request: Schema<'CreateJobPreviewRequestDto'>, signal?: AbortSignal): Promise<Schema<'CreateJobPreviewResponseDto'>> {
    return this.http.post('/job-previews', request, signal);
  }

  getProfiles(signal?: AbortSignal): Promise<Schema<'ProfileSummaryDto'>[]> {
    return this.http.get('/profiles', signal);
  }

  resolveUserShareDownloadSettings(options: Schema<'SubmissionOptionsDto'>, signal?: AbortSignal): Promise<Schema<'ResolveEffectiveSettingsResponseDto'>> {
    return this.http.post('/user-browses/download-settings', options, signal);
  }

  resolveEffectiveSettings(request: EffectiveSettingsRequest, signal?: AbortSignal): Promise<Schema<'ResolveEffectiveSettingsResponseDto'>> {
    return this.http.post('/jobs/effective-settings', request, signal);
  }

  getSearchViewLibraryMatches(viewId: string, request: Schema<'SearchViewLibraryMatchesRequestDto'>, signal: AbortSignal): Promise<string[]> {
    return this.http.post(`/search-views/${encodeURIComponent(viewId)}/library-matches`, request, signal);
  }

  resolveSearchViewSelectionSettings(viewId: string, request: Schema<'ResolveSearchViewSelectionSettingsRequestDto'>, signal?: AbortSignal): Promise<Schema<'ResolveSearchViewSelectionSettingsResponseDto'>> {
    return this.http.post(`/search-views/${encodeURIComponent(viewId)}/selection-settings`, request, signal);
  }

  countSearchViewSelection(viewId: string, request: Schema<'CountSearchViewSelectionRequestDto'>, signal?: AbortSignal): Promise<Schema<'CountSearchViewSelectionResponseDto'>> {
    return this.http.post(`/search-views/${encodeURIComponent(viewId)}/selection-counts`, request, signal);
  }

  uploadInputArtifact(file: File, signal?: AbortSignal): Promise<Schema<'InputArtifactDto'>> {
    return this.http.postBlob(`/input-artifacts${queryString({ fileName: file.name })}`, file, signal);
  }

  getJobPreview(previewId: string, signal?: AbortSignal, waitForCompletion = false): Promise<Schema<'JobPreviewSummaryDto'>> {
    return this.http.get(`/job-previews/${encodeURIComponent(previewId)}${queryString({ waitForCompletion: waitForCompletion || undefined })}`, signal);
  }

  getJobPreviewNodes(previewId: string, parentRef?: string | null, cursor?: string | null, limit = nestedPageSize, signal?: AbortSignal): Promise<CursorPage<Schema<'JobPreviewNodeDto'>>> {
    return this.http.getCursorPage<Schema<'JobPreviewNodeDto'>[]>(
      `/job-previews/${encodeURIComponent(previewId)}/nodes${queryString({ parentRef, cursor, limit })}`,
      signal,
    );
  }

  commitJobPreview(previewId: string, request: Schema<'CommitJobPreviewRequestDto'>, signal?: AbortSignal): Promise<Schema<'CommitJobPreviewResponseDto'>> {
    return this.http.post(`/job-previews/${encodeURIComponent(previewId)}/commit`, request, signal);
  }

  async submitSearch(draft: SearchDraft, signal?: AbortSignal): Promise<SubmittedSearch> {
    // Keep the accepted query/settings together if the composer changes while
    // submission or Search View creation is in flight.
    // These are JSON wire values; serialization also unwraps Svelte proxies,
    // which structuredClone cannot clone.
    draft = JSON.parse(JSON.stringify(draft)) as SearchDraft;
    const query = networkQuery(draft);
    if (!query) throw new Error('Enter a search query.');
    const options = searchSubmissionOptions(draft);

    let job: JobSummaryDto;
    if (draft.resultMode === 'generic') {
      const request: Schema<'SubmitSearchJobRequestDto'> = { queryText: query, options };
      job = await this.http.post('/jobs/search', request, signal);
    } else if (draft.resultMode === 'track' || draft.resultMode === 'song-groups') {
      const request: TrackSearchRequest = {
        options,
        songQuery: songQuery(draft),
        groupResults: draft.resultMode === 'song-groups',
      };
      job = await this.http.post('/jobs/search/tracks', request, signal);
    } else {
      const request: Schema<'SubmitAlbumSearchJobRequestDto'> = {
        options,
        albumQuery: albumQuery(draft),
        groupResults: draft.resultMode === 'album-groups',
      };
      job = await this.http.post('/jobs/search/albums', request, signal);
    }

    if (!job.jobId) throw new Error('The daemon accepted the search without returning a job ID.');
    const create: SearchViewRequest = {
      kind: searchViewKind(draft.resultMode),
      songQuery: draft.resultMode === 'track' || draft.resultMode === 'song-groups'
        ? songQuery(draft)
        : null,
      albumQuery: draft.resultMode === 'album' || draft.resultMode === 'album-groups'
        ? albumQuery(draft)
        : null,
    };
    const view = await this.createSearchView(job.jobId, create, signal);
    return { job, view, draft: { ...draft } };
  }

  createSearchView(jobId: string, request: SearchViewRequest = {}, signal?: AbortSignal): Promise<SearchViewSummaryDto> {
    return this.http.post(
      `/jobs/${encodeURIComponent(jobId)}/search-views`,
      request,
      signal,
    );
  }

  getSearchViewDefinition(viewId: string, signal?: AbortSignal): Promise<Schema<'SearchViewDefinitionDto'>> {
    return this.http.get(`/search-views/${encodeURIComponent(viewId)}/definition`, signal);
  }

  async openSearchJob(job: JobSummaryDto, signal?: AbortSignal,
    knownDetail?: Schema<'JobDetailDto'>, requestedMode?: SearchResultMode | null): Promise<SubmittedSearch> {
    if (!job.jobId) throw new Error('The search job has no ID.');
    const detail = knownDetail ?? await this.getJob(job.jobId, signal);
    const payload = detail.payload?.kind === 'search' ? detail.payload : null;
    const defaultMode: SearchResultMode | null = payload?.defaultProjection === 'Track'
      ? (payload.groupResults ? 'song-groups' : 'track')
      : payload?.defaultProjection === 'Album'
        ? (payload.groupResults ? 'album-groups' : 'album')
      : payload?.defaultProjection === 'GenericFile' ? 'generic'
      : detail.payload?.kind === 'song' ? 'track'
      : detail.payload?.kind === 'album' ? 'album'
      : detail.payload?.kind === 'song-groups' ? 'song-groups'
      : detail.payload?.kind === 'album-groups' ? 'album-groups' : null;
    const mode = defaultMode && requestedMode
      && searchModeFamily(requestedMode) === searchModeFamily(defaultMode)
      ? requestedMode : defaultMode;
    const view = await this.createSearchView(job.jobId, {
      kind: mode ? searchViewKind(mode) : undefined,
    }, signal);
    return { job: detail.summary, view, draft: searchDraftFromDetail(detail, view.kind) };
  }

  async rerunSearch(job: JobSummaryDto, view: SearchViewSummaryDto, signal?: AbortSignal): Promise<SubmittedSearch> {
    if (!canReplaySearch(job)) {
      throw new Error('This search has no retained submission to replay.');
    }
    const detail = await this.getJob(job.jobId, signal);
    if (detail.payload?.kind !== 'search') throw new Error('The retained job has no search definition.');
    const endpoint = canReplaySearchRoot(job)
      ? `/submissions/${encodeURIComponent(job.display.submissionId)}/rerun`
      : `/jobs/${encodeURIComponent(job.jobId)}/rerun`;
    const rerun = await this.http.post<JobSummaryDto>(endpoint, undefined, signal);
    if (!rerun.jobId || rerun.display.kind !== 'search') {
      throw new Error('The daemon did not return a replayed search job.');
    }
    const nextView = await this.createSearchView(rerun.jobId, { kind: view.kind }, signal);
    return { job: rerun, view: nextView, draft: searchDraftFromDetail(detail, nextView.kind) };
  }

  cancelJob(jobId: string, signal?: AbortSignal): Promise<void> {
    return this.http.post(`/jobs/${encodeURIComponent(jobId)}/cancel`, undefined, signal);
  }

  deleteSubmission(submissionId: string, signal?: AbortSignal): Promise<Schema<'DeleteSubmissionResponseDto'>> {
    return this.http.delete(`/submissions/${encodeURIComponent(submissionId)}`, signal);
  }

  runAgain(jobId: string): Promise<JobSummaryDto> {
    return this.http.post(`/jobs/${encodeURIComponent(jobId)}/rerun`);
  }

  retryDirectory(jobId: string, memberJobId?: string): Promise<JobSummaryDto> {
    return this.http.post(`/jobs/${encodeURIComponent(jobId)}/retry-directory${queryString({ memberJobId })}`);
  }

  retryTransfer(transferId: string): Promise<JobSummaryDto> {
    return this.http.post(`/transfers/${encodeURIComponent(transferId)}/retry`, undefined);
  }

  cancelTransfer(transferId: string, signal?: AbortSignal): Promise<Schema<'TransferStateDto'>> {
    return this.http.post(`/transfers/${encodeURIComponent(transferId)}/cancel`, undefined, signal);
  }

  getTransferTimeline(scope: Schema<'TransferTimelineScopeDto'>, signal?: AbortSignal): Promise<Schema<'TransferTimelineUpdateDto'>> {
    return this.http.post('/transfers/timeline', scope, signal);
  }

  getTransferGroupMembers(
    direction: 'download' | 'upload',
    username: string | null,
    groupRef: string,
    cursor?: string | null,
    limit = defaultPageSize,
    signal?: AbortSignal,
  ): Promise<TransferTimelinePageDto> {
    return this.http.get(
      `/transfers${queryString({ direction, username, groupRef, cursor, limit })}`,
      signal,
    );
  }

  cancelTransfers(direction: 'Download' | 'Upload', scope: Schema<'TransferCancellationScope'>, group?: { username: string | null; groupRef: string }, signal?: AbortSignal): Promise<TransferCommandReceiptDto> {
    const request: Schema<'BulkCancelTransfersRequestDto'> = { direction, scope, ...group };
    return this.http.post('/transfers/cancel', request, signal);
  }

  removeTransfer(transferId: string, signal?: AbortSignal): Promise<TransferCommandReceiptDto> {
    return this.http.delete(`/transfers/${encodeURIComponent(transferId)}`, signal);
  }

  removeTransfers(direction: 'download' | 'upload', filter: Pick<Schema<'RemoveTransfersRequestDto'>, 'username' | 'groupRef' | 'terminalOutcome'>, signal?: AbortSignal): Promise<TransferCommandReceiptDto> {
    const request: Schema<'RemoveTransfersRequestDto'> = { direction, ...filter };
    return this.http.post('/transfers/remove', request, signal);
  }

  getDashboardAnalytics(range: '24h' | '7d' | '30d' | '90d' | '1y' | 'all', signal?: AbortSignal): Promise<DashboardAnalyticsDto> {
    return this.http.get(`/dashboard/analytics${queryString({ range })}`, signal);
  }

  getUserProfile(username: string, refresh = false, signal?: AbortSignal): Promise<UserProfileDto> {
    return this.http.get(`/users/${encodeURIComponent(username)}/profile${queryString({ refresh })}`, signal);
  }

  startUserBrowse(username: string, refresh = false, signal?: AbortSignal): Promise<UserBrowseDto> {
    const request: Schema<'StartUserBrowseRequestDto'> = { refresh };
    return this.http.post(`/users/${encodeURIComponent(username)}/browses`, request, signal);
  }

  cancelUserBrowse(browseId: string): Promise<UserBrowseDto> {
    return this.http.post(`/user-browses/${encodeURIComponent(browseId)}/cancel`);
  }

  getUserBrowse(browseId: string, signal?: AbortSignal): Promise<UserBrowseDto> {
    return this.http.get(`/user-browses/${encodeURIComponent(browseId)}`, signal);
  }

  getUserBrowseSnapshot(browseId: string, signal?: AbortSignal): Promise<StateSnapshotDto> {
    return this.http.get(`/user-browses/${encodeURIComponent(browseId)}/snapshot`, signal);
  }

  getUserShareDirectories(browseId: string, parentId?: number | string | null, cursor?: string | null, limit = nestedPageSize, signal?: AbortSignal): Promise<Schema<'PageDtoOfBrowseDirectoryEntryDto'>> {
    return this.http.get(`/user-browses/${encodeURIComponent(browseId)}/directories${queryString({ parentId, recursive: false, cursor, limit })}`, signal);
  }

  getUserShareFiles(browseId: string, directoryId: number | string, cursor?: string | null, limit = defaultPageSize, signal?: AbortSignal): Promise<Schema<'PageDtoOfBrowseFileEntryDto'>> {
    return this.http.get(`/user-browses/${encodeURIComponent(browseId)}/directories/${directoryId}/files${queryString({ cursor, limit })}`, signal);
  }

  searchUserShares(browseId: string, query: string, cursor?: string | null, limit = nestedPageSize, signal?: AbortSignal): Promise<BrowseSearchPageDto> {
    return this.http.get(`/user-browses/${encodeURIComponent(browseId)}/search${queryString({ query, cursor, limit })}`, signal);
  }

  startUserShareDownloads(
    browseId: string,
    requestId: string,
    selections: Schema<'UserShareSelectionDto'>[],
    signal?: AbortSignal,
    excludedFileIds?: string[],
    excludedDirectoryIds?: string[],
    options?: Schema<'SubmissionOptionsDto'>,
  ): Promise<StartUserShareDownloadsResponseDto> {
    const request: Schema<'StartUserShareDownloadsRequestDto'> = { requestId, selections, excludedFileIds, excludedDirectoryIds, options };
    return this.http.post(`/user-browses/${encodeURIComponent(browseId)}/downloads`, request, signal);
  }

  getConversations(cursor?: string | null, limit = defaultPageSize, signal?: AbortSignal): Promise<Schema<'ConversationPageDto'>> {
    return this.http.get(`/chat/conversations${queryString({ cursor, limit })}`, signal);
  }

  getChatRooms(cursor?: string | null, limit = defaultPageSize, signal?: AbortSignal): Promise<Schema<'ChatRoomPageDto'>> {
    return this.http.get(`/chat/rooms${queryString({ cursor, limit })}`, signal);
  }

  getConversationSnapshot(conversationId: string, signal?: AbortSignal): Promise<StateSnapshotDto> {
    return this.http.get(`/chat/conversations/${encodeURIComponent(conversationId)}/snapshot`, signal);
  }

  async getConversationByPeer(username: string, signal?: AbortSignal): Promise<Schema<'ConversationSummaryDto'> | null> {
    try {
      return await this.http.get(`/chat/conversations/by-peer${queryString({ username })}`, signal);
    } catch (cause) {
      if (cause instanceof DaemonApiError && cause.status === 404) return null;
      throw cause;
    }
  }

  getRoomSnapshot(roomId: string, signal?: AbortSignal): Promise<StateSnapshotDto> {
    return this.http.get(`/chat/rooms/${encodeURIComponent(roomId)}/snapshot`, signal);
  }

  getConversationMessages(conversationId: string, cursor?: string | null, limit = defaultPageSize, signal?: AbortSignal): Promise<Schema<'ChatMessagePageDto'>> {
    return this.http.get(`/chat/conversations/${encodeURIComponent(conversationId)}/messages${queryString({ cursor, limit })}`, signal);
  }

  getRoomMessages(roomId: string, cursor?: string | null, limit = defaultPageSize, signal?: AbortSignal): Promise<Schema<'ChatMessagePageDto'>> {
    return this.http.get(`/chat/rooms/${encodeURIComponent(roomId)}/messages${queryString({ cursor, limit })}`, signal);
  }

  sendPrivateMessage(username: string, text: string, messageId: string, signal?: AbortSignal): Promise<Schema<'ChatMessageDto'>> {
    const request: Schema<'SendPrivateMessageRequestDto'> = { username, text, messageId };
    return this.http.post('/chat/private-messages', request, signal);
  }

  sendConversationMessage(conversationId: string, text: string, messageId: string, signal?: AbortSignal): Promise<Schema<'ChatMessageDto'>> {
    const request: Schema<'SendChatMessageRequestDto'> = { text, messageId };
    return this.http.post(`/chat/conversations/${encodeURIComponent(conversationId)}/messages`, request, signal);
  }

  sendRoomMessage(roomId: string, text: string, messageId: string, signal?: AbortSignal): Promise<Schema<'ChatMessageDto'>> {
    const request: Schema<'SendChatMessageRequestDto'> = { text, messageId };
    return this.http.post(`/chat/rooms/${encodeURIComponent(roomId)}/messages`, request, signal);
  }

  markConversationRead(conversationId: string, throughMessageId: string, signal?: AbortSignal): Promise<Schema<'ConversationSummaryDto'>> {
    const request: Schema<'MarkChatReadRequestDto'> = { throughMessageId };
    return this.http.post(`/chat/conversations/${encodeURIComponent(conversationId)}/read`, request, signal);
  }

  markRoomRead(roomId: string, throughMessageId: string, signal?: AbortSignal): Promise<Schema<'ChatRoomSummaryDto'>> {
    const request: Schema<'MarkChatReadRequestDto'> = { throughMessageId };
    return this.http.post(`/chat/rooms/${encodeURIComponent(roomId)}/read`, request, signal);
  }

  joinRoom(roomName: string, signal?: AbortSignal): Promise<Schema<'ChatRoomSummaryDto'>> {
    const request: Schema<'JoinRoomRequestDto'> = { roomName, remember: true };
    return this.http.post('/chat/rooms', request, signal);
  }

  leaveRoom(roomId: string, signal?: AbortSignal): Promise<Schema<'ChatRoomSummaryDto'>> {
    return this.http.delete(`/chat/rooms/${encodeURIComponent(roomId)}`, signal);
  }

  deleteConversation(conversationId: string, signal?: AbortSignal): Promise<void> {
    return this.http.delete(`/chat/conversations/${encodeURIComponent(conversationId)}`, signal);
  }

  getSearchViewRevisionLifetime(viewId: string, revision: number | string, signal: AbortSignal): Promise<Schema<'SearchViewRevisionLifetimeDto'>> {
    return this.http.get(`/search-views/${encodeURIComponent(viewId)}/revisions/${revision}/lifetime`, signal);
  }

  getSearchViewUpdate(viewId: string, afterRevision: number | string, signal?: AbortSignal,
    waitForChange = String(afterRevision) === '0'): Promise<SearchViewUpdateDto> {
    return this.http.get(
      `/search-views/${encodeURIComponent(viewId)}/updates${queryString({ afterRevision,
        waitForChange: waitForChange ? true : undefined })}`,
      signal,
    );
  }

  getSearchViewFiles(viewId: string, revision: number | string, cursor?: string | null, limit = searchFilePageSize, sortOrder?: Schema<'ServerSearchViewSortOrder'>, signal?: AbortSignal): Promise<SearchViewFilePageDto> {
    return this.http.get(
      `/search-views/${encodeURIComponent(viewId)}/files${queryString({ revision, cursor, limit, sortOrder })}`,
      signal,
    );
  }

  getSearchViewDirectories(viewId: string, revision: number | string, cursor?: string | null, limit = searchDirectoryPageSize, sortOrder?: Schema<'ServerSearchViewSortOrder'>, signal?: AbortSignal): Promise<SearchViewDirectoryPageDto> {
    return this.http.get(
      `/search-views/${encodeURIComponent(viewId)}/directories${queryString({ revision, cursor, limit, sortOrder })}`,
      signal,
    );
  }

  getSearchViewTrackGroups(viewId: string, revision: number | string, cursor?: string | null, limit = searchFilePageSize, signal?: AbortSignal): Promise<SearchViewTrackGroupPageDto> {
    return this.http.get(
      `/search-views/${encodeURIComponent(viewId)}/track-groups${queryString({ revision, cursor, limit })}`,
      signal,
    );
  }

  getSearchViewDirectoryFiles(viewId: string, directoryRef: string, revision: number | string, cursor?: string | null, limit = nestedPageSize, signal?: AbortSignal): Promise<Schema<'SearchViewDirectoryFilePageDto'>> {
    return this.http.get(
      `/search-views/${encodeURIComponent(viewId)}/directories/${encodeURIComponent(directoryRef)}/files${queryString({ revision, cursor, limit })}`,
      signal,
    );
  }

  getSearchViewAlbumGroups(viewId: string, revision: number | string, cursor?: string | null, limit = searchDirectoryPageSize, signal?: AbortSignal): Promise<SearchViewAlbumGroupPageDto> {
    return this.http.get(
      `/search-views/${encodeURIComponent(viewId)}/album-groups${queryString({ revision, cursor, limit })}`,
      signal,
    );
  }

  retrieveSearchViewDirectory(viewId: string, revision: number | string, directory: Schema<'PeerDirectoryRefDto'>, idempotencyKey: string): Promise<Schema<'JobSummaryDto'>> {
    return this.http.post(`/search-views/${encodeURIComponent(viewId)}/directories/retrieve`, { revision, directory, idempotencyKey });
  }

  getSearchViewTrackGroupOptions(viewId: string, groupRef: string, revision: number | string,
    query: GroupOptionQuery = {}, signal?: AbortSignal): Promise<Schema<'SearchViewTrackGroupOptionPageDto'>> {
    return this.http.get(`/search-views/${encodeURIComponent(viewId)}/track-groups/${encodeURIComponent(groupRef)}/alternatives${queryString({ revision, limit: searchFilePageSize, ...query })}`, signal);
  }

  getSearchViewAlbumGroupOptions(viewId: string, groupRef: string, revision: number | string,
    query: GroupOptionQuery = {}, signal?: AbortSignal): Promise<Schema<'SearchViewAlbumGroupOptionPageDto'>> {
    return this.http.get(`/search-views/${encodeURIComponent(viewId)}/album-groups/${encodeURIComponent(groupRef)}/alternatives${queryString({ revision, limit: searchDirectoryPageSize, ...query })}`, signal);
  }

  resolveSearchViewChosenOptions(viewId: string, request: Schema<'ResolveSearchViewChosenOptionsRequestDto'>, signal?: AbortSignal): Promise<Schema<'ResolveSearchViewChosenOptionsResponseDto'>> {
    return this.http.post(`/search-views/${encodeURIComponent(viewId)}/chosen-options/resolve`, request, signal);
  }

  commitSearchView(
    viewId: string,
    request: Schema<'CommitSearchViewSelectionRequestDto'>,
    signal?: AbortSignal,
  ): Promise<CommitSearchViewSelectionResponseDto> {
    return this.http.post(`/search-views/${encodeURIComponent(viewId)}/commit`, request, signal);
  }

}

function searchDraftFromDetail(
  detail: Schema<'JobDetailDto'>,
  kind: SearchViewSummaryDto['kind'],
): SearchDraft {
  const payload = detail.payload?.kind === 'search' ? detail.payload : null;
  const song = payload?.songQuery ?? (detail.payload?.kind === 'song' || detail.payload?.kind === 'song-groups'
    ? detail.payload.query : null);
  const album = payload?.albumQuery ?? (detail.payload?.kind === 'album' || detail.payload?.kind === 'album-groups'
    ? detail.payload.query : null);
  const resultMode = modeForProjection(kind);
  const group = isGroupSearchMode(resultMode);
  const split = group
    ? Boolean(song?.title || album?.album || album?.searchHint)
    : Boolean(song?.artist || album?.artist || (album?.album && album?.searchHint));
  return {
    mode: split ? 'split' : 'simple',
    resultMode,
    query: split ? '' : (group ? song?.artist ?? album?.artist : song?.title ?? album?.album ?? album?.searchHint) ?? payload?.queryText ?? detail.summary.display.queryText ?? detail.summary.display.itemName ?? '',
    artist: song?.artist ?? album?.artist ?? '',
    title: song?.title ?? album?.album ?? '',
    expectedLength: song?.length != null && Number(song.length) >= 0 ? Number(song.length) : undefined,
    albumContext: song?.album ?? undefined,
    sourceUri: song?.uri ?? album?.uri ?? undefined,
    artistMaybeWrong: song?.artistMaybeWrong ?? album?.artistMaybeWrong,
    albumSearchHint: album?.searchHint ?? undefined,
  };
}

export const daemonApi = new DaemonApiClient();
