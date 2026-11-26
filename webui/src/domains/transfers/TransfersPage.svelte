<script lang="ts">
  import EmptyState from '../../components/EmptyState.svelte';
  import LoadingIndicator, { RENDER_SPINNER_DELAY_MS } from '../../components/LoadingIndicator.svelte';
  import { runTransferCommands } from './commands';
  import { downloadsViews, type DownloadsView, type UserLinkActions } from '../../app/navigation';
  import PageTabs from '../../components/PageTabs.svelte';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import type { TransferStateDto, Schema } from '../../api/types';
  import { daemonApi } from '../../api/client';
  import FolderItemCard from '../../components/items/FolderItemCard.svelte';
  import TransferItemActionButton from '../../components/items/TransferItemActionButton.svelte';
  import TransferBulkActions, { type BulkCancelMode } from '../../components/TransferBulkActions.svelte';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import MutationStatus, { type MutationState } from '../../components/MutationStatus.svelte';
  import { basename, isTerminalTransfer, type FolderItemFile } from '../presentation/items';
  import { transferGroupKeyId } from './history';
  import { arrangeTransferGroups, presentTransferEntry, transferEntryActions, TransferProjection, isQueuedTransfer, type TransferTimelineEntry } from './model';

  interface Props {
    direction: 'download' | 'upload';
    readTransfer: (id: string) => TransferStateDto | undefined;
    items: readonly TransferStateDto[];
    coverage?: Schema<'TransferRetainedCoverageDto'> | null;
    summaries?: readonly Schema<'TransferGroupSummaryDto'>[];
    summaryCoverage?: Schema<'TransferRetainedCoverageDto'> | null;
    summaryError?: Error | null;
    nextCursor?: string | null;
    loading?: boolean;
    daemonOffline?: boolean;
    error?: Error | null;
    onloadmore: () => void;
    onchanged: () => Promise<void> | void;
    onretrysummaries: () => Promise<void> | void;
    ondownloadsview?: (view: DownloadsView) => void;
    /** Show more members of a folder that has more than the extent brought. */
    onexpandgroup: (username: string | null, groupRef: string, shown: number) => void;
    expandingGroups?: readonly string[];
    userActions: UserLinkActions;
    /** Hold the cards back for a frame so the page itself paints first. */
    deferred?: boolean;
  }

  let {
    direction,
    readTransfer,
    items,
    coverage = null,
    summaries = [],
    summaryCoverage = null,
    summaryError = null,
    nextCursor = null,
    loading = false,
    daemonOffline = false,
    error = null,
    onloadmore,
    onchanged,
    onretrysummaries,
    ondownloadsview,
    onexpandgroup,
    expandingGroups = [],
    userActions,
    deferred = false,
  }: Props = $props();

  let mutation = $state<MutationState>({ phase: 'idle' });
  let bulkCancelMode = $state<BulkCancelMode>('all');
  let retryingSummaries = $state(false);
  let collapsedPeers = $state<Set<string>>(new Set());
  // Reuse the last verified totals while their replacement is loading. An
  // explicit unavailable/error response still drops those totals and shows
  // the degraded state above.
  const projection = new TransferProjection();
  // Only authoritative timeline membership and summary changes arrange cards.
  // Each card below reads its own transfer IDs; byte progress cannot rebuild or
  // reorder the page, and unrelated global transfers are never dependencies.
  let groups = $derived(arrangeTransferGroups(deferred ? [] : items, direction,
    !summaryError && (summaryCoverage === null || summaryCoverage.state === 'Available') ? summaries : [], projection));
  let canBulkCancel = $derived.by(() => {
    for (const group of groups) for (const entry of group.items)
      for (const transfer of transferEntryActions(entry, projection, readTransfer)) {
        if (!transfer.cancellable) continue;
        if (bulkCancelMode === 'all' || (bulkCancelMode === 'queued' ? isQueuedTransfer(transfer) : transfer.tone === 'active'))
          return true;
      }
    return false;
  });
  let canRemoveCompleted = $derived.by(() => {
    if (summaries.some(group => Number(group.succeededCount) > 0)) return true;
    for (const group of groups) for (const entry of group.items)
      for (const transfer of transferEntryActions(entry, projection, readTransfer))
        if (transfer.tone === 'complete') return true;
    return false;
  });

  function togglePeer(key: string): void {
    const next = new Set(collapsedPeers);
    if (next.has(key)) next.delete(key); else next.add(key);
    collapsedPeers = next;
  }

  async function retryDownload(request: () => Promise<Schema<'JobSummaryDto'>>): Promise<void> {
    mutation = { phase: 'pending', label: 'Queueing download…' };
    try {
      const job = await request();
      mutation = { phase: 'succeeded', label: `Retry queued as job #${job.display.displayId}` };
      await onchanged();
    } catch (cause) {
      mutation = { phase: 'rejected', label: 'Retry failed', detail: errorMessage(cause) };
    }
  }

  async function cancelIds(ids: string[], label: string): Promise<void> {
    if (!ids.length || mutation.phase === 'pending') return;
    mutation = { phase: 'pending', label };
    const { succeeded, failed, firstError } = await runTransferCommands(ids, (id) => daemonApi.cancelTransfer(id));
    mutation = {
      phase: failed === ids.length ? 'rejected' : 'succeeded',
      label: succeeded ? `${succeeded} transfer${succeeded === 1 ? '' : 's'} cancelled` : 'Cancellation rejected',
      detail: failed ? `${failed} could not be cancelled${firstError ? `: ${firstError}` : ''}` : undefined,
    };
    await onchanged();
  }

  async function removeIds(ids: string[], label: string): Promise<void> {
    if (!ids.length || mutation.phase === 'pending') return;
    mutation = { phase: 'pending', label };
    const { succeeded, failed, firstError } = await runTransferCommands(ids, async (id) => {
      const receipt = await daemonApi.removeTransfer(id);
      if (Number(receipt.rejectedCount) || Number(receipt.failedCount)) throw new Error('The daemon rejected or failed this removal operation.');
    });
    mutation = {
      phase: failed === ids.length ? 'rejected' : 'succeeded',
      label: succeeded ? `${succeeded} transfer${succeeded === 1 ? '' : 's'} removed` : 'Remove rejected',
      detail: failed ? `${failed} could not be removed${firstError ? `: ${firstError}` : ''}` : undefined,
    };
    await onchanged();
  }

  async function cancelBulk(): Promise<void> {
    if (mutation.phase === 'pending') return;
    mutation = { phase: 'pending', label: `Cancelling ${bulkCancelMode === 'all' ? `all ${direction}s` : bulkCancelMode === 'queued' ? 'queued transfers' : 'transfers in progress'}…` };
    try {
      const scope: Schema<'TransferCancellationScope'> = bulkCancelMode === 'active' ? 'InProgress' : bulkCancelMode === 'queued' ? 'Queued' : 'All';
      const receipt = await daemonApi.cancelTransfers(direction === 'download' ? 'Download' : 'Upload', scope);
      mutation = receiptStatus(receipt, 'cancelled');
      await onchanged();
    } catch (cause) {
      mutation = { phase: 'rejected', label: 'Bulk cancellation failed', detail: errorMessage(cause) };
    }
  }

  async function removeCompleted(): Promise<void> {
    if (mutation.phase === 'pending') return;
    mutation = { phase: 'pending', label: `Removing successful ${direction}s…` };
    try {
      const receipt = await daemonApi.removeTransfers(direction, { terminalOutcome: 'Succeeded' });
      mutation = receiptStatus(receipt, 'removed');
      await onchanged();
    } catch (cause) {
      mutation = { phase: 'rejected', label: 'Remove failed', detail: errorMessage(cause) };
    }
  }

  function receiptStatus(receipt: Schema<'TransferCommandReceiptDto'>, verb: string): MutationState {
    const succeeded = Number(receipt.succeededCount);
    const rejected = Number(receipt.rejectedCount) + Number(receipt.failedCount);
    return {
      phase: succeeded || Number(receipt.noOpCount)
        ? rejected ? 'partially-succeeded' : 'succeeded'
        : 'rejected',
      label: succeeded ? `${succeeded} transfer${succeeded === 1 ? '' : 's'} ${verb}` : `No transfers ${verb}`,
      detail: rejected ? `${rejected} rejected or failed` : undefined,
    };
  }

  function cancelEntry(entry: TransferTimelineEntry): void {
    if (entry.groupRef) void cancelFolder(entry, entry.groupRef);
    else void cancelIds(entry.transfer.cancellable ? entry.sourceTransferIds : [], `Cancelling ${basename(entry.path)}…`);
  }

  function removeEntry(entry: TransferTimelineEntry): void {
    if (entry.groupRef) void removeFolder(entry, entry.groupRef);
    else void removeIds(isTerminalTransfer(entry.transfer) ? entry.sourceTransferIds : [], `Removing ${basename(entry.path)}…`);
  }

  async function cancelFolder(entry: TransferTimelineEntry, groupRef: string): Promise<void> {
    if (mutation.phase === 'pending') return;
    mutation = { phase: 'pending', label: `Cancelling ${basename(entry.path)}…` };
    try {
      const receipt = await daemonApi.cancelTransfers(direction === 'download' ? 'Download' : 'Upload', 'All', { username: entry.username, groupRef });
      mutation = receiptStatus(receipt, 'cancelled');
      await onchanged();
    } catch (cause) {
      mutation = { phase: 'rejected', label: 'Folder cancellation failed', detail: errorMessage(cause) };
    }
  }

  async function removeFolder(entry: TransferTimelineEntry, groupRef: string): Promise<void> {
    if (mutation.phase === 'pending') return;
    mutation = { phase: 'pending', label: `Removing ${basename(entry.path)}…` };
    try {
      const receipt = await daemonApi.removeTransfers(direction, { username: entry.username, groupRef });
      mutation = receiptStatus(receipt, 'removed');
      await onchanged();
    } catch (cause) {
      mutation = { phase: 'rejected', label: 'Folder removal failed', detail: errorMessage(cause) };
    }
  }

  function cancelFile(file: FolderItemFile): void {
    if (file.transfer?.cancellable) void cancelIds([file.id], `Cancelling ${file.relativePath}…`);
  }

  function removeFile(file: FolderItemFile): void {
    if (isTerminalTransfer(file.transfer)) void removeIds([file.id], `Removing ${file.relativePath}…`);
  }

  function errorMessage(cause: unknown): string {
    return cause instanceof Error ? cause.message : String(cause);
  }

  async function retrySummaries(): Promise<void> {
    if (retryingSummaries) return;
    retryingSummaries = true;
    try { await onretrysummaries(); }
    finally { retryingSummaries = false; }
  }
</script>

<section class={`page ${direction}s-page`}>
  <header class="page-heading transfers-heading">
    <div class="page-heading-title">
      <h1>{direction === 'download' ? 'Downloads' : 'Uploads'}</h1>
      {#if direction === 'download' && ondownloadsview}
        <div class="page-heading-tabs">
          <PageTabs variant="segmented" id="downloads-view" label="Downloads" value="files" onchange={ondownloadsview} items={downloadsViews} />
        </div>
      {/if}
    </div>
    <TransferBulkActions
      mode={bulkCancelMode}
      canCancel={canBulkCancel}
      {canRemoveCompleted}
      busy={mutation.phase === 'pending'}
      onmodechange={(mode) => (bulkCancelMode = mode)}
      oncancel={() => void cancelBulk()}
      onremovecompleted={() => void removeCompleted()}
    />
  </header>

  <div class="transfer-resource-state">
    {#if !loading && !error && coverage && coverage.state !== 'Available'}
      <div class="resource-state-notice unavailable" role="status"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>History {coverage?.state?.toLowerCase() ?? 'unavailable'}</strong><small>{coverage?.reason ?? 'Current transfers are still shown.'}</small></span></div>
    {/if}
    {#if error && groups.length}<div class="resource-state-notice unavailable" role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Transfers unavailable</strong><small>{error.message}</small></span><button class="button" type="button" disabled={loading} onclick={() => void onchanged()}>Retry</button></div>{/if}
    {#if summaryError}<div class="resource-state-notice unavailable" role="status"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Folder totals unavailable</strong><small>Totals cover loaded files only. {summaryError.message}</small></span><button class="button" type="button" disabled={retryingSummaries} onclick={() => void retrySummaries()}>Retry</button></div>{/if}
    <MutationStatus state={mutation} />
  </div>

  {#if deferred}
    <!-- The cards mount in the next frame. On a slow phone that takes a while. -->
    <LoadingIndicator label={`Loading ${direction}s`} blocking delayMs={RENDER_SPINNER_DELAY_MS} />
  {:else if (loading || !coverage && !error) && !groups.length}
    <LoadingIndicator label={`Loading ${direction}s`} blocking />
  {:else if error && !groups.length}
    <div class={`resource-state-notice ${daemonOffline ? 'offline' : 'unavailable'} blocking`} role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>{daemonOffline ? 'Daemon unavailable' : 'Transfers unavailable'}</strong><small>{daemonOffline ? direction === 'download' ? 'Downloads can’t be loaded while the daemon is offline.' : 'Uploads can’t be loaded while the daemon is offline.' : error.message}</small></span>{#if !daemonOffline}<button class="button" type="button" onclick={() => void onchanged()}>Retry</button>{/if}</div>
  {:else if !groups.length && !error && coverage?.state === 'Available'}
    <EmptyState icon={direction} title={`No ${direction}s`} />
  {:else if groups.length}
    <div class="transfer-peer-list">
      {#each groups as group (group.key)}
        <section class="result-peer-group peer-item-group">
          <div class="result-peer-header compact">
            <button type="button" class="peer-header-toggle" aria-label={`${collapsedPeers.has(group.key) ? 'Expand' : 'Collapse'} ${group.peer} transfers`} aria-expanded={!collapsedPeers.has(group.key)} onclick={() => togglePeer(group.key)}></button>
            <svg class:collapsed={collapsedPeers.has(group.key)} class="peer-chevron" viewBox="0 0 20 20" aria-hidden="true"><path d="M7 5l6 5-6 5" /></svg>
            <span class="peer-identity">{#if group.username}<UsernameLink username={group.username} actions={userActions}/>{:else}{group.peer}{/if}</span>
            <span class="peer-metadata"><span class="peer-result-count">{group.transferCount} {group.transferCount === 1 ? direction : `${direction}s`}</span></span>
          </div>
          {#if !collapsedPeers.has(group.key)}
            <div class="result-peer-items">
              {#each group.items as layout (layout.id)}
                {@const entry = presentTransferEntry(layout, projection, readTransfer)}
                {@const expanding = entry.groupRef != null && expandingGroups.includes(transferGroupKeyId({ direction: direction === 'download' ? 'Download' : 'Upload', username: entry.username, groupRef: entry.groupRef }))}
                <FolderItemCard path={entry.path} sizeBytes={entry.sizeBytes} files={entry.files} totalFileCount={entry.totalsKnown ? entry.totalFileCount : null} filesComplete={entry.filesComplete} transfer={entry.transfer} onloadmorefiles={entry.groupRef && entry.totalsKnown && !entry.filesComplete ? () => onexpandgroup(entry.username, entry.groupRef!, entry.files.length) : undefined} loadingMoreFiles={expanding || summaryCoverage === null}
                  actionsDisabled={mutation.phase === 'pending'} onretryfile={(file) => void retryDownload(() => daemonApi.retryTransfer(file.transfer!.record!.transferId!))}>
                  {#snippet actions()}
                    {#if direction === 'download' && layout.rows[0]?.groupKind === 'Directory' && isTerminalTransfer(entry.transfer) && entry.transfer.tone !== 'complete'}
                      {#if layout.summary?.folderRetry?.completedOnRetry}
                        <span class="badge">Completed on retry</span>
                      {:else if Number(layout.summary?.folderRetry?.missingFiles) > 0}
                        <TransferItemActionButton kind="retry-folder" missingFiles={Number(layout.summary?.folderRetry?.missingFiles)}
                          disabled={mutation.phase === 'pending'} onclick={() => void retryDownload(() =>
                            daemonApi.retryDirectory(entry.groupRef!, layout.rows[0]?.transfer.record?.identity.jobId ?? undefined))} />
                      {/if}
                    {/if}
                    {#if entry.transfer.cancellable}<TransferItemActionButton label={`Cancel ${basename(entry.path)}`} disabled={mutation.phase === 'pending'} onclick={() => cancelEntry(entry)} />
                    {:else if isTerminalTransfer(entry.transfer)}<TransferItemActionButton kind="remove" label={`Remove ${basename(entry.path)}`} disabled={mutation.phase === 'pending'} onclick={() => removeEntry(entry)} />{/if}
                  {/snippet}
                  {#snippet fileActions(file)}
                    {#if file.transfer?.cancellable}<TransferItemActionButton label={`Cancel ${file.relativePath}`} disabled={mutation.phase === 'pending'} onclick={() => cancelFile(file)} />
                    {:else if isTerminalTransfer(file.transfer)}<TransferItemActionButton kind="remove" label={`Remove ${file.relativePath}`} disabled={mutation.phase === 'pending'} onclick={() => removeFile(file)} />{/if}
                  {/snippet}
                </FolderItemCard>
              {/each}
            </div>
          {/if}
        </section>
      {/each}
    </div>
    {#if nextCursor}<LoadMoreButton loading={loading} onclick={onloadmore} />{/if}
  {/if}
</section>
