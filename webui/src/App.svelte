<script lang="ts">
  import LoadingIndicator from './components/LoadingIndicator.svelte';
  import LazyView from './components/LazyView.svelte';
  import { ViewLoader } from './app/view-loader.svelte';
  import { onMount, untrack } from 'svelte';
  import AppShell from './components/AppShell.svelte';
  import JobsList from './domains/jobs/JobsList.svelte';
  import { canDeleteSubmission, requireDeleted } from './domains/jobs/deletion';
  import { completedExtract, showsSearchResults } from './domains/jobs/presentation';
  import { JobListController, type JobsTab } from './domains/jobs/job-list.svelte';
  import { JobRouteReader } from './domains/jobs/route-reader';
  import Modal from './components/Modal.svelte';
  import NewJobComposer from './domains/jobs/NewJobComposer.svelte';
  import TransfersPage from './domains/transfers/TransfersPage.svelte';
  import { TransferTimelineController } from './domains/transfers/controller.svelte';
  import Dashboard from './domains/dashboard/Dashboard.svelte';
  import { userBrowserSession, type UserBrowseView } from './domains/users/session';
  import ConfigurationDialog from './domains/system/ConfigurationDialog.svelte';
  import type { SystemTab } from './domains/system/tabs';
  import { DaemonLiveClient, type LiveConnectionStatus } from './api/live';
  import { jobStateSummary } from './api/job-summary';
  import { updatedJob } from './api/state.svelte';
  import { daemonApi } from './api/client';
  import { type JobSummaryDto, type Schema } from './api/types';
  import { SearchViewController, type SearchViewState } from './domains/search/controller';
  import { selectedSearchCount as countSearchSelection } from './domains/search/selection';
  import { emptySearchDraft, parseSearchResultMode, searchJobPath, type SearchDraft, type SearchResultMode } from './domains/search/model';
  import { canReplaySearch } from './domains/search/replay';
  import type { PageId, UserLinkActions } from './app/navigation';

  const automaticDetail = new ViewLoader(() => import('./domains/jobs/AutomaticJobDetail.svelte'));
  const searchResults = new ViewLoader(() => import('./domains/search/SearchResults.svelte'));
  const usersPage = new ViewLoader(() => import('./domains/users/UsersPage.svelte'));
  const chatPage = new ViewLoader(() => import('./domains/chat/ChatPage.svelte'));
  const systemPage = new ViewLoader(() => import('./domains/system/SystemPage.svelte'));

  const liveClient = new DaemonLiveClient();
  const observeJobs = liveClient.observeJobs.bind(liveClient);
  const jobLists = {
    searches: new JobListController(daemonApi, (scope, next, error) => liveClient.observeJobList(scope, next, error), 'searches'),
    downloads: new JobListController(daemonApi, (scope, next, error) => liveClient.observeJobList(scope, next, error), 'downloads'),
  };
  let jobsTab = $state<JobsTab>('searches');
  let jobList = $derived(jobLists[jobsTab]);
  const jobsListPath = () => jobsTab === 'downloads'
    ? `/jobs?tab=downloads${jobLists.downloads.submissionId ? `&submission=${encodeURIComponent(jobLists.downloads.submissionId)}` : ''}` : '/jobs';

  function changeJobsTab(tab: JobsTab): void {
    jobsTab = tab;
    mutationError = null;
    setPath(jobsListPath());
  }
  const jobRoutes = new JobRouteReader(daemonApi, liveClient);
  const transferTimeline = new TransferTimelineController(daemonApi,
    (scope, next, error) => liveClient.observeTransferTimeline(scope, next, error));
  const searchController = new SearchViewController(daemonApi, 1_000,
    (id, next, error, complete) => liveClient.observeSearchView(id, next, error, complete));
  let documentVisible = $state(true);

  let activePage = $state<PageId>('dashboard');
  // The Jobs subview survives a visit to another top-level page.
  let showingSearch = $state(false);
  let composingJob = $state(false);
  let composerSearch = $state<SearchDraft | null>(null);
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
  let userBrowsers = $state({ user: userBrowserSession(), shares: userBrowserSession() });
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
  // The controller publishes replacement snapshots; result DTOs need no deep proxies.
  let searchState = $state.raw<SearchViewState>(searchController.current());
  let deletingSubmissionIds = $state(new Set<string>());
  let deferSearchRender = $state(false);
  let searchRenderFrame: number | null = null;

  function cancelDeferredSearchRender(): void {
    if (searchRenderFrame !== null) cancelAnimationFrame(searchRenderFrame);
    searchRenderFrame = null;
    deferSearchRender = false;
  }

  function renderCachedSearchAfterPaint(): void {
    deferSearchRender = true;
    // The first frame paints the selected navigation item before mounting the
    // cached result cards in the next frame. Do not delay direct job opens.
    searchRenderFrame = requestAnimationFrame(() => {
      searchRenderFrame = requestAnimationFrame(() => {
        searchRenderFrame = null;
        deferSearchRender = false;
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
    : activeSearchJob && canDeleteSubmission(activeSearchJob) ? 'delete' as const : null);
  let canRerunSearch = $derived(canReplaySearch(activeSearchJob));
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
    cancelDeferredSearchRender();
    automaticJobId = null;
    jobRouteError = null;
    const parts = pathname.replace(/\/+$/, '').split('/').filter(Boolean);
    const root = parts[0];
    if (root === 'jobs') {
      activePage = 'jobs';
      showingSearch = Boolean(parts[1]);
      pendingJobId = parts[1] ? decodeSegment(parts[1]) : null;
      if (!pendingJobId) {
        const params = new URLSearchParams(window.location.search);
        jobsTab = params.get('tab') === 'downloads' ? 'downloads' : 'searches';
        if (jobsTab === 'downloads') jobLists.downloads.filterSubmission(params.get('submission'));
      }
      if (pendingJobId) {
        const requestedMode = parseSearchResultMode(new URLSearchParams(window.location.search).get('results'));
        if (searchState.job?.jobId !== pendingJobId || !searchState.draft
          || (requestedMode && requestedMode !== searchState.draft.resultMode))
          void openJobFromPath(pendingJobId, routeLocation());
      }
      return;
    }
    if (root === 'users' || root === 'shares') {
      activePage = root;
      const mode = root === 'shares' ? 'shares' : 'user';
      if (parts[1]) selectBrowseUser(mode, decodeSegment(parts[1]));
      userBrowse = { query: userBrowsers[mode].username, mode };
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
      cancelDeferredSearchRender();
      transferTimeline.stop();
      for (const list of Object.values(jobLists)) list.stop();
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
    if (page === 'system') systemTab = 'status';
    if (composingJob && composingJobCommitPending) return;
    cancelDeferredSearchRender();
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
        renderCachedSearchAfterPaint();
        setPath(searchJobPath(pendingJobId, searchState.draft.resultMode));
        return;
      }
      setPath(jobsListPath());
      return;
    }
    activePage = page;
    if (page === 'chat') initialChatUsername = null;
    if (page === 'users' || page === 'shares') userBrowse = { query: activeUsername || userBrowse.query, mode: userView };
    setPath((page === 'users' || page === 'shares') && activeUsername
      ? `/${page}/${encodeURIComponent(activeUsername)}` : `/${page}`);
    if (page === 'downloads') void transferTimeline.load('download', true);
    if (page === 'uploads') void transferTimeline.load('upload', true);
  }

  function composeSearchDownload(draft: SearchDraft): void {
    if (composingJobCommitPending) return;
    composerSearch = JSON.parse(JSON.stringify(draft)) as SearchDraft;
    activePage = 'jobs';
    jobsTab = 'downloads';
    backToJobs();
    composingJob = true;
  }

  async function submit(next: SearchDraft): Promise<void> {
    if (searchBusy) return;
    searchResults.load();
    search = { ...next };
    searchBusy = true;
    mutationError = null;
    try {
      const submitted = await daemonApi.submitSearch(next);
      jobsTab = 'searches';
      if (submitted.job.jobId) {
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
      setPath(jobsListPath());
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
      if (submitted.job.jobId) {
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
        const source = detail ?? await jobRoutes.read(job.jobId, request.signal);
        const result = await jobRoutes.resolveDetail(source, request.signal);
        if (request.signal.aborted || routeLocation() !== origin) return;
        await openJob(result.summary, result);
      } catch (cause) {
        if (!request.signal.aborted && routeLocation() === origin) mutationError = asError(cause);
      }
      return;
    }
    jobsTab = job.kind === 'search' ? 'searches' : 'downloads';
    if (!showsSearchResults(job)) {
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
    searchResults.load();
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
    if (!canDeleteSubmission(job) || deletingSubmissionIds.has(job.submissionId!)) return false;
    deletingSubmissionIds = new Set([...deletingSubmissionIds, job.submissionId!]);
    mutationError = null;
    try {
      requireDeleted(await daemonApi.deleteSubmission(job.submissionId!));
      for (const list of Object.values(jobLists)) list.afterArchive(job.submissionId!);
      return true;
    } catch (error) { mutationError = asError(error); return false; }
    finally { deletingSubmissionIds = new Set([...deletingSubmissionIds].filter((id) => id !== job.submissionId)); }
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


  $effect(() => {
    if (documentVisible && liveStatus === 'connected'
      && (activePage === 'downloads' || activePage === 'uploads'))
      return transferTimeline.observe(activePage === 'downloads' ? 'download' : 'upload');
    transferTimeline.stop();
  });

  function selectBrowseUser(mode: UserBrowseView, username: string): void {
    if (userBrowsers[mode].username !== username) userBrowsers[mode] = userBrowserSession(username);
  }

  function openUserView(mode: UserBrowseView, username: string): void {
    selectBrowseUser(mode, username);
    activePage = mode === 'shares' ? 'shares' : 'users';
    userBrowse = { query: username, mode };
    setPath(`/${activePage}/${encodeURIComponent(username)}`);
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
  applyPath(window.location.pathname);
</script>

<AppShell {activePage} {liveStatus} {liveState} {search} {userBrowse} {searchBusy} onnavigate={navigate} onsearchchange={(value) => (search = value)} onsearchsubmit={(value) => void submit(value)} ondownloadrequest={composeSearchDownload} onuserbrowsechange={changeUserBrowse} onuserbrowsesubmit={submitUserBrowse}>
  {#if activePage === 'jobs' && pendingJobId && jobRouteError}
    <section class="page page-search redesigned-search-page">
      <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'} blocking`} role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Job unavailable'}</strong><small>{liveStatus === 'offline' ? 'This resource cannot be loaded while the daemon is offline.' : jobRouteError.message}</small></span>{#if liveStatus !== 'offline'}<button type="button" onclick={() => void openJobFromPath(pendingJobId!, routeLocation())}>Retry</button>{/if}<button type="button" onclick={backToJobs}>Back to jobs</button></div>
    </section>
  {:else if activePage === 'jobs' && showingSearch && pendingJobId && searchState.job?.jobId !== pendingJobId}
    <section class="page page-search redesigned-search-page">
      <LoadingIndicator label="Opening job" blocking />
    </section>
  {:else if activePage === 'jobs' && showingSearch && searchState.draft}
    {#if !deferSearchRender}
    <LazyView view={searchResults}>
      {#snippet children(SearchResults)}
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
          isselected={(ref, parentRef) => {
            // Controller queries must react to each published selection snapshot.
            searchState;
            return searchController.isSelected(ref, parentRef);
          }}
          selectedCount={selectedSearchCount}
          ontoggle={(ref, selected, parentRef) => searchController.toggle(ref, selected, parentRef)}
          onselectfolder={(directoryRef, relativePath, selected) => searchController.toggleDirectoryPath(directoryRef, relativePath, selected)}
          isfolderselected={(directoryRef, relativePath) => {
            searchState;
            return searchController.isDirectoryPathSelected(directoryRef, relativePath);
          }}
          onselectall={(selected) => searchController.selectAll(selected)}
          ondownload={(options) => void searchController.commit(options)}
          onresolvesettings={(options) => searchController.resolveSelectionSettings(options)}
          onloaddefinition={(viewId) => daemonApi.getSearchViewDefinition(viewId)}
          onrefine={(request, resetSelection) => searchController.refine(request, resetSelection)}
          onsort={(order) => searchController.sort(order)}
          onloadmore={() => void searchController.loadMore()}
          onretry={() => { searchJobRetry++; void searchController.refresh(true); }}
          onloaddirectory={(ref) => void searchController.loadDirectoryFiles(ref)}
          onretrievedirectory={(ref) => void searchController.retrieveDirectory(ref)}
          onopenoptions={(ref) => void searchController.openGroupOptions(ref)}
          onstepoption={(ref, direction) => void searchController.stepGroupOption(ref, direction)}
          oncloseoptions={() => searchController.closeGroupOptions()}
          onloadoptions={() => void searchController.loadMoreGroupOptions()}
          onchooseoption={(ref, option) => searchController.chooseGroupOption(ref, option)}
          ontogglealbumfile={(groupRef, fileRef, selected) => searchController.toggleAlbumGroupFile(groupRef, fileRef, selected)}
          onviewdownloads={(id) => { jobLists.downloads.filterSubmission(id); jobsTab = 'downloads'; backToJobs(); }}
          onback={backToJobs}
          onsearchagain={() => void rerunActiveSearch()}
        />
      {/snippet}
    </LazyView>
    {/if}
  {:else if activePage === 'jobs'}
    {#if automaticJobId}
      <LazyView view={automaticDetail}>
        {#snippet children(AutomaticJobDetail)}
          <AutomaticJobDetail jobId={automaticJobId!} initialDetail={automaticInitialDetail} {userActions}
            connected={liveStatus === 'connected' && documentVisible}
            {observeJobs}
            onback={backToJobs} onopenjob={(job, detail) => void openJob(job, detail)} ondelete={(job) => void deleteOpenedSubmission(job)}
            deletingSubmissions={deletingSubmissionIds} deleteError={mutationError} />
        {/snippet}
      </LazyView>
    {:else}
    {#key jobsTab}
    <JobsList
      tab={jobsTab} ontabchange={changeJobsTab} position={jobList}
      submissionId={jobList.submissionId} onclearfilter={() => { jobList.filterSubmission(null); setPath(jobsListPath()); }}
      refreshRevision={jobList.refreshRevision}
      connected={liveStatus === 'connected' && documentVisible}
      {observeJobs}
      onnew={() => { composerSearch = null; composingJob = true; }}
      jobs={jobList.items}
      loading={jobList.loading || liveStatus === 'connecting'}
      daemonOffline={liveStatus === 'offline'}
      hasMore={Boolean(jobList.cursor)}
      error={jobList.error ?? liveError}
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
    <Dashboard liveStatus={liveStatus} terminalTransferCommitRevision={liveState.daemon?.terminalTransferCommitRevision ?? 0} soulseek={liveState.daemon?.soulseekClient} transfers={liveState.transferSummary} {userActions} />
  {:else if activePage === 'downloads'}
    <TransfersPage
      direction="download"
      readTransfer={transferTimeline.rows.download.read}
      items={transferTimeline.timeline.download.items}
      globalBoundary={transferTimeline.timeline.download.boundary}
      coverage={transferTimeline.timeline.download.coverage}
      summaries={transferTimeline.timeline.download.summaries}
      summaryCoverage={transferTimeline.timeline.download.summaryCoverage}
      summaryError={transferTimeline.timeline.download.summaryError}
      nextCursor={transferTimeline.timeline.download.cursor}
      loading={transferTimeline.timeline.download.loading}
      daemonOffline={liveStatus === 'offline'}
      error={transferTimeline.timeline.download.error}
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
      readTransfer={transferTimeline.rows.upload.read}
      items={transferTimeline.timeline.upload.items}
      globalBoundary={transferTimeline.timeline.upload.boundary}
      coverage={transferTimeline.timeline.upload.coverage}
      summaries={transferTimeline.timeline.upload.summaries}
      summaryCoverage={transferTimeline.timeline.upload.summaryCoverage}
      summaryError={transferTimeline.timeline.upload.summaryError}
      nextCursor={transferTimeline.timeline.upload.cursor}
      loading={transferTimeline.timeline.upload.loading}
      daemonOffline={liveStatus === 'offline'}
      error={transferTimeline.timeline.upload.error}
      onfetchedmembers={(rows) => transferTimeline.registerMembers('upload', rows)}
      missingMembers={transferTimeline.missingMembers.upload}
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
            <UsersPage session={userBrowsers[userView]} view={userView} {liveStatus} {liveClient} onviewchange={changeUserView} onmessageuser={userActions.message} />
          {/snippet}
        </LazyView>
      {/key}
    {:else}
      <section class="page user-browser-page"><header class="user-browser-heading"><div><h1>{activePage === 'shares' ? 'Shares' : 'Users'}</h1></div></header><div class="resource-state-notice empty blocking" role="status"><span class="resource-state-dot" aria-hidden="true"></span><span class="resource-state-copy"><strong>{activePage === 'shares' ? 'No shares to display' : 'No user to display'}</strong></span></div></section>
    {/if}
  {:else if activePage === 'chat'}
    <LazyView view={chatPage}>
      {#snippet children(ChatPage)}
        <ChatPage {liveClient} {liveStatus} runtime={liveState.daemon?.chat} {userActions} initialUsername={initialChatUsername} />
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

{#if activePage === 'jobs' && !showingSearch && !automaticJobId && composingJob}
  <Modal title="Create new job" label="New job" closeLabel="Close new job" wide locked={composingJobCommitPending}
    onclose={() => { composingJob = false; }}>
    <NewJobComposer initialSearch={composerSearch} oncommittingchange={(pending) => (composingJobCommitPending = pending)} onstarted={() => { composingJobCommitPending = false; composingJob = false; if (jobLists.downloads.submissionId) jobLists.downloads.filterSubmission(null); else jobLists.downloads.refresh(); changeJobsTab('downloads'); }} />
  </Modal>
{/if}
