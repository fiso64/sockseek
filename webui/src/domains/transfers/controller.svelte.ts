import { untrack } from 'svelte';
import { defaultPageSize, type DaemonApiClient } from '../../api/client';
import type { DaemonLiveClient } from '../../api/live';
import { TransferRows, updatedTransfer } from '../../api/state.svelte';
import { type Schema, type TransferHistoryDto } from '../../api/types';
import { mergeTransferHistory, transferGroupKeys, transferGroupKeyId } from './history';

type TransferDirection = 'download' | 'upload';
type TransferHistoryState = Readonly<{
  items: readonly TransferHistoryDto[];
  boundary: TransferHistoryDto | null;
  cursor: string | null;
  coverage: Schema<'TransferRetainedCoverageDto'> | null;
  summaries: readonly Schema<'TransferGroupSummaryDto'>[];
  summaryCoverage: Schema<'TransferRetainedCoverageDto'> | null;
  summaryError: Error | null;
  loading: boolean;
  error: Error | null;
}>;

/** One requested extent owns its rows, whole-folder totals and observation.
 * Connected readers start with the observation's snapshot. HTTP supplies the
 * same snapshot when live delivery is unavailable; its fresh successor scope
 * reconciles the gap. No summary polling or client-side paging.
 */
export class TransferTimelineController {
  connected = false;
  readonly live = new TransferRows();
  // Each response replaces a snapshot; individual wire fields are not mutable
  // UI state. Deep proxies would create subscriptions for every file property.
  history = $state.raw<Readonly<Record<TransferDirection, TransferHistoryState>>>({
    download: { items: [], boundary: null, cursor: null, coverage: null, summaries: [], summaryCoverage: null, summaryError: null, loading: false, error: null },
    upload: { items: [], boundary: null, cursor: null, coverage: null, summaries: [], summaryCoverage: null, summaryError: null, loading: false, error: null },
  });
  private scopeRevision = $state({ download: 0, upload: 0 });
  private pageLimit = { download: defaultPageSize, upload: defaultPageSize };
  private httpRead = { download: false, upload: false };
  private additionalIds: Record<TransferDirection, Set<string>> = { download: new Set(), upload: new Set() };
  missingMembers = $state<Record<TransferDirection, string[]>>({ download: [], upload: [] });
  private observationFailed = false;
  private observedDirection: TransferDirection | null = null;
  private activeDirection: TransferDirection | null = null;
  private activeObservation: (() => void) | null = null;

  constructor(
    private readonly api: Pick<DaemonApiClient, 'getTransferTimeline'>,
    private readonly observeTimeline: DaemonLiveClient['observeTransferTimeline'],
  ) {}

  stop(direction?: TransferDirection): void {
    untrack(() => {
      if (!direction || direction === this.activeDirection) this.activeObservation?.();
    });
  }

  observe(direction: TransferDirection): (() => void) | undefined {
    this.scopeRevision[direction];
    if (!this.scopeRevision[direction] || this.httpRead[direction]) return;
    this.stop();
    let disposed = false;
    this.activeDirection = direction;
    const stop = untrack(() => {
      const limit = this.pageLimit[direction];
      const scope: Schema<'TransferTimelineScopeDto'> = {
        direction: direction === 'download' ? 'Download' : 'Upload', limit,
        knownRevisions: Object.fromEntries(this.history[direction].items.map(row => [row.transferId, row.revision])),
        additionalTransferIds: [...this.additionalIds[direction]], includeGroupSummaries: true,
      };
      const extraIds = new Set(scope.additionalTransferIds);
      let ids = new Set<string>();
      return this.observeTimeline(scope, update => {
        if (disposed) return;
        if (update.windowTransferIds) {
          this.observedDirection = direction;
          this.observationFailed = false;
        }
        ids = this.applyUpdate(direction, update, ids, extraIds);
      }, cause => {
        if (disposed) return;
        this.observedDirection = null;
        this.observationFailed = true;
        this.history = { ...this.history, [direction]: { ...this.history[direction], loading: false, error: cause } };
      });
    });
    const dispose = () => {
      if (disposed) return;
      disposed = true;
      this.observedDirection = null;
      this.activeDirection = null;
      this.live.clear();
      stop();
      if (this.activeObservation === dispose) this.activeObservation = null;
    };
    this.activeObservation = dispose;
    return dispose;
  }

  private applyUpdate(direction: TransferDirection, update: Schema<'TransferTimelineUpdateDto'>,
    ids: Set<string>, requestedAdditional: ReadonlySet<string>): Set<string> {
    const current = this.history[direction];
    const pendingMembers = current.items.filter(row => this.additionalIds[direction].has(row.transferId)
      && !requestedAdditional.has(row.transferId));
    if (update.windowTransferIds) ids = new Set(update.windowTransferIds);
    // A folder fetch may finish during a snapshot/scope change. Its newly
    // fetched members are not deletions in a response that never requested them.
    for (const row of pendingMembers) ids.add(row.transferId);
    for (const row of update.items) ids.add(row.transferId);
    for (const id of update.removedTransferIds) ids.delete(id);
    if (this.activeDirection === null || this.activeDirection === direction) {
      if (update.windowTransferIds) this.live.clear();
      for (const delta of update.liveTransfers ?? []) {
        const next = updatedTransfer(untrack(() => this.live.get(delta.transferId)), delta);
        if (next) this.live.set(delta.transferId, next);
      }
      for (const id of [...update.removedTransferIds, ...(update.removedLiveTransferIds ?? [])]) this.live.delete(id);
    }
    const membership = Boolean(update.windowTransferIds || update.items.length || update.removedTransferIds.length);
    const missing = update.windowTransferIds
      ? new Set([...requestedAdditional].filter(id => !ids.has(id))) : new Set(this.missingMembers[direction]);
    for (const row of update.items) missing.delete(row.transferId);
    for (const id of update.removedTransferIds) if (requestedAdditional.has(id)) missing.add(id);
    if (missing.size || this.missingMembers[direction].length)
      this.missingMembers = { ...this.missingMembers, [direction]: [...missing] };
    const rows = membership ? mergeTransferHistory(current.items, update.items, update.removedTransferIds, [...ids]) : current.items;
    let summaries = update.groupSummaries?.items ?? current.summaries;
    if (update.groupSummaries && pendingMembers.length) {
      const pendingGroups = new Set(transferGroupKeys(direction, pendingMembers, []).map(transferGroupKeyId));
      const included = new Set(summaries.map(summary => transferGroupKeyId(summary.key)));
      summaries = [...summaries, ...current.summaries.filter(summary => pendingGroups.has(transferGroupKeyId(summary.key))
        && !included.has(transferGroupKeyId(summary.key)))];
    }
    this.history = { ...this.history, [direction]: {
      ...current, items: rows, coverage: update.retainedCoverage, cursor: update.nextCursor ?? null,
      boundary: membership ? rows.find(row => row.transferId === update.boundaryTransferId) ?? null : current.boundary,
      summaries, summaryCoverage: update.groupSummaries?.retainedCoverage ?? current.summaryCoverage,
      summaryError: update.groupSummaries ? summaryFailure(update.groupSummaries) : current.summaryError,
      loading: update.windowTransferIds ? false : current.loading,
      error: update.windowTransferIds ? null : current.error,
    } };
    return ids;
  }

  registerMembers(direction: TransferDirection, rows: TransferHistoryDto[]): void {
    const previous = this.additionalIds[direction];
    const next = new Set(rows.map(row => row.transferId));
    const changed = previous.size !== next.size || [...next].some(id => !previous.has(id));
    this.additionalIds[direction] = next;
    if (rows.length) this.history = { ...this.history, [direction]: {
      ...this.history[direction], items: mergeTransferHistory(this.history[direction].items, rows),
    } };
    if (changed) this.scopeRevision[direction]++;
  }

  afterMutation(direction: TransferDirection): Promise<void> | void {
    if (this.observedDirection !== direction) return this.load(direction, true);
  }

  async load(direction: TransferDirection, reset = false): Promise<void> {
    if (this.httpRead[direction] || !reset && this.history[direction].loading) return;
    const limit = this.pageLimit[direction] + (!reset && this.scopeRevision[direction] ? defaultPageSize : 0);
    // A fresh stream already supplies the whole requested extent. Reading it
    // over HTTP first duplicates that work on connected navigation and Retry.
    if (this.connected && !this.observationFailed) {
      this.pageLimit[direction] = limit;
      this.history = { ...this.history, [direction]: { ...this.history[direction], loading: true, error: null } };
      this.scopeRevision[direction]++;
      return;
    }
    this.stop(direction);
    this.httpRead[direction] = true;
    this.history = { ...this.history, [direction]: { ...this.history[direction], loading: true, error: null } };
    const requestedAdditional = new Set(this.additionalIds[direction]);
    try {
      // HTTP and live observe the same newest-first extent. One read supplies
      // its rows/totals; a refresh needs no stale cursors or per-page merging.
      const page = await this.api.getTransferTimeline({
        direction: direction === 'download' ? 'Download' : 'Upload', limit,
        additionalTransferIds: [...requestedAdditional], includeGroupSummaries: true,
      });
      this.pageLimit[direction] = limit;
      this.applyUpdate(direction, page, new Set(), requestedAdditional);
      if (this.activeDirection === null || this.activeDirection === direction) this.observationFailed = false;
    } catch (error) {
      this.history = { ...this.history, [direction]: { ...this.history[direction], loading: false, error: asError(error) } };
    } finally {
      this.httpRead[direction] = false;
      this.scopeRevision[direction]++;
    }
  }
}

function summaryFailure(value: Schema<'TransferGroupSummariesResponseDto'>): Error | null {
  return value.retainedCoverage.state === 'Unavailable'
    ? new Error(value.retainedCoverage.reason ?? 'Transfer folder totals are unavailable.') : null;
}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}
