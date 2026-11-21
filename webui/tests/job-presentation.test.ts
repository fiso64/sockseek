import { jobSummary } from './job-fixture';
import { afterEach, expect, it, vi } from 'vitest';
import type { JobSummaryDto, Schema } from '../src/api/types';
import { canReuseJobDetail, jobDetailVersion, startJobDetailRefresh } from '../src/domains/jobs/detail-reads';
import { presentJob, presentedJobs, presentedChildren, resolvePresentedJob, sortPresentedJobsNewestFirst } from '../src/domains/jobs/presentation';
import { searchJobMode } from '../src/domains/jobs/kind';

const imported = jobSummary({ jobId: 'import', display: { kind: 'extract' }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' }, relationships: { resultJobId: 'result' } }) satisfies JobSummaryDto;
const result = jobSummary({ jobId: 'result', display: { kind: 'job-list' }, lifecycle: { lifecycleState: 'Running' } }) satisfies JobSummaryDto;
const detail = (summary: JobSummaryDto): Schema<'JobDetailDto'> => ({ summary, payload: null, childCount: 0, sourceJobAvailable: false });

afterEach(() => vi.useRealTimers());

it('ignores progress noise but observes root lifecycle and extracted-result changes', () => {
  const job = jobSummary({ jobId: 'root', lifecycle: { lifecycleState: 'Running' }, relationships: { resultJobId: null } }) satisfies JobSummaryDto;
  expect(jobDetailVersion(jobSummary({ discovery: { rawResultCount: 100 } }, job)))
    .toBe(jobDetailVersion(job));
  expect(jobDetailVersion(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' } }, job)))
    .not.toBe(jobDetailVersion(job));
  expect(jobDetailVersion(jobSummary({ relationships: { resultJobId: 'result' } }, job)))
    .not.toBe(jobDetailVersion(job));
  expect(jobDetailVersion(undefined)).not.toBe(jobDetailVersion(job));
  expect(canReuseJobDetail(job, job)).toBe(true);
  expect(canReuseJobDetail(jobSummary({ lifecycle: { activityPhase: 'None' } }, job), jobSummary({ lifecycle: { activityPhase: 'RunningChildren' } }, job))).toBe(false);
  expect(canReuseJobDetail(jobSummary({ lifecycle: { lifecycleState: 'Pending' } }, job), job)).toBe(false);
  expect(canReuseJobDetail(job, undefined)).toBe(false);
  expect(canReuseJobDetail(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' } }, job), undefined)).toBe(true);
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

it('uses accepted search projection and group presentation for compact mode badges', () => {
  const search = jobSummary({ jobId: 'search', display: { kind: 'search', searchGroupResults: false } }) satisfies JobSummaryDto;
  expect(searchJobMode(jobSummary({ display: { searchDefaultProjection: 'GenericFile' } }, search))).toBe('generic');
  expect(searchJobMode(jobSummary({ display: { searchDefaultProjection: 'Track' } }, search))).toBe('track');
  expect(searchJobMode(jobSummary({ display: { searchDefaultProjection: 'Album' } }, search))).toBe('album');
  expect(searchJobMode(jobSummary({ display: { searchDefaultProjection: 'Track', searchGroupResults: true } }, search))).toBe('song-groups');
  expect(searchJobMode(jobSummary({ display: { searchDefaultProjection: 'Album', searchGroupResults: true } }, search))).toBe('album-groups');
  expect(searchJobMode(jobSummary({ display: { searchDefaultProjection: 'Track', searchGroupResults: null } }, search))).toBeNull();
  expect(searchJobMode(jobSummary({ display: { searchDefaultProjection: null } }, search))).toBeNull();
  expect(searchJobMode(jobSummary({ display: { kind: 'song', searchDefaultProjection: 'Track' } }, search))).toBeNull();
});

it('folds successful extraction into the executable result without duplicate rows or changing its owner', () => {
  const followup = jobSummary({ jobId: 'followup', display: { kind: 'remote-file' }, relationships: { sourceJobId: 'result' } }) satisfies JobSummaryDto;
  const jobs = [imported, result, followup];
  const rows = presentedJobs(jobs, new Map(jobs.map((job) => [job.jobId!, job])));
  expect(rows.map((row) => row.job.jobId)).toEqual(['result', 'followup']);
  expect(rows[0]?.root).toBe(imported);
  expect(rows[0]?.job.lifecycle.lifecycleState).toBe('Running');
  const olderImport = jobSummary({ display: { workflowId: 'older' } }, imported);
  const olderResult = jobSummary({ display: { workflowId: 'older' }, relationships: { sourceJobId: imported.jobId } }, result);
  const independent = jobSummary({ display: { workflowId: 'newer' }, relationships: { sourceJobId: imported.jobId } }, followup);
  const scoped = [olderImport, olderResult, independent];
  expect(presentedJobs(scoped, new Map(scoped.map(job => [job.jobId!, job])))
    .map(row => row.job.jobId)).toEqual(['result', 'followup']);
});

it('orders wire timestamps by instant when whole seconds omit their fractional part', () => {
  const entries = [
    jobSummary({ jobId: 'fifth', display: { createdAtUtc: '2026-09-30T21:33:51+00:00', displayId: 5 } }),
    jobSummary({ jobId: 'ninth', display: { createdAtUtc: '2026-09-30T21:33:51.176+00:00', displayId: 9 } }),
    jobSummary({ jobId: 'earlier', display: { createdAtUtc: '2026-09-30T21:33:50.989+00:00', displayId: 4 } }),
  ].map((root) => ({ root, job: root, missingResultId: null, error: null }));
  expect(sortPresentedJobsNewestFirst(entries).map((entry) => entry.job.jobId)).toEqual(['ninth', 'fifth', 'earlier']);
});

it('does not turn active or failed extraction into its result, and reports missing or cyclic results', () => {
  const jobs = new Map<string, JobSummaryDto>([[result.jobId, result]]);
  expect(presentJob(jobSummary({ lifecycle: { lifecycleState: 'Running' } }, imported), jobs).job.display.kind).toBe('extract');
  expect(presentJob(jobSummary({ lifecycle: { terminalOutcome: 'Failed' } }, imported), jobs).job.display.kind).toBe('extract');
  expect(presentJob(imported, new Map()).missingResultId).toBe('result');
  const cyclic = new Map<string, JobSummaryDto>([[imported.jobId, imported], ['result', jobSummary({ jobId: 'result', relationships: { resultJobId: 'import' } }, imported)]]);
  expect(presentJob(imported, cyclic).error?.message).toContain('cycle');
});

it('folds nested execution siblings once, but does not hide a still-active extract step', () => {
  const rows = [imported, result];
  expect(presentedChildren(rows, new Map(rows.map((job) => [job.jobId!, job]))).map((entry) => entry.job.jobId)).toEqual(['result']);
  const active = jobSummary({ lifecycle: { lifecycleState: 'Running' } }, imported) satisfies JobSummaryDto;
  const activeRows = [active, result];
  expect(presentedChildren(activeRows, new Map(activeRows.map((job) => [job.jobId!, job]))).map((entry) => entry.job.jobId)).toEqual(['import', 'result']);
});

it('does not follow malformed result identities or cross-submission ownership', async () => {
  const mismatched = new Map<string, JobSummaryDto>([['result', jobSummary({ display: { submissionId: 'different' } }, result)]]);
  expect(presentJob(jobSummary({ display: { submissionId: 'original' } }, imported), mismatched).error?.message).toContain('different submission');
  const fetch = vi.fn().mockResolvedValue(detail(jobSummary({ jobId: 'wrong' }, result)));
  await expect(resolvePresentedJob(detail(imported), fetch, new AbortController().signal)).rejects.toThrow('different job identity');
  expect(fetch).toHaveBeenCalledTimes(1);
});

it('resolves a nested result chain with only the linked details and stops on cancellation', async () => {
  const nested = jobSummary({ jobId: 'result', relationships: { resultJobId: 'leaf' } }, imported);
  const leaf = jobSummary({ jobId: 'leaf' }, result);
  const fetch = vi.fn().mockResolvedValueOnce(detail(nested)).mockResolvedValueOnce(detail(leaf));
  expect((await resolvePresentedJob(detail(imported), fetch, new AbortController().signal)).summary).toEqual(leaf);
  expect(fetch.mock.calls).toEqual([['result'], ['leaf']]);
  const controller = new AbortController();
  const aborted = vi.fn(async () => { controller.abort(); return detail(nested); });
  await expect(resolvePresentedJob(detail(imported), aborted, controller.signal)).rejects.toThrow();
  expect(aborted).toHaveBeenCalledTimes(1);
});
