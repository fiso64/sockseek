import { daemonApi, nestedPageSize, type DaemonApiClient } from '../../api/client';
import type { Schema } from '../../api/types';
import { sameWireInteger } from '../../api/types';
import type { SubtreeRule } from './preview-selection';

type Summary = Schema<'JobPreviewSummaryDto'>;
type Node = Schema<'JobPreviewNodeDto'>;
type Branch = { items: Node[]; nextCursor: string | null; loading: boolean; error: Error | null };
export interface PreviewState {
  committing?: boolean;
  receipt?: Schema<'CommitJobPreviewResponseDto'> | null;
  summary: Summary | null;
  loading: boolean;
  branches: ReadonlyMap<string | null, Branch>;
  error: Error | null;
}

/** One browser-owned review session; no simulated planner or persisted selection. */
export class PreviewController {
  private state: PreviewState = { summary: null, loading: false, branches: new Map(), error: null };
  private listeners = new Set<(state: PreviewState) => void>();
  private lifetime = new AbortController();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private commitRequest: Schema<'CommitJobPreviewRequestDto'> | null = null;

  constructor(private readonly api: DaemonApiClient = daemonApi) {}

  subscribe(listener: (state: PreviewState) => void): () => void {
    this.listeners.add(listener);
    listener(this.state);
    return () => this.listeners.delete(listener);
  }

  stop(): void {
    this.lifetime.abort();
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }

  async create(request: Schema<'CreateJobPreviewRequestDto'>): Promise<void> {
    this.stop();
    this.lifetime = new AbortController();
    this.commitRequest = null;
    const signal = this.lifetime.signal;
    this.publish({ summary: null, loading: true, branches: new Map(), error: null });
    try {
      const result = await this.api.createJobPreview(request, signal);
      if (signal.aborted) return;
      this.publish({ ...this.state, summary: result.preview, loading: false });
      if (result.preview.state !== 'Planning') await this.loadInitialTree(result.preview);
      void this.followPlanning(signal);
    } catch (error) {
      if (!signal.aborted) this.publish({ ...this.state, loading: false, error: asError(error) });
    }
  }

  async refresh(waitForCompletion = false): Promise<void> {
    const summary = this.state.summary;
    const signal = this.lifetime.signal;
    if (!summary || signal.aborted) return;
    try {
      const next = await this.api.getJobPreview(summary.previewId, signal, waitForCompletion);
      if (signal.aborted) return;
      const changed = !sameWireInteger(next.revision, summary.revision);
      this.publish({ ...this.state, summary: next, branches: changed ? new Map() : this.state.branches, error: null });
      if (next.state !== 'Planning' && !this.state.branches.has(null)) await this.loadInitialTree(next);
    } catch (error) {
      if (!signal.aborted) this.publish({ ...this.state, error: asError(error) });
    }
  }

  async commit(selection: Schema<'RefSelectionExpressionDto'>, subtreeOverrides?: readonly SubtreeRule[]): Promise<void> {
    const summary = this.state.summary;
    const signal = this.lifetime.signal;
    if (!summary || signal.aborted || this.state.committing || this.state.receipt?.submissionId) return;
    const previous = this.commitRequest;
    const refs = [...new Set(selection.refs)].sort();
    const overrides = subtreeOverrides ? [...subtreeOverrides].sort((a, b) => a.ref.localeCompare(b.ref)) : undefined;
    const unchanged = previous?.selection.mode === selection.mode
      && previous.selection.refs.length === refs.length
      && previous.selection.refs.every((ref, index) => ref === refs[index])
      && JSON.stringify(previous.subtreeOverrides) === JSON.stringify(overrides);
    const request = unchanged ? previous : {
      revision: summary.revision,
      selection: { mode: selection.mode, refs },
      idempotencyKey: crypto.randomUUID(),
      ...(overrides ? { subtreeOverrides: overrides } : {}),
    };
    this.commitRequest = request;
    this.publish({ ...this.state, committing: true, receipt: null, error: null });
    try {
      const receipt = await this.api.commitJobPreview(summary.previewId, request, signal);
      if (signal.aborted) return;
      this.commitRequest = null;
      this.publish({ ...this.state, committing: false, receipt });
    } catch (error) {
      if (!signal.aborted) this.publish({ ...this.state, committing: false, error: asError(error) });
    }
  }

  async loadBranch(parentRef: string | null): Promise<void> {
    const summary = this.state.summary;
    const signal = this.lifetime.signal;
    const previous = this.state.branches.get(parentRef);
    if (!summary || signal.aborted || summary.state === 'Planning' || previous?.loading || (previous && !previous.error && !previous.nextCursor)) return;
    const pending: Branch = { items: previous?.items ?? [], nextCursor: previous?.nextCursor ?? null, loading: true, error: null };
    this.publish({ ...this.state, branches: new Map(this.state.branches).set(parentRef, pending) });
    try {
      const page = await this.api.getJobPreviewNodes(summary.previewId, parentRef, pending.nextCursor, nestedPageSize, signal);
      if (signal.aborted || this.state.branches.get(parentRef) !== pending) return;
      if (page.items.some((node) => (node.parentRef ?? null) !== parentRef)) throw new Error('Preview returned nodes from a different parent.');
      this.publish({ ...this.state, branches: new Map(this.state.branches).set(parentRef, { items: [...pending.items, ...page.items], nextCursor: page.nextCursor ?? null, loading: false, error: null }) });
    } catch (error) {
      if (signal.aborted || this.state.branches.get(parentRef) !== pending) return;
      this.publish({ ...this.state, branches: new Map(this.state.branches).set(parentRef, { ...pending, loading: false, error: asError(error) }) });
    }
  }

  private async loadInitialTree(summary: Summary): Promise<void> {
    await this.loadBranch(null);
    const isCurrent = () => {
      const current = this.state.summary;
      return !this.lifetime.signal.aborted && current?.previewId === summary.previewId
        && sameWireInteger(current.revision, summary.revision);
    };
    // Reveal the first page beyond short one-child wrappers such as
    // CSV Extract -> Job List, even when the import itself is large.
    let branch = this.state.branches.get(null);
    for (let depth = 0; depth < 4 && isCurrent(); depth++) {
      if (!branch || branch.loading || branch.error || branch.nextCursor || branch.items.length !== 1) break;
      const only = branch.items[0]!;
      if (Number(only.directChildCount) === 0) break;
      await this.loadBranch(only.ref);
      branch = this.state.branches.get(only.ref);
    }
    // The prototype opens ordinary small reviews as a tree. Keep that
    // presentation without fetching every branch of a large import: its
    // sibling branches and later pages remain user-driven.
    if (Number(summary.nodeCount) > 24) return;
    const queue = [...(this.state.branches.get(null)?.items ?? [])
      .filter((node) => Number(node.directChildCount) > 0).map((node) => node.ref)];
    const visited = new Set<string>();
    while (queue.length) {
      const ref = queue.shift()!;
      if (visited.has(ref)) continue;
      visited.add(ref);
      if (!isCurrent()) return;
      await this.loadBranch(ref);
      const branch = this.state.branches.get(ref);
      if (!branch || branch.loading || branch.error || branch.nextCursor) continue;
      queue.push(...branch.items.filter((node) => Number(node.directChildCount) > 0).map((node) => node.ref));
    }
  }

  private async followPlanning(signal: AbortSignal): Promise<void> {
    while (!signal.aborted && this.state.summary?.state === 'Planning') {
      await this.refresh(true);
      if (signal.aborted) return;
      if (this.state.error) {
        // Back off only failed requests. A healthy request waits at the planner
        // and returns as soon as the reviewed commands are available.
        this.timer = setTimeout(() => {
          this.timer = null;
          void this.followPlanning(signal);
        }, 1_000);
        return;
      }
    }
  }

  private publish(state: PreviewState): void {
    this.state = state;
    for (const listener of this.listeners) listener(state);
  }
}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}
