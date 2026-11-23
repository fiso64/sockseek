import { nestedPageSize, type DaemonApiClient } from '../../api/client';
import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
import { updatedJob } from '../../api/state.svelte';
import type { JobSummaryDto, Schema } from '../../api/types';

export type JobsTab = 'searches' | 'downloads';
type JobQuery = Schema<'JobQuery'>;

/** A Jobs page tab: newest-first execution roots, optionally of one submission. */
export function jobsTabQuery(tab: JobsTab, submissionId: string | null): JobQuery {
  return { kind: tab === 'searches' ? 'search' : null, submissionId, includeAll: false, newestFirst: true,
    excludeExtractResults: true, excludeKinds: tab === 'downloads' ? ['search', 'retrieve-folder'] : null };
}

/** Direct children of one job in execution order, optionally of one kind. */
export function childJobsQuery(parentJobId: string, kind: JobSummaryDto['display']['kind'] | null = null): JobQuery {
  return { parentJobId, kind, includeAll: false, newestFirst: false, excludeExtractResults: true };
}

/** One displayed server scope; it never interprets live/history retirement. */
export class JobListController {
  items = $state.raw<JobSummaryDto[]>([]);
  /** Result jobs of displayed extractions, as linked by the server. */
  linkedResults = $state.raw<ReadonlyMap<string, JobSummaryDto>>(new Map());
  cursor = $state<string | null>(null);
  loading = $state(false);
  error = $state<Error | null>(null);
  scrollTop = 0;
  submissionId = $state<string | null>(null);

  filterSubmission(id: string | null): void {
    if (this.submissionId === id) return;
    this.submissionId = id;
    this.items = [];
    this.cursor = null;
    this.limit = nestedPageSize;
    this.scrollTop = 0;
    this.refresh();
  }
  private rows = new Map<string, JobSummaryDto>();
  private ids: string[] = [];
  private limit = nestedPageSize;
  private active = false;
  private status: LiveConnectionStatus = 'connecting';
  private generation = 0;
  private cancel?: () => void;
  private newestFirst = true;

  constructor(private readonly api: Pick<DaemonApiClient, 'getJobListSnapshot'>,
    private readonly observe: DaemonLiveClient['observeJobList'],
    private readonly query: (submissionId: string | null) => JobQuery) {}

  setActive(active: boolean, status: LiveConnectionStatus): void {
    if (this.active === active && this.status === status) return;
    this.active = active;
    this.status = status;
    if (active) this.refresh();
    else this.stop();
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
    this.items = this.items.filter(row => row.display.submissionId !== submissionId);
    // Commands may finish while an old snapshot is in flight. Replace its scope
    // after acceptance rather than maintaining deletion tombstones in the client.
    if (this.active) this.refresh();
  }

  refresh(): void {
    this.stop();
    if (!this.active) return;
    const generation = this.generation;
    this.loading = true;
    this.error = null;
    if (this.status === 'connecting' || this.status === 'reconnecting') return;
    const query = this.query(this.submissionId);
    this.newestFirst = query.newestFirst;
    const scope: Schema<'JobListScopeDto'> = { query, limit: this.limit };
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
      this.linkedResults = new Map();
    }
    if (update.linkedResults?.length)
      this.linkedResults = new Map([...this.linkedResults, ...update.linkedResults.map(job => [job.jobId, job] as const)]);
    let membershipChanged = initial;
    for (const delta of update.jobs) {
      const before = this.rows.get(delta.jobId);
      const row = updatedJob(before, delta);
      if (!row) continue;
      this.rows.set(row.jobId, row);
      membershipChanged ||= !before || before.display.displayId !== row.display.displayId;
    }
    for (const id of update.removedJobIds) { this.rows.delete(id); membershipChanged = true; }
    const direction = this.newestFirst ? -1 : 1;
    if (membershipChanged) this.ids = [...this.rows.values()]
      .sort((a, b) => direction * (Number(a.display.displayId) - Number(b.display.displayId) || a.jobId.localeCompare(b.jobId)))
      .map(row => row.jobId);
    this.items = this.ids.map(id => this.rows.get(id)!);
    this.cursor = update.nextCursor ?? null;
  }
}
