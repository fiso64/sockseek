<script lang="ts">
  import type { JobSummaryDto, TransferStateDto } from '../../api/types';
  import { formatCount } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import { searchModeLabel } from '../search/model';
  import { canDeleteSubmission } from './deletion';
  import { jobKindIcon, jobKindLabel, searchJobMode } from './kind';
  import { automaticJobListStats, automaticJobProgressPercent, searchJobStats } from './list-stats';
  import { jobStatusLabel, jobStatusClass } from './status';

  let { job, root, transfer, resultState, progressPercent, deleting = false, onopen, oncancel, ondelete }: {
    job: JobSummaryDto;
    /** The job's own live transfer; only single-file jobs have one. */
    transfer?: TransferStateDto;
    root?: JobSummaryDto;
    resultState?: 'loading' | 'unavailable';
    progressPercent?: number;
    deleting?: boolean;
    onopen: (job: JobSummaryDto) => void;
    oncancel?: (job: JobSummaryDto) => void;
    ondelete?: (job: JobSummaryDto) => void;
  } = $props();

  const identity = $derived(root ?? job);
  const title = $derived(identity.display.itemName ?? identity.display.queryText ?? `${jobKindLabel(job.display.kind)} #${identity.display.displayId ?? '?'}`);
  const searchMode = $derived(searchJobMode(job));
  const icon = $derived(searchMode ?? jobKindIcon(job.display.kind));
  const typeLabel = $derived(identity.display.kind === 'extract' ? identity.display.sourceDetail ?? 'Import'
    : searchMode ? searchModeLabel(searchMode) : jobKindLabel(job.display.kind));
  const statusLabel = $derived(resultState === 'unavailable' ? 'Result unavailable'
    : resultState === 'loading' ? 'Loading result' : jobStatusLabel(job, transfer));
  const searchStats = $derived(job.display.kind === 'search' ? searchJobStats(job) : []);
  const stats = $derived(job.display.kind === 'search' || resultState ? [] : automaticJobListStats(job));
  const percent = $derived.by(() => {
    if (resultState || job.lifecycle.lifecycleState === 'Terminal') return undefined;
    const value = progressPercent ?? automaticJobProgressPercent(job);
    return value != null && Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : undefined;
  });
</script>

<div data-testid="job-row" data-job-kind={job.display.kind} data-keyboard-item={identity.jobId} tabindex="-1"
  class="search-history-row" class:automatic-history-row={job.display.kind !== 'search'}>
  <button type="button" data-testid="job-open" data-keyboard-focus class="search-history-open"
    class:automatic-history-open={job.display.kind !== 'search'} onclick={() => onopen(job)}>
    <span class="search-history-icon" data-job-icon={icon} role="img" aria-label={typeLabel} title={typeLabel}><Icon name={icon} /></span>
    <span class="search-history-copy"><span class="search-history-query">{title}</span><small>{typeLabel}</small></span>
    {#if job.display.kind === 'search' && statusLabel !== 'Complete'}
      <span class={`status-label search-status-badge ${resultState ? 'pending' : jobStatusClass(job, transfer)}`}>{statusLabel}</span>
    {/if}
    {#if searchStats.length || stats.length}
      <span data-testid="job-stats" class="search-history-stats" class:search-history-counts={job.display.kind === 'search'} title={stats.length ? stats.join(' · ') : undefined}>
        {#each searchStats as stat}
          <span class:files-count={stat.label === 'file' || stat.label === 'files'} class:locked-count={stat.label === 'locked'} class:peers-count={stat.label === 'peer' || stat.label === 'peers'}><strong>{formatCount(stat.count)}</strong> {stat.label}</span>
        {/each}
        {#each stats as stat, index}
          {#if index}<span class="stat-separator">·</span>{/if}
          <span class="automatic-history-stat">{stat}</span>
        {/each}
      </span>
    {/if}
    {#if job.display.kind !== 'search'}
      <span class="job-row-status">
        <span class="status-label job-row-status-minimum" aria-hidden="true">Complete</span>
        <span class={`status-label search-status-badge ${resultState ? 'pending' : jobStatusClass(job, transfer)}`}>{statusLabel}</span>
      </span>
    {/if}
  </button>
  {#if !resultState && oncancel && job.lifecycle.availableActions?.some(action => action.kind === 'cancel')}
    <button type="button" data-testid="job-cancel" class="button quiet danger search-history-remove" aria-label={`Cancel ${title}`} title="Cancel" onclick={() => oncancel?.(job)}><Icon name="x" /></button>
  {:else if !resultState && ondelete && canDeleteSubmission(identity)}
    <button type="button" data-testid="job-remove" class="button quiet danger search-history-remove" disabled={deleting} aria-label={`Delete ${title} submission from history`} title="Delete submission from history" onclick={() => ondelete?.(identity)}><Icon name="trash" /></button>
  {:else if job.display.kind !== 'search'}
    <span class="automatic-history-action-space" aria-hidden="true"></span>
  {/if}
  {#if percent !== undefined}
    <span data-testid="job-progress" class="progress-track job-row-progress" role="progressbar" aria-label="Job progress"
      aria-valuenow={Math.round(percent)} aria-valuemin="0" aria-valuemax="100"><i style={`width:${percent}%`}></i></span>
  {/if}
</div>
