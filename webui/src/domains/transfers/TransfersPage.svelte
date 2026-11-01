<script lang="ts">
  import { onDestroy, untrack } from 'svelte';
  import { runTransferCommands } from './commands';
  import type { UserLinkActions } from '../../app/navigation';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import type { TransferHistoryDto, TransferStateDto, Schema } from '../../api/types';
  import { daemonApi, defaultPageSize } from '../../api/client';
  import FileItemCard from '../../components/items/FileItemCard.svelte';
  import FolderItemCard from '../../components/items/FolderItemCard.svelte';
  import TransferItemActionButton from '../../components/items/TransferItemActionButton.svelte';
  import TransferBulkActions, { type BulkCancelMode } from '../../components/TransferBulkActions.svelte';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import MutationStatus, { type MutationState } from '../../components/MutationStatus.svelte';
  import { basename, type FolderItemFile } from '../presentation/items';
  import { arrangeTransferGroups, presentTransferEntry, transferEntryActions, TransferProjection, isQueuedTransfer, isTerminalTransfer, type TransferTimelineEntry, type TransferTimelineFolderEntry } from './model';
  import { transferGroupKeys, transferGroupMemberScope, transferGroupCursor } from './history';

  interface Props {
    direction: 'download' | 'upload';
    live: (id: string) => TransferStateDto | undefined;
    history: readonly TransferHistoryDto[];
    globalBoundary: TransferHistoryDto | null;
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
    onfetchedmembers: (rows: TransferHistoryDto[]) => void;
    missingMembers?: readonly string[];
    userActions: UserLinkActions;
  }

  let {
    direction,
    live,
    history,
    globalBoundary,
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
    onfetchedmembers,
    missingMembers = [],
    userActions,
  }: Props = $props();

  let mutation = $state<MutationState>({ phase: 'idle' });
  let bulkCancelMode = $state<BulkCancelMode>('all');
  let retryingSummaries = $state(false);
  let collapsedPeers = $state<Set<string>>(new Set());
  interface GroupMemberPage {
    scope: string;
    requestId: number;
    rows: TransferHistoryDto[];
    nextCursor: string | null;
    loadedOnce: boolean;
    loading: boolean;
    error: string | null;
    refreshFailedScope?: string;
  }
  let memberPages = $state<Record<string, GroupMemberPage>>({});
  $effect(() => {
    const rows = Object.values(memberPages).flatMap(page => page.rows);
    untrack(() => onfetchedmembers(rows));
  });
  $effect(() => {
    const missing = missingMembers;
    if (missing.length) untrack(() => removeArchivedMemberRows(row => missing.includes(row.transferId)));
  });
  onDestroy(() => onfetchedmembers([]));
  let nextMemberRequestId = 0;
  let memberScopes = $derived.by(() => {
    // A timeline refresh clears summaryCoverage until its replacement arrives.
    // Keep the last verified member pages visible during that handoff; a new
    // available summary below will invalidate only groups that actually changed.
    if (summaryCoverage === null && !summaryError) return null;
    const scopes = new Map<string, string>();
    if (summaryCoverage?.state !== 'Available' || summaryError) {
      // Totals are optional metadata, not ownership of already fetched files.
      // Retain pages for groups still present in the timeline/live snapshot;
      // an authoritative removal must not leave a cached folder behind.
      for (const group of transferGroupKeys(direction, history, [])) {
        const key = groupPageKey(group.username ?? null, group.groupRef);
        const page = memberPages[key];
        if (page) scopes.set(key, page.scope);
      }
      return scopes;
    }
    const rowsByGroup = new Map<string, TransferHistoryDto[]>();
    for (const row of history) {
      if (row.direction.toLowerCase() !== direction || !row.groupRef) continue;
      const key = groupPageKey(row.username ?? null, row.groupRef);
      const rows = rowsByGroup.get(key) ?? [];
      rows.push(row);
      rowsByGroup.set(key, rows);
    }
    for (const summary of summaries) {
      if (summary.key.direction.toLowerCase() !== direction) continue;
      const key = groupPageKey(summary.key.username ?? null, summary.key.groupRef);
      scopes.set(key, transferGroupMemberScope(summary, rowsByGroup.get(key) ?? [], memberPages[key]?.rows, globalBoundary));
    }
    return scopes;
  });
  let activeMemberPages = $derived.by(() => {
    const active: Record<string, GroupMemberPage> = {};
    for (const [key, page] of Object.entries(memberPages)) {
      if (memberScopes === null || memberScopes.has(key)) active[key] = page;
    }
    return active;
  });
  $effect(() => {
    // Keep loaded rows while a group changes, then replace them from that
    // group's own timeline. Status changes must not collapse an expanded card.
    const scopes = memberScopes;
    if (!scopes || summaryCoverage?.state !== 'Available' || summaryError) return;
    const removed = Object.keys(memberPages).filter((key) => !scopes.has(key));
    if (removed.length) {
      const next = { ...memberPages };
      for (const key of removed) delete next[key];
      memberPages = next;
    }
    for (const [key, page] of Object.entries(memberPages)) {
      const scope = scopes.get(key);
      if (scope && page.scope !== scope && !page.loading && page.refreshFailedScope !== scope)
        void refreshGroupMembers(key, scope, page);
    }
  });
  let mergedHistory = $derived.by(() => {
    const rows = new Map<string, TransferHistoryDto>();
    for (const page of Object.values(activeMemberPages))
      for (const item of page.rows) rows.set(item.transferId, item);
    for (const item of history) rows.set(item.transferId, item);
    return [...rows.values()];
  });
  // Reuse the last verified totals while their replacement is loading, just
  // as we reuse its member pages. An explicit unavailable/error response still
  // drops those totals and shows the degraded state above.
  const projection = new TransferProjection();
  // Only authoritative timeline membership and summary changes arrange cards.
  // Each card below reads its own live IDs; byte progress cannot rebuild or
  // reorder the page, and unrelated global transfers are never dependencies.
  let groups = $derived(arrangeTransferGroups(mergedHistory, direction,
    !summaryError && (summaryCoverage === null || summaryCoverage.state === 'Available') ? summaries : [], projection));
  let canBulkCancel = $derived.by(() => {
    for (const group of groups) for (const entry of group.items)
      for (const transfer of transferEntryActions(entry, projection, live)) {
        if (!transfer.cancellable) continue;
        if (bulkCancelMode === 'all' || (bulkCancelMode === 'queued' ? isQueuedTransfer(transfer) : transfer.tone === 'active'))
          return true;
      }
    return false;
  });
  let canRemoveCompleted = $derived.by(() => {
    for (const group of groups) for (const entry of group.items)
      for (const transfer of transferEntryActions(entry, projection, live))
        if (isTerminalTransfer(transfer)) return true;
    return false;
  });

  function togglePeer(key: string): void {
    const next = new Set(collapsedPeers);
    if (next.has(key)) next.delete(key); else next.add(key);
    collapsedPeers = next;
  }

  function groupPageKey(username: string | null, groupRef: string): string {
    return JSON.stringify([direction, username, groupRef]);
  }

  function scopeForMemberRows(key: string, rows: TransferHistoryDto[]): string | undefined {
    const summary = summaries.find(item => item.key.direction.toLowerCase() === direction
      && groupPageKey(item.key.username ?? null, item.key.groupRef) === key);
    return summary ? transferGroupMemberScope(summary, history, rows, globalBoundary) : undefined;
  }

  function finishSupersededMemberRequest(key: string, requestId: number): void {
    const current = memberPages[key];
    if (current?.requestId === requestId)
      memberPages = { ...memberPages, [key]: { ...current, loading: false } };
  }

  function removeArchivedMemberRows(matches: (row: TransferHistoryDto) => boolean): void {
    let next: typeof memberPages | undefined;
    for (const [key, page] of Object.entries(memberPages)) {
      const rows = page.rows.filter(row => !matches(row));
      if (rows.length === page.rows.length) continue;
      next ??= { ...memberPages };
      next[key] = { ...page, rows, scope: scopeForMemberRows(key, rows) ?? page.scope,
        requestId: ++nextMemberRequestId, loading: false, refreshFailedScope: undefined };
    }
    if (next) memberPages = next;
  }

  async function refreshGroupMembers(key: string, scope: string, previous: GroupMemberPage): Promise<void> {
    if (previous.loading) return;
    const requestId = ++nextMemberRequestId;
    const [, username, groupRef] = JSON.parse(key) as [string, string | null, string];
    memberPages = { ...memberPages, [key]: {
      ...previous, requestId, loading: true, error: null, refreshFailedScope: undefined,
    } };
    try {
      const rows = new Map<string, TransferHistoryDto>();
      let cursor: string | null = null;
      do {
        const limit = defaultPageSize;
        const page = await daemonApi.getTransferGroupMembers(direction, username, groupRef, cursor, limit);
        if (page.retainedCoverage.state !== 'Available')
          throw new Error(page.retainedCoverage.reason ?? 'Retained folder history is unavailable.');
        for (const item of page.items) rows.set(item.transferId, item);
        cursor = page.nextCursor ?? null;
      } while (cursor && rows.size < previous.rows.length);
      if (memberPages[key]?.requestId !== requestId) return;
      if (memberScopes?.get(key) !== scope) {
        finishSupersededMemberRequest(key, requestId);
        return;
      }
      const loadedRows = [...rows.values()];
      memberPages = { ...memberPages, [key]: {
        scope: scopeForMemberRows(key, loadedRows) ?? scope, requestId, rows: loadedRows, nextCursor: cursor,
        loadedOnce: true, loading: false, error: null,
      } };
    } catch (cause) {
      if (memberPages[key]?.requestId !== requestId) return;
      if (memberScopes?.get(key) !== scope) {
        finishSupersededMemberRequest(key, requestId);
        return;
      }
      memberPages = { ...memberPages, [key]: {
        ...previous, requestId, loading: false, error: errorMessage(cause), refreshFailedScope: scope,
      } };
    }
  }

  async function loadGroupMembers(entry: TransferTimelineFolderEntry): Promise<void> {
    const key = groupPageKey(entry.username, entry.groupRef);
    const scope = memberScopes?.get(key);
    if (scope === undefined) return;
    const previous = activeMemberPages[key];
    if (previous?.loading) return;
    if (previous?.loadedOnce && previous.scope !== scope) {
      await refreshGroupMembers(key, scope, previous);
      return;
    }
    const requestId = ++nextMemberRequestId;
    const restart = Boolean(previous?.loadedOnce && !previous.nextCursor && !previous.error);
    // Global history is already a complete newest-first prefix of this same
    // moving keyset timeline. Start this folder after that prefix rather than
    // rereading its first page. Retain the prefix too: a later global refresh
    // can shift it without shrinking an explicitly expanded folder.
    const cursor = restart ? null : transferGroupCursor(previous?.rows ?? [], previous?.nextCursor ?? null, globalBoundary, nextCursor);
    const loadedRows = new Map((previous?.rows ?? []).map(item => [item.transferId, item]));
    for (const item of history)
      if (item.direction.toLowerCase() === direction && (item.username ?? null) === entry.username && item.groupRef === entry.groupRef)
        loadedRows.set(item.transferId, item);
    memberPages = { ...memberPages, [key]: {
      scope, requestId,
      rows: previous?.rows ?? [], nextCursor: previous?.nextCursor ?? null,
      loadedOnce: previous?.loadedOnce ?? false, loading: true, error: null,
    } };
    try {
      const page = await daemonApi.getTransferGroupMembers(
        direction, entry.username, entry.groupRef, cursor);
      if (memberPages[key]?.requestId !== requestId) return;
      if (memberScopes?.get(key) !== scope) {
        finishSupersededMemberRequest(key, requestId);
        return;
      }
      if (page.retainedCoverage.state !== 'Available')
        throw new Error(page.retainedCoverage.reason ?? 'Retained folder history is unavailable.');
      const rows = restart ? new Map<string, TransferHistoryDto>() : loadedRows;
      for (const item of page.items) rows.set(item.transferId, item);
      const resultRows = [...rows.values()];
      memberPages = { ...memberPages, [key]: {
        scope: scopeForMemberRows(key, resultRows) ?? scope, requestId,
        rows: resultRows, nextCursor: page.nextCursor ?? null,
        loadedOnce: true, loading: false, error: null,
      } };
    } catch (cause) {
      if (memberPages[key]?.requestId !== requestId) return;
      if (memberScopes?.get(key) !== scope) {
        finishSupersededMemberRequest(key, requestId);
        return;
      }
      memberPages = { ...memberPages, [key]: {
        scope, requestId,
        rows: previous?.rows ?? [], nextCursor: previous?.nextCursor ?? null,
        loadedOnce: previous?.loadedOnce ?? false, loading: false, error: errorMessage(cause),
      } };
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

  async function archiveIds(ids: string[], label: string): Promise<void> {
    if (!ids.length || mutation.phase === 'pending') return;
    mutation = { phase: 'pending', label };
    const archived = new Set<string>();
    const { succeeded, failed, firstError } = await runTransferCommands(ids, async (id) => {
      const receipt = await daemonApi.archiveTransfer(id);
      if (Number(receipt.rejectedCount) || Number(receipt.failedCount)) throw new Error('The daemon rejected or failed this archive operation.');
      archived.add(id);
    });
    // An acknowledged archive is authoritative even when optional totals fail.
    // Evict only successful identities; failed siblings keep their fetched rows.
    removeArchivedMemberRows(row => archived.has(row.transferId));
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
    mutation = { phase: 'pending', label: `Removing terminal ${direction}s…` };
    try {
      const receipt = await daemonApi.archiveTransfers(direction);
      mutation = receiptStatus(receipt, 'removed');
      if (!Number(receipt.rejectedCount) && !Number(receipt.failedCount))
        removeArchivedMemberRows(row => isTerminalTransfer(projection.retained(row).transfer));
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
    if (entry.kind === 'folder') {
      void cancelFolder(entry);
      return;
    }
    void cancelIds(entry.transfer.cancellable ? entry.sourceTransferIds : [], `Cancelling ${basename(entry.path)}…`);
  }

  function archiveEntry(entry: TransferTimelineEntry): void {
    if (entry.kind === 'folder') {
      void archiveFolder(entry);
      return;
    }
    void archiveIds(isTerminalTransfer(entry.transfer) ? entry.sourceTransferIds : [], `Removing ${basename(entry.path)}…`);
  }

  async function cancelFolder(entry: TransferTimelineFolderEntry): Promise<void> {
    if (mutation.phase === 'pending') return;
    mutation = { phase: 'pending', label: `Cancelling ${basename(entry.path)}…` };
    try {
      const receipt = await daemonApi.cancelTransfers(direction === 'download' ? 'Download' : 'Upload', 'All', { username: entry.username, groupRef: entry.groupRef });
      mutation = receiptStatus(receipt, 'cancelled');
      await onchanged();
    } catch (cause) {
      mutation = { phase: 'rejected', label: 'Folder cancellation failed', detail: errorMessage(cause) };
    }
  }

  async function archiveFolder(entry: TransferTimelineFolderEntry): Promise<void> {
    if (mutation.phase === 'pending') return;
    mutation = { phase: 'pending', label: `Removing ${basename(entry.path)} history…` };
    try {
      const receipt = await daemonApi.archiveTransfers(direction, true, { username: entry.username, groupRef: entry.groupRef });
      mutation = receiptStatus(receipt, 'removed');
      if (!Number(receipt.rejectedCount) && !Number(receipt.failedCount))
        removeArchivedMemberRows(row => (row.username ?? null) === entry.username && row.groupRef === entry.groupRef
          && isTerminalTransfer(projection.retained(row).transfer));
      await onchanged();
    } catch (cause) {
      mutation = { phase: 'rejected', label: 'Folder removal failed', detail: errorMessage(cause) };
    }
  }

  function cancelFile(folder: TransferTimelineFolderEntry, file: FolderItemFile): void {
    void folder;
    if (file.transfer?.cancellable) void cancelIds([file.id], `Cancelling ${file.relativePath}…`);
  }

  function archiveFile(folder: TransferTimelineFolderEntry, file: FolderItemFile): void {
    void folder;
    if (isTerminalTransfer(file.transfer)) void archiveIds([file.id], `Removing ${file.relativePath}…`);
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
    <div><p class="eyebrow">Transfers</p><h1>{direction === 'download' ? 'Downloads' : 'Uploads'}</h1></div>
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
    {#if !loading && !error && coverage?.state !== 'Available'}
      <div class="resource-state-notice unavailable" role="status"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Retained history {coverage?.state?.toLowerCase() ?? 'unavailable'}</strong><small>{coverage?.reason ?? 'Live transfers remain available.'}</small></span></div>
    {/if}
    {#if error && groups.length}<div class="resource-state-notice unavailable" role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Transfers unavailable</strong><small>{error.message}</small></span><button type="button" disabled={loading} onclick={() => void onchanged()}>Retry</button></div>{/if}
    {#if summaryError}<div class="resource-state-notice unavailable" role="status"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Folder totals unavailable</strong><small>Transfer rows remain usable; folder totals are shown for loaded files only. {summaryError.message}</small></span><button type="button" disabled={retryingSummaries} onclick={() => void retrySummaries()}>Retry</button></div>{/if}
    <MutationStatus state={mutation} />
  </div>

  {#if loading && !groups.length}
    <div class="resource-state-notice loading blocking"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>Loading {direction}s</strong><small>Combining live state with retained history.</small></span></div>
  {:else if error && !groups.length}
    <div class={`resource-state-notice ${daemonOffline ? 'offline' : 'unavailable'} blocking`} role="alert"><span class="resource-state-dot"></span><span class="resource-state-copy"><strong>{daemonOffline ? 'Daemon unavailable' : 'Transfers unavailable'}</strong><small>{daemonOffline ? direction === 'download' ? 'Current download state cannot be loaded while the daemon is offline.' : 'Current upload state cannot be loaded while the daemon is offline.' : error.message}</small></span>{#if !daemonOffline}<button type="button" onclick={() => void onchanged()}>Retry</button>{/if}</div>
  {:else if !groups.length && !error && coverage?.state === 'Available'}
    <div class="resource-state-notice empty blocking" role="status"><span class="resource-state-dot" aria-hidden="true"></span><span class="resource-state-copy"><strong>No {direction}s</strong><small>{direction === 'download' ? 'Downloaded files and folders will appear here in creation order.' : 'Uploads will appear here as peers request shared files.'}</small></span></div>
  {:else if groups.length}
    <div class="transfer-peer-list">
      {#each groups as group (group.key)}
        <section class="result-peer-group peer-item-group">
          <div class="result-peer-header compact">
            <button type="button" class="peer-header-toggle" aria-label={`${collapsedPeers.has(group.key) ? 'Expand' : 'Collapse'} ${group.peer} transfers`} aria-expanded={!collapsedPeers.has(group.key)} onclick={() => togglePeer(group.key)}></button>
            <svg class:collapsed={collapsedPeers.has(group.key)} class="peer-chevron" viewBox="0 0 20 20" aria-hidden="true"><path d="M7 5l6 5-6 5" /></svg>
            <span class="peer-identity">{#if group.username}<UsernameLink username={group.username} actions={userActions}/>{:else}Unknown peer{/if}</span>
            <span class="peer-metadata"><span class="peer-result-count">{group.transferCount} {group.transferCount === 1 ? direction : `${direction}s`}</span></span>
          </div>
          {#if !collapsedPeers.has(group.key)}
            <div class="result-peer-items">
              {#each group.items as layout (layout.id)}
                {@const entry = presentTransferEntry(layout, projection, live)}
                {#if entry.kind === 'file'}
                  <FileItemCard path={entry.path} sizeBytes={entry.sizeBytes} audio={entry.audio} transfer={entry.transfer}>
                    {#snippet actions()}
                      {#if entry.transfer.cancellable}<TransferItemActionButton label={`Cancel ${basename(entry.path)}`} disabled={mutation.phase === 'pending'} onclick={() => cancelEntry(entry)} />
                      {:else if isTerminalTransfer(entry.transfer)}<TransferItemActionButton kind="remove" label={`Remove ${basename(entry.path)}`} disabled={mutation.phase === 'pending'} onclick={() => archiveEntry(entry)} />{/if}
                    {/snippet}
                  </FileItemCard>
                {:else}
                  {@const memberPage = activeMemberPages[groupPageKey(entry.username, entry.groupRef)]}
                  <FolderItemCard path={entry.path} sizeBytes={entry.sizeBytes} files={entry.files} totalFileCount={entry.summaryAvailable ? entry.totalFileCount : null} filesComplete={entry.filesComplete} dataStateLabel={entry.summaryAvailable ? entry.filesComplete ? undefined : 'Showing loaded files; folder totals and actions cover the entire directory.' : 'Size, progress and status cover loaded files only. Folder actions apply to the entire directory.'} transfer={entry.transfer} onloadmorefiles={entry.summaryAvailable && (!entry.filesComplete || memberPage?.error) ? () => void loadGroupMembers(entry) : undefined} loadingMoreFiles={Boolean(memberPage?.loading || summaryCoverage === null)} fileLoadError={memberPage?.error ?? null}>
                    {#snippet actions()}
                      {#if entry.transfer.cancellable}<TransferItemActionButton label={`Cancel ${basename(entry.path)}`} disabled={mutation.phase === 'pending'} onclick={() => cancelEntry(entry)} />
                      {:else if isTerminalTransfer(entry.transfer)}<TransferItemActionButton kind="remove" label={`Remove ${basename(entry.path)}`} disabled={mutation.phase === 'pending'} onclick={() => archiveEntry(entry)} />{/if}
                    {/snippet}
                    {#snippet fileActions(file)}
                      {#if file.transfer?.cancellable}<TransferItemActionButton label={`Cancel ${file.relativePath}`} disabled={mutation.phase === 'pending'} onclick={() => cancelFile(entry, file)} />
                      {:else if isTerminalTransfer(file.transfer)}<TransferItemActionButton kind="remove" label={`Remove ${file.relativePath}`} disabled={mutation.phase === 'pending'} onclick={() => archiveFile(entry, file)} />{/if}
                    {/snippet}
                  </FolderItemCard>
                {/if}
              {/each}
            </div>
          {/if}
        </section>
      {/each}
    </div>
    {#if nextCursor}<LoadMoreButton label={`Load earlier ${direction}s`} loading={loading} onclick={onloadmore} />{/if}
  {/if}
</section>
