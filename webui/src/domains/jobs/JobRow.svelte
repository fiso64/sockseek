<script lang="ts">
  import type { JobSummaryDto } from '../../api/types';
  import { formatCount } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import { searchModeLabel } from '../search/model';
  import { canDeleteSubmission } from './deletion';
  import { jobKindIcon, jobKindLabel, searchJobMode } from './kind';
  import { automaticJobListStats, automaticJobProgressPercent } from './list-stats';
  import { searchJobStats } from './search-stats';
  import { jobStatusLabel, jobStatusClass } from './status';

  let { job, root, resultState, progressPercent, deleting = false, onopen, oncancel, ondelete }: {
    job: JobSummaryDto;
    root?: JobSummaryDto;
    resultState?: 'loading' | 'unavailable';
    progressPercent?: number;
    deleting?: boolean;
    onopen: (job: JobSummaryDto) => void;
    oncancel?: (job: JobSummaryDto) => void;
    ondelete?: (job: JobSummaryDto) => void;
  } = $props();

  const identity = $derived(root ?? job);
  const title = $derived(identity.itemName ?? identity.queryText ?? `${jobKindLabel(job.kind)} #${identity.displayId ?? '?'}`);
  const searchMode = $derived(searchJobMode(job));
  const typeLabel = $derived(identity.kind === 'extract' ? identity.sourceDetail ?? 'Import'
    : searchMode ? searchModeLabel(searchMode) : jobKindLabel(job.kind));
  const statusLabel = $derived(resultState === 'unavailable' ? 'Result unavailable'
    : resultState === 'loading' ? 'Loading result' : jobStatusLabel(job));
  const searchStats = $derived(job.kind === 'search' ? searchJobStats(job) : []);
  const stats = $derived(job.kind === 'search' || resultState ? [] : automaticJobListStats(job));
  const percent = $derived.by(() => {
    if (resultState || job.lifecycleState === 'Terminal') return undefined;
    const value = progressPercent ?? automaticJobProgressPercent(job);
    return value != null && Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : undefined;
  });
</script>

<div data-testid="job-row" data-job-kind={job.kind} data-keyboard-item={identity.jobId} tabindex="-1"
  class="search-history-row" class:automatic-history-row={job.kind !== 'search'}>
  <button type="button" data-testid="job-open" data-keyboard-focus class="search-history-open"
    class:automatic-history-open={job.kind !== 'search'} onclick={() => onopen(job)}>
    <span class="search-history-icon" role="img" aria-label={typeLabel} title={typeLabel}><Icon name={searchMode ?? jobKindIcon(job.kind)} /></span>
    <span class="search-history-query">{title}</span>
    {#if job.kind === 'search' && statusLabel !== 'Complete'}
      <span class={`status-label search-status-badge ${resultState ? 'pending' : jobStatusClass(job)}`}>{statusLabel}</span>
    {/if}
    {#if searchStats.length || stats.length}
      <span data-testid="job-stats" class="search-history-stats" class:search-history-counts={job.kind === 'search'} title={stats.length ? stats.join(' · ') : undefined}>
        {#each searchStats as stat}
          <span class:files-count={stat.label === 'file' || stat.label === 'files'} class:locked-count={stat.label === 'locked'} class:peers-count={stat.label === 'peer' || stat.label === 'peers'}><strong>{formatCount(stat.count)}</strong> {stat.label}</span>
        {/each}
        {#each stats as stat, index}
          {#if index}<span class="stat-separator">·</span>{/if}
          <span class="automatic-history-stat">{stat}</span>
        {/each}
      </span>
    {/if}
    {#if job.kind !== 'search'}
      <span class={`status-label search-status-badge ${resultState ? 'pending' : jobStatusClass(job)}`}>{statusLabel}</span>
    {/if}
  </button>
  {#if !resultState && oncancel && job.availableActions?.some(action => action.kind === 'cancel')}
    <button type="button" data-testid="job-cancel" class="search-history-remove" aria-label={`Cancel ${title}`} title="Cancel" onclick={() => oncancel?.(job)}><Icon name="x" /></button>
  {:else if !resultState && ondelete && canDeleteSubmission(job)}
    <button type="button" data-testid="job-remove" class="search-history-remove" disabled={deleting} aria-label={`Remove ${title}`} title="Remove submission from history" onclick={() => ondelete?.(job)}><Icon name="x" /></button>
  {:else if job.kind !== 'search'}
    <span class="automatic-history-action-space" aria-hidden="true"></span>
  {/if}
  {#if percent !== undefined}
    <span data-testid="job-progress" class="job-row-progress" role="progressbar" aria-label="Job progress"
      aria-valuenow={Math.round(percent)} aria-valuemin="0" aria-valuemax="100"><i style={`width:${percent}%`}></i></span>
  {/if}
</div>
