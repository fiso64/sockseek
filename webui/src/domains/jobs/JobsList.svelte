<script lang="ts">
  import JobRow from './JobRow.svelte';
  import PageTabs from '../../components/PageTabs.svelte';
  import { keyboardList } from '../../lib/keyboard';
  import type { JobsTab } from './job-list.svelte';
  import { downloadsViews, type DownloadsView } from '../../app/navigation';
  import EmptyState from '../../components/EmptyState.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { onMount, untrack, tick } from 'svelte';
  import type { JobSummaryDto, Schema, TransferStateDto } from '../../api/types';
  import type { DaemonLiveClient } from '../../api/live';
  import { updatedJob } from '../../api/state.svelte';
    import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import { presentedJobs, sortPresentedJobsNewestFirst, type PresentedJob } from './presentation';

  interface Props {
    jobs: JobSummaryDto[];
    /** Result jobs of displayed extractions, provided with the list. */
    linkedResults: ReadonlyMap<string, JobSummaryDto>;
    tab: JobsTab;
    submissionId: string | null;
    onclearfilter: () => void;
    ondownloadsview: (view: DownloadsView) => void;
    position: { scrollTop: number };
    connected: boolean;
    observeJobs: DaemonLiveClient['observeJobs'];
    readJobTransfer: (jobId: string) => TransferStateDto | undefined;
    loading?: boolean;
    daemonOffline?: boolean;
    hasMore?: boolean;
    error?: Error | null;
    actionError?: Error | null;
    onretry: () => void;
    onopen: (job: JobSummaryDto, detail?: Schema<'JobDetailDto'>) => void;
    oncancel: (job: JobSummaryDto) => void;
    ondelete: (job: JobSummaryDto) => void;
    deletingSubmissions?: ReadonlySet<string>;
    onloadmore?: () => void;
    onnew: () => void;
  }

  let { jobs, linkedResults, tab, submissionId, onclearfilter, ondownloadsview, position, connected, observeJobs, readJobTransfer, loading = false, daemonOffline = false, hasMore = false, error = null, actionError = null, onretry, onopen, oncancel, ondelete, deletingSubmissions = new Set<string>(), onloadmore, onnew }: Props = $props();
  let section: HTMLElement;
  onMount(() => {
    const scroller = section.closest('main')!;
    scroller.scrollTop = position.scrollTop;
    const remember = () => { position.scrollTop = scroller.scrollTop; };
    scroller.addEventListener('scroll', remember);
    return () => scroller.removeEventListener('scroll', remember);
  });
  // Live updates for still-running results; the list provides their first value.
  let liveResults = $state(new Map<string, JobSummaryDto>());
  let known = $derived.by(() => {
    const summaries = new Map<string, JobSummaryDto>(linkedResults);
    for (const [id, summary] of liveResults) summaries.set(id, summary);
    for (const job of jobs) if (job.jobId) summaries.set(job.jobId, job);
    return summaries;
  });
  let ordered = $derived(sortPresentedJobsNewestFirst(presentedJobs(jobs, known)));

  // Extract roots and their executable results are distinct records. Observe
  // only the active linked summaries being displayed; the server handles their
  // live/history handoff just as it does for an open job detail.
  const activeResultIds = $derived(ordered.filter(entry => !entry.missingResultId
    && entry.job.jobId !== entry.root.jobId && entry.job.lifecycle.lifecycleState !== 'Terminal')
    .map(entry => entry.job.jobId!).sort().join(','));
  $effect(() => {
    const ids = activeResultIds ? activeResultIds.split(',') : [];
    if (!connected || !ids.length) return;
    return untrack(() => {
      const rows = new Map<string, Schema<'JobSummaryDto'>>();
      // A failed subscription only stops live progress; the listed values remain.
      return observeJobs(ids[0]!, ids, update => {
        const next = new Map(liveResults);
        for (const delta of update.state.jobs) {
          const row = updatedJob(rows.get(delta.jobId), delta);
          if (!row) continue;
          rows.set(row.jobId, row);
          next.set(row.jobId, row);
        }
        liveResults = next;
      }, () => {});
    });
  });
  // The list links every existing result, so a missing one was removed from history.
  function presentationError(entry: PresentedJob): Error | null {
    return entry.error ?? (entry.missingResultId ? new Error('The imported job was removed from history.') : null);
  }

</script>

<section bind:this={section} class="page page-search redesigned-search-page" use:keyboardList={{
  scope: `${tab}:${submissionId}`, revision: [ordered, loading],
  horizontal: direction => {
    if (tab !== 'downloads') return;
    if (direction > 0) ondownloadsview('files');
    void tick().then(() => document.getElementById(`downloads-view-${direction > 0 ? 'files' : 'requests'}`)?.focus());
  },
  loadPage: onloadmore,
}}>
  <header class="page-heading search-list-heading jobs-list-heading">
    <div class="page-heading-title">
      <h1>{tab === 'downloads' ? 'Downloads' : 'Searches'}</h1>
      {#if tab === 'downloads'}
        <div class="page-heading-tabs">
          <PageTabs variant="segmented" id="downloads-view" label="Downloads" value="requests" onchange={ondownloadsview} items={downloadsViews} />
        </div>
      {/if}
    </div>
    <button type="button" data-testid="new-job" class="button action-button new-job-button" onclick={onnew}><span>+</span> New job</button>
  </header>

  {#if submissionId}<div class="jobs-filter">Showing one download request <button type="button" onclick={onclearfilter}>Show all requests</button></div>{/if}
  <div role={tab === 'downloads' ? 'tabpanel' : undefined} id="jobs-tab-panel" aria-labelledby={tab === 'downloads' ? 'downloads-view-requests' : undefined}>
  {#if error && ordered.length}
    <div class="resource-state-notice unavailable" role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Jobs unavailable</strong><small>{error.message}</small></span><button class="button" type="button" disabled={loading} onclick={onretry}>Retry</button></div>
  {/if}
  {#if actionError}<div class="resource-state-notice unavailable" role="alert"><strong>Job action failed</strong><span>{actionError.message}</span></div>{/if}
  {#if loading && !ordered.length}
    <LoadingIndicator label="Loading jobs" blocking />
  {:else if error && !ordered.length}
    <div class={`resource-state-notice ${daemonOffline ? 'offline' : 'unavailable'} blocking`} role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>{daemonOffline ? 'Daemon unavailable' : 'Jobs unavailable'}</strong><small>{daemonOffline ? 'This can’t be loaded while the daemon is offline.' : error.message}</small></span>{#if !daemonOffline}<button class="button" type="button" onclick={onretry}>Retry</button>{/if}</div>
  {:else if !ordered.length && !error && !hasMore}
    <EmptyState icon={tab === 'searches' ? 'search' : 'jobs'} title={tab === 'searches' ? 'No searches yet' : 'No download jobs yet'} />
  {:else}
    <div class="panel inset search-history-list">
      {#each ordered as entry (entry.root.jobId)}
        {@const job = entry.job}
        {@const entryError = presentationError(entry)}
        <JobRow {job} root={entry.root} transfer={job.jobId ? readJobTransfer(job.jobId) : undefined}
          resultState={entryError ? 'unavailable' : undefined}
          deleting={deletingSubmissions.has(job.display.submissionId!)}
          {onopen} {oncancel} {ondelete} />
        {#if entryError}<div class="resource-state-notice unavailable" role="alert"><span>Import unavailable: {entryError.message}</span><button class="button" type="button" onclick={onretry}>Retry</button></div>{/if}
      {/each}
    </div>
    {#if hasMore && onloadmore}<div data-keyboard-page="jobs" data-loading={loading} data-failed={Boolean(error)}><LoadMoreButton loadingLabel="Loading jobs…" {loading} onclick={onloadmore} /></div>{/if}
  {/if}
  </div>
</section>
