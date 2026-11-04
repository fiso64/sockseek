<script lang="ts">
  import type { JobsTab } from './job-list.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { onMount, untrack, tick } from 'svelte';
  import type { JobSummaryDto, Schema } from '../../api/types';
  import { daemonApi } from '../../api/client';
  import type { DaemonLiveClient } from '../../api/live';
  import { updatedJob } from '../../api/state.svelte';
  import { jobStateSummary } from '../../api/job-summary';
  import { formatCount } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import { jobStatusLabel as status, jobStatusClass as statusClass } from './status';
  import { jobKindIcon, jobKindLabel, searchJobMode } from './kind';
  import { searchModeLabel } from '../search/model';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import { canArchiveRoot } from './archive';
  import { searchJobStats } from './search-stats';
  import { automaticJobListStats } from './list-stats';
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
    onarchive: (job: JobSummaryDto) => void;
    archivingSubmissions?: ReadonlySet<string>;
    onloadmore?: () => void;
    onnew: () => void;
  }

  let { jobs, tab, submissionId, onclearfilter, ontabchange, position, refreshRevision = 0, connected, observeJobs, loading = false, daemonOffline = false, hasMore = false, error = null, actionError = null, onretry, onopen, oncancel, onarchive, archivingSubmissions = new Set<string>(), onloadmore, onnew }: Props = $props();
  let section: HTMLElement;
  onMount(() => {
    const scroller = section.closest('main')!;
    scroller.scrollTop = position.scrollTop;
    const remember = () => { position.scrollTop = scroller.scrollTop; };
    scroller.addEventListener('scroll', remember);
    return () => scroller.removeEventListener('scroll', remember);
  });
  function tabKey(event: KeyboardEvent): void {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const next = event.key === 'Home' ? 'searches' : event.key === 'End' ? 'downloads'
      : tab === 'searches' ? 'downloads' : 'searches';
    ontabchange(next);
    // This list is remounted on tab changes; focus its replacement after rendering.
    void tick().then(() => document.getElementById(`jobs-tab-${next}`)?.focus());
  }
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

  function importContext(entry: PresentedJob): string {
    return entry.root.sourceDetail ?? 'Import';
  }

  function title(job: JobSummaryDto): string {
    return job.itemName ?? job.queryText ?? `${jobKindLabel(job.kind)} #${job.displayId ?? '?'}`;
  }

</script>

<section bind:this={section} class="page page-search redesigned-search-page">
  <header class="page-heading search-list-heading jobs-list-heading">
    <div><p class="eyebrow">Work</p><h1>Jobs</h1></div>
    <button type="button" class="new-job-button" onclick={onnew}><span>+</span> New job</button>
  </header>

  <div class="jobs-tabs" role="tablist" aria-label="Jobs">
    {#each [{ id: 'searches', label: 'Searches' }, { id: 'downloads', label: 'Download jobs' }] as item}
      <button type="button" role="tab" id={`jobs-tab-${item.id}`} aria-selected={tab === item.id}
        aria-controls="jobs-tab-panel" tabindex={tab === item.id ? 0 : -1} class:active={tab === item.id}
        onkeydown={tabKey} onclick={() => ontabchange(item.id as JobsTab)}>{item.label}</button>
    {/each}
  </div>
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
        {@const unavailable = Boolean(entry.missingResultId || entry.error)}
        {@const entryError = presentationError(entry)}
        {@const searchStats = job.kind === 'search' ? searchJobStats(job) : []}
        {@const automaticStats = job.kind === 'search' ? [] : automaticJobListStats(job)}
        {@const searchMode = searchJobMode(job)}
        {@const statusLabel = unavailable ? entryError ? 'Result unavailable' : 'Loading result' : status(job)}
        <div class="search-history-row" class:automatic-history-row={job.kind !== 'search'}>
          <button type="button" class="search-history-open" class:automatic-history-open={job.kind !== 'search'} onclick={() => onopen(job)}>
            <span class="search-history-query">{title(entry.root)}</span>
            {#if statusLabel !== 'Complete'}
              <span class={`search-status-badge ${unavailable ? 'pending' : statusClass(job)}`}><i></i>{statusLabel}</span>
            {/if}
            <span class="search-history-context">
              <span class={`job-type-badge ${entry.root.kind === 'extract' ? 'import' : job.kind === 'search' ? 'search' : 'automatic'}`}><Icon name={searchMode ?? jobKindIcon(job.kind)} /><span>{entry.root.kind === 'extract' ? importContext(entry) : searchMode ? searchModeLabel(searchMode) : jobKindLabel(job.kind)}</span></span>
              {#if entry.root.createdAtUtc}<span class="stat-separator">·</span><RelativeTime timestamp={entry.root.createdAtUtc} />{/if}
            </span>
            {#if searchStats.length || automaticStats.length}
              <span class="search-history-stats">
                {#each searchStats as stat, index}
                  {#if index}<span class="stat-separator">·</span>{/if}
                  <span><strong>{formatCount(stat.count)}</strong> {stat.label}</span>
                {/each}
                {#each automaticStats as stat, index}
                  {#if index}<span class="stat-separator">·</span>{/if}
                  <span>{stat}</span>
                {/each}
              </span>
            {/if}
          </button>
          {#if !unavailable && job.availableActions?.some((action) => action.kind === 'cancel')}
            <button type="button" class="search-history-remove" aria-label={`Cancel ${title(entry.root)}`} title="Cancel" onclick={() => oncancel(job)}><svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6 6l8 8M14 6l-8 8" /></svg></button>
          {:else if !unavailable && job.lifecycleState === 'Terminal' && canArchiveRoot(entry.root)}
            <button type="button" class="search-history-remove" disabled={archivingSubmissions.has(job.submissionId!)} aria-label={`Remove ${title(entry.root)}`} title="Remove submission from history (reversible archive)" onclick={() => onarchive(job)}><svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6 6l8 8M14 6l-8 8" /></svg></button>
          {:else if job.kind !== 'search'}
            <span class="automatic-history-action-space" aria-hidden="true"></span>
          {/if}
        </div>
        {#if entryError}<div class="resource-state-notice unavailable" role="alert"><span>Import unavailable: {entryError.message}</span><button type="button" onclick={() => retry(entry)}>Retry</button></div>{/if}
      {/each}
    </div>
    {#if hasMore && onloadmore}<LoadMoreButton loadingLabel="Loading jobs…" {loading} onclick={onloadmore} />{/if}
  {/if}
  </div>
</section>
