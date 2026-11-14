import type { DaemonApiClient } from '../../api/client';
import type { DaemonLiveClient } from '../../api/live';
import type { Schema } from '../../api/types';
import { jobStateSummary } from '../../api/job-summary';
import { jobDetailVersion } from './details';
import { resolvePresentedJob } from './presentation';

/** Resolves deep links/extraction results and rejects reads overtaken by relevant changes. */
export class JobRouteReader {
  constructor(private readonly api: Pick<DaemonApiClient, 'getJob'>, private readonly live: DaemonLiveClient) {}

  async read(jobId: string, signal: AbortSignal): Promise<Schema<'JobDetailDto'>> {
    let revision = 0;
    const stop = this.live.onState((_state, changes) => {
      const change = changes.jobs.get(jobId);
      if (changes.snapshot
        || jobDetailVersion(change?.previous ? jobStateSummary(change.previous) : undefined)
          !== jobDetailVersion(change?.current ? jobStateSummary(change.current) : undefined)) revision++;
    });
    try {
      for (;;) {
        const before = revision;
        const detail = await this.api.getJob(jobId, signal);
        signal.throwIfAborted();
        const current = this.live.reducer.state.jobs.get(jobId);
        if (before === revision || jobDetailVersion(detail.summary)
          === jobDetailVersion(current ? jobStateSummary(current) : undefined)) return detail;
      }
    } finally { stop(); }
  }

  async resolve(jobId: string, signal: AbortSignal): Promise<Schema<'JobDetailDto'>> {
    return this.resolveDetail(await this.read(jobId, signal), signal);
  }

  resolveDetail(detail: Schema<'JobDetailDto'>, signal: AbortSignal): Promise<Schema<'JobDetailDto'>> {
    return resolvePresentedJob(detail, id => this.read(id, signal), signal);
  }
}
