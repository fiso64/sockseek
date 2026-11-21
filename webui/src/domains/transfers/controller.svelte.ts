import { untrack } from 'svelte';
import { defaultPageSize, type DaemonApiClient } from '../../api/client';
import type { DaemonLiveClient } from '../../api/live';
import { TransferRows, updatedTransfer } from '../../api/state.svelte';
import { wireInteger, type Schema, type TransferStateDto } from '../../api/types';
import { mergeTransferHistory, transferGroupKeys, transferGroupKeyId } from './history';

type TransferDirection = 'download' | 'upload';
type TransferTimelineState = Readonly<{
  items: readonly TransferStateDto[];
  boundary: TransferStateDto | null;
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
  readonly rows = { download: new TransferRows(), upload: new TransferRows() };
  // Each response replaces a snapshot; individual wire fields are not mutable
  // UI state. Deep proxies would create subscriptions for every file property.
  timeline = $state.raw<Readonly<Record<TransferDirection, TransferTimelineState>>>({
    download: { items: [], boundary: null, cursor: null, coverage: null, summaries: [], summaryCoverage: null, summaryError: null, loading: false, error: null },
    upload: { items: [], boundary: null, cursor: null, coverage: null, summaries: [], summaryCoverage: null, summaryError: null, loading: false, error: null },
  });
  private scopeRevision = $state({ download: 0, upload: 0 });
  private pageLimit = { download: defaultPageSize, upload: defaultPageSize };
  private httpRead = { download: false, upload: false };
  private additionalIds: Record<TransferDirection, Set<string>> = { download: new Set(), upload: new Set() };
  missingMembers = $state<Record<TransferDirection, string[]>>({ download: [], upload: [] });
  private observationFailed = false;
  private epochs: Partial<Record<TransferDirection, string>> = {};
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
        knownRevisions: Object.fromEntries(this.timeline[direction].items.map(row => [row.transferId, this.rows[direction].get(row.transferId)?.revision ?? row.revision])),
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
        this.timeline = { ...this.timeline, [direction]: { ...this.timeline[direction], loading: false, error: cause } };
      });
    });
    const dispose = () => {
      if (disposed) return;
      disposed = true;
      this.observedDirection = null;
      this.activeDirection = null;
      stop();
      if (this.activeObservation === dispose) this.activeObservation = null;
    };
    this.activeObservation = dispose;
    return dispose;
  }

  private applyUpdate(direction: TransferDirection, update: Schema<'TransferTimelineUpdateDto'>,
    ids: Set<string>, requestedAdditional: ReadonlySet<string>): Set<string> {
    const current = this.timeline[direction];
    const pendingMembers = current.items.filter(row => this.additionalIds[direction].has(row.transferId)
      && !requestedAdditional.has(row.transferId));
    if (update.windowTransferIds) ids = new Set(update.windowTransferIds);
    // A folder fetch may finish during a snapshot/scope change. Its newly
    // fetched members are not deletions in a response that never requested them.
    for (const row of pendingMembers) ids.add(row.transferId);
    for (const row of update.items) ids.add(row.transferId);
    for (const id of update.removedTransferIds) ids.delete(id);
    const records = this.rows[direction];
    const epoch = update.readPosition?.epoch;
    const replacedEpoch = Boolean(update.windowTransferIds && epoch && this.epochs[direction]
      && this.epochs[direction] !== epoch);
    if (epoch) this.epochs[direction] = epoch;
    for (const row of update.items) {
      const prior = untrack(() => records.get(row.transferId));
      if (replacedEpoch || !prior || wireInteger(row.revision) >= wireInteger(prior.revision))
        records.set(row.transferId, row);
    }
    for (const delta of update.changes ?? []) {
      const next = updatedTransfer(untrack(() => records.get(delta.transferId)), delta);
      if (next) records.set(delta.transferId, next);
    }
    if (update.windowTransferIds)
      for (const id of [...records.keys()]) if (!ids.has(id)) records.delete(id);
    for (const id of update.removedTransferIds) records.delete(id);
    const membership = Boolean(update.windowTransferIds || update.items.length || update.removedTransferIds.length);
    const missing = update.windowTransferIds
      ? new Set([...requestedAdditional].filter(id => !ids.has(id))) : new Set(this.missingMembers[direction]);
    for (const row of update.items) missing.delete(row.transferId);
    for (const id of update.removedTransferIds) if (requestedAdditional.has(id)) missing.add(id);
    if (missing.size || this.missingMembers[direction].length)
      this.missingMembers = { ...this.missingMembers, [direction]: [...missing] };
    const rows = membership ? mergeTransferHistory([], [...ids]
      .map(id => records.get(id)).filter((row): row is TransferStateDto => row !== undefined)) : current.items;
    let summaries = update.groupSummaries?.items ?? current.summaries;
    if (update.groupSummaries && pendingMembers.length) {
      const pendingGroups = new Set(transferGroupKeys(direction, pendingMembers).map(transferGroupKeyId));
      const included = new Set(summaries.map(summary => transferGroupKeyId(summary.key)));
      summaries = [...summaries, ...current.summaries.filter(summary => pendingGroups.has(transferGroupKeyId(summary.key))
        && !included.has(transferGroupKeyId(summary.key)))];
    }
    this.timeline = { ...this.timeline, [direction]: {
      ...current, items: rows, coverage: update.retainedCoverage, cursor: update.nextCursor ?? null,
      boundary: membership ? rows.find(row => row.transferId === update.boundaryTransferId) ?? null : current.boundary,
      summaries, summaryCoverage: update.groupSummaries?.retainedCoverage ?? current.summaryCoverage,
      summaryError: update.groupSummaries ? summaryFailure(update.groupSummaries) : current.summaryError,
      loading: update.windowTransferIds ? false : current.loading,
      error: update.windowTransferIds ? null : current.error,
    } };
    return ids;
  }

  registerMembers(direction: TransferDirection, rows: TransferStateDto[]): void {
    for (const row of rows) {
      const records = this.rows[direction];
      const next = updatedTransfer(untrack(() => records.get(row.transferId)),
        { transferId: row.transferId, revision: row.revision, added: row });
      if (next) records.set(row.transferId, next);
    }
    const previous = this.additionalIds[direction];
    const next = new Set(rows.map(row => row.transferId));
    const changed = previous.size !== next.size || [...next].some(id => !previous.has(id));
    this.additionalIds[direction] = next;
    if (rows.length) this.timeline = { ...this.timeline, [direction]: {
      ...this.timeline[direction], items: mergeTransferHistory(this.timeline[direction].items, rows),
    } };
    if (changed) this.scopeRevision[direction]++;
  }

  afterMutation(direction: TransferDirection): Promise<void> | void {
    if (this.observedDirection !== direction) return this.load(direction, true);
  }

  async load(direction: TransferDirection, reset = false): Promise<void> {
    if (this.httpRead[direction] || !reset && this.timeline[direction].loading) return;
    const limit = this.pageLimit[direction] + (!reset && this.scopeRevision[direction] ? defaultPageSize : 0);
    // A fresh stream already supplies the whole requested extent. Reading it
    // over HTTP first duplicates that work on connected navigation and Retry.
    if (this.connected && !this.observationFailed) {
      this.pageLimit[direction] = limit;
      this.timeline = { ...this.timeline, [direction]: { ...this.timeline[direction], loading: true, error: null } };
      this.scopeRevision[direction]++;
      return;
    }
    this.stop(direction);
    this.httpRead[direction] = true;
    this.timeline = { ...this.timeline, [direction]: { ...this.timeline[direction], loading: true, error: null } };
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
      this.timeline = { ...this.timeline, [direction]: { ...this.timeline[direction], loading: false, error: asError(error) } };
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
