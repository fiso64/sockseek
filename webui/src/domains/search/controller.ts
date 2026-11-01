import { DaemonApiClient, daemonApi, defaultPageSize, nestedPageSize, type SubmittedSearch } from '../../api/client';
import type {
  CommitSearchViewSelectionResponseDto,
  Schema,
  SearchViewAggregateAlbumPageDto,
  SearchViewAggregateTrackPageDto,
  SearchViewDirectoryPageDto,
  SearchViewFilePageDto,
  SearchViewSummaryDto,
} from '../../api/types';
import { sameWireInteger, wireInteger } from '../../api/types';
import type { SearchDraft } from './model';
import { DaemonApiError } from '../../api/http';

export type SearchPage =
  | SearchViewFilePageDto
  | SearchViewDirectoryPageDto
  | SearchViewAggregateTrackPageDto
  | SearchViewAggregateAlbumPageDto;

export type SearchRow =
  | { kind: 'file'; ref: string; value: SearchViewFilePageDto['items'][number] }
  | { kind: 'directory'; ref: string; value: SearchViewDirectoryPageDto['items'][number] }
  | { kind: 'aggregate-track'; ref: string; value: SearchViewAggregateTrackPageDto['items'][number] }
  | { kind: 'aggregate-album'; ref: string; value: SearchViewAggregateAlbumPageDto['items'][number] };

export type AggregateOption =
  | { kind: 'track'; ref: string; value: Schema<'SearchViewFileDto'> }
  | { kind: 'album'; ref: string; value: Schema<'SearchViewDirectoryDto'> };

export interface AggregateOptionsPanel {
  groupRef: string;
  kind: 'track' | 'album';
  title: string;
  artist: string | null;
  shareCount: number;
  optionCount: number;
  representativeRef: string;
  revision: number | string;
  items: AggregateOption[];
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

export interface SearchViewState {
  aggregateOptions: AggregateOptionsPanel | null;
  // Per-result choices are browser state, never a daemon-owned selection resource.
  chosenOptions: ReadonlyMap<string, AggregateOption>;
  checkedChoiceRevisions: ReadonlyMap<string, string>;
  staleChoices: ReadonlySet<string>;
  choiceValidationLoading: boolean;
  choiceValidationError: Error | null;
  albumFileSelections: ReadonlyMap<string, { mode: 'Only' | 'AllExcept'; refs: ReadonlySet<string> }>;
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
  selectedAll: boolean;
  selectionRevision: number | string | null;
  selectionNeedsReview: boolean;
  selectionTopLevelCount: number | null;
  selectionRefs: Set<string>;
  selectionCounts: Schema<'CountSearchViewSelectionResponseDto'> | null;
  selectionCountsLoading: boolean;
  selectionCountsError: Error | null;
  excludedFiles: Map<string, string>;
  excludedFileIdentities: Map<string, Schema<'FileCandidateRefDto'>>;
  includedFiles: Map<string, string>;
  directoryPathRules: Schema<'SearchViewDirectoryPathRuleDto'>[];
  committing: boolean;
  canRetryPendingCommit: boolean;
  receipt: CommitSearchViewSelectionResponseDto | null;
  error: Error | null;
}

type Listener = (state: SearchViewState) => void;
type ObserveView = (viewId: string, next: (summary: SearchViewSummaryDto) => void,
  error: (error: Error) => void, complete: () => void) => () => void;

function hasPublishedProjection(summary: SearchViewSummaryDto | null): summary is SearchViewSummaryDto {
  return summary !== null && !sameWireInteger(summary.revision, 0);
}

export function selectedSearchCount(state: SearchViewState): number {
  if (!hasPublishedProjection(state.summary) || state.selectionNeedsReview) return 0;
  if (state.selectionCounts && state.summary
    && sameWireInteger(state.selectionCounts.revision,
      state.draft?.resultMode === 'generic' && state.selectionRevision !== null
        ? state.selectionRevision : state.summary.revision))
    return Number(state.selectionCounts.selectedItemCount);
  return requestedSearchCount(state);
}

function requestedSearchCount(state: SearchViewState): number {
  if (!state.selectedAll) {
    const selectedRoots = new Set(state.selectionRefs);
    const selectedBranches = new Set(state.directoryPathRules
      .filter(rule => rule.selected && !selectedRoots.has(rule.directoryRef))
      .map(rule => rule.directoryRef));
    return state.selectionRefs.size + state.includedFiles.size + selectedBranches.size;
  }
  const selectedBranches = new Set(state.directoryPathRules
    .filter(rule => rule.selected && state.selectionRefs.has(rule.directoryRef))
    .map(rule => rule.directoryRef));
  return Math.max(0, selectionScopeSize(state) - state.selectionRefs.size) + state.includedFiles.size + selectedBranches.size;
}

function selectionScopeSize(state: SearchViewState): number {
  if (state.draft?.resultMode === 'generic' && state.selectionTopLevelCount !== null)
    return state.selectionTopLevelCount;
  return Number(state.summary?.kind === 'Files'
    ? state.summary.counters.selectableOptionCount : state.summary?.counters.topLevelItemCount ?? 0);
}

export class SearchViewController {
  private listeners = new Set<Listener>();
  private pages: SearchPage[] = [];
  private retrievalJobs = new Map<string, string>();
  private retrievalInFlight = new Map<string, symbol>();
  private retrievalRequests = new Map<string, {
    viewId: string;
    revision: number | string;
    directory: Schema<'PeerDirectoryRefDto'>;
    idempotencyKey: string;
  }>();
  // Keep ownership for explicit child selections even when their page is reloaded.
  private selectedFileParents = new Map<string, string>();
  private fileRelativePaths = new Map<string, string>();
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
  private pendingCommit: {
    viewId: string;
    revision: number | string;
    mode: Schema<'RefSelectionMode'>;
    refs: string[];
    excludedFileRefs: string[];
    excludedFileIdentities: Record<string, Schema<'FileCandidateRefDto'>>;
    includedFileRefs: string[];
    chosenOptions: Record<string, string>;
    albumFileSelections: Record<string, { mode: 'Only' | 'AllExcept'; refs: string[] }>;
    directoryPathRules: Schema<'SearchViewDirectoryPathRuleDto'>[];
    useRepresentativeWhenUnchosen: boolean;
    options?: Schema<'SubmissionOptionsDto'>;
    idempotencyKey: string;
  } | null = null;
  private generation = 0;
  private refinementGeneration = 0;
  private refinementRequest: AbortController | null = null;
  private countRequestKey: string | null = null;
  private countRequest: AbortController | null = null;
  private choiceRequest: AbortController | null = null;
  private choiceRequestKey: string | null = null;
  private state: SearchViewState = {
    aggregateOptions: null,
    chosenOptions: new Map(),
    checkedChoiceRevisions: new Map(),
    staleChoices: new Set(),
    choiceValidationLoading: false,
    choiceValidationError: null,
    albumFileSelections: new Map(),
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
    selectedAll: false,
    selectionRevision: null,
    selectionNeedsReview: false,
    selectionTopLevelCount: null,
    selectionRefs: new Set(),
    selectionCounts: null,
    selectionCountsLoading: false,
    selectionCountsError: null,
    excludedFiles: new Map(),
    excludedFileIdentities: new Map(),
    includedFiles: new Map(),
    directoryPathRules: [],
    committing: false,
    canRetryPendingCommit: false,
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
    if (!this.observeView || !this.liveConnected || !this.visible || this.stopped || !summary || summary.isComplete) return;
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
    return { ...this.state, canRetryPendingCommit: this.pendingCommit !== null,
      selectionRefs: new Set(this.state.selectionRefs), excludedFiles: new Map(this.state.excludedFiles), excludedFileIdentities: new Map(this.state.excludedFileIdentities), includedFiles: new Map(this.state.includedFiles), directoryPathRules: [...this.state.directoryPathRules], chosenOptions: new Map(this.state.chosenOptions), checkedChoiceRevisions: new Map(this.state.checkedChoiceRevisions), staleChoices: new Set(this.state.staleChoices), albumFileSelections: new Map([...this.state.albumFileSelections].map(([ref, selection]) => [ref, { ...selection, refs: new Set(selection.refs) }])) };
  }

  async open(submitted: SubmittedSearch): Promise<void> {
    this.stop();
    this.stopped = false;
    this.pages = [];
    this.pendingCommit = null;
    this.summaryError = null;
    this.selectedFileParents.clear();
    this.fileRelativePaths.clear();
    this.directoryFileCounts.clear();
    this.retrievalJobs.clear();
    this.retrievalInFlight.clear();
    this.retrievalRequests.clear();
    this.state = {
      phase: 'loading',
      aggregateOptions: null,
      chosenOptions: new Map(),
      checkedChoiceRevisions: new Map(),
      staleChoices: new Set(),
      choiceValidationLoading: false,
      choiceValidationError: null,
      albumFileSelections: new Map(),
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
      selectedAll: false,
      selectionRevision: null,
      selectionNeedsReview: false,
      selectionTopLevelCount: null,
      selectionRefs: new Set(),
      selectionCounts: null,
      selectionCountsLoading: false,
      selectionCountsError: null,
      excludedFiles: new Map(),
      excludedFileIdentities: new Map(),
      includedFiles: new Map(),
      directoryPathRules: [],
      committing: false,
      canRetryPendingCommit: false,
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

  async refine(request: Schema<'CreateSearchViewRequestDto'>): Promise<boolean> {
    const jobId = this.state.job?.jobId;
    if (!jobId || this.stopped) return false;
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
      // aggregate choices stay browser-owned; the daemon resolves their stable
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
      // Revision zero has no published projection. Keep the displayed view
      // until the existing publication wait supplies a usable replacement.
      while (sameWireInteger(view.revision, 0) && !view.isComplete) {
        view = (await this.api.getSearchViewUpdate(view.viewId, view.revision, controller.signal)).summary;
        if (this.stopped || version !== this.refinementGeneration || controller.signal.aborted) return false;
      }
      // Publish rows/counts/selection together, avoiding a destructive empty frame.
      if (!await this.reloadVisiblePages(view, 1, { request: controller, replaceProjection: true, sortOrder: view.sortOrder ?? 'Relevance' })) return false;
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
    if (!summary || this.stopped || this.state.refining) return;
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
        : await this.api.getSearchViewUpdate(summary.viewId, summary.revision, updateRequest.signal);
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
        this.state = { ...this.state, error: !this.state.error || this.state.error === previousSummaryError
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
    if (this.genericSelectionReadOnly()) return;
    const refs = new Set(this.state.selectionRefs);
    const excludedFiles = new Map(this.state.excludedFiles);
    const excludedFileIdentities = new Map(this.state.excludedFileIdentities);
    const includedFiles = new Map(this.state.includedFiles);
    if (parentRef && this.state.summary?.kind === 'GenericDirectories') {
      const child = this.state.directoryFiles.get(parentRef)?.items.find(item => item.ref === ref);
      if (child) this.fileRelativePaths.set(ref, child.relativePath);
      const inherited = this.directoryPathInheritance(parentRef, this.fileRelativePaths.get(ref));
      refs.delete(ref);
      if (selected === inherited) {
        includedFiles.delete(ref);
        excludedFiles.delete(ref);
        excludedFileIdentities.delete(ref);
      } else if (selected) {
        includedFiles.set(ref, parentRef);
        excludedFiles.delete(ref);
        excludedFileIdentities.delete(ref);
      } else {
        includedFiles.delete(ref);
        excludedFiles.set(ref, parentRef);
        if (child) excludedFileIdentities.set(ref, {
          username: child.file.peer.username, filename: child.file.remoteFilename,
        });
      }
      this.state = { ...this.state, selectionRefs: refs, excludedFiles, excludedFileIdentities, includedFiles, receipt: null };
      this.pendingCommit = null;
      this.emit();
      return;
    }
    if (parentRef && this.isSelected(parentRef)) {
      if (selected) {
        excludedFiles.delete(ref);
        excludedFileIdentities.delete(ref);
      } else {
        excludedFiles.set(ref, parentRef);
        const file = this.state.directoryFiles.get(parentRef)?.items.find(item => item.ref === ref)?.file;
        if (file) excludedFileIdentities.set(ref, {
          username: file.peer.username, filename: file.remoteFilename,
        });
      }
      refs.delete(ref);
      includedFiles.delete(ref);
    } else if (parentRef && this.state.selectedAll) {
      if (selected) includedFiles.set(ref, parentRef);
      else includedFiles.delete(ref);
      refs.delete(ref);
      excludedFiles.delete(ref);
      excludedFileIdentities.delete(ref);
    } else {
      if (selected === this.state.selectedAll) refs.delete(ref);
      else refs.add(ref);
      excludedFiles.delete(ref);
      excludedFileIdentities.delete(ref);
    }
    let directoryPathRules = this.state.directoryPathRules;
    let albumFileSelections = this.state.albumFileSelections;
    if (!parentRef) {
      directoryPathRules = directoryPathRules.filter(rule => rule.directoryRef !== ref);
      const updatedAlbumFileSelections = new Map(albumFileSelections);
      updatedAlbumFileSelections.delete(ref);
      albumFileSelections = updatedAlbumFileSelections;
      for (const [fileRef, parent] of includedFiles) {
        if (parent === ref) includedFiles.delete(fileRef);
      }
      for (const [fileRef, parent] of this.selectedFileParents) {
        if (parent === ref) {
          refs.delete(fileRef);
          this.selectedFileParents.delete(fileRef);
        }
      }
      for (const [fileRef, parent] of excludedFiles) {
        if (parent === ref) {
          excludedFiles.delete(fileRef);
          excludedFileIdentities.delete(fileRef);
        }
      }
    } else if (refs.has(ref) && !this.state.selectedAll) {
      this.selectedFileParents.set(ref, parentRef);
    } else {
      this.selectedFileParents.delete(ref);
    }
    this.state = {
      ...this.state,
      selectionRefs: refs,
      excludedFiles,
      excludedFileIdentities,
      includedFiles,
      directoryPathRules,
      albumFileSelections,
      receipt: null,
    };
    this.pendingCommit = null;
    this.emit();
  }

  async openAggregateOptions(ref: string): Promise<void> {
    const summary = this.state.summary;
    const row = this.state.rows.find(row => row.ref === ref);
    if (!summary || !row || (row.kind !== 'aggregate-track' && row.kind !== 'aggregate-album')) return;
    this.state = { ...this.state, aggregateOptions: this.aggregateOptionsPanel(row, summary.revision) };
    this.emit();
    await this.loadMoreAggregateOptions();
  }

  private aggregateOptionsPanel(
    row: Extract<SearchRow, { kind: 'aggregate-track' | 'aggregate-album' }>,
    revision: number | string,
  ): AggregateOptionsPanel {
    return {
      groupRef: row.ref,
      kind: row.kind === 'aggregate-track' ? 'track' : 'album',
      title: row.kind === 'aggregate-track' ? row.value.query.title ?? 'Untitled track' : row.value.query.album ?? 'Untitled album',
      artist: row.value.query.artist ?? null,
      shareCount: Number(row.value.shareCount),
      optionCount: Number(row.value.selectableOptionCount),
      representativeRef: row.kind === 'aggregate-track'
        ? row.value.representative.ref : row.value.representative.ref.ref,
      revision,
      items: [], nextCursor: null, loading: false, error: null,
    };
  }

  closeAggregateOptions(): void {
    this.state = { ...this.state, aggregateOptions: null };
    this.emit();
  }

  async loadMoreAggregateOptions(): Promise<void> {
    const summary = this.state.summary;
    const panel = this.state.aggregateOptions;
    if (!summary || !panel || panel.loading || (panel.items.length > 0 && !panel.nextCursor && !panel.error)) return;
    const generation = this.generation;
    const pending = { ...panel, loading: true, error: null };
    this.state = { ...this.state, aggregateOptions: pending };
    this.emit();
    try {
      const page = panel.kind === 'track'
        ? await this.api.getSearchViewAggregateTrackOptions(summary.viewId, panel.groupRef, panel.revision, panel.nextCursor)
        : await this.api.getSearchViewAggregateAlbumOptions(summary.viewId, panel.groupRef, panel.revision, panel.nextCursor);
      if (this.stopped || generation !== this.generation || this.state.aggregateOptions !== pending) return;
      if (!sameWireInteger(page.revision.revision, panel.revision) || page.groupRef !== panel.groupRef)
        throw new Error('Aggregate alternatives do not match the selected group and revision.');
      const items: AggregateOption[] = panel.kind === 'track'
        ? (page.items as Schema<'SearchViewFileDto'>[]).map(value => ({ kind: 'track', ref: value.ref, value }))
        : (page.items as Schema<'SearchViewDirectoryDto'>[]).map(value => ({ kind: 'album', ref: value.ref.ref, value }));
      const directoryFiles = new Map(this.state.directoryFiles);
      for (const item of items) if (item.kind === 'album') this.seedDirectoryPreview(item.value, directoryFiles);
      this.state = { ...this.state, directoryFiles, aggregateOptions: {
        ...pending, items: [...pending.items, ...items], nextCursor: page.nextCursor ?? null, loading: false,
      } };
    } catch (error) {
      if (this.stopped || generation !== this.generation || this.state.aggregateOptions !== pending) return;
      this.state = { ...this.state, aggregateOptions: { ...pending, loading: false, error: asError(error) } };
    }
    this.emit();
  }

  chooseAggregateOption(groupRef: string, option: AggregateOption): void {
    const panel = this.state.aggregateOptions;
    if (!panel || panel.groupRef !== groupRef || panel.kind !== option.kind
        || !panel.items.some(item => item.ref === option.ref)) return;
    const chosenOptions = new Map(this.state.chosenOptions).set(groupRef, option);
    const checkedChoiceRevisions = new Map(this.state.checkedChoiceRevisions);
    if (this.state.summary) checkedChoiceRevisions.set(groupRef, choiceRevisionKey(this.state.summary));
    const staleChoices = new Set(this.state.staleChoices);
    staleChoices.delete(groupRef);
    const refs = new Set(this.state.selectionRefs);
    if (this.state.selectedAll) refs.delete(groupRef);
    else refs.add(groupRef);
    const albumFileSelections = new Map(this.state.albumFileSelections);
    albumFileSelections.delete(groupRef);
    const directoryFiles = new Map(this.state.directoryFiles);
    if (option.kind === 'album') this.seedDirectoryPreview(option.value, directoryFiles);
    this.state = { ...this.state, chosenOptions, checkedChoiceRevisions, staleChoices,
      albumFileSelections, directoryFiles,
      selectionRefs: refs, aggregateOptions: null, choiceValidationError: null, receipt: null };
    this.pendingCommit = null;
    this.emit();
    if (this.state.summary) this.validateVisibleChoices(this.state.summary);
    if (option.kind === 'album' && !option.value.initialFiles) void this.loadDirectoryFiles(option.ref);
  }

  private hasVisibleDirectory(ref: string): boolean {
    if (this.state.rows.some(row => row.kind === 'directory' && row.ref === ref)) return true;
    if (this.state.rows.some(row => row.kind === 'aggregate-album'
      && (row.value.representative.ref.ref === ref
        || (this.state.chosenOptions.get(row.ref)?.kind === 'album'
          && (this.state.chosenOptions.get(row.ref)?.value as Schema<'SearchViewDirectoryDto'>).ref.ref === ref)))) return true;
    return this.state.aggregateOptions?.items.some(option => option.kind === 'album' && option.ref === ref) ?? false;
  }

  async retrieveDirectory(ref: string): Promise<void> {
    const summary = this.state.summary;
    const row = this.state.rows.find(row => row.kind === 'directory' && row.ref === ref);
    const aggregateDirectory = this.state.rows
      .filter(row => row.kind === 'aggregate-album')
      .map(row => this.state.chosenOptions.get(row.ref)?.kind === 'album'
        ? this.state.chosenOptions.get(row.ref)!.value as Schema<'SearchViewDirectoryDto'>
        : row.kind === 'aggregate-album' ? row.value.representative : null)
      .find(directory => directory?.ref.ref === ref);
    const panelDirectory = this.state.aggregateOptions?.items.find(option => option.kind === 'album' && option.ref === ref);
    const directory = row?.kind === 'directory' ? row.value : aggregateDirectory
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
      idempotencyKey: crypto.randomUUID(),
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
      ? this.directoryFileCounts.get(ref) ?? previous.items.length + defaultPageSize
      : Math.max(this.directoryFileCounts.get(ref) ?? 0, previous?.items.length ?? 0) + defaultPageSize;
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
      for (const item of page.items) this.fileRelativePaths.set(item.ref, item.relativePath);
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
    if (selected && this.genericSelectionReadOnly()) return;
    this.selectedFileParents.clear();
    this.state = {
      ...this.state,
      selectedAll: selected,
      selectionRevision: null,
      selectionNeedsReview: false,
      selectionTopLevelCount: null,
      selectionRefs: new Set(),
      excludedFiles: new Map(),
      excludedFileIdentities: new Map(),
      includedFiles: new Map(),
      directoryPathRules: [],
      albumFileSelections: new Map(),
      receipt: null,
    };
    this.pendingCommit = null;
    this.emit();
  }

  isSelected(ref: string, parentRef?: string): boolean {
    if (this.state.selectionNeedsReview) return false;
    if (parentRef) {
      if (this.state.summary?.kind === 'GenericDirectories') {
        if (this.state.excludedFiles.has(ref)) return false;
        if (this.state.includedFiles.has(ref)) return true;
        return this.directoryPathInheritance(parentRef, this.fileRelativePaths.get(ref));
      }
      const album = this.state.albumFileSelections.get(parentRef);
      if (album) return this.isSelected(parentRef) && (album.mode === 'Only' ? album.refs.has(ref) : !album.refs.has(ref));
      if (this.state.excludedFiles.has(ref)) return false;
      if (this.isSelected(parentRef)) return true;
      if (this.state.selectedAll) return this.state.includedFiles.has(ref);
    }
    return this.state.selectedAll
      ? !this.state.selectionRefs.has(ref)
      : this.state.selectionRefs.has(ref);
  }

  private directoryPathInheritance(directoryRef: string, relativePath?: string, includeExact = false): boolean {
    let selected = this.isSelected(directoryRef);
    if (!relativePath) return selected;
    const normalized = relativePath.replace(/\\/g, '/');
    let bestLength = -1;
    for (const rule of this.state.directoryPathRules) {
      if (rule.directoryRef !== directoryRef || rule.relativePath.length <= bestLength
        || !(normalized.startsWith(`${rule.relativePath}/`)
          || includeExact && normalized === rule.relativePath)) continue;
      bestLength = rule.relativePath.length;
      selected = rule.selected;
    }
    return selected;
  }

  isDirectoryPathSelected(directoryRef: string, relativePath: string): boolean {
    return this.directoryPathInheritance(directoryRef, relativePath, true);
  }

  toggleDirectoryPath(directoryRef: string, relativePath: string, selected: boolean): void {
    if (this.state.summary?.kind !== 'GenericDirectories') return;
    if (this.genericSelectionReadOnly()) return;
    const rules = this.state.directoryPathRules.filter(rule => rule.directoryRef !== directoryRef
      || (rule.relativePath !== relativePath && !rule.relativePath.startsWith(`${relativePath}/`)));
    rules.push({ directoryRef, relativePath, selected });
    const excludedFiles = new Map(this.state.excludedFiles);
    const excludedFileIdentities = new Map(this.state.excludedFileIdentities);
    const includedFiles = new Map(this.state.includedFiles);
    for (const [ref, parent] of [...includedFiles, ...excludedFiles]) {
      if (parent !== directoryRef || !this.fileRelativePaths.get(ref)?.replace(/\\/g, '/').startsWith(`${relativePath}/`)) continue;
      includedFiles.delete(ref);
      excludedFiles.delete(ref);
      excludedFileIdentities.delete(ref);
    }
    this.state = { ...this.state, directoryPathRules: rules, includedFiles, excludedFiles, excludedFileIdentities, receipt: null };
    this.pendingCommit = null;
    this.emit();
  }

  selectedCount(): number {
    return selectedSearchCount(this.state);
  }

  private choiceIsCurrent(groupRef: string): boolean {
    const checked = this.state.checkedChoiceRevisions.get(groupRef);
    return checked !== undefined && this.state.summary !== null
      && checked === choiceRevisionKey(this.state.summary)
      && !this.state.staleChoices.has(groupRef);
  }

  private hasUnreadySelectedChoices(): boolean {
    const visible = new Set(this.state.rows
      .filter(row => row.kind === 'aggregate-track' || row.kind === 'aggregate-album')
      .map(row => row.ref));
    return [...this.state.chosenOptions.keys()].some(ref =>
      visible.has(ref) && this.isSelected(ref) && !this.choiceIsCurrent(ref));
  }

  private selectionInput(revision: number | string): Schema<'CountSearchViewSelectionRequestDto'> {
    const albumFileSelections = Object.fromEntries([...this.state.albumFileSelections]
      .filter(([ref]) => this.isSelected(ref))
      .map(([ref, selection]) => [ref, { mode: selection.mode, refs: [...selection.refs] }]));
    return {
      revision,
      selection: { mode: this.state.selectedAll ? 'AllExcept' : 'Only', refs: [...this.state.selectionRefs] },
      excludedFileRefs: [...this.state.excludedFiles.keys()],
      excludedFileIdentities: Object.fromEntries(this.state.excludedFileIdentities),
      includedFileRefs: [...this.state.includedFiles.keys()],
      chosenOptions: Object.fromEntries([...this.state.chosenOptions]
        .filter(([ref]) => this.isSelected(ref))
        .map(([ref, option]) => [ref, option.ref])),
      albumFileSelections,
      directoryPathRules: this.state.directoryPathRules,
    };
  }

  private syncSelectionCounts(): void {
    const summary = this.state.summary;
    // Exclusions may belong to a wider, earlier projection. Subtracting their
    // count is only an estimate; it cannot prove that an AllExcept set is empty.
    const maySelectUnexcludedItems = this.state.selectedAll
      && this.state.selectionRefs.size > 0 && selectionScopeSize(this.state) > 0;
    // Revision zero has no published projection to count or select yet.
    if (!hasPublishedProjection(summary) || this.stopped || this.state.selectionNeedsReview
      || (!maySelectUnexcludedItems && requestedSearchCount(this.state) === 0)
      || this.hasUnreadySelectedChoices()) {
      this.countRequest?.abort();
      this.countRequest = null;
      this.countRequestKey = null;
      if (this.state.selectionCounts || this.state.selectionCountsLoading || this.state.selectionCountsError)
        this.state = { ...this.state, selectionCounts: null, selectionCountsLoading: false, selectionCountsError: null };
      return;
    }
    const revision = this.state.draft?.resultMode === 'generic'
      ? this.state.selectionRevision ?? summary.revision : summary.revision;
    const request = this.selectionInput(revision);
    const key = selectionCountKey(summary.viewId, request);
    if (key === this.countRequestKey) return;
    this.countRequest?.abort();
    const controller = new AbortController();
    this.countRequest = controller;
    this.countRequestKey = key;
    this.state = { ...this.state, selectionCounts: null, selectionCountsLoading: true, selectionCountsError: null };
    void this.api.countSearchViewSelection(summary.viewId, request, controller.signal).then(counts => {
      if (this.stopped || this.countRequestKey !== key || controller.signal.aborted) return;
      if (!sameWireInteger(counts.revision, revision))
        throw new Error('Selection count does not match the requested revision.');
      this.state = { ...this.state, selectionCounts: counts, selectionCountsLoading: false };
      this.emit();
    }).catch(error => {
      if (this.stopped || this.countRequestKey !== key || controller.signal.aborted) return;
      this.state = { ...this.state, selectionCounts: null, selectionCountsLoading: false, selectionCountsError: asError(error) };
      this.emit();
    });
  }

  toggleAggregateAlbumFile(groupRef: string, fileRef: string, selected: boolean): void {
    const row = this.state.rows.find(row => row.kind === 'aggregate-album' && row.ref === groupRef);
    if (row?.kind !== 'aggregate-album') return;
    const option = this.choiceIsCurrent(groupRef) ? this.state.chosenOptions.get(groupRef) : null;
    const chosenOptions = option?.kind === 'album' ? this.state.chosenOptions
      : new Map(this.state.chosenOptions).set(groupRef, {
        kind: 'album', ref: row.value.representative.ref.ref, value: row.value.representative,
      });
    const checkedChoiceRevisions = new Map(this.state.checkedChoiceRevisions);
    checkedChoiceRevisions.set(groupRef, choiceRevisionKey(this.state.summary!));
    const staleChoices = new Set(this.state.staleChoices);
    staleChoices.delete(groupRef);
    const wasSelected = this.isSelected(groupRef);
    const previous = this.state.albumFileSelections.get(groupRef);
    const mode = previous?.mode ?? (wasSelected ? 'AllExcept' : 'Only');
    const refs = new Set(previous?.refs ?? []);
    if (mode === 'Only') {
      if (selected) refs.add(fileRef);
      else refs.delete(fileRef);
    } else if (selected) refs.delete(fileRef);
    else refs.add(fileRef);
    const directoryRef = chosenOptions.get(groupRef)?.ref ?? row.value.representative.ref.ref;
    const loadedFiles = this.state.directoryFiles.get(directoryRef);
    const allFilesKnown = Boolean(loadedFiles && (!loadedFiles.loading || loadedFiles.refreshing)
      && !loadedFiles.error && !loadedFiles.nextCursor);
    const allLoadedRefsMatch = allFilesKnown && loadedFiles!.items.length > 0
      && loadedFiles!.items.every(file => refs.has(file.ref));
    const selectionRefs = new Set(this.state.selectionRefs);
    const groupSelected = mode === 'AllExcept' ? !allLoadedRefsMatch : refs.size > 0;
    if (groupSelected === this.state.selectedAll) selectionRefs.delete(groupRef);
    else selectionRefs.add(groupRef);
    const albumFileSelections = new Map(this.state.albumFileSelections);
    if (!groupSelected || (mode === 'AllExcept' && refs.size === 0) || (mode === 'Only' && allLoadedRefsMatch)) albumFileSelections.delete(groupRef);
    else albumFileSelections.set(groupRef, { mode, refs });
    this.state = { ...this.state, selectionRefs, chosenOptions, checkedChoiceRevisions,
      staleChoices, albumFileSelections, choiceValidationError: null, receipt: null };
    this.pendingCommit = null;
    this.emit();
    if (this.state.summary) this.validateVisibleChoices(this.state.summary);
  }

  async commit(options?: Schema<'SubmissionOptionsDto'>): Promise<void> {
    const summary = this.state.summary;
    if (!summary || this.state.committing) return;
    const generation = this.generation;
    // A lost response must retry the exact accepted command. An explicit edit
    // to download options starts a new command, just as changing selection does.
    const frozenOptions = options == null
      ? undefined
      : JSON.parse(JSON.stringify(options)) as Schema<'SubmissionOptionsDto'>;
    if (this.pendingCommit && JSON.stringify(this.pendingCommit.options) !== JSON.stringify(frozenOptions))
      this.pendingCommit = null;
    if (!this.pendingCommit && (this.state.refining || this.state.refinementError)) return;
    if (!this.pendingCommit && this.selectedCount() === 0) return;
    if (!this.pendingCommit && this.hasUnreadySelectedChoices()) {
      this.state = { ...this.state, error: new Error('A chosen source changed. Recheck or reselect it before downloading.') };
      this.emit();
      return;
    }
    // Retries reuse the entire request, not just its key: live revisions may
    // advance after the daemon accepted a request whose response was lost.
    const revision = this.state.draft?.resultMode === 'generic'
      ? this.state.selectionRevision ?? summary.revision : summary.revision;
    const selectionInput = this.selectionInput(revision);
    const request = this.pendingCommit ??= {
      viewId: summary.viewId,
      revision,
      mode: selectionInput.selection.mode,
      refs: selectionInput.selection.refs,
      excludedFileRefs: selectionInput.excludedFileRefs ?? [],
      excludedFileIdentities: selectionInput.excludedFileIdentities ?? {},
      includedFileRefs: selectionInput.includedFileRefs ?? [],
      chosenOptions: selectionInput.chosenOptions ?? {},
      albumFileSelections: selectionInput.albumFileSelections ?? {},
      directoryPathRules: selectionInput.directoryPathRules ?? [],
      useRepresentativeWhenUnchosen: summary.kind === 'AggregateTracks' || summary.kind === 'AggregateAlbums',
      options: frozenOptions,
      idempotencyKey: crypto.randomUUID(),
    };
    this.state = { ...this.state, committing: true, receipt: null, error: null };
    this.emit();
    try {
      const receipt = await this.api.commitSearchView(
        request.viewId,
        request.revision,
        request.mode,
        request.refs,
        request.idempotencyKey,
        undefined,
        request.excludedFileRefs,
        request.includedFileRefs,
        request.chosenOptions,
        request.albumFileSelections,
        request.options,
        request.excludedFileIdentities,
        request.useRepresentativeWhenUnchosen,
        request.directoryPathRules,
      );
      if (generation !== this.generation) return;
      this.state = { ...this.state, committing: false, receipt };
      if (this.pendingCommit === request) this.pendingCommit = null;
      this.emit();
    } catch (error) {
      if (generation !== this.generation) return;
      // The daemon replays accepted receipts before resolving the issuing View.
      // A received 404 therefore rejects this request; an unknown outcome keeps
      // its exact command/key so a lost response can still be recovered.
      if (error instanceof DaemonApiError && error.status === 404 && this.pendingCommit === request)
        this.pendingCommit = null;
      this.state = { ...this.state, committing: false, error: asError(error) };
      this.emit();
    }
  }

  async resolveSelectionSettings(signal?: AbortSignal): Promise<Schema<'ResolveSearchViewSelectionSettingsResponseDto'>> {
    const summary = this.state.summary;
    if (!summary) throw new Error('Search results are not available.');
    return this.api.resolveSearchViewSelectionSettings(summary.viewId, {
      revision: this.state.draft?.resultMode === 'generic'
        ? this.state.selectionRevision ?? summary.revision : summary.revision,
      selection: {
        mode: this.state.selectedAll ? 'AllExcept' : 'Only',
        refs: [...this.state.selectionRefs],
      },
      includedFileRefs: [...this.state.includedFiles.keys()],
      directoryPathRules: this.state.directoryPathRules,
    }, signal);
  }

  private cancelPageRequest(): void {
    this.request?.abort();
    this.request = null;
    this.state = { ...this.state, loadingMore: false };
  }

  private async reloadVisiblePages(summary: SearchViewSummaryDto, count: number,
    options?: { request?: AbortController; replaceProjection?: boolean; sortOrder?: Schema<'ServerSearchViewSortOrder'> }): Promise<boolean> {
    this.cancelPageRequest();
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
        : mode === 'album-aggregate'
          ? (pages as SearchViewAggregateAlbumPageDto[]).flatMap(page => page.items.map(item => item.representative))
          : [];
      const orderingOnly = Boolean(options?.request) && !replacing;
      // Reordering the same snapshot cannot invalidate cached child pages or
      // advance a pinned selection. Retry/validation belongs to an actual refresh.
      const directoryFiles = orderingOnly ? new Map(this.state.directoryFiles)
        : await this.refreshDirectoryPages(summary, directories, request.signal);
      if (this.stopped || request.signal.aborted) return false;
      const selection = orderingOnly ? {}
        : await this.genericSelectionBinding(summary, pages, request.signal, replacing);
      if (this.stopped || request.signal.aborted) return false;
      if (replacing) {
        this.pendingCommit = null;
        this.state = { ...this.state, refining: false, refinementError: null, loadingMore: false,
          selectionRevision: null, selectionTopLevelCount: null,
          selectionNeedsReview: this.state.draft?.resultMode === 'generic' && this.hasGenericSelection(),
          aggregateOptions: null, retrievals: new Map(), selectionCounts: null,
          selectionCountsLoading: false, selectionCountsError: null,
          choiceValidationLoading: false, choiceValidationError: null, receipt: null };
      }
      this.state = { ...this.state, ...selection, sortOrder: order,
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

  private async genericSelectionBinding(summary: SearchViewSummaryDto, pages: SearchPage[],
    signal: AbortSignal, replacing: boolean): Promise<Partial<SearchViewState>> {
    if (this.state.draft?.resultMode !== 'generic'
      || (!replacing && this.state.summary?.viewId !== summary.viewId)
      || !hasPublishedProjection(summary)
      || (!replacing && !this.state.selectionNeedsReview && (this.state.selectionRevision === null
        || sameWireInteger(this.state.selectionRevision, summary.revision)))) return {};
    let counts: Partial<SearchViewState> = {};
    const refs = [...new Set([
      ...this.state.selectionRefs,
      ...this.state.directoryPathRules.map(rule => rule.directoryRef),
      ...this.state.includedFiles.values(),
      ...this.state.excludedFiles.values(),
    ])];
    // The page already proves membership for visible roots. Query only when
    // a choice is off-page or disappeared; don't recount visible roots merely
    // to validate them before computing the actual selection count.
    const visibleRoots = new Set(pages.flatMap(page =>
      (page as SearchViewDirectoryPageDto).items.map(item => item.ref.ref)));
    if (refs.some(ref => !visibleRoots.has(ref))) {
      try {
        const request: Schema<'CountSearchViewSelectionRequestDto'> = {
          revision: summary.revision,
          selection: { mode: 'Only', refs },
        };
        const checked = await this.api.countSearchViewSelection(summary.viewId, request, signal);
        const currentRefs = new Set([
          ...this.state.selectionRefs,
          ...this.state.directoryPathRules.map(rule => rule.directoryRef),
          ...this.state.includedFiles.values(),
          ...this.state.excludedFiles.values(),
        ]);
        if (signal.aborted || !sameWireInteger(checked.revision, summary.revision)
          || refs.length !== currentRefs.size
          || refs.some(ref => !currentRefs.has(ref))
          || Number(checked.selectedItemCount) !== refs.length) return {};
        // Simple folder choices use this exact count for display too. Reuse the
        // existing count slot, but never substitute root validation for a count
        // with exclusions, path rules, exact children or AllExcept semantics.
        const key = selectionCountKey(summary.viewId, request);
        if (key === selectionCountKey(summary.viewId, this.selectionInput(summary.revision))) {
          this.countRequest?.abort();
          this.countRequest = null;
          this.countRequestKey = key;
          counts = { selectionCounts: checked,
            selectionCountsLoading: false, selectionCountsError: null };
        }
      } catch {
        // Keep the previous exact revision, or leave a replacement unbound,
        // if its references cannot be validated.
        return {};
      }
    }
    return { ...counts,
      selectionNeedsReview: false,
      selectionRevision: summary.revision,
      selectionTopLevelCount: Number(summary.counters.topLevelItemCount) };
  }

  private loadPage(summary: SearchViewSummaryDto, cursor: string | null, sortOrder: Schema<'ServerSearchViewSortOrder'>, signal?: AbortSignal): Promise<SearchPage> {
    const mode = this.state.draft?.resultMode;
    if (mode === 'generic' || mode === 'album') {
      return this.api.getSearchViewDirectories(summary.viewId, summary.revision, cursor, nestedPageSize, sortOrder, signal);
    }
    if (mode === 'song-aggregate') {
      return this.api.getSearchViewAggregateTracks(summary.viewId, summary.revision, cursor, nestedPageSize, signal);
    }
    if (mode === 'album-aggregate') {
      return this.api.getSearchViewAggregateAlbums(summary.viewId, summary.revision, cursor, nestedPageSize, signal);
    }
    return this.api.getSearchViewFiles(summary.viewId, summary.revision, cursor, defaultPageSize, sortOrder, signal);
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
    } else if (mode === 'song-aggregate') {
      for (const page of this.pages as SearchViewAggregateTrackPageDto[]) {
        for (const item of page.items) rows.push({ kind: 'aggregate-track', ref: item.ref, value: item });
      }
    } else if (mode === 'album-aggregate') {
      for (const page of this.pages as SearchViewAggregateAlbumPageDto[]) {
        for (const item of page.items) rows.push({ kind: 'aggregate-album', ref: item.ref, value: item });
      }
    }
    const directoryFiles = refreshedDirectories ?? (sameWireInteger(this.state.summary?.revision ?? -1, summary.revision)
      ? new Map(this.state.directoryFiles) : new Map<string, DirectoryFilesPage>());
    const retrievals = new Map(this.state.retrievals);
    for (const row of rows) {
      if (row.kind === 'directory') {
        if (!directoryFiles.has(row.ref)) this.seedDirectoryPreview(row.value, directoryFiles);
        if (!retrievals.has(row.ref)
          && (this.retrievalJobs.has(row.ref) || this.retrievalInFlight.has(row.ref)))
          retrievals.set(row.ref, { loading: true, error: null });
      } else if (row.kind === 'aggregate-album') {
        if (!directoryFiles.has(row.value.representative.ref.ref))
          this.seedDirectoryPreview(row.value.representative, directoryFiles);
        const ref = row.value.representative.ref.ref;
        if (!retrievals.has(ref)
          && (this.retrievalJobs.has(ref) || this.retrievalInFlight.has(ref)))
          retrievals.set(ref, { loading: true, error: null });
      }
    }
    for (const files of directoryFiles.values())
      for (const item of files.items) this.fileRelativePaths.set(item.ref, item.relativePath);
    const previousPanel = this.state.aggregateOptions;
    const panelNeedsNewRevision = Boolean(previousPanel && !sameWireInteger(previousPanel.revision, summary.revision));
    const panelRow = panelNeedsNewRevision && previousPanel
      ? rows.find((row): row is Extract<SearchRow, { kind: 'aggregate-track' | 'aggregate-album' }> =>
        row.ref === previousPanel.groupRef && (row.kind === 'aggregate-track' || row.kind === 'aggregate-album')
        && (row.kind === 'aggregate-track' ? 'track' : 'album') === previousPanel.kind)
      : null;
    const aggregateOptions = panelNeedsNewRevision
      ? panelRow ? this.aggregateOptionsPanel(panelRow, summary.revision) : null
      : previousPanel;
    this.state = {
      ...this.state,
      phase: 'ready',
      aggregateOptions,
      directoryFiles,
      retrievals,
      summary,
      rows,
      nextCursor: this.pages.at(-1)?.nextCursor ?? null,
      error: null,
    };
    this.emit();
    this.validateVisibleChoices(summary);
    if (panelRow) void this.loadMoreAggregateOptions();
  }

  private validateVisibleChoices(summary: SearchViewSummaryDto): void {
    const visible = this.state.rows.filter(row =>
      row.kind === 'aggregate-track' || row.kind === 'aggregate-album');
    const choices = Object.fromEntries(visible.flatMap(row => {
      const choice = this.state.chosenOptions.get(row.ref);
      const checked = this.state.checkedChoiceRevisions.get(row.ref);
      return choice && checked !== choiceRevisionKey(summary)
        ? [[row.ref, choice.ref]] : [];
    }));
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
        && this.state.chosenOptions.get(item.groupRef)?.ref === item.optionRef
        && !this.state.directoryFiles.has(item.album.ref.ref) ? [item.album] : []);
      const restored = await this.refreshDirectoryPages(summary, albums, request.signal);
      if (this.stopped || request.signal.aborted || this.choiceRequest !== request
        || this.state.summary?.viewId !== summary.viewId
        || !sameWireInteger(this.state.summary.revision, summary.revision)) return;
      const byGroup = new Map(resolved.items.map(item => [item.groupRef, item]));
      const chosenOptions = new Map(this.state.chosenOptions);
      const checkedChoiceRevisions = new Map(this.state.checkedChoiceRevisions);
      const staleChoices = new Set(this.state.staleChoices);
      const directoryFiles = new Map(this.state.directoryFiles);
      for (const [groupRef, optionRef] of Object.entries(choices)) {
        if (chosenOptions.get(groupRef)?.ref !== optionRef) continue;
        const item = byGroup.get(groupRef);
        checkedChoiceRevisions.set(groupRef, choiceRevisionKey(summary));
        if (item?.optionRef === optionRef && (item.track || item.album)) {
          staleChoices.delete(groupRef);
          if (item.track) chosenOptions.set(groupRef, { kind: 'track', ref: optionRef, value: item.track });
          else if (item.album) {
            chosenOptions.set(groupRef, { kind: 'album', ref: optionRef, value: item.album });
            const files = restored.get(item.album.ref.ref);
            if (files) {
              directoryFiles.set(item.album.ref.ref, files);
              for (const child of files.items) this.fileRelativePaths.set(child.ref, child.relativePath);
            } else this.seedDirectoryPreview(item.album, directoryFiles);
          }
        } else staleChoices.add(groupRef);
      }
      this.state = { ...this.state, chosenOptions, checkedChoiceRevisions, staleChoices,
        directoryFiles, choiceValidationLoading: false, choiceValidationError: null };
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
    for (const item of preview.items) this.fileRelativePaths.set(item.ref, item.relativePath);
    target.set(ref, { items: [...preview.items], directorySummary: directory,
      nextCursor: preview.nextCursor ?? null, loading: false, error: null });
  }

  private schedulePoll(): void {
    if (this.timer) clearTimeout(this.timer);
    if (this.observation && this.retrievalJobs.size === 0) return;
    if (this.stopped || !this.visible || (this.state.summary?.isComplete && this.retrievalJobs.size === 0
      && this.state.job?.lifecycleState === 'Terminal')) return;
    this.timer = setTimeout(async () => {
      this.timer = null;
      await this.refresh();
      this.schedulePoll();
    }, this.pollIntervalMs);
  }

  private emit(): void {
    if (this.state.draft?.resultMode === 'generic' && this.state.summary) {
      const hasSelection = this.hasGenericSelection();
      if (!hasSelection && this.state.selectionRevision !== null)
        this.state = { ...this.state, selectionRevision: null, selectionTopLevelCount: null };
      else if (hasSelection && !this.state.selectionNeedsReview
        && this.state.selectionRevision === null && hasPublishedProjection(this.state.summary))
        this.state = { ...this.state,
          selectionRevision: this.state.summary.revision,
          selectionTopLevelCount: Number(this.state.summary.counters.topLevelItemCount) };
    }
    this.syncSelectionCounts();
    const value = this.current();
    for (const listener of this.listeners) listener(value);
  }

  private genericSelectionReadOnly(): boolean {
    return this.state.draft?.resultMode === 'generic'
      && (this.state.selectionNeedsReview || (this.state.summary !== null
        && this.state.selectionRevision !== null
        && !sameWireInteger(this.state.selectionRevision, this.state.summary.revision)));
  }

  private hasGenericSelection(): boolean {
    return this.state.selectedAll || this.state.selectionRefs.size > 0
      || this.state.includedFiles.size > 0 || this.state.directoryPathRules.length > 0;
  }
}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

function choiceRevisionKey(summary: SearchViewSummaryDto): string {
  return `${summary.viewId}:${String(summary.revision)}`;
}

function selectionCountKey(viewId: string, request: Schema<'CountSearchViewSelectionRequestDto'>): string {
  // Sorting set-derived refs ignores insertion order; revision and the complete
  // selection expression still invalidate any formerly exact count.
  return JSON.stringify([viewId, request.revision, request.selection.mode,
    [...request.selection.refs].sort(), [...(request.excludedFileRefs ?? [])].sort(),
    Object.entries(request.excludedFileIdentities ?? {}).sort(([a], [b]) => a.localeCompare(b)),
    [...(request.includedFileRefs ?? [])].sort(),
    Object.entries(request.chosenOptions ?? {}).sort(([a], [b]) => a.localeCompare(b)),
    Object.entries(request.albumFileSelections ?? {}).map(([ref, files]) =>
      [ref, files.mode, [...files.refs].sort()]).sort(([a], [b]) => String(a).localeCompare(String(b))),
    request.directoryPathRules ?? []]);
}
