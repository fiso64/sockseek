<script lang="ts">
  import type { Snippet } from 'svelte';
  import { daemonApi } from '../../api/client';
  import AppliedProfiles from '../AppliedProfiles.svelte';
  import Icon from '../Icon.svelte';
  import Modal from '../Modal.svelte';
  import { jobSkipExplanation } from '../../domains/jobs/status';
  import { folderSources, isBusyTransfer, type FolderItemFile, type TransferPresentation } from '../../domains/presentation/items';
  import { formatBytes, formatCount, humanizeStateValue } from '../../app/format';

  let { transfer, path, sizeBytes, files, filesComplete = false, circular = false, children, class: extraClass = '' }: {
    transfer: TransferPresentation;
    path?: string;
    sizeBytes?: number | null;
    files?: readonly FolderItemFile[];
    filesComplete?: boolean;
    circular?: boolean;
    children?: Snippet;
    class?: string;
  } = $props();
  let open = $state(false);
  let message = $derived(transfer.detail ?? transfer.job?.lifecycle.failureMessage);
  let expanded = $state(false);
  let technicalDetails = $derived(open && expanded && transfer.job?.lifecycle.hasFailureDetail && transfer.job.jobId
    ? daemonApi.getJobFailureDetail(transfer.job.jobId) : undefined);
  // A long queue is still one short badge.
  let queuePosition = $derived(transfer.queuePosition ? transfer.queuePosition > 999 ? '999+' : String(transfer.queuePosition) : '');
  let classes = $derived(`status-label ${circular ? 'status-label-icon folder-file-state' : 'transfer-state'} ${transfer.tone ?? ''} ${isBusyTransfer(transfer) ? 'busy' : ''} ${queuePosition ? 'with-count' : ''} transfer-${transfer.direction ?? 'download'} ${extraClass}`);

  type Field = [string, string | undefined];
  function date(value?: string | null): string | undefined {
    if (!value) return undefined;
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? undefined : parsed.toLocaleString();
  }
  function meaningful(value?: string | null): string | undefined {
    return value && value !== 'None' ? humanizeStateValue(value) : undefined;
  }
  // Only construct/format these sections while the dialog is open. They follow
  // the same live row/group updates as the label, without additional requests.
  function sections(): { title: string; fields: Field[] }[] {
    const row = transfer.record;
    const group = transfer.group;
    const job = transfer.job;
    const file = transfer.file;
    const fallback = transfer.fallback ?? row?.identity.fallback;
    const directory = transfer.directory;
    const sources = folderSources(files ?? [], filesComplete);
    const remoteFolders = sources.folders;
    const peer = transfer.target?.username ?? row?.identity.username ?? group?.key.username ?? sources.peer;
    const terminal = job ? job.lifecycle.lifecycleState === 'Terminal' : row?.status.isTerminal;
    const localPath = file?.downloadPath ?? directory?.downloadPath ?? row?.status.localPath;
    const bytes = row?.progress.bytesTransferred ?? file?.bytesTransferred ?? directory?.bytesTransferred ?? group?.transferredBytes;
    const reportedTotal = file?.fileSize ?? (directory?.unknownFileSizeCount === 0 ? directory.totalKnownBytes : undefined) ?? row?.progress.totalBytes ?? (group && Number(group.unknownTotalCount) === 0 ? group.knownTotalBytes : sizeBytes);
    const total = reportedTotal != null && Number(reportedTotal) >= 0 ? reportedTotal : undefined;
    const details: { title: string; fields: Field[] }[] = [
      { title: 'Transfer', fields: [
        ['Status', transfer.state],
        ['Direction', meaningful(transfer.direction)],
        ['Peer', peer ?? undefined],
        ['Provider', fallback?.provider],
        ['Source title', fallback?.title ?? undefined],
        ['Source reference', fallback?.reference ?? undefined],
        [files ? 'Folder' : 'Remote path', fallback ? undefined : files ? path : transfer.target?.filename ?? row?.identity.remotePath ?? path],
        [sources.complete ? remoteFolders.length === 1 ? 'Remote folder' : 'Remote folders' : 'Remote folders (loaded files)', remoteFolders.length ? remoteFolders.join('\n') : undefined],
        [job?.lifecycle.skipReason === 'AlreadyExists' ? 'Existing path' : 'Local path', localPath ?? undefined],
        ['Size', total == null ? undefined : formatBytes(total)],
        ['Queue position', transfer.queuePosition ? formatCount(transfer.queuePosition) : undefined],
        ['Attempts', row ? formatCount(row.status.attemptCount) : undefined],
        ['Folder attempt', directory?.attemptNumber == null ? undefined : formatCount(directory.attemptNumber)],
        ['Skip reason', jobSkipExplanation(job, file?.existingFileSource ?? directory?.existingFileSource) || undefined],
        ['Source', file?.reused ? 'Reused a completed local download from this workflow' : undefined],
        ['Failure reason', meaningful(job && terminal ? job.lifecycle.failureReason : row?.status.failureReason)],
        ['Cancellation', meaningful(job?.lifecycle.cancellationSource) ?? meaningful(row?.status.cancellationSource)],
        ['Activity', terminal ? undefined : meaningful(job?.lifecycle.activityPhase)],
      ] },
      { title: 'Progress', fields: [
        ['Transferred', bytes == null ? undefined : formatBytes(bytes)],
        ['Remaining', terminal || bytes == null || total == null ? undefined : formatBytes(Math.max(0, Number(total) - Number(bytes)))],
        ['Complete', transfer.progressPercent == null ? undefined : `${transfer.progressPercent.toFixed(1)}%`],
        ['Speed', transfer.speed],
        ['Time remaining', transfer.eta],
      ] },
      { title: 'Timing', fields: [
        ['Requested', date(row?.scheduling?.requestedAtUtc ?? transfer.createdAt ?? job?.display.createdAtUtc)],
        ['Started', date(row?.scheduling?.startedAtUtc)],
        ['Finished', date(row?.status.completedAtUtc ?? transfer.completedAt)],
        ['Last progress', date(row?.progress.lastProgressAtUtc)],
        ['Resumes at', terminal ? undefined : date(job?.lifecycle.activityUntilUtc)],
      ] },
    ];
    if (group) details.splice(1, 0, { title: 'Files', fields: [
      ['Total', formatCount(group.fileCount)], ['Queued', formatCount(group.queuedCount)],
      ['In progress', formatCount(group.inProgressCount)], ['Complete', formatCount(group.succeededCount)],
      ['Failed', formatCount(group.failedCount)], ['Cancelled', formatCount(group.cancelledCount)],
      ['Interrupted', formatCount(group.interruptedCount)],
    ] });
    if (directory) details.splice(1, 0, { title: 'Files', fields: [
      ['Total', formatCount(directory.fileCount)], ['Finished', formatCount(directory.terminalFileCount)],
      ['Downloaded', formatCount(directory.downloadedFileCount)], ['Skipped', formatCount(directory.skippedFileCount)],
      ['Reused', formatCount(directory.reusedFileCount)], ['Failed', formatCount(directory.failedFileCount)],
    ] });
    return details.map(section => ({ ...section, fields: section.fields.filter(([, value]) => value !== undefined && value !== '') }))
      .filter(section => section.fields.length);
  }
</script>

<button type="button" class={classes} onclick={() => { open = true; }}
  aria-label={circular ? queuePosition ? `${transfer.state}, position ${transfer.queuePosition}` : transfer.state : undefined} aria-haspopup="dialog" title={transfer.tone === 'failed' && message ? message : 'Show transfer details'}>
  {#if children}{@render children()}{:else}{transfer.state}{/if}
  {#if queuePosition}<span class="status-label-count" data-testid="queue-position">{circular ? '' : '#'}{queuePosition}</span>{/if}
  {#if !circular}<Icon name="info" class="status-details-icon" />{/if}
</button>
{#if open}
  <Modal title="Transfer details" closeLabel="Close transfer details" onclose={() => { open = false; expanded = false; }}>
    {#if message || transfer.job?.lifecycle.hasFailureDetail || transfer.tone === 'failed'}
      <section class="transfer-details-message">
        <h3>{transfer.tone === 'failed' ? 'Failure' : 'Details'}</h3>
        {#if message}<p>{message}</p>{:else if !transfer.job?.lifecycle.hasFailureDetail}<p>No further failure details were recorded.</p>{/if}
        {#if transfer.job?.lifecycle.hasFailureDetail}
          <details bind:open={expanded}>
            <summary>Technical details</summary>
            {#if technicalDetails}
              {#await technicalDetails}<p>Loading…</p>
              {:then detail}<p>{detail}</p>
              {:catch error}<p role="alert">Could not load technical details. {error.message}</p>{/await}
            {/if}
          </details>
        {/if}
      </section>
    {/if}
    {#each sections() as section (section.title)}
      <section>
        <h3>{section.title}</h3>
        <dl>{#each section.fields as [label, value] (label)}<dt>{label}</dt><dd>{value}</dd>{/each}</dl>
      </section>
    {/each}
    {#if transfer.job?.display.appliedProfiles?.length}<section><h3>Profiles</h3><AppliedProfiles profiles={transfer.job.display.appliedProfiles} /></section>{/if}
  </Modal>
{/if}

<style>
  h3 { margin: 0 0 0.5rem; font-size: 0.85rem; }
  details { margin-top: 1rem; }
  summary { cursor: pointer; color: var(--muted); }
  section + section { margin-top: 1.25rem; }
  dl { display: grid; grid-template-columns: minmax(6rem, 1fr) minmax(0, 3fr); gap: 0.4rem 1rem; margin: 0; font-size: 0.85rem; }
  dt { color: var(--muted); }
  dd { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
  p { white-space: pre-wrap; overflow-wrap: anywhere; margin: 0; }
  .transfer-details-message { padding: 0.75rem; border: 1px solid var(--border); border-radius: 6px; }
</style>
