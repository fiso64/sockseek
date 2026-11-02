import { nestedPageSize, type DaemonApiClient } from '../../api/client';
import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
import { jobStateSummary } from '../../api/job-summary';
import { updatedJob } from '../../api/state.svelte';
import type { JobStateDto, JobSummaryDto, Schema } from '../../api/types';

/** One displayed server scope; it never interprets live/history retirement. */
export class JobListController {
  items = $state.raw<JobSummaryDto[]>([]);
  cursor = $state<string | null>(null);
  loading = $state(false);
  error = $state<Error | null>(null);
  refreshRevision = $state(0);
  private rows = new Map<string, JobStateDto>();
  private ids: string[] = [];
  private limit = nestedPageSize;
  private active = false;
  private status: LiveConnectionStatus = 'connecting';
  private generation = 0;
  private cancel?: () => void;

  constructor(private readonly api: Pick<DaemonApiClient, 'getJobListSnapshot'>,
    private readonly observe: DaemonLiveClient['observeJobList']) {}

  setActive(active: boolean, status: LiveConnectionStatus): void {
    if (this.active === active && this.status === status) return;
    this.active = active;
    this.status = status;
    this.stop();
    if (active && status !== 'connecting' && status !== 'reconnecting') this.refresh();
  }

  stop(): void {
    this.generation++;
    this.cancel?.();
    this.cancel = undefined;
    this.loading = false;
  }

  loadMore(): void {
    if (!this.cursor || this.loading) return;
    this.limit += nestedPageSize;
    this.refresh();
  }

  afterArchive(submissionId: string): void {
    this.items = this.items.filter(row => row.submissionId !== submissionId);
    // Commands may finish while an old snapshot is in flight. Replace its scope
    // after acceptance rather than maintaining archive tombstones in the client.
    if (this.active) this.refresh();
  }

  refresh(): void {
    this.stop();
    if (!this.active) return;
    const generation = this.generation;
    this.loading = true;
    this.error = null;
    const scope: Schema<'JobListScopeDto'> = {
      query: { lifecycleState: null, terminalOutcome: null, kind: null, workflowId: null,
        includeAll: false, newestFirst: true, excludeExtractResults: true, archived: false },
      limit: this.limit,
    };
    const next = (value: Schema<'JobListUpdateDto'>) => {
      if (generation !== this.generation) return;
      this.apply(value);
      this.loading = false;
    };
    const fail = (cause: unknown) => {
      if (generation !== this.generation) return;
      this.loading = false;
      this.error = cause instanceof Error ? cause : new Error(String(cause));
    };
    if (this.status === 'connected') {
      try { this.cancel = this.observe(scope, next, fail); }
      catch (cause) { fail(cause); }
    } else {
      const request = new AbortController();
      this.cancel = () => request.abort();
      void this.api.getJobListSnapshot(scope, request.signal).then(next).catch(fail);
    }
  }

  private apply(update: Schema<'JobListUpdateDto'>): void {
    const initial = update.windowJobIds != null;
    if (initial) {
      // A fresh scope is authoritative even with lower retained revisions.
      this.rows.clear();
      this.ids = [...update.windowJobIds!];
      this.refreshRevision++;
    }
    let membershipChanged = initial;
    for (const delta of update.jobs) {
      const before = this.rows.get(delta.jobId);
      const row = updatedJob(before, delta);
      if (!row) continue;
      this.rows.set(row.jobId, row);
      membershipChanged ||= !before || before.display.displayId !== row.display.displayId;
    }
    for (const id of update.removedJobIds) { this.rows.delete(id); membershipChanged = true; }
    if (membershipChanged) this.ids = [...this.rows.values()]
      .sort((a, b) => Number(b.display.displayId) - Number(a.display.displayId) || b.jobId.localeCompare(a.jobId))
      .map(row => row.jobId);
    this.items = this.ids.map(id => jobStateSummary(this.rows.get(id)!));
    this.cursor = update.nextCursor ?? null;
  }
}
