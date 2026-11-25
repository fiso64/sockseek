<script lang="ts">
  import JobRow from './JobRow.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import EmptyState from '../../components/EmptyState.svelte';
  import { untrack } from 'svelte';
  import { SvelteMap } from 'svelte/reactivity';
  import TransferStateLabel from '../../components/items/TransferStateLabel.svelte';
  import SplitButton from '../../components/SplitButton.svelte';
  import { newJobStartFromRerun, type NewJobStart } from './submission';
  import TransferItemActionButton from '../../components/items/TransferItemActionButton.svelte';
  import { audioAttributes, basename, type TransferPresentation } from '../presentation/items';
  import { daemonApi, defaultPageSize, nestedPageSize } from '../../api/client';
  import { DaemonApiError } from '../../api/http';
  import type { JobSummaryDto, Schema } from '../../api/types';
  import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
  import { JobListController, childJobsQuery } from './job-list.svelte';
  import { TransferRows, updatedTransfer, updatedJob } from '../../api/state.svelte';
    import { liveTransferPresentation } from '../transfers/model';
  import { selectedJobFile, jobFileTransfer } from './selected-file';
  import { directoryManifestFileRow, directoryKnownSize } from './directory-files';
  import { canReuseJobDetail, jobDetailVersion, startJobDetailRefresh, loadJobAncestors, readJobSummaries, type JobSummaryRead } from './detail-reads';
  import { completedExtract, presentedChildren, resolvePresentedJob } from './presentation';
  import { jobDetailInfo } from './detail-info';
  import { jobStatusLabel, jobStatusClass, jobTransferTone } from './status';
  import { jobKindLabel } from './kind';
  import { canDeleteSubmission } from './deletion';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import { formatCount, humanizeStateValue } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import { searchDraftFromJob, type SearchDraft } from '../search/model';
  import FileItemCard from '../../components/items/FileItemCard.svelte';
  import FolderItemCard from '../../components/items/FolderItemCard.svelte';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import type { UserLinkActions } from '../../app/navigation';

  let { jobId, initialDetail = null, userActions, connected, liveStatus, visible, observeJobs, observeJobList, onback, onopenjob, oneditagain, onsearch, ondelete, deletingSubmissions = new Set<string>(), deleteError = null }: {
    jobId: string;
    initialDetail?: Schema<'JobDetailDto'> | null;
    userActions: UserLinkActions;
    connected: boolean;
    liveStatus: LiveConnectionStatus;
    visible: boolean;
    observeJobs: DaemonLiveClient['observeJobs'];
    observeJobList: DaemonLiveClient['observeJobList'];
    onback: () => void;
    onopenjob: (job: JobSummaryDto | string, detail?: Schema<'JobDetailDto'>) => void;
    oneditagain: (start: NewJobStart) => void;
    onsearch: (draft: SearchDraft) => void;
    ondelete: (job: JobSummaryDto) => void;
    deletingSubmissions?: ReadonlySet<string>;
    deleteError?: Error | null;
  } = $props();
  let detail = $state.raw<Schema<'JobDetailDto'> | null>(null);
  let observedSource = $state<{ jobId: string; sourceId: string | null; available: boolean } | null>(null);
  let sourceRead = $state.raw<JobSummaryRead | null>(null);
  let sourceRetry = $state(0);
  let job = $derived(detail?.summary);
  let sourceJobAvailable = $derived(observedSource?.jobId === jobId && observedSource.sourceId === job?.relationships.sourceJobId
    ? observedSource.available : detail?.sourceJobAvailable === true);
  // Extracted results already navigate through their source during route resolution.
  // Only independently submitted follow-ups expose provenance as a related job.
  let sourceJobId = $derived(job?.jobId === jobId && sourceJobAvailable
    && job.display.role === 'user-root' && !job.relationships.parentJobId ? job.relationships.sourceJobId : null);
  let payload = $derived(detail?.payload);
  let ancestors = $state<JobSummaryDto[]>([]);
  let parent = $derived(ancestors.at(-1) ?? null);
  let parentError = $state<Error | null>(null);
  // Related jobs use the same live list scope as the Jobs page: an album lists
  // its folder retrievals, grouped downloads their generated members, and other
  // collections their direct children. Folder files come from the manifest.
  // undefined: not known yet; null: no related list; 'all': every direct child.
  let relatedKind = $derived<JobSummaryDto['display']['kind'] | 'all' | null | undefined>(
    detail?.summary.jobId !== jobId ? undefined
    : payload?.kind === 'remote-directory' ? null
    : payload?.kind === 'album' ? 'retrieve-folder'
    : payload?.kind === 'song-groups' ? 'song' : payload?.kind === 'album-groups' ? 'album' : 'all');
  let relatedList = $state<JobListController | null>(null);
  $effect(() => {
    const id = jobId;
    const kind = relatedKind;
    if (kind === undefined) return;
    const list = kind === null ? null
      : new JobListController(daemonApi, observeJobList, () => childJobsQuery(id, kind === 'all' ? null : kind));
    relatedList = list;
    return () => list?.stop();
  });
  $effect(() => { relatedList?.setActive(visible, liveStatus); });
  let children = $derived(relatedList?.items ?? []);
  const observedJobs = new SvelteMap<string, JobSummaryDto>();
  const noObservedJobs: ReadonlyMap<string, JobSummaryDto> = new Map();
  let liveJobsById = $derived(connected ? observedJobs : noObservedJobs);
  let observedChildCount = $state<number | null>(null);
  let liveRoot = $derived(liveJobsById.get(jobId));
  // A parent can enter RunningChildren before its first displayed children are
  // registered. Observe their arrival, not progress or every undisplayed child.
  let liveChildExtent = $derived(Math.min(observedChildCount ?? 0, nestedPageSize));
  let liveDetailVersion = $derived(`${liveChildExtent}:${jobDetailVersion(liveRoot ?? undefined)}`);
  let observedDetailVersion = '';
  let invalidateDetail: () => void = () => {};
  let visibleChildren = $derived(children.map((child) => {
    const live = liveJobsById.get(child.jobId!);
    return live ?? child;
  }));
  let childRecords = $derived.by(() => {
    const known = new Map<string, JobSummaryDto>(relatedList?.linkedResults ?? []);
    for (const child of visibleChildren) if (child.jobId) known.set(child.jobId, child);
    for (const id of known.keys()) {
      const live = liveJobsById.get(id);
      if (live) known.set(id, live);
    }
    return known;
  });
  let relatedRows = $derived(presentedChildren(visibleChildren, childRecords));
  let error = $state<Error | null>(null);
  let actionError = $state<Error | null>(null);
  let manifest = $state.raw<Schema<'DirectoryManifestPageDto'> | null>(null);
  let manifestEntries = $state.raw<Schema<'DirectoryManifestEntryDto'>[]>([]);
  let manifestNext = $state<number | null>(null);
  let manifestError = $state<Error | null>(null);
  let manifestMissing = $state(false);
  let manifestLoading = $state(false);
  let manifestCheckedAt = 0;
  let pendingManifestRefresh: { attempt?: number } | null = null;
  const scopedTransfers = new TransferRows();
  let transferError = $state<Error | null>(null);
  let transferRetry = $state(0);
  let liveTransfers = $derived([...scopedTransfers.values()]);
  let observedJobIds = $derived([...new Set([jobId,
    ...(sourceJobId ? [sourceJobId] : []),
    ...children.map(child => child.jobId).filter((id): id is string => Boolean(id)),
    ...relatedRows.map(row => row.job.jobId).filter((id): id is string => Boolean(id)),
    ...manifestEntries.map(entry => entry.childJobId).filter((id): id is string => Boolean(id)),
  ])].sort().join(','));
  $effect(() => {
    transferRetry;
    transferError = null;
    if (!connected) return;
    const ids = observedJobIds.split(',');
    const requestedIds = new Set(ids);
    const rootId = jobId;
    untrack(() => scopedTransfers.clear());
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
  let lifetime: AbortController;
  let sourceJob = $derived(sourceJobId && liveJobsById.has(sourceJobId)
    ? liveJobsById.get(sourceJobId)!
    : sourceRead?.summary?.jobId === sourceJobId ? sourceRead?.summary ?? null : null);
  $effect(() => {
    const id = sourceJobId;
    sourceRetry;
    sourceRead = null;
    if (!id) return;
    const request = new AbortController();
    void readJobSummaries([id], (ids, signal) => daemonApi.getJobSummaries(ids, signal), request.signal)
      .then(reads => { if (!request.signal.aborted) sourceRead = reads.get(id) ?? null; });
    return () => request.abort();
  });
  let parentJobId = $derived(job?.relationships.parentJobId);
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
  let parentTitle = $derived(parent?.display.itemName ?? parent?.display.queryText ?? 'parent job');
  // A song's album is context when no album job already supplies that breadcrumb.
  let albumContext = $derived(payload?.kind === 'song' && payload.query.album
    && !ancestors.some(ancestor => ancestor.display.kind === 'album'
      || (ancestor.display.itemName ?? ancestor.display.queryText) === payload.query.album)
    ? payload.query.album : null);
  let relatedTotal = $derived.by(() => {
    const complete = relatedList && !relatedList.loading && !relatedList.cursor && !relatedList.error;
    if (payload?.kind === 'album') return complete ? relatedRows.length : null;
    const declared = payload?.kind === 'job-list' ? payload.count
      : payload?.kind === 'song-groups' ? payload.songCount
      : payload?.kind === 'album-groups' ? payload.resultCount
      : null;
    if (declared != null && (Number(declared) > 0 || !Number(detail?.childCount))) return Number(declared);
    return complete ? relatedRows.length : null;
  });
  let showRelatedChildren = $derived(Boolean(detail) && (
    payload?.kind === 'job-list' || payload?.kind === 'song-groups' || payload?.kind === 'album-groups'
    // Album Song children are file members in the folder card; only real
    // Retrieve Folder operations belong in its separate related-jobs list.
    || payload?.kind === 'album' && relatedRows.length > 0
    || payload?.kind === 'extract' && (Number(detail?.childCount) > 0 || children.length > 0)
  ));
  let showRelatedJobs = $derived(showRelatedChildren && (relatedTotal !== 0 || Boolean(relatedList?.loading) || Boolean(relatedList?.error)) || Boolean(sourceJobId));
  let displayedRelatedRows = $derived(showRelatedChildren ? relatedRows : []);
  // A lone source is named for what it is: the same kind of job is the attempt this one repeats.
  let onlySource = $derived(Boolean(sourceJobId) && !displayedRelatedRows.length);
  let displayedRelatedTotal = $derived((showRelatedChildren ? relatedTotal ?? relatedRows.length : 0)
    + (sourceJobId ? 1 : 0));
  let planStateLabel = $derived(!manifest && manifestMissing && job?.lifecycle.lifecycleState === 'Terminal'
    ? 'The accepted file plan is not available for this job.' : undefined);
  let activeFileTransfer = $derived(liveTransfers.find((transfer) => transfer.identity.jobId === jobId && !transfer.status.isTerminal));
  let selectedFile = $derived(selectedJobFile(payload, activeFileTransfer));
  let repeatOptions = $derived(detail?.repeatOptions);

  let retryId = $derived(repeatOptions?.retryTransferId);
  let missingFiles = $derived(repeatOptions?.canRunAgain ? 0 : Number(repeatOptions?.missingFiles ?? 0));
  let retryBusy = $state(false);

  async function editAgain() {
    const owner = jobId;
    retryBusy = true;
    try {
      const start = newJobStartFromRerun(await daemonApi.getRerunDraft(owner));
      if (jobId === owner) oneditagain(start);
    } catch (cause) {
      if (jobId === owner) actionError = new Error(String(cause));
    } finally { retryBusy = false; }
  }

  // A job that found nothing usable can be looked into: the same search with
  // the same conditions, which the search page shows and lets you remove.
  let canSearchAgain = $derived(job?.lifecycle.failureReason === 'NoMatchingResults' || job?.lifecycle.failureReason === 'NoSearchResults');
  let searchAgainBusy = $state(false);
  async function searchAgain() {
    const owner = jobId;
    searchAgainBusy = true;
    try {
      const draft = searchDraftFromJob(await daemonApi.getSearchDraft(owner));
      if (jobId === owner) onsearch(draft);
    } catch (cause) {
      if (jobId === owner) actionError = new Error(String(cause));
    } finally { searchAgainBusy = false; }
  }

  async function repeat(submit: () => Promise<JobSummaryDto>) {
    const owner = jobId;
    retryBusy = true;
    try {
      const next = await submit();
      if (jobId === owner) onopenjob(next);
    } catch (cause) {
      if (jobId === owner) actionError = new Error(String(cause));
    } finally { retryBusy = false; }
  }

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
      ? liveJobsById.get(entry.childJobId)! : entry.child,
    live: entry.childJobId ? liveTransfersByJob.get(entry.childJobId) : undefined,
    ownerTerminal: job?.lifecycle.lifecycleState === 'Terminal',
  })));
  let folderTransfer = $derived.by((): TransferPresentation | undefined => {
    if (payload?.kind !== 'album' && payload?.kind !== 'remote-directory') return undefined;
    const total = Number(payload.directory.fileCount);
    const successful = Number(payload.directory.successfulFileCount);
    return {
      job: job ?? undefined, directory: payload.directory,
      state: job ? jobStatusLabel(job) : humanizeStateValue(payload.directory.phase),
      tone: job?.lifecycle.lifecycleState !== 'Terminal' ? 'active' : jobTransferTone(job),
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
  let skippedOutput = $derived(job?.lifecycle.terminalOutcome !== 'Skipped' ? null
    : payload?.kind === 'song' ? payload.file : payload?.kind === 'album' ? payload.directory : null);
  let title = $derived(job?.display.itemName ?? job?.display.queryText ?? jobKindLabel(job?.display.kind));
  let hasContent = $derived(Boolean(selectedFile) || payload?.kind === 'remote-directory'
    || payload?.kind === 'album' && !skippedOutput && Boolean(payload.resolvedFolderPath || manifest));
  let info = $derived(job ? jobDetailInfo(job, payload, hasContent) : null);
  let detailTransfer = $derived(selectedFile ? jobFileTransfer(job, selectedFile.file, selectedFile.target, activeFileTransfer, payload?.kind === 'song' ? payload.fallback : null)
    : folderTransfer ?? jobFileTransfer(job, payload?.kind === 'song' ? payload.file : null));

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
    let firstDetail = initialDetail?.summary.jobId === id ? initialDetail : null;
    // Display the route's captured detail immediately. The scoped initial state
    // reconciles it once; missing global child rows are not evidence it is stale.
    const request = new AbortController();
    lifetime = request;
    detail = null;
    error = null;
    actionError = null;
    manifest = null;
    manifestEntries = [];
    manifestNext = null;
    manifestError = null;
    manifestMissing = false;
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
        const lifecycleChanged = detail?.summary.lifecycle.lifecycleState !== next.summary.lifecycle.lifecycleState;
        const childrenChanged = detail?.childCount !== next.childCount || lifecycleChanged;
        const outcomesChanged = childOutcomes(detail?.payload) !== childOutcomes(next.payload);
        detail = next;
        error = null;
        // The live related list follows membership and outcomes itself. Without
        // a connection it holds one snapshot; refresh it when the job changed.
        if (!connected && detail && (childrenChanged || outcomesChanged)) untrack(() => relatedList?.refresh());
        const directory = next.payload?.kind === 'album' || next.payload?.kind === 'remote-directory'
          ? next.payload.directory : null;
        const attempt = directory?.attemptNumber == null ? null : Number(directory.attemptNumber);
        if (directory && attempt === null) manifestMissing = next.summary.lifecycle.lifecycleState === 'Terminal';
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
          || fileStatesPending && next.summary.lifecycle.lifecycleState !== 'Terminal' && Date.now() - manifestCheckedAt >= 2_000)
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
      () => Boolean(error) || detail?.summary.lifecycle.lifecycleState !== 'Terminal',
      () => observedDetailVersion !== liveDetailVersion || Boolean(error));
    invalidateDetail = invalidate;
    return () => {
      request.abort();
      if (invalidateDetail === invalidate) invalidateDetail = () => {};
    };
  });

  function detailMatchesObservation(): boolean {
    return Boolean(detail && liveRoot && !error
      && canReuseJobDetail(detail.summary, liveRoot)
      && Number(detail.childCount) >= liveChildExtent);
  }

  $effect(() => {
    const version = liveDetailVersion;
    untrack(() => {
      // Initial/replayed observations need no read when HTTP already covers them.
      if (detailMatchesObservation()
        || !liveRoot && detail?.summary.lifecycle.lifecycleState === 'Terminal' && !error) {
        observedDetailVersion = version;
        return;
      }
      if (version !== observedDetailVersion) invalidateDetail();
    });
  });

  async function loadManifest(reset = false, request = lifetime, id = jobId, attempt?: number) {
    if (manifestLoading) {
      if (reset) pendingManifestRefresh = { attempt };
      return;
    }
    manifestLoading = true;
    manifestMissing = false;
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
        manifestMissing = true;
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

{#snippet folderRetry()}
  <TransferItemActionButton kind="retry-folder" {missingFiles}
    disabled={retryBusy} onclick={() => repeat(() => daemonApi.retryDirectory(jobId))} />
{/snippet}

<section class="page page-search redesigned-search-page automatic-job-detail">
  <header class="job-detail-heading">
    <button type="button" class="button quiet icon-button back-button" aria-label={job?.relationships.parentJobId ? `Back to ${parentTitle}` : 'Back to jobs'} onclick={() => job?.relationships.parentJobId ? onopenjob(parent ?? job.relationships.parentJobId) : onback()}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M12.5 4.5L7 10l5.5 5.5M7.5 10H16" /></svg>
    </button>
    <div class="job-detail-title">
      <h1>{detail ? title : 'Job'}</h1>
    </div>
    {#if job}
      <div class="job-detail-meta">
        {#each ancestors as ancestor (ancestor.jobId)}
          {@const name = ancestor.display.itemName ?? ancestor.display.queryText ?? 'Parent job'}
          <button type="button" title={name} onclick={() => onopenjob(ancestor)}>{name}</button><span aria-hidden="true">/</span>
        {/each}
        {#if albumContext}<span>{albumContext}</span><span aria-hidden="true">/</span>{/if}
        <span>{jobKindLabel(job.display.kind)}</span>
      </div>
    {/if}

    {#if job}
      {@const statusLabel = jobStatusLabel(job)}
      <div class="job-detail-heading-summary">
        {#if job.display.createdAtUtc}<span class="job-detail-header-fact job-detail-age"><RelativeTime timestamp={job.display.createdAtUtc} /></span>{/if}
        {#if !info && !hasContent}<TransferStateLabel transfer={detailTransfer} class={`search-status-badge ${jobStatusClass(job)}`}>{statusLabel}</TransferStateLabel>{/if}
        {#if repeatOptions?.canRunAgain}
          <!-- The main action reads the source as it is now when there is one.
               Replaying the saved items is the alternative, not the default. -->
          <SplitButton label="Run again" icon="refresh" testId="run-again" class="collapse-narrow" disabled={retryBusy}
            menuLabel="More ways to run again"
            onclick={() => repeat(() => daemonApi.runAgain(jobId, repeatOptions.canExtractAgain))}
            items={[
              ...(repeatOptions.canExtractAgain
                ? [{ id: 'exact', label: 'Run this exact list', onselect: () => void repeat(() => daemonApi.runAgain(jobId)) }] : []),
              ...(repeatOptions.canEditAgain ? [{ id: 'options', label: 'Run again with options…', onselect: () => void editAgain() }] : []),
            ]} />
        {/if}
        {#if job.lifecycle.availableActions?.some((action) => action.kind === 'cancel')}
          <button type="button" class="button danger action-button collapse-narrow" aria-label="Cancel" title="Cancel job" onclick={() => void cancel()}><Icon name="x" /><span>Cancel</span></button>
        {:else if canDeleteSubmission(job)}
          <button type="button" class="button danger action-button collapse-narrow" data-testid="delete-submission" aria-label="Delete submission" title="Delete submission from history" disabled={deletingSubmissions.has(job.display.submissionId!)} onclick={() => ondelete(job)}><Icon name="trash" /><span>Delete submission</span></button>
        {/if}
      </div>
    {/if}
  </header>
  {#if !detail && !error}<LoadingIndicator label="Loading job" blocking />{/if}
  {#if error}<div class="resource-state-notice unavailable" role="alert">{error.message}</div>{/if}
  {#if connected && transferError}<div class="resource-state-notice unavailable" role="alert" data-testid="job-live-progress-notice"><span>Live file progress unavailable: {transferError.message}</span><button class="button" type="button" onclick={() => transferRetry++}>Retry</button></div>{/if}
  {#if parentError}<div class="resource-state-notice unavailable" role="alert">Parent job unavailable: {parentError.message}</div>{/if}
  {#if relatedList?.error}<div class="resource-state-notice unavailable" role="alert"><span>{payload?.kind === 'album' ? 'Related jobs' : 'Child jobs'} unavailable: {relatedList.error.message}</span><button class="button" type="button" disabled={relatedList.loading} onclick={() => relatedList?.refresh()}>Retry</button></div>{/if}
  {#if manifestError}<div class="resource-state-notice unavailable" role="alert"><span>Directory files unavailable: {manifestError.message}</span><button class="button" type="button" disabled={manifestLoading} onclick={() => void loadManifest(true)}>Retry</button></div>{/if}
  {#if actionError}<div class="resource-state-notice unavailable" role="alert">{actionError.message}</div>{/if}
  {#if deleteError}<div class="resource-state-notice unavailable" role="alert"><strong>Job action failed</strong><span>{deleteError.message}</span></div>{/if}
  {#if info && job}
    <section class="panel job-info-card" class:warning={job.lifecycle.terminalOutcome === 'PartialSuccess'} class:failed={job.lifecycle.terminalOutcome === 'Failed'} data-testid="job-info-card" aria-label="Job information">
      {#if info.message || info.counts.length || !hasContent}
        <div class="job-info-heading">
          {#if info.message || info.counts.length}
            <div class="job-info-copy">
              {#if info.message}<strong>{info.message}</strong>{/if}
              {#if info.counts.length}<div class="job-info-counts">{#each info.counts as count}<span>{count}</span>{/each}</div>{/if}
            </div>
          {/if}
          {#if !hasContent}<TransferStateLabel transfer={detailTransfer} class={`search-status-badge ${jobStatusClass(job)}`}>{jobStatusLabel(job)}</TransferStateLabel>{/if}
        </div>
      {/if}
      {#if info.progress}
        <div class="progress-track job-info-progress" role="progressbar" aria-label="Jobs finished" aria-valuemin={0} aria-valuemax={info.progress.total} aria-valuenow={info.progress.completed}>
          <i style={`width:${info.progress.percent}%`}></i>
        </div>
      {/if}
      {#if info.facts.length}
        <dl class="job-info-facts">{#each info.facts as fact}<div><dt>{fact.label}</dt><dd>{#if fact.peer}<UsernameLink username={fact.value} actions={userActions} />{:else}{fact.value}{/if}</dd></div>{/each}</dl>
      {/if}
      {#if info.rejections.length}<ul class="rejection-list" data-testid="job-rejections">{#each info.rejections as line}<li>{line}</li>{/each}</ul>{/if}
      {#if canSearchAgain}
        <div class="job-info-actions">
          <button type="button" class="button action-button" data-testid="job-search-again" disabled={!connected || searchAgainBusy} onclick={searchAgain}
            title="Run this job's search with its conditions, and remove conditions there to see what they rejected">
            <Icon name="search" /><span>Search for this</span>
          </button>
        </div>
      {/if}
      {#if info.note}<p class="job-info-note">{info.note}</p>{/if}
    </section>
  {/if}
  {#if payload?.kind === 'extract' && payload.resultJobId && !showRelatedChildren}
    {@const resultId = payload.resultJobId}
    <button type="button" class="button job-child-load-more" onclick={() => onopenjob(resultId)}>Open extracted job</button>
  {/if}
  {#if selectedFile}
    {@const target = selectedFile.target}
    {@const file = selectedFile.file}
    <section class="job-content-section">
      <div class="job-source-heading">
        <div class="job-source-summary">
          <span class="job-source-identity">From: {#if target}<UsernameLink username={target.username} actions={userActions} />{:else}{selectedFile.sourceName}{/if}</span>
          {#if target && payload?.kind === 'song'}
            {#if selectedPeerUploadSpeed != null}<span>{(Number(selectedPeerUploadSpeed) / 1_000_000).toFixed(1)} MB/s</span>{/if}
            {#if selectedPeerHasFreeUploadSlot != null}<span title="Slot availability when the peer answered the search" class:available={selectedPeerHasFreeUploadSlot}>{selectedPeerHasFreeUploadSlot ? 'Free slot' : 'No free slot'}</span>{/if}
          {/if}
        </div>
      </div>
      {#snippet fileRetry()}
        <TransferItemActionButton kind="retry" label={`Retry ${basename(selectedFile.path)}`} disabled={retryBusy} onclick={() => repeat(() => daemonApi.retryTransfer(retryId!))} />
      {/snippet}
      <FileItemCard path={selectedFile.path} sizeBytes={file.fileSize == null && target?.size == null ? null : Number(file.fileSize ?? target?.size)}
        audio={audioAttributes(target)}
        transfer={detailTransfer} retry={retryId ? fileRetry : undefined} />
    </section>
  {:else if payload?.kind === 'album' && !skippedOutput}
    {#if payload.resolvedFolderPath || manifest}
      <section class="job-content-section">
        {#if payload.resolvedFolderUsername}<div class="job-source-heading"><div class="job-source-summary"><span class="job-source-identity">From: <UsernameLink username={payload.resolvedFolderUsername} actions={userActions} /></span></div></div>{/if}
        <FolderItemCard path={(payload.resolvedFolderPath ?? manifest?.displayRoot) || 'Selected files'} sizeBytes={directoryKnownSize(payload.directory)} files={folderFiles} totalFileCount={folderFileCount} filesComplete={manifest != null && !moreFolderFiles} dataStateLabel={planStateLabel} transfer={folderTransfer} retry={missingFiles > 0 ? folderRetry : undefined} />
        {#if moreFolderFiles}<button type="button" class="button job-child-load-more" disabled={manifestLoading} onclick={() => void loadMoreFiles()}>Load more</button>{/if}
      </section>
    {/if}
  {:else if payload?.kind === 'remote-directory'}
    <section class="job-content-section">
      {#if payload.sourceUsername}<div class="job-source-heading"><div class="job-source-summary"><span class="job-source-identity">From: <UsernameLink username={payload.sourceUsername} actions={userActions} /></span></div></div>{/if}
      <FolderItemCard path={(payload.sourceFolderPath ?? manifest?.displayRoot ?? payload.displayRoot) || 'Selected files'} sizeBytes={directoryKnownSize(payload.directory)} files={folderFiles} totalFileCount={folderFileCount} filesComplete={manifest != null && !moreFolderFiles} dataStateLabel={planStateLabel} transfer={folderTransfer} retry={missingFiles > 0 ? folderRetry : undefined} />
      {#if moreFolderFiles}<button type="button" class="button job-child-load-more" disabled={manifestLoading} onclick={() => void loadMoreFiles()}>Load more</button>{/if}
    </section>
  {/if}
  {#if showRelatedJobs}
    <section class="job-related-section">
      <header class="job-section-heading"><h2>{onlySource ? sourceJob?.display.kind === job?.display.kind ? 'Earlier attempt' : 'Started from' : sourceJobId ? 'Related jobs' : payload?.kind === 'extract' ? 'Result' : payload?.kind === 'song-groups' ? 'Generated songs' : payload?.kind === 'album-groups' ? 'Generated albums' : payload?.kind === 'album' ? 'Related jobs' : 'Jobs'}</h2>
        {#if !onlySource && (!info?.counts.length || sourceJobId)}<span>{formatCount(displayedRelatedTotal)} {showRelatedChildren && relatedTotal == null ? 'loaded ' : ''}{displayedRelatedTotal === 1 ? 'job' : 'jobs'}</span>{/if}
      </header>
      <div class="job-child-list">
        {#if sourceJobId}
          {#if sourceJob}
            <JobRow job={sourceJob} onopen={onopenjob} />
          {:else if sourceRead}
            <div class="resource-state-notice unavailable" role="alert"><span>Source job unavailable: {sourceRead.error?.message ?? 'The source job was removed from history.'}</span><button class="button" type="button" onclick={() => sourceRetry++}>Retry</button></div>
          {:else}
            <LoadingIndicator label="Loading related job" />
          {/if}
        {/if}
        {#each displayedRelatedRows as entry (entry.job.jobId)}
          {@const child = entry.job}
          <JobRow job={child} root={entry.root} transfer={child.jobId ? liveTransfersByJob.get(child.jobId) : undefined}
            resultState={entry.error || entry.missingResultId ? 'unavailable' : undefined}
            progressPercent={child.jobId ? childFileProgress.get(child.jobId) : undefined}
            onopen={onopenjob} oncancel={job => void cancel(job.jobId)} />
          {#if entry.error || entry.missingResultId}
            <div class="resource-state-notice unavailable" role="alert"><span>Extracted result unavailable: {entry.error?.message ?? 'The linked job was removed from history.'}</span></div>
          {/if}
        {:else}
          {#if !sourceJobId && relatedList && !relatedList.loading && !relatedList.cursor && !relatedList.error}
            <EmptyState class="inline-empty-state" icon="jobs" title="No child jobs" />
          {/if}
        {/each}
      </div>
      {#if relatedList?.cursor}
        <button type="button" class="button job-child-load-more"
          disabled={relatedList.loading} onclick={() => relatedList?.loadMore()}>Load more</button>
      {/if}
    </section>
  {/if}
</section>
