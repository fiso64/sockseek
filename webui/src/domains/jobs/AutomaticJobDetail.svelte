<script lang="ts">
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { untrack } from 'svelte';
  import { SvelteMap } from 'svelte/reactivity';
  import TransferStateLabel from '../../components/items/TransferStateLabel.svelte';
  import { audioAttributes, basename, type FolderItemFile, type TransferPresentation } from '../presentation/items';
  import { daemonApi, defaultPageSize, nestedPageSize } from '../../api/client';
  import { DaemonApiError } from '../../api/http';
  import type { JobStateDto, JobSummaryDto, Schema } from '../../api/types';
  import type { DaemonLiveClient } from '../../api/live';
  import { TransferRows, updatedTransfer, updatedJob } from '../../api/state.svelte';
  import { jobStateSummary } from '../../api/job-summary';
  import { liveTransferPresentation } from '../transfers/model';
  import { selectedJobFile, jobFileTransfer } from './selected-file';
  import { directoryManifestFileRow, directoryKnownSize } from './directory-files';
  import { canReuseJobDetail, jobDetailVersion, startJobDetailRefresh, readJobSummaries, jobSummaryRetryIds, type JobSummaryRead } from './details';
  import { completedExtract, presentedChildren, resolvePresentedJob, extractSourceLabel } from './presentation';
  import { jobHeaderStats } from './header-stats';
  import { automaticJobListStats, automaticJobProgressPercent } from './list-stats';
  import { jobStatusLabel, jobStatusClass, jobTransferTone } from './status';
  import { jobKindIcon, jobKindLabel } from './kind';
  import { canDeleteSubmission } from './deletion';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import { loadJobAncestors } from './ancestors';
  import { formatCount, humanizeStateValue } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import FileItemCard from '../../components/items/FileItemCard.svelte';
  import FolderItemCard from '../../components/items/FolderItemCard.svelte';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import type { UserLinkActions } from '../../app/navigation';

  let { jobId, initialDetail = null, userActions, connected, observeJobs, onback, onopenjob, ondelete, deletingSubmissions = new Set<string>(), deleteError = null }: {
    jobId: string;
    initialDetail?: Schema<'JobDetailDto'> | null;
    userActions: UserLinkActions;
    connected: boolean;
    observeJobs: DaemonLiveClient['observeJobs'];
    onback: () => void;
    onopenjob: (job: JobSummaryDto, detail?: Schema<'JobDetailDto'>) => void;
    ondelete: (job: JobSummaryDto) => void;
    deletingSubmissions?: ReadonlySet<string>;
    deleteError?: Error | null;
  } = $props();
  let detail = $state.raw<Schema<'JobDetailDto'> | null>(null);
  let observedSource = $state<{ jobId: string; sourceId: string | null; available: boolean } | null>(null);
  let payload = $derived(detail?.payload);
  let ancestors = $state<JobSummaryDto[]>([]);
  let parent = $derived(ancestors.at(-1) ?? null);
  let parentError = $state<Error | null>(null);
  let children = $state.raw<JobSummaryDto[]>([]);
  let retrievalChildren = $state.raw<JobSummaryDto[]>([]);
  let retrievalCursor = $state<string | null>(null);
  let retrievalLoading = $state(false);
  let retrievalMoreRequested = $state(false);
  let retrievalRefreshPending = false;
  let retrievalLoaded = $state(false);
  let retrievalError = $state<Error | null>(null);
  const observedJobs = new SvelteMap<string, JobStateDto>();
  const noObservedJobs: ReadonlyMap<string, JobStateDto> = new Map();
  let liveJobsById = $derived(connected ? observedJobs : noObservedJobs);
  let observedChildCount = $state<number | null>(null);
  let observedRetrievalCount = $state<number | null>(null);
  let liveRoot = $derived(liveJobsById.get(jobId));
  // A parent can enter RunningChildren before its first displayed children are
  // registered. Observe their arrival, not progress or every undisplayed child.
  let liveChildExtent = $derived(Math.min(observedChildCount ?? 0, nestedPageSize));
  let liveRetrievalExtent = $derived(Math.min(observedRetrievalCount ?? 0, nestedPageSize));
  let liveDetailVersion = $derived(`${liveChildExtent}:${jobDetailVersion(liveRoot ? jobStateSummary(liveRoot) : undefined)}`);
  let observedDetailVersion = '';
  let invalidateDetail: () => void = () => {};
  let visibleChildren = $derived(children.map((child) => {
    const live = liveJobsById.get(child.jobId!);
    return live ? { ...child, ...jobStateSummary(live) } : child;
  }));
  let visibleRetrievalChildren = $derived(retrievalChildren.map((child) => {
    const live = liveJobsById.get(child.jobId!);
    return live ? { ...child, ...jobStateSummary(live) } : child;
  }));
  let linkedChildren = $state(new Map<string, JobSummaryRead>());
  let childRecords = $derived.by(() => {
    const known = new Map<string, JobSummaryDto>();
    for (const [id, read] of linkedChildren) if (read.summary) known.set(id, read.summary);
    for (const child of visibleChildren) if (child.jobId) known.set(child.jobId, child);
    for (const child of visibleRetrievalChildren) if (child.jobId) known.set(child.jobId, child);
    for (const [id, job] of known) {
      const live = liveJobsById.get(id);
      if (live) known.set(id, { ...job, ...jobStateSummary(live) });
    }
    return known;
  });
  let childRows = $derived(presentedChildren(visibleChildren, childRecords));
  let relatedRows = $derived(payload?.kind === 'album'
    ? presentedChildren(visibleRetrievalChildren, childRecords) : childRows);
  let cursor = $state<string | null>(null);
  let error = $state<Error | null>(null);
  let childError = $state<Error | null>(null);
  let actionError = $state<Error | null>(null);
  let manifest = $state.raw<Schema<'DirectoryManifestPageDto'> | null>(null);
  let manifestEntries = $state.raw<Schema<'DirectoryManifestEntryDto'>[]>([]);
  let manifestNext = $state<number | null>(null);
  let manifestError = $state<Error | null>(null);
  let manifestLoading = $state(false);
  let manifestCheckedAt = 0;
  let pendingManifestRefresh: { attempt?: number } | null = null;
  let loading = $state(false);
  const scopedTransfers = new TransferRows();
  let transferError = $state<Error | null>(null);
  let transferRetry = $state(0);
  let liveTransfers = $derived([...scopedTransfers.values()]);
  let observedJobIds = $derived([...new Set([jobId,
    ...children.map(child => child.jobId).filter((id): id is string => Boolean(id)),
    ...retrievalChildren.map(child => child.jobId).filter((id): id is string => Boolean(id)),
    ...linkedChildren.keys(),
    ...childRows.map(row => row.job.jobId).filter((id): id is string => Boolean(id)),
    ...manifestEntries.map(entry => entry.childJobId).filter((id): id is string => Boolean(id)),
  ])].sort().join(','));
  $effect(() => {
    transferRetry;
    if (!connected) return;
    const ids = observedJobIds.split(',');
    const requestedIds = new Set(ids);
    const rootId = jobId;
    untrack(() => scopedTransfers.clear());
    transferError = null;
    let disposed = false;
    let initializing = true;
    const stop = observeJobs(rootId, ids, update => {
      if (disposed) return;
      // A new observation is authoritative, including retained revisions after
      // reconnect. Core history revisions need not exceed the old live cursor.
      if (initializing) { observedJobs.clear(); initializing = false; }
      if (update.sourceJobAvailable != null)
        observedSource = { jobId: rootId, sourceId: update.sourceJobId ?? null, available: update.sourceJobAvailable };
      if (update.childCount != null) observedChildCount = Number(update.childCount);
      if (update.retrievalChildCount != null) observedRetrievalCount = Number(update.retrievalChildCount);
      // The server owns final rows through durability handoff. Keep the keyed
      // records while the visible extent changes; no live/history reconstruction.
      for (const id of untrack(() => [...observedJobs.keys()])) if (!requestedIds.has(id)) observedJobs.delete(id);
      for (const delta of update.state.jobs) {
        const next = updatedJob(untrack(() => liveJobsById.get(delta.jobId)), delta);
        if (next) observedJobs.set(delta.jobId, next);
      }
      for (const delta of update.state.transfers) {
        const next = updatedTransfer(untrack(() => scopedTransfers.get(delta.transferId)), delta);
        if (next) scopedTransfers.set(delta.transferId, next);
      }
      for (const id of update.state.removedTransferIds) scopedTransfers.delete(id);
    }, error => { if (!disposed) transferError = error; });
    return () => { disposed = true; stop(); scopedTransfers.clear(); };
  });
  let moreChildrenRequested = $state(false);
  let childRefreshPending = false;
  let loadedChildCount = $state<number | string | null>(null);
  let lifetime: AbortController;
  let job = $derived(detail?.summary);
  let sourceJobAvailable = $derived(observedSource?.jobId === jobId && observedSource.sourceId === job?.sourceJobId
    ? observedSource.available : detail?.sourceJobAvailable === true);
  let parentJobId = $derived(job?.parentJobId);
  $effect(() => {
    const id = parentJobId;
    ancestors = [];
    parentError = null;
    if (!id) return;
    const request = new AbortController();
    void loadJobAncestors(id, (ancestorId) => daemonApi.getJob(ancestorId, request.signal), request.signal).then((value) => {
      if (!request.signal.aborted) ancestors = value;
    }).catch((cause) => {
      if (!request.signal.aborted) parentError = cause instanceof Error ? cause : new Error(String(cause));
    });
    return () => request.abort();
  });
  let parentTitle = $derived(parent?.itemName ?? parent?.queryText ?? 'parent job');
  let relatedTotal = $derived.by(() => {
    if (payload?.kind === 'album')
      return retrievalLoaded && !retrievalCursor && !retrievalError ? relatedRows.length : null;
    const declared = payload?.kind === 'job-list' ? payload.count
      : payload?.kind === 'song-groups' ? payload.songCount
      : payload?.kind === 'album-groups' ? payload.resultCount
      : null;
    if (declared != null && (Number(declared) > 0 || !Number(detail?.childCount))) return Number(declared);
    return loadedChildCount != null && !cursor && !childError ? childRows.length : null;
  });
  let showRelatedJobs = $derived(Boolean(detail) && (
    payload?.kind === 'job-list' || payload?.kind === 'song-groups' || payload?.kind === 'album-groups'
    // Album Song children are file members in the folder card; only real
    // Retrieve Folder operations belong in its separate related-jobs list.
    || payload?.kind === 'album' && relatedRows.length > 0
    || payload?.kind === 'extract' && (Number(detail?.childCount) > 0 || children.length > 0)
  ));
  let planStateLabel = $derived(manifest ? undefined : job?.lifecycleState === 'Terminal'
    ? 'The accepted file plan is not available for this job.'
    : manifestLoading ? 'Loading the accepted file plan…' : 'Waiting for the accepted file plan.');
  let activeFileTransfer = $derived(liveTransfers.find((transfer) => transfer.identity.jobId === jobId && !transfer.status.isTerminal));
  let selectedFile = $derived(selectedJobFile(payload, activeFileTransfer));
  let selectedPeerUploadSpeed = $derived(activeFileTransfer?.identity.peerUploadSpeed
    ?? (selectedFile?.peer ? selectedFile.peer.uploadSpeed : payload?.kind === 'song' ? payload.resolvedUploadSpeed : null));
  let selectedPeerHasFreeUploadSlot = $derived(activeFileTransfer?.identity.peerHasFreeUploadSlot
    ?? (selectedFile?.peer ? selectedFile.peer.hasFreeUploadSlot : payload?.kind === 'song' ? payload.resolvedHasFreeUploadSlot : null));
  let liveTransfersByJob = $derived(new Map(liveTransfers
    .filter((transfer) => transfer.identity.jobId && !transfer.status.isTerminal)
    .map((transfer) => [transfer.identity.jobId!, transfer])));
  let fileStatesPending = $derived(manifestEntries.some(entry => entry.childJobId && !entry.target
    && !entry.child && !liveJobsById.has(entry.childJobId)));
  let moreFolderFiles = $derived(manifestNext != null);
  let folderFileCount = $derived(manifest ? Number(manifest.entryCount) + Number(manifest.supplementalEntryCount) : null);
  let folderFiles = $derived(manifestEntries.map(entry => directoryManifestFileRow(entry, {
    child: entry.childJobId && liveJobsById.has(entry.childJobId)
      ? jobStateSummary(liveJobsById.get(entry.childJobId)!) : entry.child,
    live: entry.childJobId ? liveTransfersByJob.get(entry.childJobId) : undefined,
    ownerTerminal: job?.lifecycleState === 'Terminal',
  })));
  let folderTransfer = $derived.by((): TransferPresentation | undefined => {
    if (payload?.kind !== 'album' && payload?.kind !== 'remote-directory') return undefined;
    const total = Number(payload.directory.fileCount);
    const successful = Number(payload.directory.successfulFileCount);
    return {
      job: job ?? undefined, directory: payload.directory,
      detail: job?.failureMessage ?? undefined,
      state: job ? jobStatusLabel(job) : humanizeStateValue(payload.directory.phase),
      tone: job?.lifecycleState !== 'Terminal' ? 'active' : jobTransferTone(job),
      direction: 'download',
      progressPercent: payload.directory.progressPercent == null ? undefined : Number(payload.directory.progressPercent),
      progressText: total > 0
        ? successful === total ? `${formatCount(total)} ${total === 1 ? 'file' : 'files'} complete`
          : `${formatCount(successful)} of ${formatCount(total)} ${total === 1 ? 'file' : 'files'} complete`
        : undefined,
    };
  });
  let childFileProgress = $derived.by(() => {
    const progress = new Map<string, number>();
    for (const [id, transfer] of liveTransfersByJob) {
      const percent = liveTransferPresentation(transfer).progressPercent;
      if (percent !== undefined) progress.set(id, percent);
    }
    return progress;
  });
  let title = $derived(job?.itemName ?? job?.queryText ?? jobKindLabel(job?.kind));
  let progress = $derived(payload?.kind === 'job-list'
    && payload.succeededJobCount != null && payload.failedJobCount != null
    ? { total: payload.count, completed: payload.succeededJobCount, failed: payload.failedJobCount }
    : payload?.kind === 'song-groups'
      && payload.succeededSongCount != null && payload.failedSongCount != null
      ? { total: payload.songCount, completed: payload.succeededSongCount, failed: payload.failedSongCount }
      : payload?.kind === 'album-groups'
        && payload.succeededAlbumCount != null && payload.failedAlbumCount != null
        ? { total: payload.resultCount, completed: payload.succeededAlbumCount, failed: payload.failedAlbumCount }
      : null);
  let outcomeCountsUnavailable = $derived(payload?.kind === 'job-list'
    ? payload.succeededJobCount == null || payload.failedJobCount == null
    : payload?.kind === 'song-groups'
      ? payload.succeededSongCount == null || payload.failedSongCount == null
      : payload?.kind === 'album-groups'
        ? payload.succeededAlbumCount == null || payload.failedAlbumCount == null
        : false);

  function childOutcomes(value: Schema<'JobPayloadDto'> | null | undefined): string {
    if (value?.kind === 'job-list') return JSON.stringify([value.activeJobCount, value.completedJobCount, value.succeededJobCount, value.failedJobCount]);
    if (value?.kind === 'song-groups') return JSON.stringify([value.completedSongCount, value.succeededSongCount, value.failedSongCount]);
    if (value?.kind === 'album-groups') return JSON.stringify([value.completedAlbumCount, value.succeededAlbumCount, value.failedAlbumCount]);
    if (value?.kind === 'album' || value?.kind === 'remote-directory') return JSON.stringify([value.directory.attemptNumber, value.directory.fileCount, value.directory.terminalFileCount]);
    return '';
  }

  $effect(() => {
    const id = jobId;
    untrack(() => observedJobs.clear());
    observedChildCount = null;
    observedRetrievalCount = null;
    let firstDetail = initialDetail?.summary.jobId === id ? initialDetail : null;
    // Display the route's captured detail immediately. The scoped initial state
    // reconciles it once; missing global child rows are not evidence it is stale.
    const request = new AbortController();
    lifetime = request;
    detail = null;
    children = [];
    retrievalChildren = [];
    retrievalCursor = null;
    retrievalLoading = false;
    retrievalMoreRequested = false;
    retrievalRefreshPending = false;
    retrievalLoaded = false;
    retrievalError = null;
    linkedChildren = new Map();
    cursor = null;
    loading = false;
    moreChildrenRequested = false;
    childRefreshPending = false;
    loadedChildCount = null;
    error = null;
    childError = null;
    actionError = null;
    manifest = null;
    manifestEntries = [];
    manifestNext = null;
    manifestError = null;
    manifestLoading = false;
    manifestCheckedAt = 0;
    pendingManifestRefresh = null;
    async function refresh() {
      // Read the current live version without making it a dependency of this
      // effect: invalidation must preserve the loaded rows and UI state.
      observedDetailVersion = untrack(() => liveDetailVersion);
      try {
        const next = await (firstDetail ?? daemonApi.getJob(id, request.signal));
        firstDetail = null;
        if (request.signal.aborted) return;
        if (completedExtract(next.summary)) {
          const result = await resolvePresentedJob(next, (resultId) => daemonApi.getJob(resultId, request.signal), request.signal);
          if (!request.signal.aborted) onopenjob(result.summary, result);
          return;
        }
        const lifecycleChanged = detail?.summary.lifecycleState !== next.summary.lifecycleState;
        const childrenChanged = detail?.childCount !== next.childCount || lifecycleChanged;
        const outcomesChanged = childOutcomes(detail?.payload) !== childOutcomes(next.payload);
        // The live workflow retains child summaries until retirement. Their
        // outcome updates already feed visibleChildren; only missing live
        // coverage, membership changes or lifecycle handoff need another page.
        // Folder members come from the manifest, not a second child collection.
        // Album retrieval operations are a distinct, actually displayed list.
        const hasChildList = next.payload?.kind !== 'album' && next.payload?.kind !== 'remote-directory';
        const refreshChildren = hasChildList && (loadedChildCount !== next.childCount || childrenChanged || outcomesChanged
          && children.some(child => !liveJobsById.has(child.jobId!)));
        const refreshRetrieval = next.payload?.kind === 'album' && (!retrievalLoaded || lifecycleChanged
          || (liveRoot ? retrievalChildren.length < liveRetrievalExtent : childrenChanged || outcomesChanged
            || visibleRetrievalChildren.some(child => child.lifecycleState !== 'Terminal')));
        detail = next;
        error = null;
        await Promise.all([
          refreshChildren ? loadChildren(true, request, id)
            : hasChildList ? hydrateVisibleChildren(request, id) : Promise.resolve(),
          refreshRetrieval ? loadRetrievalChildren(true, request, id) : Promise.resolve(),
        ]);
        const directory = next.payload?.kind === 'album' || next.payload?.kind === 'remote-directory'
          ? next.payload.directory : null;
        const attempt = directory?.attemptNumber == null ? null : Number(directory.attemptNumber);
        if (attempt === null && manifest) {
          manifest = null;
          manifestEntries = [];
          manifestNext = null;
        }
        // Child registration/outcomes already arrive on the live stream. Only
        // membership, an attempt change or retirement needs another file page.
        const membershipChanged = manifest && directory && Number(directory.fileCount)
          !== Number(manifest.entryCount) + Number(manifest.supplementalEntryCount);
        if (attempt !== null && (manifest && (Number(manifest.attemptNumber) !== attempt || membershipChanged || lifecycleChanged
          || outcomesChanged && !liveRoot
          || fileStatesPending && next.summary.lifecycleState !== 'Terminal' && Date.now() - manifestCheckedAt >= 2_000)
          || (!manifest || !manifest.childrenMaterialized) && Date.now() - manifestCheckedAt >= 2_000)) {
          if (manifest && Number(manifest.attemptNumber) !== attempt) {
            manifest = null;
            manifestEntries = [];
            manifestNext = null;
          }
          await loadManifest(true, request, id, attempt);
        }
        if (detailMatchesObservation()) observedDetailVersion = untrack(() => liveDetailVersion);
      } catch (cause) {
        if (!request.signal.aborted) error = cause instanceof Error ? cause : new Error(String(cause));
      }
    }
    const invalidate = startJobDetailRefresh(refresh, request.signal,
      () => Boolean(error) || detail?.summary.lifecycleState !== 'Terminal',
      () => observedDetailVersion !== liveDetailVersion || Boolean(error));
    invalidateDetail = invalidate;
    return () => {
      request.abort();
      if (invalidateDetail === invalidate) invalidateDetail = () => {};
    };
  });

  function detailMatchesObservation(): boolean {
    return Boolean(detail && liveRoot && !error
      && canReuseJobDetail(detail.summary, jobStateSummary(liveRoot))
      && Number(detail.childCount) >= liveChildExtent);
  }

  $effect(() => {
    const version = liveDetailVersion;
    untrack(() => {
      // Initial/replayed observations need no read when HTTP already covers them.
      if (detailMatchesObservation()
        || !liveRoot && detail?.summary.lifecycleState === 'Terminal' && !error) {
        observedDetailVersion = version;
        return;
      }
      if (version !== observedDetailVersion) invalidateDetail();
    });
  });

  async function loadChildren(reset = false, request = lifetime, id = jobId) {
    if (!reset) moreChildrenRequested = true;
    if (loading) { if (reset) childRefreshPending = true; return; }
    if (detail?.summary.jobId === id && Number(detail.childCount) === 0) {
      children = [];
      cursor = null;
      loadedChildCount = 0;
      childError = null;
      moreChildrenRequested = false;
      return;
    }
    loading = true;
    try {
      const visibleCount = reset ? children.length : 0;
      const merged = new Map((reset ? [] : children).map((child) => [child.jobId, child]));
      let nextCursor = reset ? null : cursor;
      // Grouped execution also registers an internal JobList. Its generated
      // members are already direct children; page only the displayed kind so
      // wrappers neither duplicate rows nor consume the user's page size.
      const kind = payload?.kind === 'song-groups' ? 'song'
        : payload?.kind === 'album-groups' ? 'album' : undefined;
      do {
        const page = await daemonApi.getJobChildren(id, nextCursor, request.signal, kind);
        if (request.signal.aborted) return;
        for (const child of page.items) merged.set(child.jobId, child);
        nextCursor = page.nextCursor ?? null;
      } while (nextCursor && merged.size < visibleCount);
      children = [...merged.values()];
      cursor = nextCursor;
      loadedChildCount = detail?.childCount ?? null;
      await hydrateVisibleChildren(request, id);
      childError = null;
    } catch (cause) {
      if (!request.signal.aborted) childError = cause instanceof Error ? cause : new Error(String(cause));
    } finally {
      if (!request.signal.aborted) {
        loading = false;
        if (!reset) moreChildrenRequested = false;
        // Keep a user's continuation when background reconciliation is in
        // flight. Run it first; a pending refresh then preserves that extent.
        if (moreChildrenRequested && cursor) void loadChildren(false, request, id);
        else {
          moreChildrenRequested = false;
          if (childRefreshPending) { childRefreshPending = false; void loadChildren(true, request, id); }
        }
      }
    }
  }

  async function loadRetrievalChildren(reset = false, request = lifetime, id = jobId) {
    if (!reset) retrievalMoreRequested = true;
    if (retrievalLoading) { if (reset) retrievalRefreshPending = true; return; }
    if (detail?.summary.jobId === id && Number(detail.childCount) === 0) {
      retrievalChildren = [];
      retrievalCursor = null;
      retrievalLoaded = true;
      retrievalError = null;
      retrievalMoreRequested = false;
      return;
    }
    retrievalLoading = true;
    try {
      const loadedCount = reset ? retrievalChildren.length : 0;
      const merged = new Map((reset ? [] : retrievalChildren).map((child) => [child.jobId, child]));
      let nextCursor = reset ? null : retrievalCursor;
      do {
        const page = await daemonApi.getJobChildren(id, nextCursor, request.signal, 'retrieve-folder');
        if (request.signal.aborted) return;
        for (const child of page.items) merged.set(child.jobId, child);
        nextCursor = page.nextCursor ?? null;
      } while (nextCursor && merged.size < loadedCount);
      retrievalChildren = [...merged.values()];
      retrievalCursor = nextCursor;
      retrievalLoaded = true;
      retrievalError = null;
    } catch (cause) {
      if (!request.signal.aborted) retrievalError = cause instanceof Error ? cause : new Error(String(cause));
    } finally {
      if (!request.signal.aborted) {
        retrievalLoading = false;
        if (!reset) retrievalMoreRequested = false;
        if (retrievalMoreRequested && retrievalCursor) void loadRetrievalChildren(false, request, id);
        else {
          retrievalMoreRequested = false;
          if (retrievalRefreshPending) {
            retrievalRefreshPending = false;
            void loadRetrievalChildren(true, request, id);
          }
        }
      }
    }
  }

  async function hydrateVisibleChildren(request = lifetime, id = jobId) {
    const refreshed = new Set<string>();
    while (!request.signal.aborted) {
      const missing = [...new Set(childRows.flatMap(entry => {
        if (entry.missingResultId && !linkedChildren.has(entry.missingResultId)) return [entry.missingResultId];
        const cached = linkedChildren.get(entry.job.jobId!)?.summary;
        return cached && cached.lifecycleState !== 'Terminal' && !liveJobsById.has(cached.jobId!)
          && !refreshed.has(cached.jobId!) ? [cached.jobId!] : [];
      }))];
      if (!missing.length) return;
      for (const id of missing) refreshed.add(id);
      const batch = await readJobSummaries(missing,
        (ids, signal) => daemonApi.getJobSummaries(ids, signal), request.signal);
      if (jobId === id && !request.signal.aborted) linkedChildren = new Map([...linkedChildren, ...batch]);
    }
  }

  async function showMoreChildren() {
    if (payload?.kind === 'album') {
      if (retrievalCursor) await loadRetrievalChildren();
      return;
    }
    if (cursor) await loadChildren();
  }

  async function retryChildResult(id: string) {
    const reads = new Map(linkedChildren);
    for (const failedId of jobSummaryRetryIds(linkedChildren, id)) reads.delete(failedId);
    linkedChildren = reads;
    await hydrateVisibleChildren();
  }

  async function loadManifest(reset = false, request = lifetime, id = jobId, attempt?: number) {
    if (manifestLoading) {
      if (reset) pendingManifestRefresh = { attempt };
      return;
    }
    manifestLoading = true;
    manifestCheckedAt = Date.now();
    try {
      const pinnedAttempt = attempt ?? (manifest?.attemptNumber == null ? undefined : Number(manifest.attemptNumber));
      const visibleCount = reset && Number(manifest?.attemptNumber) === pinnedAttempt ? manifestEntries.length : 0;
      let after = reset ? -1 : manifestNext ?? -1;
      const loaded: Schema<'DirectoryManifestEntryDto'>[] = [];
      let page: Schema<'DirectoryManifestPageDto'>;
      do {
        page = await daemonApi.getDirectoryManifest(id, pinnedAttempt, after, defaultPageSize, request.signal);
        if (request.signal.aborted) return;
        loaded.push(...page.items);
        after = page.nextOrdinal == null ? -1 : Number(page.nextOrdinal);
      } while (page.nextOrdinal != null && loaded.length < visibleCount);
      const currentAttempt = detail?.payload?.kind === 'album' || detail?.payload?.kind === 'remote-directory'
        ? detail.payload.directory.attemptNumber : null;
      if (currentAttempt == null || Number(currentAttempt) !== Number(page.attemptNumber)) return;
      manifest = page;
      manifestEntries = reset ? loaded : [...manifestEntries, ...loaded];
      manifestNext = page.nextOrdinal == null ? null : Number(page.nextOrdinal);
      manifestError = null;
    } catch (cause) {
      if (request.signal.aborted) return;
      if (cause instanceof DaemonApiError && cause.status === 404) {
        if (reset) { manifest = null; manifestEntries = []; manifestNext = null; }
        manifestError = null;
      } else {
        manifestError = cause instanceof Error ? cause : new Error(String(cause));
      }
    } finally {
      finishManifestLoad(request, id);
    }
  }

  function finishManifestLoad(request: AbortController, id: string) {
    if (request.signal.aborted) return;
    manifestLoading = false;
    if (pendingManifestRefresh) {
      const pending = pendingManifestRefresh;
      pendingManifestRefresh = null;
      void loadManifest(true, request, id, pending.attempt);
    }
  }

  async function loadMoreFiles() {
    if (!manifestLoading && manifestNext != null) await loadManifest();
  }

  async function cancel(id = jobId) {
    const request = lifetime;
    actionError = null;
    try { await daemonApi.cancelJob(id, request.signal); }
    catch (cause) { if (!request.signal.aborted) actionError = cause instanceof Error ? cause : new Error(String(cause)); }
  }
</script>

<section class="page page-search redesigned-search-page automatic-job-detail">
  <header class="job-detail-heading">
    <button type="button" class="icon-button back-button" aria-label={job?.parentJobId ? `Back to ${parentTitle}` : 'Back to jobs'} onclick={() => job?.parentJobId ? onopenjob(parent ?? { jobId: job.parentJobId }) : onback()}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M12.5 4.5L7 10l5.5 5.5M7.5 10H16" /></svg>
    </button>
    <div class="job-detail-title">
      {#if job}
        <div class="job-detail-meta">
          <span class="job-detail-kicker">{#each ancestors as ancestor (ancestor.jobId)}<button type="button" onclick={() => onopenjob(ancestor)}>{ancestor.itemName ?? ancestor.queryText ?? 'Parent job'}</button><span>/</span>{/each}<span>{jobKindLabel(job.kind)}</span></span>
          {#if job.sourceDetail}<span>{job.sourceDetail}</span>{/if}
          {#if payload?.kind === 'song' && payload.query.album}<span>Album: <strong>{payload.query.album}</strong></span>{/if}
          {#if job.sourceJobId && sourceJobAvailable && job.role === 'user-root' && !job.parentJobId}<span><button type="button" class="job-detail-source-link" aria-label="Open source job" onclick={() => onopenjob({ jobId: job!.sourceJobId! })}>Source job ↗</button></span>{/if}
        </div>
      {/if}
      <h1>{detail ? title : 'Job'}</h1>
    </div>
    {#if job}
      {@const statusLabel = jobStatusLabel(job)}
      <div class="job-detail-heading-summary">
        {#each jobHeaderStats(payload) as stat}<span class="job-detail-header-fact">{stat}</span>{/each}
        {#if job.createdAtUtc}<span class="job-detail-header-fact job-detail-age"><RelativeTime timestamp={job.createdAtUtc} /></span>{/if}
        {#if statusLabel !== 'Complete'}
          <span class={`search-status-badge ${jobStatusClass(job)}`}><i></i>{statusLabel}</span>
        {/if}
        {#if job.availableActions?.some((action) => action.kind === 'cancel')}
          <button type="button" class="delete-search-button quiet-cancel" onclick={() => void cancel()}><Icon name="x" /><span>Cancel</span></button>
        {:else if canDeleteSubmission(job)}
          <button type="button" class="delete-search-button" title="Delete submission from history" disabled={deletingSubmissions.has(job.submissionId!)} onclick={() => ondelete(job)}><Icon name="trash" /><span>Delete</span></button>
        {/if}
      </div>
    {/if}
  </header>
  {#if !detail && !error}<LoadingIndicator label="Loading job" blocking />{/if}
  {#if error}<div class="resource-state-notice unavailable" role="alert">{error.message}</div>{/if}
  {#if transferError}<div class="resource-state-notice unavailable" role="alert"><span>Live file progress unavailable: {transferError.message}</span><button type="button" onclick={() => transferRetry++}>Retry</button></div>{/if}
  {#if parentError}<div class="resource-state-notice unavailable" role="alert">Parent job unavailable: {parentError.message}</div>{/if}
  {#if childError}<div class="resource-state-notice unavailable" role="alert"><span>Child jobs unavailable: {childError.message}</span><button type="button" disabled={loading} onclick={() => void loadChildren(true)}>Retry</button></div>{/if}
  {#if retrievalError && payload?.kind === 'album'}<div class="resource-state-notice unavailable" role="alert"><span>Related jobs unavailable: {retrievalError.message}</span><button type="button" disabled={retrievalLoading} onclick={() => void loadRetrievalChildren(true)}>Retry</button></div>{/if}
  {#if manifestError}<div class="resource-state-notice unavailable" role="alert"><span>Directory files unavailable: {manifestError.message}</span><button type="button" disabled={manifestLoading} onclick={() => void loadManifest(true)}>Retry</button></div>{/if}
  {#if actionError}<div class="resource-state-notice unavailable" role="alert">{actionError.message}</div>{/if}
  {#if deleteError}<div class="resource-state-notice unavailable" role="alert"><strong>Job action failed</strong><span>{deleteError.message}</span></div>{/if}
  {#if job?.failureMessage}<div class="job-detail-empty"><strong>{job.failureMessage}</strong><span>{job.failureDetail ?? ''}</span></div>{/if}
  {#if payload?.kind === 'extract'}
    <dl class="job-detail-facts">
      <div><dt>Source</dt><dd>{extractSourceLabel(payload.inputType)}</dd></div>
      <div><dt>Input</dt><dd title={payload.input}>{payload.input}</dd></div>
    </dl>
    {#if payload.resultJobId}
      <button type="button" class="job-child-load-more" onclick={() => onopenjob({ jobId: payload?.kind === 'extract' ? payload.resultJobId ?? undefined : undefined })}>Open extracted job</button>
    {:else}
      {#if job?.lifecycleState === 'Terminal'}
        <div class="job-detail-empty"><strong>{job.terminalOutcome === 'Failed' ? 'Extraction failed' : 'No extracted job'}</strong><span>The source did not produce a job.</span></div>
      {:else}
        <LoadingIndicator label="Extracting source" blocking />
      {/if}
    {/if}
  {:else if payload?.kind === 'generic'}
    <dl class="job-detail-facts"><div><dt>Detail</dt><dd>{payload.text}</dd></div></dl>
  {:else if payload?.kind === 'retrieve-folder'}
    <dl class="job-detail-facts">
      <div><dt>From</dt><dd><UsernameLink username={payload.username} actions={userActions} /></dd></div>
      <div><dt>Folder</dt><dd title={payload.folderPath}>{payload.folderPath}</dd></div>
      <div><dt>Outcome</dt><dd>{humanizeStateValue(payload.retrievalOutcome)}</dd></div>
      <div><dt>New files</dt><dd>{formatCount(payload.newFilesFoundCount)}</dd></div>
    </dl>
  {:else if selectedFile}
    {@const target = selectedFile.target}
    {@const file = selectedFile.file}
    <section class="job-content-section">
      <div class="job-source-heading">
        <div class="job-source-summary">
          <span class="job-source-identity">From: <UsernameLink username={target.username} actions={userActions} /></span>
          {#if payload?.kind === 'song'}
            {#if selectedPeerUploadSpeed != null}<span>{(Number(selectedPeerUploadSpeed) / 1_000_000).toFixed(1)} MB/s</span>{/if}
            {#if selectedPeerHasFreeUploadSlot != null}<span class:available={selectedPeerHasFreeUploadSlot}>{selectedPeerHasFreeUploadSlot ? 'Free slot' : 'No free slot'}</span>{/if}
          {/if}
        </div>
      </div>
      <FileItemCard path={target.filename} sizeBytes={file.fileSize == null && target.size == null ? null : Number(file.fileSize ?? target.size)}
        audio={audioAttributes(target)}
        transfer={jobFileTransfer(job, file, target, activeFileTransfer)} />
    </section>
  {:else if payload?.kind === 'song'}
    {#if job?.lifecycleState === 'Terminal'}
      <div class="job-detail-empty"><strong>{job.terminalOutcome === 'Failed' ? 'No matching file' : 'No selected file'}</strong><span>{job.terminalOutcome === 'Failed' ? 'No candidate satisfied this job.' : job.skipReason && job.skipReason !== 'None' ? humanizeStateValue(job.skipReason) : jobStatusLabel(job)}</span></div>
    {:else}
      <LoadingIndicator label="Finding a file" blocking />
    {/if}
    <TransferStateLabel transfer={jobFileTransfer(job, payload.file)} />
  {:else if payload?.kind === 'album'}
    {#if payload.resolvedFolderPath || manifest}
      <section class="job-content-section">
        {#if payload.resolvedFolderUsername}<div class="job-source-heading"><div class="job-source-summary"><span class="job-source-identity">From: <UsernameLink username={payload.resolvedFolderUsername} actions={userActions} /></span></div></div>{/if}
        <FolderItemCard path={(payload.resolvedFolderPath ?? manifest?.displayRoot) || 'Selected files'} sizeBytes={directoryKnownSize(payload.directory)} files={folderFiles} totalFileCount={folderFileCount} filesComplete={manifest != null && !moreFolderFiles} dataStateLabel={planStateLabel} transfer={folderTransfer} />
        {#if moreFolderFiles}<button type="button" class="job-child-load-more" disabled={manifestLoading} onclick={() => void loadMoreFiles()}>Load more</button>{/if}
      </section>
    {:else}
      {#if job?.lifecycleState === 'Terminal'}
        <div class="job-detail-empty"><strong>Folder details unavailable</strong><span>No selected folder plan is available for this job.</span></div>
      {:else}
        <LoadingIndicator label="Finding an album" blocking />
      {/if}
    {/if}
  {:else if payload?.kind === 'remote-directory'}
    <section class="job-content-section">
      {#if payload.sourceUsername}<div class="job-source-heading"><div class="job-source-summary"><span class="job-source-identity">From: <UsernameLink username={payload.sourceUsername} actions={userActions} /></span></div></div>{/if}
      <FolderItemCard path={(payload.sourceFolderPath ?? manifest?.displayRoot ?? payload.displayRoot) || 'Selected files'} sizeBytes={directoryKnownSize(payload.directory)} files={folderFiles} totalFileCount={folderFileCount} filesComplete={manifest != null && !moreFolderFiles} dataStateLabel={planStateLabel} transfer={folderTransfer} />
      {#if moreFolderFiles}<button type="button" class="job-child-load-more" disabled={manifestLoading} onclick={() => void loadMoreFiles()}>Load more</button>{/if}
    </section>
  {:else if payload && payload.kind !== 'job-list' && payload.kind !== 'song-groups' && payload.kind !== 'album-groups'}
    <div class="resource-state-notice unavailable">This job's detailed result presentation is not connected yet.</div>
  {/if}
  {#if progress}
    <section class="job-run-progress" aria-label={`${progress.completed} of ${progress.total} complete`}>
      <div class="job-run-progress-copy"><strong>{formatCount(progress.completed)} of {formatCount(progress.total)} complete</strong><span>{Number(progress.failed) ? `${formatCount(progress.failed)} failed · ` : ''}{formatCount(Math.max(0, Number(progress.total) - Number(progress.completed) - Number(progress.failed)))} remaining</span></div>
      <div class="job-run-progress-track"><i style={`width:${Number(progress.total) > 0 ? Number(progress.completed) / Number(progress.total) * 100 : 0}%`}></i></div>
    </section>
  {/if}
  {#if outcomeCountsUnavailable}
    <div class="resource-state-notice degraded" role="status"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Outcome counts unavailable</strong><small>This job has no retained outcome totals; loaded children may be incomplete.</small></span></div>
  {/if}
  {#if showRelatedJobs}
    <section class="job-related-section">
      <header class="job-section-heading"><h2>{payload?.kind === 'extract' ? 'Result' : payload?.kind === 'song-groups' ? 'Generated songs' : payload?.kind === 'album-groups' ? 'Generated albums' : payload?.kind === 'album' ? 'Related jobs' : 'Jobs'}</h2>
        <span>{formatCount(relatedTotal ?? relatedRows.length)} {relatedTotal == null ? 'loaded ' : ''}{(relatedTotal ?? relatedRows.length) === 1 ? 'job' : 'jobs'}</span>
      </header>
      <div class="job-child-list">
        {#each relatedRows as entry (entry.job.jobId)}
          {@const child = entry.job}
          {@const unavailable = Boolean(entry.missingResultId || entry.error)}
          {@const childTitle = child.itemName ?? child.queryText ?? jobKindLabel(child.kind)}
          {@const childStats = automaticJobListStats(child)}
          {@const percent = child.jobId && (child.kind === 'song' || child.kind === 'remote-file')
            ? childFileProgress.get(child.jobId) : automaticJobProgressPercent(child)}
          <div class="job-child-row" class:has-progress={percent !== undefined}>
            <button type="button" class="job-child-open" onclick={() => onopenjob(child)}>
              <span class="job-child-icon"><Icon name={jobKindIcon(child.kind)} /></span>
              <span class="job-child-copy"><strong>{childTitle}</strong><small>{jobKindLabel(child.kind)}{#if child.createdAtUtc}{' · '}<RelativeTime timestamp={child.createdAtUtc} />{/if}</small></span>
              <span class="job-child-meta">{#if childStats.length}<span class="job-child-stat">{childStats[0]}</span><span class="stat-separator">·</span>{/if}<span class={`search-status-badge ${unavailable ? 'pending' : jobStatusClass(child)}`}><i></i>{unavailable ? 'Result unavailable' : jobStatusLabel(child)}</span></span>
              {#if percent !== undefined}<span class="job-child-progress" aria-label={`${Math.round(percent)}% complete`}><i style={`width:${percent}%`}></i></span>{/if}
            </button>
            {#if !unavailable && child.jobId && child.availableActions?.some((action) => action.kind === 'cancel')}
              <button type="button" class="job-row-action" aria-label={`Cancel ${childTitle}`} title="Cancel" onclick={() => void cancel(child.jobId)}><Icon name="x" /></button>
            {:else if !unavailable && canDeleteSubmission(child)}
              <button type="button" class="job-row-action" aria-label={`Remove ${childTitle}`} title="Remove submission from history" disabled={deletingSubmissions.has(child.submissionId!)} onclick={() => ondelete(child)}><Icon name="x" /></button>
            {/if}
          </div>
          {#if entry.error || entry.missingResultId && linkedChildren.has(entry.missingResultId)}
            <div class="resource-state-notice unavailable" role="alert"><span>Extracted result unavailable: {entry.error?.message ?? linkedChildren.get(entry.missingResultId!)?.error?.message ?? 'The linked job is not retained.'}</span>{#if entry.missingResultId}<button type="button" onclick={() => void retryChildResult(entry.missingResultId!)}>Retry</button>{/if}</div>
          {/if}
        {:else}
          {#if !loading && !cursor && !childError}
            <div class="job-detail-empty"><strong>No child jobs</strong></div>
          {/if}
        {/each}
      </div>
      {#if payload?.kind === 'album' ? retrievalCursor : cursor}
        <button type="button" class="job-child-load-more"
          disabled={payload?.kind === 'album' ? retrievalMoreRequested : moreChildrenRequested}
          onclick={() => void showMoreChildren()}>Load more</button>
      {/if}
    </section>
  {/if}
</section>
