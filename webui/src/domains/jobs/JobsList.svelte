<script lang="ts">
  import JobRow from './JobRow.svelte';
  import PageTabs from '../../components/PageTabs.svelte';
  import { keyboardList } from '../../lib/keyboard';
  import type { JobsTab } from './job-list.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { onMount, untrack, tick } from 'svelte';
  import type { JobSummaryDto, Schema } from '../../api/types';
  import { daemonApi } from '../../api/client';
  import type { DaemonLiveClient } from '../../api/live';
  import { updatedJob } from '../../api/state.svelte';
  import { jobStateSummary } from '../../api/job-summary';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import { canDeleteRoot } from './deletion';
  import { presentedJobs, sortPresentedJobsNewestFirst, type PresentedJob } from './presentation';
  import { readJobSummaries, jobSummaryRetryIds, type JobSummaryRead } from './details';

  interface Props {
    jobs: JobSummaryDto[];
    tab: JobsTab;
    submissionId: string | null;
    onclearfilter: () => void;
    ontabchange: (tab: JobsTab) => void;
    position: { scrollTop: number };
    refreshRevision?: number;
    connected: boolean;
    observeJobs: DaemonLiveClient['observeJobs'];
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

  let { jobs, tab, submissionId, onclearfilter, ontabchange, position, refreshRevision = 0, connected, observeJobs, loading = false, daemonOffline = false, hasMore = false, error = null, actionError = null, onretry, onopen, oncancel, ondelete, deletingSubmissions = new Set<string>(), onloadmore, onnew }: Props = $props();
  let section: HTMLElement;
  onMount(() => {
    const scroller = section.closest('main')!;
    scroller.scrollTop = position.scrollTop;
    const remember = () => { position.scrollTop = scroller.scrollTop; };
    scroller.addEventListener('scroll', remember);
    return () => scroller.removeEventListener('scroll', remember);
  });
  const lifetime = new AbortController();
  onMount(() => () => lifetime.abort());
  let reads = $state(new Map<string, JobSummaryRead>());
  let readVersions = $state(new Map<string, string>());
  let reading = $state(false);
  let known = $derived.by(() => {
    const summaries = new Map<string, JobSummaryDto>();
    for (const [id, read] of reads) if (read.summary) summaries.set(id, read.summary);
    for (const job of jobs) if (job.jobId) summaries.set(job.jobId, job);
    return summaries;
  });
  let ordered = $derived(sortPresentedJobsNewestFirst(presentedJobs(jobs, known)));
  let neededReads = $derived.by(() => {
    const wanted = new Map<string, string>();
    const provided = new Set(jobs.map(job => job.jobId));
    for (const entry of ordered) {
      const id = entry.missingResultId ?? entry.job.jobId!;
      const cached = reads.get(id)?.summary;
      if (entry.missingResultId || !connected && cached && cached.lifecycleState !== 'Terminal'
        && !provided.has(id)) wanted.set(id, `linked-result:${refreshRevision}`);
    }
    return [...wanted].filter(([id, version]) => readVersions.get(id) !== version);
  });

  // Extract roots and their executable results are distinct records. Observe
  // only the active linked summaries being displayed; the server handles their
  // live/history handoff just as it does for an open job detail.
  const activeResultIds = $derived(ordered.filter(entry => !entry.missingResultId
    && entry.job.jobId !== entry.root.jobId && entry.job.lifecycleState !== 'Terminal')
    .map(entry => entry.job.jobId!).sort().join(','));
  $effect(() => {
    const ids = activeResultIds ? activeResultIds.split(',') : [];
    if (!connected || !ids.length) return;
    return untrack(() => {
      const rows = new Map<string, Schema<'JobStateDto'>>();
      return observeJobs(ids[0]!, ids, update => {
        const next = new Map(reads);
        for (const delta of update.state.jobs) {
          const row = updatedJob(rows.get(delta.jobId), delta);
          if (!row) continue;
          rows.set(row.jobId, row);
          next.set(row.jobId, { summary: jobStateSummary(row), error: null });
        }
        reads = next;
      }, cause => {
        const error = cause instanceof Error ? cause : new Error(String(cause));
        reads = new Map([...reads, ...ids.map(id => [id, { summary: null, error }] as const)]);
      });
    });
  });
  $effect(() => {
    const wanted = neededReads;
    if (reading || !wanted.length) return;
    reading = true;
    const versions = new Map(wanted);
    void readJobSummaries(wanted.map(([id]) => id), (ids, signal) => daemonApi.getJobSummaries(ids, signal), lifetime.signal)
      .then(batch => {
        if (lifetime.signal.aborted) return;
        reads = new Map([...reads, ...batch]);
        readVersions = new Map([...readVersions, ...versions]);
      }).finally(() => { if (!lifetime.signal.aborted) reading = false; });
  });

  function presentationError(entry: PresentedJob): Error | null {
    if (entry.error) return entry.error;
    if (entry.missingResultId && reads.has(entry.missingResultId)) return reads.get(entry.missingResultId)?.error
      ?? new Error('The extracted result is not available.');
    return null;
  }

  function retry(entry: PresentedJob): void {
    const versions = new Map(readVersions);
    for (const id of jobSummaryRetryIds(reads, entry.missingResultId ?? entry.root.jobId!)) versions.delete(id);
    readVersions = versions;
  }

</script>

<section bind:this={section} class="page page-search redesigned-search-page" use:keyboardList={{
  scope: `${tab}:${submissionId}`, revision: [ordered, loading],
  horizontal: direction => {
    const next = direction > 0 ? 'downloads' : 'searches';
    if (next !== tab) ontabchange(next);
    void tick().then(() => document.getElementById(`jobs-tab-${next}`)?.focus());
  },
  loadPage: onloadmore,
}}>
  <header class="page-heading search-list-heading jobs-list-heading">
    <div class="page-heading-title">
      <h1>Jobs</h1>
      <div class="page-heading-tabs">
        <PageTabs variant="segmented" id="jobs-tab" label="Jobs" value={tab} onchange={ontabchange}
          items={[{ id: 'searches', label: 'Searches' }, { id: 'downloads', label: 'Download jobs' }]} />
      </div>
    </div>
    <button type="button" class="new-job-button" onclick={onnew}><span>+</span> New job</button>
  </header>

  {#if submissionId}<div class="jobs-filter">Showing one download request <button type="button" onclick={onclearfilter}>Show all download jobs</button></div>{/if}
  <div role="tabpanel" id="jobs-tab-panel" aria-labelledby={`jobs-tab-${tab}`}>
  {#if error && ordered.length}
    <div class="resource-state-notice unavailable" role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Jobs unavailable</strong><small>{error.message}</small></span><button type="button" disabled={loading} onclick={onretry}>Retry</button></div>
  {/if}
  {#if actionError}<div class="resource-state-notice unavailable" role="alert"><strong>Job action failed</strong><span>{actionError.message}</span></div>{/if}
  {#if loading && !ordered.length}
    <LoadingIndicator label="Loading jobs" blocking />
  {:else if error && !ordered.length}
    <div class={`resource-state-notice ${daemonOffline ? 'offline' : 'unavailable'} blocking`} role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>{daemonOffline ? 'Daemon unavailable' : 'Jobs unavailable'}</strong><small>{daemonOffline ? 'This resource cannot be loaded while the daemon is offline.' : error.message}</small></span>{#if !daemonOffline}<button type="button" onclick={onretry}>Retry</button>{/if}</div>
  {:else if !ordered.length && !error && !hasMore}
    <div class="resource-state-notice empty blocking" role="status"><span class="resource-state-dot" aria-hidden="true"></span><span class="resource-state-copy"><strong>{tab === 'searches' ? 'No searches yet' : 'No download jobs yet'}</strong></span></div>
  {:else}
    <div class="search-history-list">
      {#each ordered as entry (entry.root.jobId)}
        {@const job = entry.job}
        {@const entryError = presentationError(entry)}
        <JobRow {job} root={entry.root}
          resultState={entryError ? 'unavailable' : entry.missingResultId ? 'loading' : undefined}
          deleting={deletingSubmissions.has(job.submissionId!)}
          {onopen} {oncancel} ondelete={canDeleteRoot(entry.root) ? ondelete : undefined} />
        {#if entryError}<div class="resource-state-notice unavailable" role="alert"><span>Import unavailable: {entryError.message}</span><button type="button" onclick={() => retry(entry)}>Retry</button></div>{/if}
      {/each}
    </div>
    {#if hasMore && onloadmore}<div data-keyboard-page="jobs" data-loading={loading} data-failed={Boolean(error)}><LoadMoreButton loadingLabel="Loading jobs…" {loading} onclick={onloadmore} /></div>{/if}
  {/if}
  </div>
</section>
