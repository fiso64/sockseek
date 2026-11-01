<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import AppShell from './components/AppShell.svelte';
  import JobsList from './domains/jobs/JobsList.svelte';
  import { canArchiveSubmission, requireArchived } from './domains/jobs/archive';
  import { completedExtract, resolvePresentedJob } from './domains/jobs/presentation';
  import { jobDetailVersion } from './domains/jobs/details';
  import NewJobComposer from './domains/jobs/NewJobComposer.svelte';
  import AutomaticJobDetail from './domains/jobs/AutomaticJobDetail.svelte';
  import SearchResults from './domains/search/SearchResults.svelte';
  import TransfersPage from './domains/transfers/TransfersPage.svelte';
  import { TransferTimelineController } from './domains/transfers/controller.svelte';
  import { readPrecedesSnapshot, refreshVisibleExtent } from './app/paging';
  import Dashboard from './domains/dashboard/Dashboard.svelte';
  import UsersPage, { type UserBrowseView } from './domains/users/UsersPage.svelte';
  import ChatPage from './domains/chat/ChatPage.svelte';
  import { DaemonLiveClient, type LiveConnectionStatus, type ReplicatedDaemonState } from './api/live';
  import { jobStateSummary } from './api/job-summary';
  import { updatedJob } from './api/state.svelte';
  import { daemonApi } from './api/client';
  import { type JobSummaryDto, type Schema } from './api/types';
  import { SearchViewController, selectedSearchCount as countSearchSelection, type SearchViewState } from './domains/search/controller';
  import { emptySearchDraft, parseSearchResultMode, searchJobPath, type SearchDraft, type SearchResultMode } from './domains/search/model';
  import { canReplaySearch } from './domains/search/replay';
  import { presentedJobs } from './domains/jobs/presentation';
  import type { PageId, UserLinkActions } from './app/navigation';

  const liveClient = new DaemonLiveClient();
  const observeJobs = liveClient.observeJobs.bind(liveClient);
  const transferTimeline = new TransferTimelineController(daemonApi,
    (scope, next, error) => liveClient.observeTransferTimeline(scope, next, error));
  const searchController = new SearchViewController(daemonApi, 1_000,
    (id, next, error, complete) => liveClient.observeSearchView(id, next, error, complete));
  let documentVisible = $state(true);

  let activePage = $state<PageId>('dashboard');
  // The Jobs subview survives a visit to another top-level page.
  let showingSearch = $state(false);
  let composingJob = $state(false);
  let composingJobCommitPending = $state(false);
  let automaticJobId = $state<string | null>(null);
  let automaticInitialDetail = $state<Schema<'JobDetailDto'> | null>(null);
  let detailRefreshRevision = $state(0);
  let pendingJobRead: { jobId: string; revision: number } | null = null;
  let pendingJobId = $state<string | null>(null);
  let jobRouteError = $state<Error | null>(null);
  let openingJobKey = $state<string | null>(null);
  const searchModeByJob = new Map<string, SearchResultMode>();
  let search = $state<SearchDraft>({ ...emptySearchDraft });
  let userBrowse = $state<{ query: string; mode: UserBrowseView }>({ query: '', mode: 'user' });
  let activeUsername = $state('');
  let userView = $state<UserBrowseView>('user');
  let initialChatUsername = $state<string | null>(null);
  let searchBusy = $state(false);
  let rerunningSearch = $state(false);
  let mutationError = $state<Error | null>(null);
  let removingSearch = $state(false);
  let liveStatus = $state<LiveConnectionStatus>('offline');
  let liveError = $state<Error | null>(null);
  const liveState = liveClient.reducer.state;
  let searchState = $state<SearchViewState>(searchController.current());
  let historyJobs = $state<JobSummaryDto[]>([]);
  let archivedSubmissionIds = $state(new Set<string>());
  let archivingSubmissionIds = $state(new Set<string>());
  let archiveRevision = 0;
  let historyCursor = $state<string | null>(null);
  let historyRefreshRevision = $state(0);
  let historyLoading = $state(false);
  let historyRefreshPending = false;
  let historyError = $state<Error | null>(null);
  let historyReadPosition: Schema<'StateStreamPositionDto'> | null | undefined;
  let historySnapshotPosition: Schema<'StateStreamPositionDto'> | null = null;


  let observedSearchJob = $state.raw<Schema<'JobStateDto'> | null>(null);
  let searchJobError = $state<Error | null>(null);
  let searchJobRetry = $state(0);
  let observedSearchId = $derived(activePage === 'jobs' && showingSearch && searchState.job?.parentJobId
    ? searchState.job.jobId : null);
  $effect(() => {
    const id = observedSearchId;
    searchJobRetry;
    observedSearchJob = null;
    searchJobError = null;
    if (!id || liveStatus !== 'connected' || !documentVisible) return;
    let disposed = false;
    const stop = observeJobs(id, [], update => {
      if (disposed) return;
      for (const delta of update.state.jobs) if (delta.jobId === id)
        observedSearchJob = updatedJob(untrack(() => observedSearchJob ?? undefined), delta) ?? null;
    }, error => { if (!disposed) searchJobError = error; });
    return () => { disposed = true; stop(); };
  });
  let activeLiveJob = $derived(searchState.job?.jobId
    ? observedSearchJob?.jobId === searchState.job.jobId ? observedSearchJob
      : liveState.jobs.get(searchState.job.jobId) ?? null
    : null);
  let activeSearchJob = $derived(searchState.job
    ? { ...searchState.job, ...(activeLiveJob ? jobStateSummary(activeLiveJob) : {}) }
    : null);
  let activeSearchAction = $derived(activeSearchJob?.availableActions?.some((action) => action.kind === 'cancel')
    ? 'cancel' as const
    : activeSearchJob && canArchiveSubmission(activeSearchJob) ? 'archive' as const : null);
  let canRerunSearch = $derived(canReplaySearch(activeSearchJob));
  let knownJobs = $derived.by(() => {
    const jobs = new Map<string, JobSummaryDto>();
    for (const job of historyJobs) {
      if (job.jobId) jobs.set(job.jobId, job);
    }
    for (const liveJob of liveState.rootJobs.values()) {
      const retained = jobs.get(liveJob.jobId);
      jobs.set(liveJob.jobId, { ...retained, ...jobStateSummary(liveJob) });
    }
    // Match the default paged jobs query: live child updates and details opened
    // through a deep link must not become extra top-level history rows.
    return [...jobs.values()].filter((job) => !job.parentJobId && !archivedSubmissionIds.has(job.submissionId!));
  });
  let selectedSearchCount = $derived(countSearchSelection(searchState));

  function routeLocation(): string {
    return window.location.pathname + window.location.search;
  }

  function setPath(path: string, replace = false): void {
    if (routeLocation() === path) return;
    if (replace) window.history.replaceState(null, '', path);
    else window.history.pushState(null, '', path);
  }

  function decodeSegment(value: string): string {
    try { return decodeURIComponent(value); }
    catch { return value; }
  }

  function applyPath(pathname: string): void {
    automaticJobId = null;
    jobRouteError = null;
    const parts = pathname.replace(/\/+$/, '').split('/').filter(Boolean);
    const root = parts[0];
    if (root === 'jobs') {
      activePage = 'jobs';
      showingSearch = Boolean(parts[1]);
      pendingJobId = parts[1] ? decodeSegment(parts[1]) : null;
      if (pendingJobId) {
        const requestedMode = parseSearchResultMode(new URLSearchParams(window.location.search).get('results'));
        if (searchState.job?.jobId !== pendingJobId || !searchState.draft
          || (requestedMode && requestedMode !== searchState.draft.resultMode))
          void openJobFromPath(pendingJobId, routeLocation());
      } else {
        void loadJobs(true);
      }
      return;
    }
    if (root === 'users') {
      activePage = 'users';
      activeUsername = parts[1] ? decodeSegment(parts[1]) : activeUsername;
      userView = parts[2] === 'shares' ? 'shares' : 'user';
      userBrowse = { query: activeUsername, mode: userView };
      return;
    }
    if (root === 'chat') {
      activePage = 'chat';
      initialChatUsername = parts[1] === 'users' && parts[2] ? decodeSegment(parts[2]) : null;
      return;
    }
    if (root === 'downloads' || root === 'uploads' || root === 'settings' || root === 'dashboard') {
      activePage = root;
      return;
    }
    activePage = 'dashboard';
    setPath('/dashboard', true);
  }

  $effect(() => {
    if (documentVisible && activePage === 'jobs' && showingSearch && searchState.job?.jobId === pendingJobId)
      void searchController.resume();
    else searchController.pause();
  });

  onMount(() => {
    const visibilityChanged = () => { documentVisible = !document.hidden; };
    visibilityChanged();
    document.addEventListener('visibilitychange', visibilityChanged);
    const stateSubscription = liveClient.onState((state, history, changes) => {
      const initialSnapshot = changes.initialSnapshot;
      const snapshotReconciled = initialSnapshot || (history.jobs && history.removedJobIds === undefined);
      if (snapshotReconciled) historySnapshotPosition = changes.jobHistoryPosition;
      const pendingChange = pendingJobRead ? changes.jobs.get(pendingJobRead.jobId) : undefined;
      if (pendingJobRead && (history.removedJobIds?.includes(pendingJobRead.jobId)
        || jobDetailVersion(pendingChange?.previous ? jobStateSummary(pendingChange.previous) : undefined)
          !== jobDetailVersion(pendingChange?.current ? jobStateSummary(pendingChange.current) : undefined)))
        pendingJobRead.revision++;
      // A snapshot can miss the whole workflow, leaving no removal ID or live
      // version change. Both that boundary and exact retirement invalidate detail.
      if (automaticJobId && (snapshotReconciled || history.removedJobIds?.includes(automaticJobId)))
        detailRefreshRevision++;
      if (history.jobs) {
        // Keep previously displayed roots during the live-to-history handoff.
        // Do not invent terminal outcomes when terminal + removal coalesce.
        const retained = new Map(historyJobs.map((job) => [job.jobId, job]));
        for (const { previous, current } of changes.jobs.values()) {
          if (previous && !previous.relationships.parentJobId && !current) {
            retained.set(previous.jobId, { ...retained.get(previous.jobId), ...jobStateSummary(previous) });
          }
        }
        historyJobs = [...retained.values()];
      }
      if ((history.jobs || initialSnapshot && !historyLoading && !historyError
        && readPrecedesSnapshot(historyReadPosition, historySnapshotPosition)) && jobsListVisible()) {
        void loadJobs(true);
      }

    });
    const statusSubscription = liveClient.onStatus((status, error) => {
      searchController.setLiveConnected(status === 'connected');
      liveStatus = status;
      transferTimeline.connected = status === 'connected';
      liveError = error;
      if (status !== 'connected') {
        // An old daemon epoch cannot constrain successful offline HTTP reads.
        historySnapshotPosition = null;
        return;
      }
      // Snapshots reconcile successful history. A failed initial read has no
      // position, so retry it when live startup succeeds.
      if (historyError && jobsListVisible())
        void loadJobs(true);
      if (activePage === 'jobs' && pendingJobId && jobRouteError)
        void openJobFromPath(pendingJobId, routeLocation());
      if (activePage === 'jobs' && showingSearch && searchState.phase === 'failed')
        void searchController.refresh(true);
      if (activePage === 'downloads' || activePage === 'uploads') {
        const direction = activePage === 'downloads' ? 'download' : 'upload';
        if (transferTimeline.history[direction].error)
          void transferTimeline.load(direction, true);
      }
    });
    const searchSubscription = searchController.subscribe((state) => (searchState = state));
    const popstate = () => {
      if (composingJob && composingJobCommitPending) {
        // Back must not unmount the request owner before its commit receipt.
        // The matching forward entry is the current route; the second
        // popstate leaves the composer in place until the receipt arrives.
        window.history.forward();
        return;
      }
      applyPath(window.location.pathname);
    };
    const beforeunload = (event: BeforeUnloadEvent) => {
      if (!composingJob || !composingJobCommitPending) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('popstate', popstate);
    window.addEventListener('beforeunload', beforeunload);
    const startup: Promise<unknown>[] = [
      liveClient.startDaemon().catch((error: unknown) => (liveError = asError(error))),
    ];
    if (activePage === 'downloads') startup.push(transferTimeline.load('download', true));
    if (activePage === 'uploads') startup.push(transferTimeline.load('upload', true));
    void Promise.allSettled(startup);
    return () => {
      transferTimeline.stop();
      stateSubscription();
      statusSubscription();
      searchSubscription();
      window.removeEventListener('popstate', popstate);
      window.removeEventListener('beforeunload', beforeunload);
      routeRequest?.abort();
      searchController.stop();
      document.removeEventListener('visibilitychange', visibilityChanged);
      void liveClient.stop();
    };
  });

  function navigate(page: PageId): void {
    if (composingJob && composingJobCommitPending) return;
    const wasOnJobs = activePage === 'jobs';
    routeRequest?.abort();
    jobRouteError = null;
    automaticJobId = null;
    automaticInitialDetail = null;
    pendingJobId = null;
    composingJob = false;
    if (page === 'jobs') {
      activePage = 'jobs';
      if (wasOnJobs && showingSearch) {
        showingSearch = false;
        pendingJobId = null;
      } else if (showingSearch && searchState.job?.jobId && searchState.draft) {
        pendingJobId = searchState.job.jobId;
        setPath(searchJobPath(pendingJobId, searchState.draft.resultMode));
        return;
      }
      setPath('/jobs');
      void loadJobs(true);
      return;
    }
    activePage = page;
    if (page === 'chat') initialChatUsername = null;
    setPath(page === 'users' && activeUsername
      ? `/users/${encodeURIComponent(activeUsername)}${userView === 'shares' ? '/shares' : ''}`
      : `/${page}`);
    if (page === 'downloads') void transferTimeline.load('download', true);
    if (page === 'uploads') void transferTimeline.load('upload', true);
  }

  async function submit(next: SearchDraft): Promise<void> {
    if (searchBusy) return;
    search = { ...next };
    searchBusy = true;
    mutationError = null;
    try {
      const submitted = await daemonApi.submitSearch(next);
      if (submitted.job.jobId) {
        historyJobs = [submitted.job, ...historyJobs.filter((job) => job.jobId !== submitted.job.jobId)];
        searchModeByJob.set(submitted.job.jobId, submitted.draft.resultMode);
      }
      await searchController.open(submitted);
      activePage = 'jobs';
      showingSearch = true;
      pendingJobId = submitted.job.jobId ?? null;
      if (pendingJobId) setPath(searchJobPath(pendingJobId, submitted.draft.resultMode));
    } catch (error) {
      mutationError = asError(error);
      activePage = 'jobs';
      showingSearch = false;
      setPath('/jobs');
    } finally {
      searchBusy = false;
    }
  }

  async function rerunActiveSearch(): Promise<void> {
    const job = activeSearchJob;
    const view = searchState.summary;
    if (!canRerunSearch || !job || !view || rerunningSearch) return;
    rerunningSearch = true;
    mutationError = null;
    try {
      const submitted = await daemonApi.rerunSearch(job, view);
      search = submitted.draft;
      if (submitted.job.jobId) {
        historyJobs = [submitted.job, ...historyJobs.filter((item) => item.jobId !== submitted.job.jobId)];
        searchModeByJob.set(submitted.job.jobId, submitted.draft.resultMode);
      }
      await searchController.open(submitted);
      pendingJobId = submitted.job.jobId ?? null;
      if (pendingJobId) setPath(searchJobPath(pendingJobId, submitted.draft.resultMode));
    } catch (error) { mutationError = asError(error); }
    finally { rerunningSearch = false; }
  }

  function jobsListVisible(): boolean {
    return activePage === 'jobs' && !showingSearch && !automaticJobId && !pendingJobId;
  }

  async function loadJobs(reset = false): Promise<void> {
    if (historyLoading) {
      if (reset) historyRefreshPending = true;
      return;
    }
    const readingArchiveRevision = archiveRevision;
    historyLoading = true;
    historyError = null;
    try {
      const page = reset
        ? await refreshVisibleExtent(presentedJobs(historyJobs.filter(job => !job.parentJobId),
          new Map(historyJobs.map(job => [job.jobId!, job]))).length,
          (cursor) => daemonApi.getJobs(cursor), (job: JobSummaryDto) => job.jobId!)
        : await daemonApi.getJobs(historyCursor);
      const merged = new Map<string, JobSummaryDto>();
      for (const job of reset ? [] : historyJobs) {
        if (job.jobId) merged.set(job.jobId, job);
      }
      for (const job of page.items) {
        if (job.jobId) merged.set(job.jobId, job);
      }
      if (readingArchiveRevision === archiveRevision) {
        // A current default-history response can authoritatively reveal a
        // submission restored by another client. A pre-archive response cannot.
        const visible = new Set(page.items.map((job) => job.submissionId));
        archivedSubmissionIds = new Set([...archivedSubmissionIds].filter((id) => !visible.has(id)));
      }
      historyJobs = [...merged.values()];
      historyCursor = page.nextCursor ?? null;
      if (reset) {
        historyReadPosition = page.readPosition;
        historyRefreshRevision++;
      }
      if (jobsListVisible() && readPrecedesSnapshot(historyReadPosition, historySnapshotPosition))
        historyRefreshPending = true;
    } catch (error) {
      historyError = asError(error);
    } finally {
      historyLoading = false;
      if (historyRefreshPending) {
        historyRefreshPending = false;
        void loadJobs(true);
      }
    }
  }

  let routeRequest: AbortController | null = null;

  function liveJobVersion(state: ReplicatedDaemonState, jobId: string): string {
    const job = state.jobs.get(jobId);
    return jobDetailVersion(job ? jobStateSummary(job) : undefined);
  }

  async function readRouteJob(jobId: string, signal: AbortSignal): Promise<Schema<'JobDetailDto'>> {
    const read = { jobId, revision: 0 };
    pendingJobRead = read;
    try {
      for (;;) {
        const revision = read.revision;
        const detail = await daemonApi.getJob(jobId, signal);
        signal.throwIfAborted();
        if (revision === read.revision || jobDetailVersion(detail.summary) === liveJobVersion(liveState, jobId))
          return detail;
        // A relevant change overtook this response. Reconcile against the
        // latest version; unrelated updates and already-current replies do not
        // cause another read. No timer or completed-job assumption is needed.
      }
    } finally { if (pendingJobRead === read) pendingJobRead = null; }
  }

  async function openJobFromPath(jobId: string, route: string): Promise<void> {
    routeRequest?.abort();
    jobRouteError = null;
    if (pendingJobId === jobId) showingSearch = true;
    const request = new AbortController();
    routeRequest = request;
    try {
      const root = await readRouteJob(jobId, request.signal);
      const detail = await resolvePresentedJob(root, (id) => readRouteJob(id, request.signal), request.signal);
      if (request.signal.aborted || routeLocation() !== route) return;
      historyJobs = [root.summary, ...historyJobs.filter((job) => job.jobId !== jobId)];
      await openJob(detail.summary, detail);
    } catch (error) {
      if (request.signal.aborted || routeLocation() !== route) return;
      jobRouteError = asError(error);
      showingSearch = false;
    }
  }

  async function openJob(job: JobSummaryDto, detail?: Schema<'JobDetailDto'>): Promise<void> {
    if (!job.jobId) return;
    if (!job.kind) {
      await openJobFromPath(job.jobId, routeLocation());
      return;
    }
    if (completedExtract(job)) {
      const origin = routeLocation();
      routeRequest?.abort();
      const request = new AbortController();
      routeRequest = request;
      try {
        const source = detail ?? await readRouteJob(job.jobId, request.signal);
        const result = await resolvePresentedJob(source, (id) => readRouteJob(id, request.signal), request.signal);
        if (request.signal.aborted || routeLocation() !== origin) return;
        await openJob(result.summary, result);
      } catch (cause) {
        if (!request.signal.aborted && routeLocation() === origin) mutationError = asError(cause);
      }
      return;
    }
    if (job.kind !== 'search') {
      automaticJobId = job.jobId;
      automaticInitialDetail = detail ?? null;
      openingJobKey = null;
      showingSearch = false;
      composingJob = false;
      activePage = 'jobs';
      pendingJobId = job.jobId;
      mutationError = null;
      setPath(`/jobs/${encodeURIComponent(job.jobId)}`);
      return;
    }
    const route = routeLocation();
    const openKey = `${job.jobId}|${route}`;
    if (openingJobKey === openKey) {
      return;
    }
    automaticJobId = null;
    openingJobKey = openKey;
    mutationError = null;
    try {
      const requestedMode = parseSearchResultMode(new URLSearchParams(window.location.search).get('results'))
        ?? searchModeByJob.get(job.jobId);
      const opened = await daemonApi.openSearchJob(job, undefined, detail, requestedMode);
      if (openingJobKey !== openKey || routeLocation() !== route) return;
      await searchController.open(opened);
      if (openingJobKey !== openKey || routeLocation() !== route) return;
      search = { ...opened.draft };
      searchModeByJob.set(job.jobId, opened.draft.resultMode);
      activePage = 'jobs';
      showingSearch = true;
      pendingJobId = job.jobId;
      setPath(searchJobPath(job.jobId, opened.draft.resultMode),
        route.split('?')[0] === `/jobs/${encodeURIComponent(job.jobId)}`);
    } catch (error) {
      if (openingJobKey !== openKey || routeLocation() !== route) return;
      mutationError = asError(error);
      showingSearch = false;
      setPath('/jobs');
    } finally {
      if (openingJobKey === openKey) openingJobKey = null;
    }
  }

  async function cancelJob(job: JobSummaryDto): Promise<void> {
    if (!job.jobId) return;
    mutationError = null;
    try { await daemonApi.cancelJob(job.jobId); }
    catch (error) { mutationError = asError(error); }
  }

  async function archiveSubmission(job: JobSummaryDto): Promise<boolean> {
    if (!canArchiveSubmission(job) || archivingSubmissionIds.has(job.submissionId!)) return false;
    archivingSubmissionIds = new Set([...archivingSubmissionIds, job.submissionId!]);
    mutationError = null;
    try {
      requireArchived(await daemonApi.archiveSubmission(job.submissionId!));
      archiveRevision++;
      archivedSubmissionIds = new Set([...archivedSubmissionIds, job.submissionId!]);
      historyJobs = historyJobs.filter((item) => item.submissionId !== job.submissionId);
      return true;
    } catch (error) { mutationError = asError(error); return false; }
    finally { archivingSubmissionIds = new Set([...archivingSubmissionIds].filter((id) => id !== job.submissionId)); }
  }

  async function removeActiveSearch(): Promise<void> {
    const job = activeSearchJob;
    const action = activeSearchAction;
    if (!job || !action || removingSearch) return;
    removingSearch = true;
    try {
      if (action === 'cancel') await cancelJob(job);
      else await archiveOpenedSubmission(job);
    } finally { removingSearch = false; }
  }

  async function archiveOpenedSubmission(job: JobSummaryDto): Promise<void> {
    const origin = routeLocation();
    if (await archiveSubmission(job) && routeLocation() === origin) backToJobs();
  }


  $effect(() => {
    if (documentVisible && liveStatus === 'connected'
      && (activePage === 'downloads' || activePage === 'uploads'))
      return transferTimeline.observe(activePage === 'downloads' ? 'download' : 'upload');
    transferTimeline.stop();
  });

  function openUser(username: string): void {
    activeUsername = username;
    userView = 'user';
    userBrowse = { query: username, mode: 'user' };
    activePage = 'users';
    setPath(`/users/${encodeURIComponent(username)}`);
  }

  const userActions: UserLinkActions = {
    profile: openUser,
    shares: (username) => {
      activeUsername = username;
      userView = 'shares';
      userBrowse = { query: username, mode: 'shares' };
      activePage = 'users';
      setPath(`/users/${encodeURIComponent(username)}/shares`);
    },
    message: (username) => {
      initialChatUsername = username;
      activePage = 'chat';
      setPath(`/chat/users/${encodeURIComponent(username)}`);
    },
  };

  function submitUserBrowse(value: { query: string; mode: UserBrowseView }): void {
    const username = value.query.trim();
    if (!username) return;
    activeUsername = username;
    userView = value.mode;
    userBrowse = { ...value, query: username };
    activePage = 'users';
    setPath(`/users/${encodeURIComponent(username)}${value.mode === 'shares' ? '/shares' : ''}`);
  }

  function changeUserView(value: UserBrowseView): void {
    userView = value;
    userBrowse = { query: activeUsername, mode: value };
    setPath(activeUsername ? `/users/${encodeURIComponent(activeUsername)}${value === 'shares' ? '/shares' : ''}` : '/users');
  }

  function backToJobs(): void {
    routeRequest?.abort();
    automaticJobId = null;
    automaticInitialDetail = null;
    showingSearch = false;
    pendingJobId = null;
    jobRouteError = null;
    setPath('/jobs');
    void loadJobs(true);
  }

  function asError(error: unknown): Error {
    return error instanceof Error ? error : new Error(String(error));
  }

  // Resolve the initial route before mounting a page. Deep links must not
  // briefly mount Dashboard and issue an analytics request only to abort it.
  applyPath(window.location.pathname);
</script>

<AppShell {activePage} {liveStatus} {liveState} {search} {userBrowse} {searchBusy} onnavigate={navigate} onsearchchange={(value) => (search = value)} onsearchsubmit={(value) => void submit(value)} onuserbrowsechange={(value) => (userBrowse = value)} onuserbrowsesubmit={submitUserBrowse}>
  {#if activePage === 'jobs' && pendingJobId && jobRouteError}
    <section class="page page-search redesigned-search-page">
      <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'} blocking`} role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Job unavailable'}</strong><small>{liveStatus === 'offline' ? 'This resource cannot be loaded while the daemon is offline.' : jobRouteError.message}</small></span>{#if liveStatus !== 'offline'}<button type="button" onclick={() => void openJobFromPath(pendingJobId!, routeLocation())}>Retry</button>{/if}<button type="button" onclick={backToJobs}>Back to jobs</button></div>
    </section>
  {:else if activePage === 'jobs' && showingSearch && pendingJobId && searchState.job?.jobId !== pendingJobId}
    <section class="page page-search redesigned-search-page">
      <div class="resource-state-notice loading blocking" role="status"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Opening job</strong><small>Loading this job from the daemon.</small></span></div>
    </section>
  {:else if activePage === 'jobs' && showingSearch && searchState.draft}
    <SearchResults
      {userActions}
      daemonOffline={liveStatus === 'offline'}
      viewState={searchState}
      liveJob={activeLiveJob}
      liveJobError={searchJobError}
      removeAction={activeSearchAction}
      removePending={removingSearch}
      actionError={mutationError}
      onremove={() => void removeActiveSearch()}
      searchAgainAvailable={canRerunSearch}
      searchAgainPending={rerunningSearch}
      isselected={(ref, parentRef) => searchController.isSelected(ref, parentRef)}
      selectedCount={selectedSearchCount}
      ontoggle={(ref, selected, parentRef) => searchController.toggle(ref, selected, parentRef)}
      onselectfolder={(directoryRef, relativePath, selected) => searchController.toggleDirectoryPath(directoryRef, relativePath, selected)}
      isfolderselected={(directoryRef, relativePath) => searchController.isDirectoryPathSelected(directoryRef, relativePath)}
      onselectall={(selected) => searchController.selectAll(selected)}
      ondownload={(options) => void searchController.commit(options)}
      onresolvesettings={() => searchController.resolveSelectionSettings()}
      onloaddefinition={(viewId) => daemonApi.getSearchViewDefinition(viewId)}
      onrefine={(request) => searchController.refine(request)}
      onsort={(order) => searchController.sort(order)}
      onloadmore={() => void searchController.loadMore()}
      onretry={() => { searchJobRetry++; void searchController.refresh(true); }}
      onloaddirectory={(ref) => void searchController.loadDirectoryFiles(ref)}
      onretrievedirectory={(ref) => void searchController.retrieveDirectory(ref)}
      onopenoptions={(ref) => void searchController.openAggregateOptions(ref)}
      oncloseoptions={() => searchController.closeAggregateOptions()}
      onloadoptions={() => void searchController.loadMoreAggregateOptions()}
      onchooseoption={(ref, option) => searchController.chooseAggregateOption(ref, option)}
      ontogglealbumfile={(groupRef, fileRef, selected) => searchController.toggleAggregateAlbumFile(groupRef, fileRef, selected)}
      onback={backToJobs}
      onsearchagain={() => void rerunActiveSearch()}
    />
  {:else if activePage === 'jobs'}
    {#if automaticJobId}
      <AutomaticJobDetail jobId={automaticJobId} initialDetail={automaticInitialDetail} refreshRevision={detailRefreshRevision} {userActions}
        connected={liveStatus === 'connected' && documentVisible}
        {observeJobs}
        onback={backToJobs} onopenjob={(job, detail) => void openJob(job, detail)} onarchive={(job) => void archiveOpenedSubmission(job)}
        archivingSubmissions={archivingSubmissionIds} archiveError={mutationError} />
    {:else}
    <JobsList
      refreshRevision={historyRefreshRevision}
      onnew={() => (composingJob = true)}
      jobs={knownJobs}
      loading={historyLoading || liveStatus === 'connecting'}
      daemonOffline={liveStatus === 'offline'}
      hasMore={Boolean(historyCursor)}
      error={historyError ?? liveError}
      actionError={mutationError}
      onretry={() => void loadJobs(true)}
      onopen={(job, detail) => void openJob(job, detail)}
      oncancel={(job) => void cancelJob(job)}
      onarchive={(job) => void archiveSubmission(job)}
      archivingSubmissions={archivingSubmissionIds}
      onloadmore={() => void loadJobs()}
    />
    {/if}
  {:else if activePage === 'dashboard'}
    <Dashboard liveStatus={liveStatus} terminalTransferCommitRevision={liveState.daemon?.terminalTransferCommitRevision ?? 0} soulseek={liveState.daemon?.soulseekClient} transfers={liveState.transferSummary} {userActions} />
  {:else if activePage === 'downloads'}
    <TransfersPage
      direction="download"
      live={transferTimeline.live.read}
      history={transferTimeline.history.download.items}
      globalBoundary={transferTimeline.history.download.boundary}
      coverage={transferTimeline.history.download.coverage}
      summaries={transferTimeline.history.download.summaries}
      summaryCoverage={transferTimeline.history.download.summaryCoverage}
      summaryError={transferTimeline.history.download.summaryError}
      nextCursor={transferTimeline.history.download.cursor}
      loading={transferTimeline.history.download.loading}
      daemonOffline={liveStatus === 'offline'}
      error={transferTimeline.history.download.error}
      onfetchedmembers={(rows) => transferTimeline.registerMembers('download', rows)}
      missingMembers={transferTimeline.missingMembers.download}
      onloadmore={() => void transferTimeline.load('download')}
      onchanged={() => transferTimeline.afterMutation('download')}
      onretrysummaries={() => transferTimeline.load('download', true)}
      {userActions}
    />
  {:else if activePage === 'uploads'}
    <TransfersPage
      direction="upload"
      live={transferTimeline.live.read}
      history={transferTimeline.history.upload.items}
      globalBoundary={transferTimeline.history.upload.boundary}
      coverage={transferTimeline.history.upload.coverage}
      summaries={transferTimeline.history.upload.summaries}
      summaryCoverage={transferTimeline.history.upload.summaryCoverage}
      summaryError={transferTimeline.history.upload.summaryError}
      nextCursor={transferTimeline.history.upload.cursor}
      loading={transferTimeline.history.upload.loading}
      daemonOffline={liveStatus === 'offline'}
      error={transferTimeline.history.upload.error}
      onfetchedmembers={(rows) => transferTimeline.registerMembers('upload', rows)}
      missingMembers={transferTimeline.missingMembers.upload}
      onloadmore={() => void transferTimeline.load('upload')}
      onchanged={() => transferTimeline.afterMutation('upload')}
      onretrysummaries={() => transferTimeline.load('upload', true)}
      {userActions}
    />
  {:else if activePage === 'users'}
    {#if activeUsername}
      <UsersPage username={activeUsername} view={userView} {liveStatus} {liveClient} onviewchange={changeUserView} onmessageuser={userActions.message} />
    {:else}
      <section class="page user-browser-page"><header class="user-browser-heading"><div><p class="eyebrow">Network</p><h1>User browser</h1></div></header><div class="empty-state"><strong>Choose a Soulseek user</strong><p>Enter an exact username above to view their profile or shared files.</p></div></section>
    {/if}
  {:else if activePage === 'chat'}
    <ChatPage {liveClient} {liveStatus} runtime={liveState.daemon?.chat} {userActions} initialUsername={initialChatUsername} />
  {:else}
    <section class="page settings-page">
      <header class="page-heading"><p class="eyebrow">Configuration</p><h1>Settings</h1></header>
      <div class="empty-state settings-empty-state"><strong>Not implemented yet</strong><p>Settings are not implemented in this prototype.</p></div>
    </section>
  {/if}
</AppShell>

{#if activePage === 'jobs' && !showingSearch && !automaticJobId && composingJob}
  <div class="new-job-modal">
    <button type="button" class="new-job-modal-backdrop" aria-label="Close new job" disabled={composingJobCommitPending} onclick={() => (composingJob = false)}></button>
    <div class="new-job-modal-dialog" role="dialog" aria-modal="true" aria-label="New job">
      <NewJobComposer onclose={() => { if (!composingJobCommitPending) composingJob = false; }} oncommittingchange={(pending) => (composingJobCommitPending = pending)} onstarted={() => { composingJobCommitPending = false; composingJob = false; void loadJobs(true); }} />
    </div>
  </div>
{/if}
