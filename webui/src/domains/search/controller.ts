import { randomUUID } from '../../app/uuid';
import { DaemonApiClient, daemonApi, searchFilePageSize, searchDirectoryPageSize, type SubmittedSearch } from '../../api/client';
import type {
  CommitSearchViewSelectionResponseDto,
  Schema,
  SearchViewAlbumGroupPageDto,
  SearchViewTrackGroupPageDto,
  SearchViewDirectoryPageDto,
  SearchViewFilePageDto,
  SearchViewSummaryDto,
} from '../../api/types';
import { sameWireInteger, wireInteger } from '../../api/types';
import type { SearchDraft } from './model';
import { DaemonApiError } from '../../api/http';
import { SearchSelection, hasPublishedProjection, selectionCountKey,
  type GroupOption, type SearchSelectionState } from './selection';

export type SearchPage =
  | SearchViewFilePageDto
  | SearchViewDirectoryPageDto
  | SearchViewTrackGroupPageDto
  | SearchViewAlbumGroupPageDto;

export type SearchRow =
  | { kind: 'file'; ref: string; value: SearchViewFilePageDto['items'][number] }
  | { kind: 'directory'; ref: string; value: SearchViewDirectoryPageDto['items'][number] }
  | { kind: 'track-group'; ref: string; value: SearchViewTrackGroupPageDto['items'][number] }
  | { kind: 'album-group'; ref: string; value: SearchViewAlbumGroupPageDto['items'][number] };

export interface GroupOptionsPanel {
  groupRef: string;
  kind: 'track' | 'album';
  title: string;
  artist: string | null;
  shareCount: number;
  optionCount: number;
  representativeRef: string;
  revision: number | string;
  items: GroupOption[];
  nextCursor: string | null;
  loading: boolean;
  error: Error | null;
}

interface DirectoryFilesPage {
  items: Schema<'SearchViewDirectoryFileDto'>[];
  directorySummary?: Schema<'SearchViewDirectoryDto'>;
  nextCursor: string | null;
  loading: boolean;
  refreshing?: boolean;
  error: Error | null;
}

export interface SearchViewState extends SearchSelectionState {
  groupOptions: GroupOptionsPanel | null;
  optionSteps: ReadonlyMap<string, { loading: boolean; error: Error | null; position?: number; optionRef?: string; revision?: string }>;
  choiceValidationLoading: boolean;
  choiceValidationError: Error | null;
  retrievals: ReadonlyMap<string, { loading: boolean; error: Error | null }>;
  directoryFiles: ReadonlyMap<string, DirectoryFilesPage>;
  phase: 'idle' | 'loading' | 'ready' | 'failed';
  job: SubmittedSearch['job'] | null;
  draft: SearchDraft | null;
  summary: SearchViewSummaryDto | null;
  sortOrder: Schema<'ServerSearchViewSortOrder'>;
  rows: SearchRow[];
  nextCursor: string | null;
  loadingMore: boolean;
  refining: boolean;
  refinementError: Error | null;
  selectionCountsLoading: boolean;
  selectionCountsError: Error | null;
  committing: boolean;
  canRetryPendingCommit: boolean;
  receipt: CommitSearchViewSelectionResponseDto | null;
  error: Error | null;
}

type Listener = (state: SearchViewState) => void;
type ObserveView = (viewId: string, next: (summary: SearchViewSummaryDto) => void,
  error: (error: Error) => void, complete: () => void) => () => void;

export function searchViewUnavailable(state: Pick<SearchViewState, 'error' | 'refinementError' | 'selectionCountsError' | 'choiceValidationError' | 'groupOptions' | 'directoryFiles'>): boolean {
  return [state.error, state.refinementError, state.selectionCountsError, state.choiceValidationError,
    state.groupOptions?.error, ...[...state.directoryFiles.values()].map(files => files.error)]
    .some(error => error instanceof DaemonApiError && error.status === 404);
}

export class SearchViewController {
  private readonly selection = new SearchSelection();
  private listeners = new Set<Listener>();
  private pages: SearchPage[] = [];
  private optionPages = new Map<string, { revision: string; items: GroupOption[]; nextCursor: string | null; loaded: boolean }>();
  private retrievalJobs = new Map<string, string>();
  private retrievalInFlight = new Map<string, symbol>();
  private retrievalRequests = new Map<string, {
    viewId: string;
    revision: number | string;
    directory: Schema<'PeerDirectoryRefDto'>;
    idempotencyKey: string;
  }>();
  private directoryFileCounts = new Map<string, number>();
  private stopped = true;
  private visible = true;
  private request: AbortController | null = null;
  private updateRequest: AbortController | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private refreshing: Promise<void> | null = null;
  private summaryError: Error | null = null;
  private observation: { viewId: string; release: () => void } | null = null;
  private observedSummary: SearchViewSummaryDto | null = null;
  private applyingObservation = false;
  private liveConnected = false;

  private generation = 0;
  private refinementGeneration = 0;
  private refinementRequest: AbortController | null = null;
  private countRequestKey: string | null = null;
  private countRequest: AbortController | null = null;
  private choiceRequest: AbortController | null = null;
  private choiceRequestKey: string | null = null;
  private state: Omit<SearchViewState, keyof SearchSelectionState | 'canRetryPendingCommit'> = {
    groupOptions: null,
    optionSteps: new Map(),
    choiceValidationLoading: false,
    choiceValidationError: null,
    retrievals: new Map(),
    directoryFiles: new Map(),
    phase: 'idle',
    loadingMore: false,
    refining: false,
    refinementError: null,
    job: null,
    draft: null,
    summary: null,
    sortOrder: 'Relevance',
    rows: [],
    nextCursor: null,
    selectionCountsLoading: false,
    selectionCountsError: null,
    committing: false,
    receipt: null,
    error: null,
  };

  constructor(
    private readonly api: DaemonApiClient = daemonApi,
    private readonly pollIntervalMs = 1_000,
    private readonly observeView?: ObserveView,
  ) {}

  setLiveConnected(connected: boolean): void {
    this.liveConnected = connected;
    if (!connected) this.stopObservation();
    else this.startObservation();
    this.schedulePoll();
  }

  private stopObservation(): void {
    const observation = this.observation;
    this.observation = null;
    this.observedSummary = null;
    observation?.release();
  }

  private startObservation(): void {
    const summary = this.state.summary;
    if (!this.observeView || !this.liveConnected || !this.visible || this.stopped || !summary || summary.isComplete || searchViewUnavailable(this.state)) return;
    if (this.observation?.viewId === summary.viewId) return;
    this.stopObservation();
    const observation = { viewId: summary.viewId, release: () => {} };
    this.observation = observation;
    const ended = () => {
      if (this.observation !== observation) return;
      this.observation = null;
      this.schedulePoll();
    };
    try {
      observation.release = this.observeView(summary.viewId, (next) => {
        if (this.observation !== observation) return;
        this.observedSummary = next;
        void this.applyObservedSummary();
      }, (error) => {
        if (this.observation !== observation) return;
        this.summaryError = error;
        this.state = { ...this.state, error };
        this.emit();
        ended();
      }, ended);
    } catch (error) {
      ended();
      this.summaryError = asError(error);
      this.state = { ...this.state, error: this.summaryError };
      this.emit();
    }
  }

  private async applyObservedSummary(): Promise<void> {
    if (this.applyingObservation) return;
    this.applyingObservation = true;
    try {
      // One visible-page refresh at a time; keep the newest summary arriving
      // during it. A terminal update must not disappear behind an earlier read.
      while (this.observedSummary && !this.stopped && this.visible) {
        if (this.state.refining) return;
        if (this.refreshing) await this.refreshing;
        if (this.state.refining) return;
        const summary = this.observedSummary;
        this.observedSummary = null;
        if (summary && summary.viewId === this.state.summary?.viewId)
          await this.refresh(false, summary);
      }
    } finally { this.applyingObservation = false; }
  }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    listener(this.current());
    return () => this.listeners.delete(listener);
  }

  current(): SearchViewState {
    return { ...this.state, ...this.selection.snapshot(),
      canRetryPendingCommit: this.selection.pendingCommit !== null };
  }

  async open(submitted: SubmittedSearch): Promise<void> {
    this.stop();
    this.stopped = false;
    this.pages = [];
    this.optionPages.clear();
    this.selection.reset();
    this.summaryError = null;
    this.directoryFileCounts.clear();
    this.retrievalJobs.clear();
    this.retrievalInFlight.clear();
    this.retrievalRequests.clear();
    this.state = {
      phase: 'loading',
      groupOptions: null,
      optionSteps: new Map(),
      choiceValidationLoading: false,
      choiceValidationError: null,
      retrievals: new Map(),
      loadingMore: false,
      refining: false,
      refinementError: null,
      directoryFiles: new Map(),
      job: submitted.job,
      draft: { ...submitted.draft },
      summary: submitted.view,
      sortOrder: submitted.view.sortOrder ?? 'Relevance',
      rows: [],
      nextCursor: null,
      selectionCountsLoading: false,
      selectionCountsError: null,
      committing: false,
      receipt: null,
      error: null,
    };
    this.emit();
    await this.reloadVisiblePages(submitted.view, 1);
    this.startObservation();
    this.schedulePoll();
  }

  pause(): void {
    if (!this.visible) return;
    this.visible = false;
    this.stopObservation();
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    const changed = this.state.loadingMore || this.state.refining;
    this.updateRequest?.abort();
    this.updateRequest = null;
    this.refreshing = null;
    this.refinementRequest?.abort();
    this.refinementRequest = null;
    this.refinementGeneration += 1;
    // An explicit open may still be loading its first page before the router
    // displays that job. Let that one requested page finish; cancel continuations.
    if (this.state.phase !== 'loading' || this.pages.length > 0) this.cancelPageRequest();
    if (this.state.refining) this.state = { ...this.state, refining: false };
    if (changed) this.emit();
  }

  async resume(): Promise<void> {
    if (this.visible) return;
    this.visible = true;
    if (this.stopped || !this.state.summary) return;
    this.startObservation();
    if (!this.observation) await this.refresh();
    this.schedulePoll();
  }

  stop(): void {
    this.stopObservation();
    this.generation += 1;
    this.updateRequest?.abort();
    this.updateRequest = null;
    this.refinementGeneration += 1;
    this.refinementRequest?.abort();
    this.refinementRequest = null;
    this.countRequest?.abort();
    this.countRequest = null;
    this.choiceRequest?.abort();
    this.choiceRequest = null;
    this.choiceRequestKey = null;
    this.countRequestKey = null;
    this.refreshing = null;
    this.state = { ...this.state, loadingMore: false };
    this.stopped = true;
    this.retrievalInFlight.clear();
    this.retrievalRequests.clear();
    this.cancelPageRequest();
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }

  async refine(request: Schema<'CreateSearchViewRequestDto'>, resetSelection = false): Promise<boolean> {
    const jobId = this.state.job?.jobId;
    if (!jobId || this.stopped || (resetSelection && this.selection.pendingCommit)
      || (!resetSelection && searchViewUnavailable(this.state))) return false;
    const version = ++this.refinementGeneration;
    this.refinementRequest?.abort();
    const controller = new AbortController();
    this.refinementRequest = controller;
    this.state = { ...this.state, refining: true, refinementError: null };
    this.emit();
    try {
      let view = await this.api.createSearchView(jobId, request, controller.signal);
      if (this.stopped || version !== this.refinementGeneration || controller.signal.aborted) return false;
      // A view is a replaceable projection of the same search. Selections and
      // group choices stay browser-owned; the daemon resolves their stable
      // refs against the new view. Validate folder references before rebinding:
      // an absent AllExcept exclusion must never silently broaden the selection.
      this.generation += 1;
      this.updateRequest?.abort();
      this.updateRequest = null;
      this.cancelPageRequest();
      this.countRequest?.abort();
      this.countRequest = null;
      this.choiceRequest?.abort();
      this.choiceRequest = null;
      this.choiceRequestKey = null;
      this.countRequestKey = null;
      this.refreshing = null;
      if (this.timer) clearTimeout(this.timer);
      this.timer = null;
      // A retrieval still belongs to this source search after a projection
      // change. Keep observing its job so completion can refresh the new view;
      // only its old view-bound spinner is cleared.
      // Keep the displayed view until a replacement is usable. Historical
      // searches need the complete projection; live searches can show a prefix.
      while (!view.isComplete && (sameWireInteger(view.revision, 0) || this.state.job?.lifecycleState === 'Terminal')) {
        view = (await this.api.getSearchViewUpdate(view.viewId, view.revision, controller.signal,
          this.state.job?.lifecycleState === 'Terminal' ? true : undefined)).summary;
        if (this.stopped || version !== this.refinementGeneration || controller.signal.aborted) return false;
      }
      // Publish rows/counts/selection together, avoiding a destructive empty frame.
      if (!await this.reloadVisiblePages(view, 1, { request: controller, replaceProjection: true, resetSelection, sortOrder: view.sortOrder ?? 'Relevance' })) return false;
      if (this.stopped || version !== this.refinementGeneration) return false;
      this.startObservation();
      this.schedulePoll();
      return true;
    } catch (error) {
      if (this.stopped || version !== this.refinementGeneration || controller.signal.aborted) return false;
      this.state = { ...this.state, refining: false, refinementError: asError(error) };
      this.emit();
      this.schedulePoll();
      return false;
    } finally {
      if (this.refinementRequest === controller) this.refinementRequest = null;
      void this.applyObservedSummary();
    }
  }

  async sort(order: Schema<'ServerSearchViewSortOrder'>): Promise<boolean> {
    const summary = this.state.summary;
    if (!summary || this.stopped || !this.visible) return false;
    if (!this.state.refining && order === this.state.sortOrder) return true;
    const version = ++this.refinementGeneration;
    this.refinementRequest?.abort();
    const request = new AbortController();
    this.refinementRequest = request;
    this.updateRequest?.abort();
    this.updateRequest = null;
    this.refreshing = null;
    this.cancelPageRequest();
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.state = { ...this.state, refining: true, refinementError: null };
    this.emit();
    try {
      // Ordering changes neither membership nor selection. Keep the same View,
      // revision, counts, child pages and browser-owned choices.
      if (!await this.reloadVisiblePages(summary, Math.max(1, this.pages.length),
        { request, sortOrder: order })) return false;
      if (this.stopped || version !== this.refinementGeneration) return false;
      this.schedulePoll();
      return true;
    } catch (error) {
      if (this.stopped || version !== this.refinementGeneration || request.signal.aborted) return false;
      this.state = { ...this.state, refining: false, refinementError: asError(error) };
      this.emit();
      this.schedulePoll();
      return false;
    } finally {
      if (this.refinementRequest === request) this.refinementRequest = null;
      void this.applyObservedSummary();
    }
  }

  async refresh(reloadPages = false, observed?: SearchViewSummaryDto): Promise<void> {
    if (this.refreshing) return this.refreshing;
    const summary = this.state.summary;
    if (!summary || this.stopped || this.state.refining || searchViewUnavailable(this.state)) return;
    if (observed && wireInteger(observed.revision) < wireInteger(summary.revision)) return;
    const generation = this.generation;
    const updateRequest = new AbortController();
    this.updateRequest = updateRequest;
    this.refreshing = (async () => {
      const completed: string[] = [];
      for (const [ref, jobId] of this.retrievalJobs) {
        let job: Schema<'JobSummaryDto'>;
        try {
          job = (await this.api.getJob(jobId, updateRequest.signal)).summary;
        } catch (error) {
          if (this.stopped || generation !== this.generation || updateRequest.signal.aborted) return;
          if (this.hasVisibleDirectory(ref)) {
            this.state = { ...this.state, retrievals: new Map(this.state.retrievals).set(ref, { loading: true, error: asError(error) }) };
            this.emit();
          }
          continue;
        }
        if (this.stopped || generation !== this.generation || updateRequest.signal.aborted) return;
        if (this.hasVisibleDirectory(ref))
          this.state = { ...this.state, retrievals: new Map(this.state.retrievals).set(ref, { loading: true, error: null }) };
        if (job.lifecycleState !== 'Terminal') continue;
        const error = job.terminalOutcome === 'Succeeded' ? null : new Error(job.failureMessage ?? 'Folder retrieval failed.');
        if (error) {
          this.retrievalJobs.delete(ref);
          if (this.hasVisibleDirectory(ref)) {
            this.state = { ...this.state, retrievals: new Map(this.state.retrievals).set(ref, { loading: false, error }) };
            this.emit();
          }
        } else completed.push(ref);
      }
      const update = observed
        ? { summary: observed, hasNewRevision: !sameWireInteger(observed.revision, summary.revision) }
        : await this.api.getSearchViewUpdate(summary.viewId, summary.revision, updateRequest.signal,
          this.waitingForInitialResults() ? true : undefined);
      if (this.stopped || generation !== this.generation || updateRequest.signal.aborted) return;
      if (update.hasNewRevision || reloadPages || completed.length > 0 || this.pages.length === 0) {
        if (!await this.reloadVisiblePages(update.summary, Math.max(1, this.pages.length))) return;
      } else {
        // A successful summary poll cannot resolve a page or commit failure.
        this.state = { ...this.state, summary: update.summary,
          error: this.state.error === this.summaryError ? null : this.state.error };
        this.summaryError = null;
        this.emit();
      }
      // A completed Search View is not the job's terminal outcome. Once the
      // job leaves the bounded live snapshot, retain its authoritative status
      // for this open detail instead of relabeling cancellations as Complete.
      if (update.summary.isComplete && this.state.job?.jobId
        && this.state.job.lifecycleState !== 'Terminal') {
        try {
          const job = (await this.api.getJob(this.state.job.jobId, updateRequest.signal)).summary;
          if (this.stopped || generation !== this.generation || updateRequest.signal.aborted) return;
          this.state = { ...this.state, job };
          this.emit();
        } catch {
          // Keep the result page usable; the next summary poll retries status.
        }
      }
      for (const ref of completed) {
        if (!this.hasVisibleDirectory(ref)) {
          this.retrievalJobs.delete(ref);
          continue;
        }
        await this.loadDirectoryFiles(ref);
        if (this.stopped || generation !== this.generation || updateRequest.signal.aborted) return;
        const files = this.state.directoryFiles.get(ref);
        if (!files || files.error || files.loading) continue;
        this.retrievalJobs.delete(ref);
        this.state = { ...this.state, retrievals: new Map(this.state.retrievals).set(ref, { loading: false, error: null }) };
        this.emit();
      }
    })().catch((error: unknown) => {
      if (!this.stopped && generation === this.generation && !updateRequest.signal.aborted) {
        const previousSummaryError = this.summaryError;
        this.summaryError = asError(error);
        this.state = { ...this.state, phase: this.state.phase === 'loading' ? 'failed' : this.state.phase,
          error: !this.state.error || this.state.error === previousSummaryError
          ? this.summaryError : this.state.error };
        this.emit();
      }
    }).finally(() => {
      if (this.updateRequest === updateRequest) {
        this.updateRequest = null;
        this.refreshing = null;
      }
    });
    return this.refreshing;
  }

  async loadMore(): Promise<void> {
    const generation = this.generation;
    const requestedView = this.state.summary?.viewId;
    // A refresh can publish usable pages before its final job-status lookup
    // finishes. Keep an enabled-button click until that operation settles.
    while (this.refreshing && !this.stopped && this.visible) await this.refreshing;
    const summary = this.state.summary;
    if (!summary || summary.viewId !== requestedView || generation !== this.generation
      || !this.state.nextCursor || this.stopped || !this.visible || this.state.loadingMore || this.state.refining) return;
    const pages = this.pages;
    const request = new AbortController();
    this.request = request;
    this.state = { ...this.state, loadingMore: true };
    this.emit();
    try {
      const page = await this.loadPage(summary, this.state.nextCursor, this.state.sortOrder, request.signal);
      if (this.stopped || request.signal.aborted || generation !== this.generation || pages !== this.pages || !sameWireInteger(page.revision.revision, summary.revision)) return;
      this.pages = [...this.pages, page];
      this.publishPages(summary);
    } catch (error) {
      if (this.stopped || request.signal.aborted || generation !== this.generation || pages !== this.pages) return;
      this.state = { ...this.state, error: asError(error) };
      this.emit();
    } finally {
      if (this.request === request) {
        this.request = null;
        this.state = { ...this.state, loadingMore: false };
        this.emit();
      }
    }
  }

  toggle(ref: string, selected: boolean, parentRef?: string): void {
    const child = parentRef ? this.state.directoryFiles.get(parentRef)?.items.find(item => item.ref === ref) : undefined;
    if (!this.selection.toggle(ref, selected, parentRef, child)) return;
    this.state = { ...this.state, receipt: null };
    this.emit();
  }

  async openGroupOptions(ref: string): Promise<void> {
    const summary = this.state.summary;
    const row = this.state.rows.find(row => row.ref === ref);
    if (!summary || !row || (row.kind !== 'track-group' && row.kind !== 'album-group')) return;
    this.state = { ...this.state, groupOptions: this.groupOptionsPanel(row, summary.revision) };
    this.emit();
    await this.loadMoreGroupOptions();
  }

  private groupOptionsPanel(
    row: Extract<SearchRow, { kind: 'track-group' | 'album-group' }>,
    revision: number | string,
  ): GroupOptionsPanel {
    return {
      groupRef: row.ref,
      kind: row.kind === 'track-group' ? 'track' : 'album',
      title: row.kind === 'track-group' ? row.value.query.title ?? 'Untitled track' : row.value.query.album ?? 'Untitled album',
      artist: row.value.query.artist ?? null,
      shareCount: Number(row.value.shareCount),
      optionCount: Number(row.value.optionCount),
      representativeRef: row.kind === 'track-group'
        ? row.value.representative.ref : row.value.representative.ref.ref,
      revision,
      items: [], nextCursor: null, loading: false, error: null,
    };
  }

  closeGroupOptions(): void {
    this.state = { ...this.state, groupOptions: null };
    this.emit();
  }

  async loadMoreGroupOptions(): Promise<void> {
    const summary = this.state.summary;
    const panel = this.state.groupOptions;
    if (!summary || !panel || panel.loading || (panel.items.length > 0 && !panel.nextCursor && !panel.error)) return;
    const generation = this.generation;
    const pending = { ...panel, loading: true, error: null };
    this.state = { ...this.state, groupOptions: pending };
    this.emit();
    try {
      const page = panel.kind === 'track'
        ? await this.api.getSearchViewTrackGroupOptions(summary.viewId, panel.groupRef, panel.revision, panel.nextCursor)
        : await this.api.getSearchViewAlbumGroupOptions(summary.viewId, panel.groupRef, panel.revision, panel.nextCursor);
      if (this.stopped || generation !== this.generation || this.state.groupOptions !== pending) return;
      if (!sameWireInteger(page.revision.revision, panel.revision) || page.groupRef !== panel.groupRef)
        throw new Error('Group alternatives do not match the selected group and revision.');
      const items: GroupOption[] = panel.kind === 'track'
        ? (page.items as Schema<'SearchViewFileDto'>[]).map(value => ({ kind: 'track', ref: value.ref, value }))
        : (page.items as Schema<'SearchViewDirectoryDto'>[]).map(value => ({ kind: 'album', ref: value.ref.ref, value }));
      const directoryFiles = new Map(this.state.directoryFiles);
      for (const item of items) if (item.kind === 'album') this.seedDirectoryPreview(item.value, directoryFiles);
      this.state = { ...this.state, directoryFiles, groupOptions: {
        ...pending, items: [...pending.items, ...items], nextCursor: page.nextCursor ?? null, loading: false,
      } };
    } catch (error) {
      if (this.stopped || generation !== this.generation || this.state.groupOptions !== pending) return;
      this.state = { ...this.state, groupOptions: { ...pending, loading: false, error: asError(error) } };
    }
    this.emit();
  }

  chooseGroupOption(groupRef: string, option: GroupOption): void {
    const panel = this.state.groupOptions;
    if (!panel || panel.groupRef !== groupRef || panel.kind !== option.kind
      || !panel.items.some(item => item.ref === option.ref)) return;
    const status = new Map(this.state.optionSteps);
    status.set(groupRef, { loading: false, error: null, position: panel.items.findIndex(item => item.ref === option.ref) + 1,
      optionRef: option.ref, revision: `${this.state.summary?.viewId}:${String(panel.revision)}` });
    this.state = { ...this.state, optionSteps: status };
    this.applyGroupOption(groupRef, option);
  }

  private applyGroupOption(groupRef: string, option: GroupOption, select = true): void {
    if (!this.selection.choose(groupRef, option, select)) return;
    const directoryFiles = new Map(this.state.directoryFiles);
    if (option.kind === 'album') this.seedDirectoryPreview(option.value, directoryFiles);
    this.state = { ...this.state, directoryFiles, groupOptions: null, choiceValidationError: null, receipt: null };
    this.emit();
    if (this.state.summary) this.validateVisibleChoices(this.state.summary);
    if (option.kind === 'album' && !option.value.initialFiles) void this.loadDirectoryFiles(option.ref);
  }

  async stepGroupOption(groupRef: string, direction: -1 | 1): Promise<void> {
    const summary = this.state.summary;
    const row = this.state.rows.find(item => item.ref === groupRef);
    if (!summary || !row || (row.kind !== 'track-group' && row.kind !== 'album-group') || this.state.optionSteps.get(groupRef)?.loading) return;
    const generation = this.generation;
    const revision = `${summary.viewId}:${String(summary.revision)}`;
    const representative = row.kind === 'track-group' ? row.value.representative.ref : row.value.representative.ref.ref;
    const chosen = this.selection.value.chosenOptions.get(groupRef);
    const currentRef = chosen && this.selection.value.checkedChoiceRevisions.get(groupRef) === revision && !this.selection.value.staleChoices.has(groupRef) ? chosen.ref : representative;
    let cache = this.optionPages.get(groupRef);
    if (!cache || cache.revision !== revision) {
      cache = { revision, items: [], nextCursor: null, loaded: false };
      this.optionPages.set(groupRef, cache);
    }
    const setStatus = (status: { loading: boolean; error: Error | null; position?: number; optionRef?: string; revision?: string }) => {
      this.state = { ...this.state, optionSteps: new Map(this.state.optionSteps).set(groupRef, { ...this.state.optionSteps.get(groupRef), ...status }) };
      this.emit();
    };
    setStatus({ loading: true, error: null });
    try {
      let index = cache.items.findIndex(item => item.ref === currentRef);
      while ((!cache.loaded || (cache.nextCursor && (index < 0 || (direction === 1 && index === cache.items.length - 1))))) {
        const page = row.kind === 'track-group'
          ? await this.api.getSearchViewTrackGroupOptions(summary.viewId, groupRef, summary.revision, cache.nextCursor)
          : await this.api.getSearchViewAlbumGroupOptions(summary.viewId, groupRef, summary.revision, cache.nextCursor);
        if (this.stopped || generation !== this.generation) return;
        if (this.state.summary?.viewId !== summary.viewId || !sameWireInteger(this.state.summary.revision, summary.revision)) {
          setStatus({ loading: false, error: null });
          return;
        }
        if (!sameWireInteger(page.revision.revision, summary.revision) || page.groupRef !== groupRef) throw new Error('Group alternatives do not match the selected group and revision.');
        const items: GroupOption[] = row.kind === 'track-group'
          ? (page.items as Schema<'SearchViewFileDto'>[]).map(value => ({ kind: 'track', ref: value.ref, value }))
          : (page.items as Schema<'SearchViewDirectoryDto'>[]).map(value => ({ kind: 'album', ref: value.ref.ref, value }));
        cache.items.push(...items);
        cache.nextCursor = page.nextCursor ?? null;
        cache.loaded = true;
        index = cache.items.findIndex(item => item.ref === currentRef);
      }
      const target = index >= 0 ? cache.items[index + direction] : undefined;
      if (target && this.selection.value.chosenOptions.get(groupRef)?.ref === chosen?.ref) this.applyGroupOption(groupRef, target, false);
      setStatus({ loading: false, error: null, position: target ? index + direction + 1 : index + 1,
        optionRef: target?.ref ?? currentRef, revision });
    } catch (error) {
      if (!this.stopped && generation === this.generation) setStatus({ loading: false, error: asError(error) });
    }
  }

  private hasVisibleDirectory(ref: string): boolean {
    if (this.state.rows.some(row => row.kind === 'directory' && row.ref === ref)) return true;
    if (this.state.rows.some(row => row.kind === 'album-group'
      && (row.value.representative.ref.ref === ref
        || (this.selection.value.chosenOptions.get(row.ref)?.kind === 'album'
          && (this.selection.value.chosenOptions.get(row.ref)?.value as Schema<'SearchViewDirectoryDto'>).ref.ref === ref)))) return true;
    return this.state.groupOptions?.items.some(option => option.kind === 'album' && option.ref === ref) ?? false;
  }

  async retrieveDirectory(ref: string): Promise<void> {
    const summary = this.state.summary;
    const row = this.state.rows.find(row => row.kind === 'directory' && row.ref === ref);
    const groupDirectory = this.state.rows
      .filter(row => row.kind === 'album-group')
      .map(row => this.selection.value.chosenOptions.get(row.ref)?.kind === 'album'
        ? this.selection.value.chosenOptions.get(row.ref)!.value as Schema<'SearchViewDirectoryDto'>
        : row.kind === 'album-group' ? row.value.representative : null)
      .find(directory => directory?.ref.ref === ref);
    const panelDirectory = this.state.groupOptions?.items.find(option => option.kind === 'album' && option.ref === ref);
    const directory = row?.kind === 'directory' ? row.value : groupDirectory
      ?? (panelDirectory?.kind === 'album' ? panelDirectory.value : null);
    if (!summary || !directory || this.state.retrievals.get(ref)?.loading
      || this.retrievalJobs.has(ref) || this.retrievalInFlight.has(ref)) return;
    const generation = this.generation;
    // A lost response retries the exact accepted request, including its
    // revision and key. A received job response ends this logical request.
    const request = this.retrievalRequests.get(ref) ?? {
      viewId: summary.viewId,
      revision: summary.revision,
      directory: directory.ref,
      idempotencyKey: randomUUID(),
    };
    this.retrievalRequests.set(ref, request);
    const inFlight = Symbol();
    this.retrievalInFlight.set(ref, inFlight);
    this.state = { ...this.state, retrievals: new Map(this.state.retrievals).set(ref, { loading: true, error: null }) };
    this.emit();
    try {
      const job = await this.api.retrieveSearchViewDirectory(
        request.viewId, request.revision, request.directory, request.idempotencyKey);
      if (this.stopped || this.retrievalRequests.get(ref) !== request) return;
      if (!job.jobId) throw new Error('Folder retrieval did not return a job ID.');
      this.retrievalRequests.delete(ref);
      this.retrievalJobs.set(ref, job.jobId);
      if (generation !== this.generation) {
        this.schedulePoll();
        return;
      }
      await this.refresh();
      this.schedulePoll();
    } catch (error) {
      if (this.stopped || this.retrievalRequests.get(ref) !== request
        || (generation !== this.generation && !this.hasVisibleDirectory(ref))) return;
      this.state = { ...this.state, retrievals: new Map(this.state.retrievals).set(ref, { loading: false, error: asError(error) }) };
      this.emit();
    } finally {
      if (this.retrievalInFlight.get(ref) === inFlight) this.retrievalInFlight.delete(ref);
    }
  }

  async loadDirectoryFiles(ref: string): Promise<void> {
    const summary = this.state.summary;
    const generation = this.generation;
    const previous = this.state.directoryFiles.get(ref);
    if (!summary || this.stopped || previous?.loading || (previous && !previous.error && !previous.nextCursor)) return;
    const requestedCount = previous?.error
      ? this.directoryFileCounts.get(ref) ?? previous.items.length + searchFilePageSize
      : Math.max(this.directoryFileCounts.get(ref) ?? 0, previous?.items.length ?? 0) + searchFilePageSize;
    this.directoryFileCounts.set(ref, requestedCount);
    const pending = { ...previous, items: previous?.items ?? [],
      nextCursor: previous?.nextCursor ?? null, loading: true, error: null };
    this.state = { ...this.state, directoryFiles: new Map(this.state.directoryFiles).set(ref, pending) };
    this.emit();
    try {
      const page = await this.api.getSearchViewDirectoryFiles(summary.viewId, ref, summary.revision,
        pending.nextCursor, requestedCount - pending.items.length);
      if (generation !== this.generation || this.state.directoryFiles.get(ref) !== pending) return;
      if (!sameWireInteger(page.revision.revision, summary.revision) || page.directory.ref !== ref) {
        throw new Error('Directory file page does not match the requested directory and revision.');
      }
      this.selection.rememberFiles(page.items);
      this.state = { ...this.state, directoryFiles: new Map(this.state.directoryFiles).set(ref, {
        items: [...pending.items, ...page.items], directorySummary: page.directorySummary,
        nextCursor: page.nextCursor ?? null, loading: false, error: null,
      }) };
    } catch (error) {
      if (generation !== this.generation || this.state.directoryFiles.get(ref) !== pending) return;
      this.state = { ...this.state, directoryFiles: new Map(this.state.directoryFiles).set(ref, { ...pending, loading: false, error: asError(error) }) };
    }
    this.emit();
  }

  selectAll(selected: boolean): void {
    if (!this.selection.selectAll(selected)) return;
    this.state = { ...this.state, receipt: null };
    this.emit();
  }

  isSelected(ref: string, parentRef?: string): boolean {
    return this.selection.isSelected(ref, parentRef);
  }

  isDirectoryPathSelected(directoryRef: string, relativePath: string): boolean {
    return this.selection.isDirectoryPathSelected(directoryRef, relativePath);
  }

  toggleDirectoryPath(directoryRef: string, relativePath: string, selected: boolean): void {
    if (!this.selection.toggleDirectoryPath(directoryRef, relativePath, selected)) return;
    this.state = { ...this.state, receipt: null };
    this.emit();
  }

  selectedCount(): number { return this.selection.selectedCount(); }

  private syncSelectionCounts(): void {
    const summary = this.state.summary;
    if (!summary || this.stopped || !this.selection.needsCounts(this.visibleGroupRefs())) {
      this.countRequest?.abort();
      this.countRequest = null;
      this.countRequestKey = null;
      this.selection.clearCounts();
      if (this.state.selectionCountsLoading || this.state.selectionCountsError)
        this.state = { ...this.state, selectionCountsLoading: false, selectionCountsError: null };
      return;
    }
    const request = this.selection.input();
    const key = selectionCountKey(summary.viewId, request);
    if (key === this.countRequestKey) return;
    this.countRequest?.abort();
    this.countRequest = null;
    this.countRequestKey = key;
    // A refinement's membership check may already have supplied this exact count.
    if (this.selection.value.selectionCounts) {
      this.state = { ...this.state, selectionCountsLoading: false, selectionCountsError: null };
      return;
    }
    const controller = new AbortController();
    this.countRequest = controller;
    this.state = { ...this.state, selectionCountsLoading: true, selectionCountsError: null };
    void this.api.countSearchViewSelection(summary.viewId, request, controller.signal).then(counts => {
      if (this.stopped || this.countRequestKey !== key || controller.signal.aborted) return;
      if (!this.selection.acceptCounts(request, counts))
        throw new Error('Selection count does not match the requested selection and revision.');
      this.state = { ...this.state, selectionCountsLoading: false };
      this.emit();
    }).catch(error => {
      if (this.stopped || this.countRequestKey !== key || controller.signal.aborted) return;
      this.selection.clearCounts();
      this.state = { ...this.state, selectionCountsLoading: false, selectionCountsError: asError(error) };
      this.emit();
    });
  }

  private visibleGroupRefs(): string[] {
    return this.state.rows.filter(row => row.kind === 'track-group' || row.kind === 'album-group')
      .map(row => row.ref);
  }

  toggleAlbumGroupFile(groupRef: string, fileRef: string, selected: boolean): void {
    const row = this.state.rows.find(row => row.kind === 'album-group' && row.ref === groupRef);
    if (row?.kind !== 'album-group') return;
    const choice = this.selection.choiceIsCurrent(groupRef) ? this.selection.value.chosenOptions.get(groupRef) : null;
    const directoryRef = choice?.kind === 'album' ? choice.ref : row.value.representative.ref.ref;
    const files = this.state.directoryFiles.get(directoryRef);
    const completeRefs = files && (!files.loading || files.refreshing) && !files.error && !files.nextCursor
      ? files.items.filter(file => file.file.visibility !== 'Locked').map(file => file.ref) : undefined;
    if (!this.selection.toggleAlbumFile(groupRef, row.value.representative, fileRef, selected, completeRefs)) return;
    this.state = { ...this.state, choiceValidationError: null, receipt: null };
    this.emit();
    if (this.state.summary) this.validateVisibleChoices(this.state.summary);
  }

  async commit(options?: Schema<'SubmissionOptionsDto'>): Promise<void> {
    const summary = this.state.summary;
    if (!summary || this.state.committing) return;
    const generation = this.generation;
    const retry = this.selection.retry(options);
    if (!retry && (this.state.refining || this.state.refinementError || searchViewUnavailable(this.state))) return;
    if (!retry && this.selectedCount() === 0) return;
    if (!retry && this.selection.hasUnreadySelectedChoices(this.visibleGroupRefs())) {
      this.state = { ...this.state, error: new Error('A chosen source changed. Recheck or reselect it before downloading.') };
      this.emit();
      return;
    }
    const pending = retry ?? this.selection.captureCommit(options);
    this.state = { ...this.state, committing: true, receipt: null, error: null };
    this.emit();
    try {
      const receipt = await this.api.commitSearchView(pending.viewId, pending.request);
      if (generation !== this.generation) return;
      this.state = { ...this.state, committing: false, receipt };
      this.selection.finishCommit(pending);
      this.emit();
    } catch (error) {
      if (generation !== this.generation) return;
      // The daemon replays accepted receipts before resolving the issuing View.
      // A received 404 therefore rejects this request; an unknown outcome keeps
      // its exact command/key so a lost response can still be recovered.
      if (error instanceof DaemonApiError && error.status === 404)
        this.selection.finishCommit(pending);
      this.state = { ...this.state, committing: false, error: asError(error) };
      this.emit();
    }
  }

  async resolveSelectionSettings(options?: Schema<'SubmissionOptionsDto'>, signal?: AbortSignal): Promise<Schema<'ResolveSearchViewSelectionSettingsResponseDto'>> {
    const summary = this.state.summary;
    if (!summary) throw new Error('Search results are not available.');
    const { revision, selection, includedFileRefs, directoryPathRules } = this.selection.input();
    return this.api.resolveSearchViewSelectionSettings(summary.viewId,
      { revision, selection, includedFileRefs, directoryPathRules, options }, signal);
  }

  private cancelPageRequest(): void {
    this.request?.abort();
    this.request = null;
    this.state = { ...this.state, loadingMore: false };
  }

  private async reloadVisiblePages(summary: SearchViewSummaryDto, count: number,
    options?: { request?: AbortController; replaceProjection?: boolean; resetSelection?: boolean; sortOrder?: Schema<'ServerSearchViewSortOrder'> }): Promise<boolean> {
    this.cancelPageRequest();
    // Opening a finished search rebuilds its projection from stored inputs.
    // Observe that catch-up without fetching or displaying intermediate pages.
    if (!options?.replaceProjection && this.pages.length === 0
      && this.state.job?.lifecycleState === 'Terminal' && !summary.isComplete) {
      this.state = { ...this.state, summary, phase: 'loading', error: null };
      this.emit();
      return true;
    }
    const replacing = options?.replaceProjection ?? false;
    const request = options?.request ?? new AbortController();
    const order = options?.sortOrder ?? this.state.sortOrder;
    this.request = request;
    try {
      const pages: SearchPage[] = [];
      let cursor: string | null = null;
      // The summary already proves an empty projection. Fetching its page
      // adds latency and traffic but cannot yield any cards.
      for (let index = 0; Number(summary.counters.topLevelItemCount) > 0 && index < count; index += 1) {
        const page = await this.loadPage(summary, cursor, order, request.signal);
        if (!sameWireInteger(page.revision.revision, summary.revision)) {
          throw new Error('Search View page revision does not match its summary.');
        }
        pages.push(page);
        cursor = page.nextCursor ?? null;
        if (!cursor) break;
      }
      if (this.stopped || request.signal.aborted) return false;
      const mode = this.state.draft?.resultMode;
      const directories = mode === 'generic' || mode === 'album'
        ? (pages as SearchViewDirectoryPageDto[]).flatMap(page => page.items)
        : mode === 'album-groups'
          ? (pages as SearchViewAlbumGroupPageDto[]).flatMap(page => page.items.map(item => item.representative))
          : [];
      const orderingOnly = Boolean(options?.request) && !replacing;
      // Reordering the same snapshot cannot invalidate cached child pages or
      // advance a pinned selection. Retry/validation belongs to an actual refresh.
      const directoryFiles = orderingOnly ? new Map(this.state.directoryFiles)
        : await this.refreshDirectoryPages(summary, directories, request.signal);
      if (this.stopped || request.signal.aborted) return false;
      if (options?.resetSelection) this.selection.reset();
      const visibleRoots = new Set(directories.map(directory => directory.ref.ref));
      const rebase = replacing ? this.selection.prepareRebase(summary, visibleRoots) : null;
      let checked: Schema<'CountSearchViewSelectionResponseDto'> | undefined;
      if (rebase?.validation) {
        try { checked = await this.api.countSearchViewSelection(summary.viewId, rebase.validation, request.signal); }
        catch { /* A replacement remains unbound when its roots cannot be validated. */ }
      }
      if (this.stopped || request.signal.aborted) return false;
      if (replacing) {
        this.selection.replaceProjection(summary, rebase, checked);
        this.state = { ...this.state, refining: false, refinementError: null, loadingMore: false,
          groupOptions: null, retrievals: new Map(),
          selectionCountsLoading: false, selectionCountsError: null,
          choiceValidationLoading: false, choiceValidationError: null, receipt: null };
      }
      this.state = { ...this.state, sortOrder: order,
        ...(options?.request ? { refining: false, refinementError: null } : {}) };
      this.pages = pages;
      this.publishPages(summary, directoryFiles);
      return true;
    } catch (error) {
      if (request.signal.aborted || this.stopped) return false;
      if (options?.request) throw error;
      this.state = { ...this.state, phase: this.pages.length ? 'ready' : 'failed', error: asError(error) };
      this.emit();
      return false;
    } finally {
      if (this.request === request) this.request = null;
    }
  }

  private loadPage(summary: SearchViewSummaryDto, cursor: string | null, sortOrder: Schema<'ServerSearchViewSortOrder'>, signal?: AbortSignal): Promise<SearchPage> {
    const mode = this.state.draft?.resultMode;
    if (mode === 'generic' || mode === 'album') {
      return this.api.getSearchViewDirectories(summary.viewId, summary.revision, cursor, searchDirectoryPageSize, sortOrder, signal);
    }
    if (mode === 'song-groups') {
      return this.api.getSearchViewTrackGroups(summary.viewId, summary.revision, cursor, searchFilePageSize, signal);
    }
    if (mode === 'album-groups') {
      return this.api.getSearchViewAlbumGroups(summary.viewId, summary.revision, cursor, searchDirectoryPageSize, signal);
    }
    return this.api.getSearchViewFiles(summary.viewId, summary.revision, cursor, searchFilePageSize, sortOrder, signal);
  }

  private publishPages(summary: SearchViewSummaryDto, refreshedDirectories?: Map<string, DirectoryFilesPage>): void {
    const rows: SearchRow[] = [];
    const mode = this.state.draft?.resultMode;
    if (mode === 'track') {
      for (const page of this.pages as SearchViewFilePageDto[]) {
        for (const item of page.items) rows.push({ kind: 'file', ref: item.ref, value: item });
      }
    } else if (mode === 'generic' || mode === 'album') {
      for (const page of this.pages as SearchViewDirectoryPageDto[]) {
        for (const item of page.items) rows.push({ kind: 'directory', ref: item.ref.ref, value: item });
      }
    } else if (mode === 'song-groups') {
      for (const page of this.pages as SearchViewTrackGroupPageDto[]) {
        for (const item of page.items) rows.push({ kind: 'track-group', ref: item.ref, value: item });
      }
    } else if (mode === 'album-groups') {
      for (const page of this.pages as SearchViewAlbumGroupPageDto[]) {
        for (const item of page.items) rows.push({ kind: 'album-group', ref: item.ref, value: item });
      }
    }
    const directoryFiles = refreshedDirectories ?? (sameWireInteger(this.state.summary?.revision ?? -1, summary.revision)
      ? new Map(this.state.directoryFiles) : new Map<string, DirectoryFilesPage>());
    const retrievals = new Map(this.state.retrievals);
    for (const row of rows) {
      const choice = this.selection.choiceIsCurrent(row.ref) ? this.selection.value.chosenOptions.get(row.ref) : null;
      const value = row.kind === 'file' || row.kind === 'directory' ? row.value : choice?.value ?? row.value.representative;
      this.selection.rememberResult(row.ref, value.visibility === 'Locked');
      if (row.kind === 'directory') {
        if (!directoryFiles.has(row.ref)) this.seedDirectoryPreview(row.value, directoryFiles);
        if (!retrievals.has(row.ref)
          && (this.retrievalJobs.has(row.ref) || this.retrievalInFlight.has(row.ref)))
          retrievals.set(row.ref, { loading: true, error: null });
      } else if (row.kind === 'album-group') {
        if (!directoryFiles.has(row.value.representative.ref.ref))
          this.seedDirectoryPreview(row.value.representative, directoryFiles);
        const ref = row.value.representative.ref.ref;
        if (!retrievals.has(ref)
          && (this.retrievalJobs.has(ref) || this.retrievalInFlight.has(ref)))
          retrievals.set(ref, { loading: true, error: null });
      }
    }
    for (const files of directoryFiles.values())
      this.selection.rememberFiles(files.items);
    const previousPanel = this.state.groupOptions;
    const panelNeedsNewRevision = Boolean(previousPanel && !sameWireInteger(previousPanel.revision, summary.revision));
    const panelRow = panelNeedsNewRevision && previousPanel
      ? rows.find((row): row is Extract<SearchRow, { kind: 'track-group' | 'album-group' }> =>
        row.ref === previousPanel.groupRef && (row.kind === 'track-group' || row.kind === 'album-group')
        && (row.kind === 'track-group' ? 'track' : 'album') === previousPanel.kind)
      : null;
    const groupOptions = panelNeedsNewRevision
      ? panelRow ? this.groupOptionsPanel(panelRow, summary.revision) : null
      : previousPanel;
    this.state = {
      ...this.state,
      phase: 'ready',
      groupOptions,
      directoryFiles,
      retrievals,
      summary,
      rows,
      nextCursor: this.pages.at(-1)?.nextCursor ?? null,
      error: null,
    };
    this.emit();
    this.validateVisibleChoices(summary);
    if (panelRow) void this.loadMoreGroupOptions();
  }

  private validateVisibleChoices(summary: SearchViewSummaryDto): void {
    const choices = this.selection.choicesToValidate(this.visibleGroupRefs());
    if (Object.keys(choices).length === 0) {
      this.choiceRequest?.abort();
      this.choiceRequest = null;
      this.choiceRequestKey = null;
      if (this.state.choiceValidationLoading || this.state.choiceValidationError) {
        this.state = { ...this.state, choiceValidationLoading: false, choiceValidationError: null };
        this.emit();
      }
      return;
    }
    const key = JSON.stringify([summary.viewId, summary.revision, choices]);
    if (this.choiceRequest && this.choiceRequestKey === key) return;
    this.choiceRequest?.abort();
    const request = new AbortController();
    this.choiceRequest = request;
    this.choiceRequestKey = key;
    this.state = { ...this.state, choiceValidationLoading: true, choiceValidationError: null };
    this.emit();
    void this.api.resolveSearchViewChosenOptions(summary.viewId,
      { revision: summary.revision, chosenOptions: choices }, request.signal).then(async resolved => {
      if (this.stopped || request.signal.aborted || this.choiceRequest !== request
        || !sameWireInteger(this.state.summary?.revision ?? -1, summary.revision)
        || this.state.summary?.viewId !== summary.viewId) return;
      if (!sameWireInteger(resolved.revision.revision, summary.revision))
        throw new Error('Chosen sources do not match the visible Search View revision.');
      const albums = resolved.items.flatMap(item => item.album
        && this.selection.value.chosenOptions.get(item.groupRef)?.ref === item.optionRef
        && !this.state.directoryFiles.has(item.album.ref.ref) ? [item.album] : []);
      const restored = await this.refreshDirectoryPages(summary, albums, request.signal);
      if (this.stopped || request.signal.aborted || this.choiceRequest !== request
        || this.state.summary?.viewId !== summary.viewId
        || !sameWireInteger(this.state.summary.revision, summary.revision)) return;
      const directoryFiles = new Map(this.state.directoryFiles);
      for (const album of this.selection.acceptChoices(summary, choices, resolved)) {
        const files = restored.get(album.ref.ref);
        if (files) {
          directoryFiles.set(album.ref.ref, files);
          this.selection.rememberFiles(files.items);
        } else this.seedDirectoryPreview(album, directoryFiles);
      }
      this.state = { ...this.state, directoryFiles, choiceValidationLoading: false, choiceValidationError: null };
      this.emit();
    }).catch(error => {
      if (this.stopped || request.signal.aborted || this.choiceRequest !== request) return;
      this.state = { ...this.state, choiceValidationLoading: false,
        choiceValidationError: asError(error) };
      this.emit();
    }).finally(() => {
      if (this.choiceRequest === request) {
        this.choiceRequest = null;
        this.choiceRequestKey = null;
      }
    });
  }

  private async refreshDirectoryPages(summary: SearchViewSummaryDto,
    directories: Schema<'SearchViewDirectoryDto'>[], signal: AbortSignal): Promise<Map<string, DirectoryFilesPage>> {
    const result = new Map<string, DirectoryFilesPage>();
    const pending = new Map(this.state.directoryFiles);
    let loadingChanged = false;
    const reads = directories.map(async directory => {
      const ref = directory.ref.ref;
      const current = this.state.directoryFiles.get(ref);
      if (this.state.summary?.viewId === summary.viewId
        && sameWireInteger(this.state.summary.revision, summary.revision) && current && !current.error) {
        result.set(ref, current);
        return;
      }
      const preview = directory.initialFiles;
      const requestedCount = Math.max(preview?.items.length ?? 0,
        this.directoryFileCounts.get(ref) ?? 0);
      const initial: DirectoryFilesPage = { items: [...(preview?.items ?? [])],
        directorySummary: directory, nextCursor: preview?.nextCursor ?? null, loading: false, error: null };
      result.set(ref, initial);
      // Only continued folders need another read. Never reuse a cursor or
      // append children from the previous revision: new arrivals can reorder.
      if (requestedCount <= initial.items.length || (preview && !preview.nextCursor)) return;
      if (current && !current.loading) {
        pending.set(ref, { ...current, loading: true, refreshing: true, error: null });
        loadingChanged = true;
      }
      try {
        const page = await this.api.getSearchViewDirectoryFiles(summary.viewId, ref, summary.revision,
          initial.nextCursor, requestedCount - initial.items.length, signal);
        if (!sameWireInteger(page.revision.revision, summary.revision) || page.directory.ref !== ref)
          throw new Error('Directory file page does not match the requested directory and revision.');
        result.set(ref, { items: [...initial.items, ...page.items],
          directorySummary: page.directorySummary ?? directory,
          nextCursor: page.nextCursor ?? null, loading: false, error: null });
      } catch (error) {
        result.set(ref, { ...initial, error: asError(error) });
      }
    });
    if (loadingChanged) {
      this.state = { ...this.state, directoryFiles: pending };
      this.emit();
    }
    await Promise.all(reads);
    return result;
  }

  private seedDirectoryPreview(directory: Schema<'SearchViewDirectoryDto'>, target: Map<string, DirectoryFilesPage>): void {
    const preview = directory.initialFiles;
    if (!preview) return;
    const ref = directory.ref.ref;
    const previous = target.get(ref);
    if (previous && !previous.error) return;
    this.selection.rememberFiles(preview.items);
    target.set(ref, { items: [...preview.items], directorySummary: directory,
      nextCursor: preview.nextCursor ?? null, loading: false, error: null });
  }

  private waitingForInitialResults(): boolean {
    return this.state.job?.lifecycleState === 'Terminal' && !this.state.summary?.isComplete
      && this.pages.length === 0 && !this.state.error;
  }

  private schedulePoll(): void {
    if (this.timer) clearTimeout(this.timer);
    if (this.observation && this.retrievalJobs.size === 0) return;
    if (this.stopped || !this.visible || searchViewUnavailable(this.state) || (this.state.summary?.isComplete && this.retrievalJobs.size === 0
      && this.state.job?.lifecycleState === 'Terminal')) return;
    this.timer = setTimeout(async () => {
      this.timer = null;
      await this.refresh();
      this.schedulePoll();
    }, this.waitingForInitialResults() ? 0 : this.pollIntervalMs);
  }

  private emit(): void {
    if (searchViewUnavailable(this.state)) {
      this.stopObservation();
      if (this.timer) clearTimeout(this.timer);
      this.timer = null;
    }
    this.selection.observe(this.state.summary);
    this.syncSelectionCounts();
    const value = this.current();
    for (const listener of this.listeners) listener(value);
  }

}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}
