import { expect, test, vi } from 'vitest';
import { JobListController } from '../src/domains/jobs/job-list.svelte';
import type { DaemonLiveClient } from '../src/api/live';
import type { JobSummaryDto, Schema } from '../src/api/types';
import { automaticJobListStats, automaticJobProgressPercent } from '../src/domains/jobs/list-stats';

function row(kind: JobSummaryDto['kind'], stats?: JobSummaryDto['stats'], lifecycleState?: JobSummaryDto['lifecycleState']): JobSummaryDto {
  return { kind, stats, lifecycleState: lifecycleState ?? 'Running' };
}

test('shows daemon-owned container progress and failure counts without reading children', () => {
  expect(automaticJobListStats(row('job-list', {
    memberCount: 12, completedMemberCount: 8, failedMemberCount: 1,
  }))).toEqual(['8 / 12 jobs finished', '1 unsuccessful']);
  expect(automaticJobListStats(row('album-groups', {
    memberCount: 5, completedMemberCount: 2, failedMemberCount: 0,
  }))).toEqual(['2 / 5 albums finished']);
  expect(automaticJobListStats(row('album', {
    memberCount: 9, completedMemberCount: 3, failedMemberCount: 0,
    folderCount: 7, fileCount: 9,
  }))).toEqual(['3 / 9 files finished']);
});

test('handles unavailable summary statistics without inventing progress', () => {
  expect(automaticJobListStats(row('song-groups', {
    memberCount: 3, completedMemberCount: null, failedMemberCount: null,
  }))).toEqual(['3 songs']);
  expect(automaticJobListStats(row('job-list'))).toEqual([]);
});

test('keeps kind-specific single-job stats', () => {
  expect(automaticJobListStats({ ...row('song', { candidateCount: 2 }), activityPhase: 'Searching' })).toEqual(['2 candidates']);
  expect(automaticJobListStats(row('remote-file', { knownBytes: 12_000_000 }))).toEqual(['12.0 MB']);
  expect(automaticJobListStats(row('remote-directory', { fileCount: 4 }))).toEqual(['4 files']);
  expect(automaticJobListStats(row('retrieve-folder', { newFilesFoundCount: 3 }))).toEqual(['3 new files']);
});

test('discovery counts yield to execution progress and terminal outcomes', () => {
  for (const kind of ['song', 'album'] as const) {
    const job = { kind, stats: { candidateCount: 20, folderCount: 30, memberCount: 10,
      completedMemberCount: 0, downloadedMemberCount: 0, skippedMemberCount: 0, failedMemberCount: 0 } };
    const discovery = automaticJobListStats({ ...job, lifecycleState: 'Running', activityPhase: 'Searching' });
    expect(discovery).toHaveLength(1);
    expect(automaticJobListStats({ ...job, lifecycleState: 'AwaitingSelection' })).toEqual(discovery);
    expect(automaticJobListStats({ ...job, lifecycleState: 'Running', activityPhase: 'Downloading' })).not.toEqual(discovery);
    for (const terminalOutcome of ['Succeeded', 'Failed', 'Skipped', 'Cancelled', 'PartialSuccess'] as const) {
      expect(automaticJobListStats({ ...job, lifecycleState: 'Terminal', terminalOutcome,
        activityPhase: 'Searching' })).not.toEqual(discovery);
    }
  }
});

test('keeps finished work distinct from successful work, including cancellation', () => {
  const stats = { memberCount: 10, completedMemberCount: 4, failedMemberCount: 1,
    skippedMemberCount: 1, downloadedMemberCount: 1, reusedMemberCount: 1 };
  expect(automaticJobListStats({ kind: 'remote-directory', stats, lifecycleState: 'Terminal', terminalOutcome: 'Cancelled' }))
    .toEqual(['4 / 10 files finished', '1 downloaded', '1 reused', '1 skipped', '1 unsuccessful']);
  for (const [kind, noun] of [['job-list', 'jobs'], ['song-groups', 'songs'], ['album-groups', 'albums']] as const) {
    expect(automaticJobListStats({ kind, stats, lifecycleState: 'Terminal', terminalOutcome: 'PartialSuccess' }))
      .toEqual([`4 / 10 ${noun} finished`, '2 succeeded', '1 skipped', '1 unsuccessful']);
    expect(automaticJobListStats({ kind, stats, lifecycleState: 'Pending' })).toEqual([`10 ${noun}`]);
  }
});

test('does not infer zero work or successful downloads from missing or unfinished facts', () => {
  for (const kind of ['song', 'album', 'song-groups', 'album-groups', 'extract', 'remote-directory'] as const) {
    expect(automaticJobListStats({ kind, lifecycleState: 'Pending', stats: { memberCount: 0 } })).toEqual([]);
    expect(automaticJobListStats({ kind, lifecycleState: 'Running', stats: { memberCount: 0 } })).toEqual([]);
  }
  expect(automaticJobListStats({ kind: 'album', lifecycleState: 'Terminal', terminalOutcome: 'Succeeded',
    stats: { memberCount: 9, completedMemberCount: 9 } })).toEqual(['9 files']);
  expect(automaticJobListStats({ kind: 'job-list', lifecycleState: 'Terminal', terminalOutcome: 'Succeeded',
    stats: { memberCount: 0, completedMemberCount: 0 } })).toEqual(['0 jobs']);
  expect(automaticJobListStats({ kind: 'remote-file', stats: { knownBytes: 0 } })).toHaveLength(1);
});

test('terminal reasons work without statistics and do not leak into active jobs', () => {
  for (const kind of ['song', 'album', 'song-groups', 'album-groups', 'job-list', 'extract', 'remote-file', 'remote-directory'] as const) {
    const job = { kind, failureReason: 'NoMatchingResults' as const, skipReason: 'AlreadyExists' as const };
    expect(automaticJobListStats({ ...job, lifecycleState: 'Terminal', terminalOutcome: 'Failed' })).toEqual(['No matching results']);
    expect(automaticJobListStats({ ...job, lifecycleState: 'Terminal', terminalOutcome: 'Skipped' })).toEqual(['Already exists']);
    expect(automaticJobListStats({ ...job, lifecycleState: 'Running' })).toEqual([]);
  }
  expect(automaticJobListStats({ kind: 'remote-file', lifecycleState: 'Terminal', terminalOutcome: 'Failed',
    failureMessage: 'Size mismatch' })).toEqual(['Size mismatch']);
  expect(automaticJobListStats({ kind: 'extract', lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' })).toEqual(['No job produced']);
});

test('uses byte progress for directories and count progress for running containers', () => {
  expect(automaticJobProgressPercent(row('album', {
    memberCount: 4, completedMemberCount: 1, progressPercent: 45.5,
  }, 'Running'))).toBe(45.5);
  expect(automaticJobProgressPercent(row('song-groups', {
    memberCount: 5, completedMemberCount: 2,
  }, 'Running'))).toBe(40);
  expect(automaticJobProgressPercent(row('album-groups', {
    memberCount: 0, completedMemberCount: 0,
  }, 'Running'))).toBeUndefined();
  expect(automaticJobProgressPercent(row('job-list', {
    memberCount: 5, completedMemberCount: null,
  }, 'Running'))).toBeUndefined();
  expect(automaticJobProgressPercent(row('album', {
    memberCount: 4, completedMemberCount: 4, progressPercent: 100,
  }, 'Terminal'))).toBeUndefined();
});

function listFixture() {
  const subscriptions: { next: Parameters<DaemonLiveClient['observeJobList']>[1]; stop: ReturnType<typeof vi.fn> }[] = [];
  const getJobListSnapshot = vi.fn().mockResolvedValue({ jobs: [], removedJobIds: [], windowJobIds: [], nextCursor: null });
  const observe = vi.fn<DaemonLiveClient['observeJobList']>((_, next) => {
    const stop = vi.fn();
    subscriptions.push({ next, stop });
    return stop;
  });
  const controller = new JobListController({ getJobListSnapshot }, observe);
  return { controller, getJobListSnapshot, observe, subscriptions };
}

function listedJob(id: string, revision = 1): Schema<'JobStateDto'> {
  return { jobId: id, revision, display: { displayId: Number(id), itemName: id, submissionId: id },
    lifecycle: { lifecycleState: 'Running' }, discovery: {}, relationships: {} } as Schema<'JobStateDto'>;
}

function listSnapshot(...rows: Schema<'JobStateDto'>[]): Schema<'JobListUpdateDto'> {
  return { jobs: rows.map(added => ({ jobId: added.jobId, revision: added.revision, added })),
    windowJobIds: rows.map(row => row.jobId), removedJobIds: [], nextCursor: 'older' };
}

test('Jobs uses one server scope for navigation, expansion and authoritative reconnect', () => {
  const f = listFixture();
  f.controller.setActive(true, 'connecting');
  expect(f.observe).not.toHaveBeenCalled();
  f.controller.setActive(true, 'connected');
  expect(f.observe.mock.lastCall?.[0].limit).toBe(100);
  expect(f.observe.mock.lastCall?.[0].query.kind).toBe('search');
  f.subscriptions[0]!.next(listSnapshot(listedJob('2', 20), listedJob('1')));
  f.subscriptions[0]!.next({ jobs: [{ jobId: '2', revision: 21,
    lifecycle: { ...listedJob('2').lifecycle, lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' } }],
    removedJobIds: [], nextCursor: 'older' });
  expect(f.controller.items[0]!.terminalOutcome).toBe('Succeeded');
  f.controller.loadMore();
  expect(f.subscriptions[0]!.stop).toHaveBeenCalledOnce();
  expect(f.observe.mock.lastCall?.[0].limit).toBe(200);
  f.subscriptions[1]!.next(listSnapshot(listedJob('2', 0), listedJob('1', 0)));
  expect(f.controller.items[0]!.lifecycleState).toBe('Running');
  f.controller.setActive(true, 'reconnecting');
  f.controller.setActive(true, 'connected');
  expect(f.observe.mock.lastCall?.[0].limit).toBe(200);
  f.subscriptions[2]!.next(listSnapshot(listedJob('3', 0)));
  f.subscriptions[1]!.next(listSnapshot(listedJob('99')));
  expect(f.controller.items.map(row => row.jobId)).toEqual(['3']);
  expect(f.getJobListSnapshot).not.toHaveBeenCalled();
  f.controller.setActive(false, 'connected');
  expect(f.subscriptions[2]!.stop).toHaveBeenCalledOnce();
  f.subscriptions[2]!.next(listSnapshot(listedJob('99')));
  expect(f.controller.items.map(row => row.jobId)).toEqual(['3']);
});

test('Job deletion discards an in-flight snapshot and disconnected reads use the same contract', async () => {
  const f = listFixture();
  f.controller.setActive(true, 'connected');
  f.subscriptions[0]!.next(listSnapshot(listedJob('2'), listedJob('1')));
  f.controller.afterArchive('2');
  f.subscriptions[0]!.next(listSnapshot(listedJob('2')));
  expect(f.controller.items.map(row => row.jobId)).toEqual(['1']);
  f.subscriptions[1]!.next(listSnapshot(listedJob('1')));
  f.controller.setActive(false, 'connected');
  f.controller.afterArchive('1');
  expect(f.observe).toHaveBeenCalledTimes(2);
  f.controller.setActive(true, 'failed');
  await Promise.resolve();
  expect(f.getJobListSnapshot).toHaveBeenCalledOnce();
  expect(f.controller.items).toEqual([]);
  expect(f.controller.loading).toBe(false);
});


test('Download jobs scopes exclude searches and retrievals before paging and retain their own window', () => {
  const f = listFixture();
  const downloads = new JobListController({ getJobListSnapshot: f.getJobListSnapshot }, f.observe, 'downloads');
  downloads.setActive(true, 'connected');
  expect(f.observe.mock.lastCall?.[0].query).toMatchObject({ kind: null, excludeKinds: ['search', 'retrieve-folder'] });
  f.subscriptions[0]!.next(listSnapshot(listedJob('1')));
  downloads.loadMore();
  downloads.setActive(false, 'connected');
  f.controller.setActive(true, 'connected');
  expect(f.observe.mock.lastCall?.[0]).toMatchObject({ limit: 100, query: { kind: 'search' } });
  f.controller.setActive(false, 'connected');
  downloads.setActive(true, 'connected');
  expect(f.observe.mock.lastCall?.[0].limit).toBe(200);
  downloads.filterSubmission('request-1');
  expect(f.observe.mock.lastCall?.[0]).toMatchObject({ limit: 100, query: { submissionId: 'request-1' } });
  downloads.filterSubmission(null);
  expect(f.observe.mock.lastCall?.[0].query.submissionId).toBeNull();
});
