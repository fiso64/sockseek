<script lang="ts">
  import { appPath, routePathname } from './app/paths';
  import LoadingIndicator, { RENDER_SPINNER_DELAY_MS } from './components/LoadingIndicator.svelte';
  import EmptyState from './components/EmptyState.svelte';
  import LazyView from './components/LazyView.svelte';
  import { ViewLoader } from './app/view-loader.svelte';
  import { onMount, untrack } from 'svelte';
  import AppShell from './components/AppShell.svelte';
  import JobsList from './domains/jobs/JobsList.svelte';
  import { canDeleteSubmission, requireDeleted } from './domains/jobs/deletion';
  import { completedExtract, showsSearchResults } from './domains/jobs/presentation';
  import { JobListController, jobsTabQuery, type JobsTab } from './domains/jobs/job-list.svelte';
  import { JobRouteReader } from './domains/jobs/route-reader';
  import { createChatDrafts } from './domains/chat/messages';
  import { newJobStartFromSearch, type NewJobStart } from './domains/jobs/submission';
  import NewJobComposer from './domains/jobs/NewJobComposer.svelte';
  import TransfersPage from './domains/transfers/TransfersPage.svelte';
  import { TransferTimelineController } from './domains/transfers/controller.svelte';
  import Dashboard from './domains/dashboard/Dashboard.svelte';
  import { userBrowserSession, loadUserBrowserSession, saveUserBrowserUsername, type UserBrowseView } from './domains/users/session';
  import { soulseekLinkPath } from './domains/users/soulseek-link';
  import ConfigurationDialog from './domains/system/ConfigurationDialog.svelte';
  import type { SystemTab } from './domains/system/tabs';
  import { DaemonLiveClient, type LiveConnectionStatus } from './api/live';
    import { updatedJob } from './api/state.svelte';
  import { daemonApi, type SubmittedSearch } from './api/client';
  import { type JobSummaryDto, type Schema } from './api/types';
  import { SearchViewController, type SearchViewState } from './domains/search/controller';
  import { selectedSearchCount as countSearchSelection } from './domains/search/selection';
  import { emptySearchDraft, parseSearchResultMode, searchJobPath, type SearchDraft, type SearchResultMode } from './domains/search/model';
  import { canReplaySearch } from './domains/search/replay';
  import type { DownloadsView, PageId, UserLinkActions, ViewId } from './app/navigation';

  const automaticDetail = new ViewLoader(() => import('./domains/jobs/AutomaticJobDetail.svelte'));
  const searchResults = new ViewLoader(() => import('./domains/search/SearchResults.svelte'));
  const usersPage = new ViewLoader(() => import('./domains/users/UsersPage.svelte'));
  const chatDrafts = createChatDrafts();
  const chatPage = new ViewLoader(() => import('./domains/chat/ChatPage.svelte'));
  const systemPage = new ViewLoader(() => import('./domains/system/SystemPage.svelte'));

  const liveClient = new DaemonLiveClient();
  const observeJobs = liveClient.observeJobs.bind(liveClient);
  const jobLists = {
    searches: new JobListController(daemonApi, (scope, next, error) => liveClient.observeJobList(scope, next, error), () => jobsTabQuery('searches')),
    downloads: new JobListController(daemonApi, (scope, next, error) => liveClient.observeJobList(scope, next, error), () => jobsTabQuery('downloads')),
  };
  let jobsTab = $state<JobsTab>('searches');
  let jobList = $derived(jobLists[jobsTab]);
  const jobsListPath = () => jobsTab === 'downloads' ? '/jobs?tab=downloads' : '/jobs';

  const jobRoutes = new JobRouteReader(daemonApi, liveClient);
  const transferTimeline = new TransferTimelineController(daemonApi,
    (scope, next, error) => liveClient.observeTransferTimeline(scope, next, error));
  const searchController = new SearchViewController(daemonApi, 1_000,
    (id, next, error, complete) => liveClient.observeSearchView(id, next, error, complete));
  let documentVisible = $state(true);

  let activePage = $state<ViewId>('dashboard');
  // Searches and download requests are both job views; the navigation names
  // the one being shown.
  let navigationPage = $derived<PageId>(activePage === 'jobs' ? jobsTab : activePage);
  // The Downloads entry returns to whichever of its views was used last.
  let downloadsView = $state<DownloadsView>('files');
  $effect(() => {
    if (activePage === 'downloads') downloadsView = 'files';
    else if (activePage === 'jobs' && jobsTab === 'downloads') downloadsView = 'requests';
  });

  function showDownloadsView(view: DownloadsView): void {
    mutationError = null;
    if (view === 'files') {
      activePage = 'downloads';
      deferCachedPage();
      setPath('/downloads');
      void transferTimeline.load('download', true);
      return;
    }
    jobsTab = 'downloads';
    activePage = 'jobs';
    backToJobs();
  }
  // The Jobs subview survives a visit to another top-level page.
  let showingSearch = $state(false);
  let composer = $state<{ start: NewJobStart | null } | null>(null);
  let composingJobCommitPending = $state(false);
  let automaticJobId = $state<string | null>(null);
  let automaticInitialDetail = $state<Schema<'JobDetailDto'> | null>(null);
  let pendingJobId = $state<string | null>(null);
  let jobRouteError = $state<Error | null>(null);
  let openingJobKey = $state<string | null>(null);
  const searchModeByJob = new Map<string, SearchResultMode>();
  let search = $state<SearchDraft>({ ...emptySearchDraft,
    resultMode: parseSearchResultMode(document.documentElement.dataset.searchMode ?? null)
      ?? emptySearchDraft.resultMode,
  });
  let userBrowse = $state<{ query: string; mode: UserBrowseView }>({ query: '', mode: 'user' });
  let systemTab = $state<SystemTab>('status');
  let userBrowsers = $state({ user: loadUserBrowserSession('user'), shares: loadUserBrowserSession('shares') });
  let userView = $derived<UserBrowseView>(activePage === 'shares' ? 'shares' : 'user');
  let activeUsername = $derived(userBrowsers[userView].username);
  let initialChatUsername = $state<string | null>(null);
  let searchBusy = $state(false);
  let rerunningSearch = $state(false);
  let mutationError = $state<Error | null>(null);
  let removingSearch = $state(false);
  let liveStatus = $state<LiveConnectionStatus>('connecting');
  let liveError = $state<Error | null>(null);
  const liveState = liveClient.reducer.state;
  const configurationRevision = $derived(Number(liveState.daemon?.configuration?.revision ?? 0));
  // The controller publishes replacement snapshots; result DTOs need no deep proxies.
  let searchState = $state.raw<SearchViewState>(searchController.current());
  let deletingSubmissionIds = $state(new Set<string>());
  // A view with many cached cards is expensive to mount. Holding its content
  // back lets the click that opened it paint first.
  let deferredRender = $state<'search' | 'transfers' | null>(null);
  let deferredRenderFrame: number | null = null;

  function cancelDeferredRender(): void {
    if (deferredRenderFrame !== null) cancelAnimationFrame(deferredRenderFrame);
    deferredRenderFrame = null;
    deferredRender = null;
  }

  // Every way of reaching a page (tab, link, Back/Forward) goes through here once
  // the page is chosen, so cached content never mounts in the same frame.
  function deferCachedPage(): void {
    if (activePage === 'downloads' || activePage === 'uploads') {
      if (transferTimeline.timeline[activePage === 'downloads' ? 'download' : 'upload'].items.length)
        renderCachedAfterPaint('transfers');
    } else if (activePage === 'jobs' && showingSearch && searchState.draft && searchState.job?.jobId === pendingJobId)
      renderCachedAfterPaint('search');
  }

  function renderCachedAfterPaint(view: 'search' | 'transfers'): void {
    cancelDeferredRender();
    deferredRender = view;
    // The first frame paints the selected navigation item and the page frame
    // before the cached cards mount in the next one. Do not delay a first load
    // or a direct open, which have nothing cached to mount.
    deferredRenderFrame = requestAnimationFrame(() => {
      deferredRenderFrame = requestAnimationFrame(() => {
        deferredRenderFrame = null;
        deferredRender = null;
      });
    });
  }

  $effect(() => {
    const active = documentVisible && jobsListVisible();
    const status = liveStatus;
    const tab = jobsTab;
    untrack(() => {
      jobLists.searches.setActive(active && tab === 'searches', status);
      jobLists.downloads.setActive(active && tab === 'downloads', status);
    });
  });

  let observedSearchJob = $state.raw<Schema<'JobSummaryDto'> | null>(null);
  let searchJobError = $state<Error | null>(null);
  let searchJobRetry = $state(0);
  let observedSearchId = $derived(activePage === 'jobs' && showingSearch && searchState.job?.relationships.parentJobId
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
  let activeSearchJob = $derived(activeLiveJob ?? searchState.job);
  let activeSearchAction = $derived(activeSearchJob?.lifecycle.availableActions?.some((action) => action.kind === 'cancel')
    ? 'cancel' as const
    : activeSearchJob && canDeleteSubmission(activeSearchJob) ? 'delete' as const : null);
  let canRerunSearch = $derived(canReplaySearch(activeSearchJob));
  let selectedSearchCount = $derived(countSearchSelection(searchState));

  function routeLocation(): string {
    return routePathname() + window.location.search;
  }

  function setPath(path: string, replace = false): void {
    if (routeLocation() === path) return;
    if (replace) window.history.replaceState(null, '', appPath(path));
    else window.history.pushState(null, '', appPath(path));
  }

  function decodeSegment(value: string): string {
    try { return decodeURIComponent(value); }
    catch { return value; }
  }

  // TODO [ARCHITECTURE]: Navigation is a hand-written state machine over activePage, jobsTab,
  // showingSearch, pendingJobId and automaticJobId, and still calls Searches
  // and Downloads "jobs". Derive the view from one parsed route value.
  function applyPath(pathname: string): void {
    composer = null;
    cancelDeferredRender();
    routeRequest?.abort();
    // A pasted link after the app's address, or after /shares/ or /users/
    // (some proxies merge its '//').
    const pasted = /^(?:\/(?:shares|users))?\/slsk:\/*(.*)$/i.exec(pathname);
    const linked = pasted ? soulseekLinkPath(`slsk://${pasted[1]}`) : null;
    if (linked) {
      setPath(linked, true);
      pathname = linked;
    }
    const parts = pathname.replace(/\/+$/, '').split('/').filter(Boolean);
    const root = parts[0];
    if (root === 'jobs') {
      automaticJobId = null;
      automaticInitialDetail = null;
      jobRouteError = null;
      activePage = 'jobs';
      showingSearch = Boolean(parts[1]);
      pendingJobId = parts[1] ? decodeSegment(parts[1]) : null;
      if (!pendingJobId) {
        const params = new URLSearchParams(window.location.search);
        jobsTab = params.get('tab') === 'downloads' ? 'downloads' : 'searches';
      }
      if (pendingJobId) {
        const requestedMode = parseSearchResultMode(new URLSearchParams(window.location.search).get('results'));
        if (searchState.job?.jobId !== pendingJobId || !searchState.draft
          || (requestedMode && requestedMode !== searchState.draft.resultMode))
          void openJobFromPath(pendingJobId, routeLocation());
        else
          deferCachedPage();
      }
      return;
    }
    if (root === 'users' || root === 'shares') {
      activePage = root;
      const mode = root === 'shares' ? 'shares' : 'user';
      if (parts[1]) selectBrowseUser(mode, decodeSegment(parts[1]));
      userBrowse = { query: userBrowsers[mode].username, mode };
      // /shares/user/folder/file shows that folder or file, as its link does.
      if (mode === 'shares' && parts.length > 2)
        userBrowsers.shares.shares.focus = { path: parts.slice(2).map(decodeSegment).join('\\') };
      return;
    }
    if (root === 'chat') {
      activePage = 'chat';
      initialChatUsername = parts[1] === 'users' && parts[2] ? decodeSegment(parts[2]) : null;
      return;
    }
    if (root === 'system') {
      activePage = 'system';
      systemTab = parts[1] === 'shares' || parts[1] === 'management' || parts[1] === 'api' ? parts[1] : 'status';
      return;
    }
    if (root === 'downloads' || root === 'uploads' || root === 'dashboard') {
      activePage = root;
      deferCachedPage();
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
    let hiddenTimer: ReturnType<typeof setTimeout> | undefined;
    const visibilityChanged = () => {
      documentVisible = !document.hidden;
      clearTimeout(hiddenTimer);
      if (documentVisible) void liveClient.startDaemon().catch((error: unknown) => (liveError = asError(error)));
      // Keep quick tab switches cheap. A tab left in the background gets a
      // fresh daemon snapshot when it returns, including work finished meanwhile.
      else hiddenTimer = setTimeout(() => void liveClient.suspend(), 5000);
    };
    const statusSubscription = liveClient.onStatus((status, error) => {
      searchController.setLiveConnected(status === 'connected');
      liveStatus = status;
      transferTimeline.connected = status === 'connected';
      liveError = error;
      if (status !== 'connected') return;
      if (activePage === 'jobs' && pendingJobId && jobRouteError)
        void openJobFromPath(pendingJobId, routeLocation());
      if (activePage === 'jobs' && showingSearch && searchState.phase === 'failed')
        void searchController.refresh(true);
      if (activePage === 'downloads' || activePage === 'uploads') {
        const direction = activePage === 'downloads' ? 'download' : 'upload';
        if (transferTimeline.timeline[direction].error)
          void transferTimeline.load(direction, true);
      }
    });
    const searchSubscription = searchController.subscribe((state) => (searchState = state));
    const popstate = () => {
      if (composer && composingJobCommitPending) {
        // Back must not unmount the request owner before its commit receipt.
        // The matching forward entry is the current route; the second
        // popstate leaves the composer in place until the receipt arrives.
        window.history.forward();
        return;
      }
      applyPath(routePathname());
    };
    const beforeunload = (event: BeforeUnloadEvent) => {
      if (!composer || !composingJobCommitPending) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('popstate', popstate);
    window.addEventListener('beforeunload', beforeunload);
    document.addEventListener('visibilitychange', visibilityChanged);
    visibilityChanged();
    if (activePage === 'downloads' || activePage === 'uploads')
      void transferTimeline.load(activePage === 'downloads' ? 'download' : 'upload', true);
    return () => {
      cancelDeferredRender();
      transferTimeline.stop();
      for (const list of Object.values(jobLists)) list.stop();
      statusSubscription();
      searchSubscription();
      window.removeEventListener('popstate', popstate);
      window.removeEventListener('beforeunload', beforeunload);
      routeRequest?.abort();
      searchController.stop();
      clearTimeout(hiddenTimer);
      document.removeEventListener('visibilitychange', visibilityChanged);
      void liveClient.stop();
    };
  });

  function navigate(page: PageId): void {
    if (page === 'system') systemTab = 'status';
    if (composer && composingJobCommitPending) return;
    cancelDeferredRender();
    routeRequest?.abort();
    jobRouteError = null;
    composer = null;
    if (page === 'searches' || page === 'downloads' && activePage !== 'downloads' && downloadsView === 'requests') {
      // A job view remembered from this entry is restored; one that belongs
      // to the other entry is not shown under this name.
      const remembered = jobsTab === page;
      const alreadyHere = activePage === 'jobs' && remembered;
      jobsTab = page;
      activePage = 'jobs';
      mutationError = null;
      if (alreadyHere || !remembered) {
        backToJobs();
        return;
      }
      if (automaticJobId) {
        setPath(`/jobs/${encodeURIComponent(automaticJobId)}`);
        return;
      }
      if (showingSearch && searchState.job?.jobId === pendingJobId && searchState.draft) {
        pendingJobId = searchState.job.jobId;
        deferCachedPage();
        setPath(searchJobPath(pendingJobId, searchState.draft.resultMode));
        return;
      }
      if (pendingJobId) {
        setPath(`/jobs/${encodeURIComponent(pendingJobId)}`);
        void openJobFromPath(pendingJobId, routeLocation());
        return;
      }
      setPath(jobsListPath());
      return;
    }
    activePage = page;
    if (page === 'chat') initialChatUsername = null;
    // The bar keeps what was typed; only its User/Shares mode follows the tab.
    if (page === 'users' || page === 'shares') userBrowse = { ...userBrowse, mode: userView };
    setPath((page === 'users' || page === 'shares') && activeUsername
      ? `/${page}/${encodeURIComponent(activeUsername)}` : `/${page}`);
    if (page === 'downloads' || page === 'uploads') {
      deferCachedPage();
      void transferTimeline.load(page === 'downloads' ? 'download' : 'upload', true);
    }
  }

  function openJobDraft(): void {
    composer = { start: null };
  }

  function composeSearchDownload(draft: SearchDraft): void {
    if (composingJobCommitPending) return;
    composer = { start: newJobStartFromSearch($state.snapshot(draft)) };
  }

  // The results page reads the view's definition (its filters and condition pills).
  // Start that read alongside the rows rather than after they render.
  let prefetchedDefinition: { viewId: string; read: Promise<Schema<'SearchViewDefinitionDto'>> } | null = null;
  function openSearch(submitted: SubmittedSearch): Promise<void> {
    const read = daemonApi.getSearchViewDefinition(submitted.view.viewId);
    read.catch(() => {}); // An unused prefetch must not report an unhandled failure.
    prefetchedDefinition = { viewId: submitted.view.viewId, read };
    return searchController.open(submitted);
  }
  function loadDefinition(viewId: string): Promise<Schema<'SearchViewDefinitionDto'>> {
    const prefetched = prefetchedDefinition?.viewId === viewId ? prefetchedDefinition.read : null;
    prefetchedDefinition = null;
    return prefetched ?? daemonApi.getSearchViewDefinition(viewId);
  }

  async function submit(next: SearchDraft): Promise<void> {
    if (searchBusy) return;
    searchResults.load();
    searchBusy = true;
    const origin = routeLocation();
    mutationError = null;
    try {
      const submitted = await daemonApi.submitSearch(next);
      if (submitted.job.jobId) {
        searchModeByJob.set(submitted.job.jobId, submitted.draft.resultMode);
      }
      if (routeLocation() !== origin) return;
      await openSearch(submitted);
      if (routeLocation() !== origin) return;
      jobsTab = 'searches';
      automaticJobId = null;
      automaticInitialDetail = null;
      activePage = 'jobs';
      showingSearch = true;
      pendingJobId = submitted.job.jobId ?? null;
      if (pendingJobId) setPath(searchJobPath(pendingJobId, submitted.draft.resultMode));
    } catch (error) {
      if (routeLocation() !== origin) return;
      mutationError = asError(error);
      activePage = 'jobs';
      backToJobs();
    } finally {
      searchBusy = false;
    }
  }

  async function rerunActiveSearch(): Promise<void> {
    const job = activeSearchJob;
    const view = searchState.summary;
    if (!canRerunSearch || !job || !view || rerunningSearch) return;
    rerunningSearch = true;
    const origin = routeLocation();
    mutationError = null;
    try {
      const submitted = await daemonApi.rerunSearch(job, view);
      if (submitted.job.jobId) {
        searchModeByJob.set(submitted.job.jobId, submitted.draft.resultMode);
      }
      if (routeLocation() !== origin) return;
      await openSearch(submitted);
      if (routeLocation() !== origin) return;
      pendingJobId = submitted.job.jobId ?? null;
      if (pendingJobId) setPath(searchJobPath(pendingJobId, submitted.draft.resultMode));
    } catch (error) { if (routeLocation() === origin) mutationError = asError(error); }
    finally { rerunningSearch = false; }
  }

  function jobsListVisible(): boolean {
    return activePage === 'jobs' && !showingSearch && !automaticJobId && !pendingJobId;
  }

  let routeRequest: AbortController | null = null;

  async function openJobFromPath(jobId: string, route: string): Promise<void> {
    routeRequest?.abort();
    jobRouteError = null;
    if (pendingJobId === jobId) showingSearch = true;
    const request = new AbortController();
    routeRequest = request;
    try {
      const detail = await jobRoutes.resolve(jobId, request.signal);
      if (request.signal.aborted || routeLocation() !== route) return;

      // A link to an import shows the job it produced; that redirect replaces
      // the import's entry so Back leaves instead of redirecting again.
      await openJob(detail.summary, detail, detail.summary.jobId !== jobId
        && route.split('?')[0] === `/jobs/${encodeURIComponent(jobId)}`);
    } catch (error) {
      if (request.signal.aborted || routeLocation() !== route) return;
      jobRouteError = asError(error);
      showingSearch = false;
    }
  }

  function openAutomaticJob(jobId: string, detail: Schema<'JobDetailDto'> | null = null, replace = false): void {
    routeRequest?.abort();
    jobRouteError = null;
    jobsTab = 'downloads';
    automaticJobId = jobId;
    automaticInitialDetail = detail;
    openingJobKey = null;
    showingSearch = false;
    composer = null;
    activePage = 'jobs';
    pendingJobId = jobId;
    mutationError = null;
    setPath(`/jobs/${encodeURIComponent(jobId)}`, replace);
  }

  async function openJob(job: JobSummaryDto | string, detail?: Schema<'JobDetailDto'>, replace = false): Promise<void> {
    if (typeof job === 'string') {
      await openJobFromPath(job, routeLocation());
      return;
    }
    if (completedExtract(job)) {
      const origin = routeLocation();
      routeRequest?.abort();
      const request = new AbortController();
      routeRequest = request;
      try {
        const source = detail ?? await jobRoutes.read(job.jobId, request.signal);
        const result = await jobRoutes.resolveDetail(source, request.signal);
        if (request.signal.aborted || routeLocation() !== origin) return;
        await openJob(result.summary, result, replace);
      } catch (cause) {
        if (!request.signal.aborted && routeLocation() === origin) mutationError = asError(cause);
      }
      return;
    }
    jobsTab = job.display.kind === 'search' ? 'searches' : 'downloads';
    if (!showsSearchResults(job)) {
      openAutomaticJob(job.jobId, detail, replace);
      return;
    }
    searchResults.load();
    const route = routeLocation();
    const openKey = `${job.jobId}|${route}`;
    if (openingJobKey === openKey) {
      return;
    }
    automaticJobId = null;
    automaticInitialDetail = null;
    openingJobKey = openKey;
    mutationError = null;
    try {
      const requestedMode = parseSearchResultMode(new URLSearchParams(window.location.search).get('results'))
        ?? searchModeByJob.get(job.jobId);
      const opened = await daemonApi.openSearchJob(job, undefined, detail, requestedMode);
      if (openingJobKey !== openKey || routeLocation() !== route) return;
      await openSearch(opened);
      if (openingJobKey !== openKey || routeLocation() !== route) return;
      searchModeByJob.set(job.jobId, opened.draft.resultMode);
      activePage = 'jobs';
      showingSearch = true;
      pendingJobId = job.jobId;
      setPath(searchJobPath(job.jobId, opened.draft.resultMode),
        replace || route.split('?')[0] === `/jobs/${encodeURIComponent(job.jobId)}`);
    } catch (error) {
      if (openingJobKey !== openKey || routeLocation() !== route) return;
      mutationError = asError(error);
      showingSearch = false;
      setPath(jobsListPath());
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

  async function deleteSubmission(job: JobSummaryDto): Promise<boolean> {
    if (!canDeleteSubmission(job) || deletingSubmissionIds.has(job.display.submissionId!)) return false;
    deletingSubmissionIds = new Set([...deletingSubmissionIds, job.display.submissionId!]);
    mutationError = null;
    try {
      requireDeleted(await daemonApi.deleteSubmission(job.display.submissionId!));
      for (const list of Object.values(jobLists)) list.afterArchive(job.display.submissionId!);
      return true;
    } catch (error) { mutationError = asError(error); return false; }
    finally { deletingSubmissionIds = new Set([...deletingSubmissionIds].filter((id) => id !== job.display.submissionId)); }
  }

  async function removeActiveSearch(): Promise<void> {
    const job = activeSearchJob;
    const action = activeSearchAction;
    if (!job || !action || removingSearch) return;
    removingSearch = true;
    try {
      if (action === 'cancel') await cancelJob(job);
      else await deleteOpenedSubmission(job);
    } finally { removingSearch = false; }
  }

  async function deleteOpenedSubmission(job: JobSummaryDto): Promise<void> {
    const origin = routeLocation();
    if (await deleteSubmission(job) && routeLocation() === origin) backToJobs();
  }


  // The transfer stream supplies its own snapshot. On a still-connected socket
  // it can refresh the visible rows while the daemon overview catches up.
  let transferStreamReady = $derived(documentVisible && (liveStatus === 'connected'
    || (liveStatus === 'connecting' && liveClient.canObserve)));
  $effect(() => {
    if (transferStreamReady && (activePage === 'downloads' || activePage === 'uploads'))
      return transferTimeline.observe(activePage === 'downloads' ? 'download' : 'upload');
    transferTimeline.stop();
  });

  function selectBrowseUser(mode: UserBrowseView, username: string): void {
    if (userBrowsers[mode].username !== username) {
      userBrowsers[mode] = userBrowserSession(username);
      saveUserBrowserUsername(mode, username);
    }
  }

  function openUserView(mode: UserBrowseView, username: string): void {
    selectBrowseUser(mode, username);
    activePage = mode === 'shares' ? 'shares' : 'users';
    userBrowse = { ...userBrowse, mode };
    setPath(`/${activePage}/${encodeURIComponent(username)}`);
  }

  // Any slsk:// link in the app (chats, descriptions) opens its app address:
  // a profile, or shares filtered to the linked folder or file. Modified clicks
  // keep the browser's own handling.
  function openSoulseekLink(event: MouseEvent): void {
    if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    const link = event.target instanceof Element ? event.target.closest('a[href^="slsk://" i]') : null;
    const path = link ? soulseekLinkPath(link.getAttribute('href') ?? '') : null;
    if (!path) return;
    event.preventDefault();
    setPath(path);
    applyPath(path);
  }

  // Editing a linked filter leaves the link; the address drops its path without a history entry.
  function leaveSharesLink(): void {
    if (activePage === 'shares' && activeUsername) setPath(`/shares/${encodeURIComponent(activeUsername)}`, true);
  }

  const userActions: UserLinkActions = {
    profile: username => openUserView('user', username),
    shares: username => openUserView('shares', username),
    message: (username) => {
      initialChatUsername = username;
      activePage = 'chat';
      setPath(`/chat/users/${encodeURIComponent(username)}`);
    },
  };

  function submitUserBrowse(value: { query: string; mode: UserBrowseView }): void {
    const username = value.query.trim();
    if (username) openUserView(value.mode, username);
  }

  // The page's User/Shares toggle explicitly follows the displayed user.
  // Sidebar and search-mode navigation only restore that tab's own session.
  function changeUserView(value: UserBrowseView): void {
    if (activeUsername) openUserView(value, activeUsername);
  }

  function changeUserBrowse(value: { query: string; mode: UserBrowseView }): void {
    userBrowse = value;
    if (value.mode !== userView) navigate(value.mode === 'shares' ? 'shares' : 'users');
  }

  function backToJobs(): void {
    routeRequest?.abort();
    automaticJobId = null;
    automaticInitialDetail = null;
    showingSearch = false;
    pendingJobId = null;
    jobRouteError = null;
    setPath(jobsListPath());
  }

  function asError(error: unknown): Error {
    return error instanceof Error ? error : new Error(String(error));
  }

  // Resolve the initial route before mounting a page. Deep links must not
  // briefly mount Dashboard and issue an analytics request only to abort it.
  applyPath(routePathname());
</script>

<svelte:document onclick={openSoulseekLink} />

<AppShell activePage={navigationPage} {liveStatus} {liveState} {search} {userBrowse} {searchBusy} onnavigate={navigate} onsearchchange={(value) => (search = value)} onsearchsubmit={(value) => { if (!searchBusy) search = { ...value }; void submit(value); }} ondownloadrequest={composeSearchDownload} onuserbrowsechange={changeUserBrowse} onuserbrowsesubmit={submitUserBrowse}>
  {#if activePage === 'jobs' && pendingJobId && jobRouteError}
    <section class="page page-search redesigned-search-page">
      <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'} blocking`} role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Job unavailable'}</strong><small>{liveStatus === 'offline' ? 'This can’t be loaded while the daemon is offline.' : jobRouteError.message}</small></span>{#if liveStatus !== 'offline'}<button class="button" type="button" onclick={() => void openJobFromPath(pendingJobId!, routeLocation())}>Retry</button>{/if}<button class="button" type="button" onclick={backToJobs}>Back to jobs</button></div>
    </section>
  {:else if activePage === 'jobs' && showingSearch && (deferredRender === 'search' || pendingJobId && searchState.job?.jobId !== pendingJobId)}
    <section class="page page-search redesigned-search-page">
      <LoadingIndicator label="Opening job" blocking delayMs={deferredRender === 'search' ? RENDER_SPINNER_DELAY_MS : 0} />
    </section>
  {:else if activePage === 'jobs' && showingSearch && searchState.draft}
    <LazyView view={searchResults}>
      {#snippet children(SearchResults)}
        <SearchResults
          {userActions}
          {configurationRevision}
          daemonOffline={liveStatus === 'offline'}
          connected={documentVisible && liveStatus === 'connected'}
          viewState={searchState}
          liveJob={activeLiveJob}
          liveJobError={searchJobError}
          removeAction={activeSearchAction}
          removePending={removingSearch}
          actionError={mutationError}
          onremove={() => void removeActiveSearch()}
          searchAgainAvailable={canRerunSearch}
          searchAgainPending={rerunningSearch}
          isselected={(ref, parentRef) => {
            // Controller queries must react to each published selection snapshot.
            searchState;
            return searchController.isSelected(ref, parentRef);
          }}
          selectedCount={selectedSearchCount}
          onbeginrange={(additive) => searchController.beginRange(additive)}
          ontoggle={(ref, selected, parentRef) => searchController.toggle(ref, selected, parentRef)}
          onselectfolder={(ref, path, selected, includeFiltered) => searchController.toggleDirectoryPath(ref, path, selected, includeFiltered)}
          isfolderselected={(directoryRef, relativePath, filtered) => {
            searchState;
            return searchController.isDirectoryPathSelected(directoryRef, relativePath, filtered);
          }}
          onselectall={(selected) => searchController.selectAll(selected)}
          ondownload={(options) => searchController.commit(options)}
          onlibrarymatches={(id, request, signal) => daemonApi.getSearchViewLibraryMatches(id, request, signal)}
          onresolvesettings={(options) => searchController.resolveSelectionSettings(options)}
          oncheckrevision={(id, revision, signal) => daemonApi.getSearchViewRevisionLifetime(id, revision, signal)}
          onloaddefinition={loadDefinition}
          onrefine={(request, resetSelection) => searchController.refine(request, resetSelection)}
          onsort={(order) => searchController.sort(order)}
          onloadmore={() => void searchController.loadMore()}
          onretry={() => { searchJobRetry++; void searchController.refresh(true); }}
          onloaddirectory={(ref) => void searchController.loadDirectoryFiles(ref)}
          onretrievedirectory={(ref) => void searchController.retrieveDirectory(ref)}
          onopenoptions={(ref) => void searchController.openGroupOptions(ref)}
          onstepoption={(ref, direction) => void searchController.stepGroupOption(ref, direction)}
          oncloseoptions={() => searchController.closeGroupOptions()}
          onfilteroptions={(text) => void searchController.filterGroupOptions(text)}
          onloadoptions={() => void searchController.loadMoreGroupOptions()}
          onchooseoption={(ref, option) => searchController.chooseGroupOption(ref, option)}
          ontogglealbumfile={(groupRef, fileRefs, selected) => searchController.toggleAlbumGroupFile(groupRef, fileRefs, selected)}
          onopendownload={(id) => void openJob(id)}
          onback={backToJobs}
          onsearchagain={() => void rerunActiveSearch()}
        />
      {/snippet}
    </LazyView>
  {:else if activePage === 'jobs'}
    {#if automaticJobId}
      <LazyView view={automaticDetail}>
        {#snippet children(AutomaticJobDetail)}
          <AutomaticJobDetail jobId={automaticJobId!} initialDetail={automaticInitialDetail} {userActions}
            connected={liveStatus === 'connected' && documentVisible}
            {liveStatus} visible={documentVisible}
            {observeJobs} observeJobList={(scope, next, error) => liveClient.observeJobList(scope, next, error)}
            onback={backToJobs} oneditagain={(start) => { if (!composingJobCommitPending) composer = { start }; }} onsearch={(draft) => void submit(draft)} onopenjob={(job, detail, replace) => void openJob(job, detail, replace)} ondelete={(job) => void deleteOpenedSubmission(job)}
            deletingSubmissions={deletingSubmissionIds} deleteError={mutationError} />
        {/snippet}
      </LazyView>
    {:else}
    {#key jobsTab}
    <JobsList
      tab={jobsTab} ondownloadsview={showDownloadsView} position={jobList}
      connected={liveStatus === 'connected' && documentVisible}
      {observeJobs} readJobTransfer={liveState.readJobTransfer}
      onnew={openJobDraft}
      jobs={jobList.items}
      linkedResults={jobList.linkedResults}
      loading={jobList.loading || liveStatus === 'connecting' || liveStatus === 'reconnecting'}
      daemonOffline={liveStatus === 'offline'}
      hasMore={Boolean(jobList.cursor)}
      error={jobList.error ?? (liveStatus === 'offline' || liveStatus === 'failed' ? liveError : null)}
      actionError={mutationError}
      onretry={() => jobList.refresh()}
      onopen={(job, detail) => void openJob(job, detail)}
      oncancel={(job) => void cancelJob(job)}
      ondelete={(job) => void deleteSubmission(job)}
      deletingSubmissions={deletingSubmissionIds}
      onloadmore={() => jobList.loadMore()}
    />
    {/key}
    {/if}
  {:else if activePage === 'dashboard'}
    <Dashboard liveStatus={liveStatus} terminalTransferCommitRevision={liveState.daemon?.terminalTransferCommitRevision ?? 0} soulseek={liveState.daemon?.soulseekClient} activity={liveState.activitySummary} {userActions} />
  {:else if activePage === 'downloads'}
    <TransfersPage
      direction="download"
      deferred={deferredRender === 'transfers'}
      ondownloadsview={showDownloadsView}
      readTransfer={transferTimeline.rows.download.read}
      items={transferTimeline.timeline.download.items}
      coverage={transferTimeline.timeline.download.coverage}
      summaries={transferTimeline.timeline.download.summaries}
      summaryCoverage={transferTimeline.timeline.download.summaryCoverage}
      summaryError={transferTimeline.timeline.download.summaryError}
      nextCursor={transferTimeline.timeline.download.cursor}
      loading={transferTimeline.timeline.download.loading}
      daemonOffline={liveStatus === 'offline'}
      error={transferTimeline.timeline.download.error}
      onexpandgroup={(username, groupRef, shown) => transferTimeline.expandGroup('download', username, groupRef, shown)}
      expandingGroups={transferTimeline.expandingGroups.download}
      onloadmore={() => void transferTimeline.load('download')}
      onchanged={() => transferTimeline.afterMutation('download')}
      onretrysummaries={() => transferTimeline.load('download', true)}
      {userActions}
    />
  {:else if activePage === 'uploads'}
    <TransfersPage
      direction="upload"
      deferred={deferredRender === 'transfers'}
      readTransfer={transferTimeline.rows.upload.read}
      items={transferTimeline.timeline.upload.items}
      coverage={transferTimeline.timeline.upload.coverage}
      summaries={transferTimeline.timeline.upload.summaries}
      summaryCoverage={transferTimeline.timeline.upload.summaryCoverage}
      summaryError={transferTimeline.timeline.upload.summaryError}
      nextCursor={transferTimeline.timeline.upload.cursor}
      loading={transferTimeline.timeline.upload.loading}
      daemonOffline={liveStatus === 'offline'}
      error={transferTimeline.timeline.upload.error}
      onexpandgroup={(username, groupRef, shown) => transferTimeline.expandGroup('upload', username, groupRef, shown)}
      expandingGroups={transferTimeline.expandingGroups.upload}
      onloadmore={() => void transferTimeline.load('upload')}
      onchanged={() => transferTimeline.afterMutation('upload')}
      onretrysummaries={() => transferTimeline.load('upload', true)}
      {userActions}
    />
  {:else if activePage === 'users' || activePage === 'shares'}
    {#if activeUsername}
      {#key userBrowsers[userView]}
        <LazyView view={usersPage}>
          {#snippet children(UsersPage)}
            <UsersPage {configurationRevision} session={userBrowsers[userView]} {documentVisible} view={userView} {liveStatus} {liveClient} onviewchange={changeUserView} onmessageuser={userActions.message} {userActions} onopenjob={(id) => void openJob(id)} onleavelink={leaveSharesLink} />
          {/snippet}
        </LazyView>
      {/key}
    {:else}
      <section class="page user-browser-page">
        <header class="page-heading user-browser-heading"><div><h1>{activePage === 'shares' ? 'Shares' : 'Users'}</h1></div></header>
        <EmptyState icon={activePage === 'shares' ? 'folder' : 'user'}
          title={activePage === 'shares' ? 'No shares to display' : 'No user to display'} />
      </section>
    {/if}
  {:else if activePage === 'chat'}
    <LazyView view={chatPage}>
      {#snippet children(ChatPage)}
        <ChatPage drafts={chatDrafts} {liveClient} {liveStatus} runtime={liveState.daemon?.chat} {userActions} initialUsername={initialChatUsername} />
      {/snippet}
    </LazyView>
  {:else}
    <LazyView view={systemPage}>
      {#snippet children(SystemPage)}
        <SystemPage daemon={liveState.daemon} {liveStatus} {liveClient} tab={systemTab}
          ontabchange={tab => { systemTab = tab; setPath(tab === 'status' ? '/system' : `/system/${tab}`); }} />
      {/snippet}
    </LazyView>
  {/if}
</AppShell>

<ConfigurationDialog configuration={liveState.daemon?.configuration} epoch={liveState.epoch} />

{#if composer}
  {#key composer}
    <NewJobComposer connected={liveStatus === 'connected' && documentVisible} start={composer.start}
      onclose={() => composer = null}
      oncommittingchange={(pending) => (composingJobCommitPending = pending)}
      onstarted={(rootJobId) => { composingJobCommitPending = false; composer = null; openAutomaticJob(rootJobId); }} />
  {/key}
{/if}
