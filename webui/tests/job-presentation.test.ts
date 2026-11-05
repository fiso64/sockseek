import { afterEach, expect, it, vi } from 'vitest';
import type { JobSummaryDto, Schema } from '../src/api/types';
import { canReuseJobDetail, jobDetailVersion, startJobDetailRefresh } from '../src/domains/jobs/details';
import { presentJob, presentedJobs, presentedChildren, resolvePresentedJob, sortPresentedJobsNewestFirst } from '../src/domains/jobs/presentation';
import { searchJobMode } from '../src/domains/jobs/kind';

const imported = { jobId: 'import', kind: 'extract', lifecycleState: 'Terminal', terminalOutcome: 'Succeeded', resultJobId: 'result' } satisfies JobSummaryDto;
const result = { jobId: 'result', kind: 'job-list', lifecycleState: 'Running' } satisfies JobSummaryDto;
const detail = (summary: JobSummaryDto): Schema<'JobDetailDto'> => ({ summary, payload: null, childCount: 0 });

afterEach(() => vi.useRealTimers());

it('ignores progress noise but observes root lifecycle and extracted-result changes', () => {
  const job = { jobId: 'root', lifecycleState: 'Running', resultJobId: null } satisfies JobSummaryDto;
  expect(jobDetailVersion({ ...job, discoveryRawResultCount: 100 }))
    .toBe(jobDetailVersion(job));
  expect(jobDetailVersion({ ...job, lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' }))
    .not.toBe(jobDetailVersion(job));
  expect(jobDetailVersion({ ...job, resultJobId: 'result' }))
    .not.toBe(jobDetailVersion(job));
  expect(jobDetailVersion(undefined)).not.toBe(jobDetailVersion(job));
  expect(canReuseJobDetail(job, job)).toBe(true);
  expect(canReuseJobDetail({ ...job, activityPhase: 'None' }, { ...job, activityPhase: 'RunningChildren' })).toBe(false);
  expect(canReuseJobDetail({ ...job, lifecycleState: 'Pending' }, job)).toBe(false);
  expect(canReuseJobDetail(job, undefined)).toBe(false);
  expect(canReuseJobDetail({ ...job, lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' }, undefined)).toBe(true);
});

it('coalesces in-flight invalidations and replaces the superseded fallback timer', async () => {
  vi.useFakeTimers();
  const owner = new AbortController();
  let release!: () => void;
  const read = vi.fn().mockResolvedValue(undefined);
  read.mockImplementationOnce(() => new Promise<void>(resolve => { release = resolve; }));
  let polling = true;
  const invalidate = startJobDetailRefresh(read, owner.signal, () => polling);
  invalidate();
  invalidate();
  expect(read).toHaveBeenCalledTimes(1);
  release();
  await vi.advanceTimersByTimeAsync(0);
  expect(read).toHaveBeenCalledTimes(2);
  await vi.advanceTimersByTimeAsync(900);
  invalidate();
  await vi.advanceTimersByTimeAsync(0);
  expect(read).toHaveBeenCalledTimes(3);
  await vi.advanceTimersByTimeAsync(100);
  expect(read).toHaveBeenCalledTimes(3);
  await vi.advanceTimersByTimeAsync(900);
  expect(read).toHaveBeenCalledTimes(4);
  polling = false;
  await vi.advanceTimersByTimeAsync(1_000);
  expect(read).toHaveBeenCalledTimes(5);
  expect(vi.getTimerCount()).toBe(0);
  await vi.advanceTimersByTimeAsync(5_000);
  expect(read).toHaveBeenCalledTimes(5);
  invalidate();
  await vi.advanceTimersByTimeAsync(0);
  expect(read).toHaveBeenCalledTimes(6);
  expect(vi.getTimerCount()).toBe(0);
  // A subsequent failure or new execution can resume fallback recovery.
  polling = true;
  invalidate();
  await vi.advanceTimersByTimeAsync(1_000);
  expect(read).toHaveBeenCalledTimes(8);
  owner.abort();
  await vi.advanceTimersByTimeAsync(5_000);
  expect(read).toHaveBeenCalledTimes(8);
});

it('skips a trailing invalidation already covered by the completed response', async () => {
  vi.useFakeTimers();
  const owner = new AbortController();
  let release!: () => void;
  let stale = true;
  const read = vi.fn(() => new Promise<void>(resolve => { release = () => { stale = false; resolve(); }; }));
  const invalidate = startJobDetailRefresh(read, owner.signal, () => true, () => stale);
  invalidate();
  release();
  await vi.advanceTimersByTimeAsync(0);
  expect(read).toHaveBeenCalledTimes(1);
  await vi.advanceTimersByTimeAsync(1_000);
  expect(read).toHaveBeenCalledTimes(2); // The ordinary fallback still runs.
  owner.abort();
  release();
});

it('does not start queued reads after navigation aborts an in-flight owner', async () => {
  vi.useFakeTimers();
  const owner = new AbortController();
  let release!: () => void;
  const read = vi.fn(() => new Promise<void>(resolve => { release = resolve; }));
  const invalidate = startJobDetailRefresh(read, owner.signal, () => true);
  invalidate();
  owner.abort();
  release();
  await vi.advanceTimersByTimeAsync(5_000);
  invalidate();
  expect(read).toHaveBeenCalledTimes(1);
  expect(vi.getTimerCount()).toBe(0);
  const stopped = vi.fn().mockResolvedValue(undefined);
  startJobDetailRefresh(stopped, owner.signal, () => true)();
  expect(stopped).not.toHaveBeenCalled();
});

it('uses accepted search projection and aggregate presentation for compact mode badges', () => {
  const search = { jobId: 'search', kind: 'search', searchAggregatePresentation: false } satisfies JobSummaryDto;
  expect(searchJobMode({ ...search, searchDefaultProjection: 'GenericFile' })).toBe('generic');
  expect(searchJobMode({ ...search, searchDefaultProjection: 'Track' })).toBe('track');
  expect(searchJobMode({ ...search, searchDefaultProjection: 'Album' })).toBe('album');
  expect(searchJobMode({ ...search, searchDefaultProjection: 'Track', searchAggregatePresentation: true })).toBe('song-aggregate');
  expect(searchJobMode({ ...search, searchDefaultProjection: 'Album', searchAggregatePresentation: true })).toBe('album-aggregate');
  expect(searchJobMode({ ...search, searchDefaultProjection: 'Track', searchAggregatePresentation: null })).toBeNull();
  expect(searchJobMode({ ...search, searchDefaultProjection: null })).toBeNull();
  expect(searchJobMode({ ...search, kind: 'song', searchDefaultProjection: 'Track' })).toBeNull();
});

it('folds successful extraction into the executable result without duplicate rows or changing its owner', () => {
  const followup = { jobId: 'followup', kind: 'remote-file', sourceJobId: 'result' } satisfies JobSummaryDto;
  const jobs = [imported, result, followup];
  const rows = presentedJobs(jobs, new Map(jobs.map((job) => [job.jobId!, job])));
  expect(rows.map((row) => row.job.jobId)).toEqual(['result', 'followup']);
  expect(rows[0]?.root).toBe(imported);
  expect(rows[0]?.job.lifecycleState).toBe('Running');
  const olderImport = { ...imported, workflowId: 'older' };
  const olderResult = { ...result, workflowId: 'older', sourceJobId: imported.jobId };
  const independent = { ...followup, workflowId: 'newer', sourceJobId: imported.jobId };
  const scoped = [olderImport, olderResult, independent];
  expect(presentedJobs(scoped, new Map(scoped.map(job => [job.jobId!, job])))
    .map(row => row.job.jobId)).toEqual(['result', 'followup']);
});

it('orders wire timestamps by instant when whole seconds omit their fractional part', () => {
  const entries = [
    { jobId: 'fifth', createdAtUtc: '2026-09-30T21:33:51+00:00', displayId: 5 },
    { jobId: 'ninth', createdAtUtc: '2026-09-30T21:33:51.176+00:00', displayId: 9 },
    { jobId: 'earlier', createdAtUtc: '2026-09-30T21:33:50.989+00:00', displayId: 4 },
  ].map((root) => ({ root, job: root, missingResultId: null, error: null }));
  expect(sortPresentedJobsNewestFirst(entries).map((entry) => entry.job.jobId)).toEqual(['ninth', 'fifth', 'earlier']);
});

it('does not turn active or failed extraction into its result, and reports missing or cyclic results', () => {
  const jobs = new Map<string, JobSummaryDto>([[result.jobId, result]]);
  expect(presentJob({ ...imported, lifecycleState: 'Running' }, jobs).job.kind).toBe('extract');
  expect(presentJob({ ...imported, terminalOutcome: 'Failed' }, jobs).job.kind).toBe('extract');
  expect(presentJob(imported, new Map()).missingResultId).toBe('result');
  const cyclic = new Map<string, JobSummaryDto>([[imported.jobId, imported], ['result', { ...imported, jobId: 'result', resultJobId: 'import' }]]);
  expect(presentJob(imported, cyclic).error?.message).toContain('cycle');
});

it('folds nested execution siblings once, but does not hide a still-active extract step', () => {
  const rows = [imported, result];
  expect(presentedChildren(rows, new Map(rows.map((job) => [job.jobId!, job]))).map((entry) => entry.job.jobId)).toEqual(['result']);
  const active = { ...imported, lifecycleState: 'Running' } satisfies JobSummaryDto;
  const activeRows = [active, result];
  expect(presentedChildren(activeRows, new Map(activeRows.map((job) => [job.jobId!, job]))).map((entry) => entry.job.jobId)).toEqual(['import', 'result']);
});

it('does not follow malformed result identities or cross-submission ownership', async () => {
  const mismatched = new Map<string, JobSummaryDto>([['result', { ...result, submissionId: 'different' }]]);
  expect(presentJob({ ...imported, submissionId: 'original' }, mismatched).error?.message).toContain('different submission');
  const fetch = vi.fn().mockResolvedValue(detail({ ...result, jobId: 'wrong' }));
  await expect(resolvePresentedJob(detail(imported), fetch, new AbortController().signal)).rejects.toThrow('different job identity');
  expect(fetch).toHaveBeenCalledTimes(1);
});

it('resolves a nested result chain with only the linked details and stops on cancellation', async () => {
  const nested = { ...imported, jobId: 'result', resultJobId: 'leaf' };
  const leaf = { ...result, jobId: 'leaf' };
  const fetch = vi.fn().mockResolvedValueOnce(detail(nested)).mockResolvedValueOnce(detail(leaf));
  expect((await resolvePresentedJob(detail(imported), fetch, new AbortController().signal)).summary).toEqual(leaf);
  expect(fetch.mock.calls).toEqual([['result'], ['leaf']]);
  const controller = new AbortController();
  const aborted = vi.fn(async () => { controller.abort(); return detail(nested); });
  await expect(resolvePresentedJob(detail(imported), aborted, controller.signal)).rejects.toThrow();
  expect(aborted).toHaveBeenCalledTimes(1);
});
