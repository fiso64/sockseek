import { SvelteMap, createSubscriber } from 'svelte/reactivity';
import { untrack } from 'svelte';
import type {
  DaemonStateDto, JobStateDto, SearchStateDto, StateSnapshotDto, StateUpdateBatchDto,
  TransferStateDto, WireInteger, WorkflowStateDto, Schema,
} from './types';
import { wireInteger } from './types';
import type { BatchApplyStatus } from './live';

export type LiveTransferSummary = { [Field in keyof Schema<'LiveTransferSummaryDto'>]: number };

function transferSummary(value?: Schema<'LiveTransferSummaryDto'> | null): LiveTransferSummary {
  return {
    active: Number(value?.active ?? 0), downloads: Number(value?.downloads ?? 0),
    uploads: Number(value?.uploads ?? 0), queued: Number(value?.queued ?? 0),
    failed: Number(value?.failed ?? 0), downloadRate: Number(value?.downloadRate ?? 0),
    uploadRate: Number(value?.uploadRate ?? 0),
  };
}

/** Keyed reactive reads: observing one ID does not subscribe to every live row. */
export interface ReplicatedDaemonState {
  readonly daemon: DaemonStateDto | null;
  readonly workflows: ReadonlyMap<string, WorkflowStateDto>;
  readonly jobs: ReadonlyMap<string, JobStateDto>;
  readonly rootJobs: ReadonlyMap<string, JobStateDto>;
  readonly childCounts: ReadonlyMap<string, { total: number; retrievals: number }>;
  readonly searches: ReadonlyMap<string, SearchStateDto>;
  readonly transfers: ReadonlyMap<string, TransferStateDto>;
  readonly readTransfer: (id: string) => TransferStateDto | undefined;
  readonly transferSummary: Readonly<LiveTransferSummary>;
  readonly epoch: string | null;
  readonly sequence: WireInteger;
}

interface RowChange<T> { previous: T | undefined; current: T | undefined; }
export interface DaemonStateChanges {
  snapshot: boolean;
  initialSnapshot: boolean;
  jobHistoryPosition: Schema<'StateStreamPositionDto'> | null;
  previousDaemon: DaemonStateDto | null;
  jobs: ReadonlyMap<string, RowChange<JobStateDto>>;
  transfers: ReadonlyMap<string, RowChange<TransferStateDto>>;
}

function newer(next: WireInteger, current: WireInteger): boolean {
  return wireInteger(next) > wireInteger(current);
}

export function updatedJob(current: JobStateDto | undefined, delta: Schema<'JobDeltaDto'>): JobStateDto | undefined {
  if (current && !newer(delta.revision, current.revision)) return current;
  if (delta.added) return delta.added;
  return current ? {
    ...current, revision: delta.revision,
    display: delta.display ?? current.display,
    lifecycle: delta.lifecycle ?? current.lifecycle,
    discovery: delta.discovery ?? current.discovery,
    relationships: delta.relationships ?? current.relationships,
    stats: delta.stats ?? current.stats,
  } : undefined;
}

export function updatedTransfer(current: TransferStateDto | undefined, delta: Schema<'TransferDeltaDto'>): TransferStateDto | undefined {
  if (current && !newer(delta.revision, current.revision)) return current;
  if (delta.added) return delta.added;
  return current ? {
    ...current, revision: delta.revision,
    status: delta.status ?? current.status,
    progress: delta.progress ?? current.progress,
    scheduling: delta.scheduling ?? current.scheduling,
  } : undefined;
}

// Membership indexes are private, non-reactive bookkeeping. Consumers subscribe
// to the actual records; retirement visits only the owner's dependent IDs.
type Membership = Map<string, Set<string>>;
function move(index: Membership, id: string, previous?: string | null, current?: string | null): void {
  if (previous === current) return;
  if (previous) {
    const members = index.get(previous);
    members?.delete(id);
    if (!members?.size) index.delete(previous);
  }
  if (current) {
    let members = index.get(current);
    if (!members) index.set(current, members = new Set());
    members.add(id);
  }
}

/** Keyed transfer reads, including absent IDs, shared by global and page scopes. */
export class TransferRows extends SvelteMap<string, TransferStateDto> {
  private readers = new Map<string, { subscribe: () => void; update: () => void }>();
  constructor() { super(); }

  // SvelteMap.get on an absent key otherwise observes every map change.
  readonly read = (id: string): TransferStateDto | undefined => {
    const subscribe = this.readers.get(id)?.subscribe ?? createSubscriber(update => {
      this.readers.set(id, { subscribe, update });
      return () => { this.readers.delete(id); };
    });
    subscribe();
    return untrack(() => this.get(id));
  };

  override set(id: string, row: TransferStateDto): this {
    if (untrack(() => this.get(id)) === row) return this;
    super.set(id, row);
    this.readers.get(id)?.update();
    return this;
  }

  override delete(id: string): boolean {
    const removed = super.delete(id);
    if (removed) this.readers.get(id)?.update();
    return removed;
  }

  override clear(): void {
    const ids = [...this.keys()];
    super.clear();
    for (const id of ids) this.readers.get(id)?.update();
  }
}

export class DaemonStateReducer implements ReplicatedDaemonState {
  private currentDaemon = $state.raw<DaemonStateDto | null>(null);
  private currentEpoch = $state<string | null>(null);
  private currentSequence = $state<WireInteger>(0);
  private workflowRows = new SvelteMap<string, WorkflowStateDto>();
  private jobRows = new SvelteMap<string, JobStateDto>();
  private roots = new SvelteMap<string, JobStateDto>();
  private children = new SvelteMap<string, { total: number; retrievals: number }>();
  private searchRows = new SvelteMap<string, SearchStateDto>();
  private transferRows = new TransferRows();
  readonly readTransfer = this.transferRows.read;
  private counts = $state.raw<LiveTransferSummary>(transferSummary());
  private jobsByWorkflow: Membership = new Map();
  private searchesByWorkflow: Membership = new Map();
  private transfersByWorkflow: Membership = new Map();
  private transfersByJob: Membership = new Map();
  private changedJobs = new Map<string, RowChange<JobStateDto>>();
  private changedTransfers = new Map<string, RowChange<TransferStateDto>>();
  private snapshotApplied = false;
  private initialSnapshot = false;
  private jobHistoryPosition: Schema<'StateStreamPositionDto'> | null = null;
  private previousDaemon: DaemonStateDto | null = null;

  get daemon() { return this.currentDaemon; }
  get epoch() { return this.currentEpoch; }
  get sequence() { return this.currentSequence; }
  get workflows(): ReadonlyMap<string, WorkflowStateDto> { return this.workflowRows; }
  get jobs(): ReadonlyMap<string, JobStateDto> { return this.jobRows; }
  get rootJobs(): ReadonlyMap<string, JobStateDto> { return this.roots; }
  get childCounts(): ReadonlyMap<string, { total: number; retrievals: number }> { return this.children; }
  get searches(): ReadonlyMap<string, SearchStateDto> { return this.searchRows; }
  get transfers(): ReadonlyMap<string, TransferStateDto> { return this.transferRows; }
  get transferSummary(): Readonly<LiveTransferSummary> { return this.counts; }

  /** Stable read facade, not a copied/sorted snapshot of every collection. */
  get state(): ReplicatedDaemonState { return this; }

  /** Fold buffered updates before publication without copying unchanged rows. */
  takeChanges(): DaemonStateChanges {
    const changes = {
      snapshot: this.snapshotApplied, initialSnapshot: this.initialSnapshot,
      jobHistoryPosition: this.jobHistoryPosition,
      previousDaemon: this.previousDaemon, jobs: this.changedJobs, transfers: this.changedTransfers,
    };
    this.changedJobs = new Map();
    this.changedTransfers = new Map();
    this.snapshotApplied = this.initialSnapshot = false;
    this.previousDaemon = this.currentDaemon;
    return changes;
  }

  applySnapshot(snapshot: StateSnapshotDto): ReplicatedDaemonState {
    if (snapshot.scope.kind !== 'daemon') throw new Error('Expected a daemon-scoped live snapshot.');
    this.initialSnapshot ||= this.currentEpoch === null;
    this.snapshotApplied = true;
    this.jobHistoryPosition = snapshot.jobHistoryPosition ?? null;
    this.currentDaemon = snapshot.daemon ?? null;
    this.counts = transferSummary(snapshot.transferSummary);
    this.workflowRows.clear();
    for (const row of snapshot.workflows) this.workflowRows.set(row.summary.workflowId, row);
    // Replacement is authoritative even when a reconnect changes the epoch.
    this.replace(this.jobRows, snapshot.jobs, row => row.jobId, (id, row) => this.setJob(id, row));
    this.replace(this.searchRows, snapshot.searches, row => row.jobId, (id, row) => this.setSearch(id, row));
    this.replace(this.transferRows, snapshot.transfers, row => row.transferId, (id, row) => this.setTransfer(id, row));
    this.currentEpoch = snapshot.position.epoch;
    this.currentSequence = snapshot.position.sequence;
    return this;
  }

  applyBatch(batch: StateUpdateBatchDto): BatchApplyStatus {
    if (batch.scope.kind !== 'daemon') throw new Error('Expected a daemon-scoped live batch.');
    if (this.currentEpoch === null || this.currentEpoch !== batch.epoch) return 'recovery-required';
    if (!newer(batch.sequence, this.currentSequence)) return 'stale';
    if (wireInteger(batch.previousSequence) > wireInteger(this.currentSequence)) return 'recovery-required';
    const state = batch.state;
    if (state.transferSummary) this.counts = transferSummary(state.transferSummary);
    if (state.daemon && (!this.currentDaemon || newer(state.daemon.revision, this.currentDaemon.revision)))
      this.currentDaemon = state.daemon;
    for (const row of state.workflows) {
      const current = this.workflowRows.get(row.summary.workflowId);
      if (!current || newer(row.revision, current.revision)) this.workflowRows.set(row.summary.workflowId, row);
    }
    for (const delta of state.jobs) {
      this.setJob(delta.jobId, updatedJob(this.jobRows.get(delta.jobId), delta));
    }
    for (const row of state.searches) {
      const current = this.searchRows.get(row.jobId);
      if (!current || newer(row.revision, current.revision)) this.setSearch(row.jobId, row);
    }
    for (const delta of state.transfers) {
      this.setTransfer(delta.transferId, updatedTransfer(this.transferRows.get(delta.transferId), delta));
    }
    for (const id of state.removedTransferIds) this.setTransfer(id, undefined);
    for (const id of state.removedSearchJobIds) this.setSearch(id, undefined);
    for (const id of state.removedJobIds) this.removeJob(id);
    for (const id of state.removedWorkflowIds) {
      this.workflowRows.delete(id);
      for (const job of this.jobsByWorkflow.get(id) ?? []) this.removeJob(job);
      for (const search of this.searchesByWorkflow.get(id) ?? []) this.setSearch(search, undefined);
      for (const transfer of this.transfersByWorkflow.get(id) ?? []) this.setTransfer(transfer, undefined);
    }
    this.currentSequence = batch.sequence;
    return 'applied';
  }

  private replace<T>(rows: ReadonlyMap<string, T>, next: T[], key: (row: T) => string,
    set: (id: string, row: T | undefined) => void): void {
    const remaining = new Set(rows.keys());
    for (const row of next) { const id = key(row); remaining.delete(id); set(id, row); }
    for (const id of remaining) set(id, undefined);
  }

  private setJob(id: string, row: JobStateDto | undefined): void {
    const previous = this.jobRows.get(id);
    if (previous === row) return;
    move(this.jobsByWorkflow, id, previous?.display.workflowId, row?.display.workflowId);
    const beforeParent = previous?.relationships.parentJobId;
    const afterParent = row?.relationships.parentJobId;
    const beforeRetrieval = previous?.display.kind === 'retrieve-folder';
    const afterRetrieval = row?.display.kind === 'retrieve-folder';
    if (beforeParent !== afterParent || beforeRetrieval !== afterRetrieval) {
      if (beforeParent) this.updateChildren(beforeParent, -1, beforeRetrieval ? -1 : 0);
      if (afterParent) this.updateChildren(afterParent, 1, afterRetrieval ? 1 : 0);
    }
    if (row && !afterParent) this.roots.set(id, row); else this.roots.delete(id);
    this.changedJobs.set(id, { previous: this.changedJobs.has(id) ? this.changedJobs.get(id)!.previous : previous, current: row });
    if (row) this.jobRows.set(id, row); else this.jobRows.delete(id);
  }

  private removeJob(id: string): void {
    this.setJob(id, undefined);
    this.setSearch(id, undefined);
    for (const transfer of this.transfersByJob.get(id) ?? []) this.setTransfer(transfer, undefined);
  }

  private updateChildren(id: string, total: number, retrievals: number): void {
    const previous = this.children.get(id);
    const next = { total: (previous?.total ?? 0) + total, retrievals: (previous?.retrievals ?? 0) + retrievals };
    if (next.total) this.children.set(id, next); else this.children.delete(id);
  }

  private setSearch(id: string, row: SearchStateDto | undefined): void {
    move(this.searchesByWorkflow, id, this.searchRows.get(id)?.workflowId, row?.workflowId);
    if (row) this.searchRows.set(id, row); else this.searchRows.delete(id);
  }

  private setTransfer(id: string, row: TransferStateDto | undefined): void {
    const previous = this.transferRows.get(id);
    if (previous === row) return;
    move(this.transfersByWorkflow, id, previous?.identity.workflowId, row?.identity.workflowId);
    move(this.transfersByJob, id, previous?.identity.jobId, row?.identity.jobId);
    this.changedTransfers.set(id, { previous: this.changedTransfers.has(id) ? this.changedTransfers.get(id)!.previous : previous, current: row });
    if (row) this.transferRows.set(id, row); else this.transferRows.delete(id);
  }

}
