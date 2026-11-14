import { expect, it } from 'vitest';
import { jobDetailInfo } from '../src/domains/jobs/detail-info';
import type { JobSummaryDto, Schema } from '../src/api/types';
import { jobStatusClass, jobStatusLabel, jobTransferTone } from '../src/domains/jobs/status';
import { searchJobStats } from '../src/domains/jobs/search-stats';

it('keeps unknown search counts absent while preserving explicit zero and public counts', () => {
  expect(searchJobStats({})).toEqual([]);
  expect(searchJobStats({ discoveryRawResultCount: 8, discoveryPublicFileCount: 5, discoveryLockedFileCount: 0 })).toEqual([{ count: 5, label: 'files' }, { count: 0, label: 'locked' }]);
  expect(searchJobStats({ discoveryObservedPeerCount: 2 })).toEqual([{ count: 2, label: 'peers' }]);
  expect(searchJobStats({ discoveryPublicFileCount: '1', discoveryLockedFileCount: 0, discoveryObservedPeerCount: 1 }))
    .toEqual([{ count: '1', label: 'file' }, { count: 0, label: 'locked' }, { count: 1, label: 'peer' }]);
  for (const projection of ['Track', 'Album'] as const) {
    expect(searchJobStats({ kind: 'search', searchDefaultProjection: projection, searchGroupResults: true,
      discoveryPublicFileCount: 5, discoveryObservedPeerCount: 2 }))
      .toEqual([{ count: 5, label: 'files' }, { count: 2, label: 'peers' }]);
  }
});

it('shares prototype lifecycle labels without presenting skipped jobs as failed', () => {
  expect(jobStatusLabel({ lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' })).toBe('Complete');
  expect(jobStatusClass({ lifecycleState: 'Terminal', terminalOutcome: 'Skipped' })).toBe('skipped');
  expect(jobStatusClass({ lifecycleState: 'Terminal', terminalOutcome: 'PartialSuccess' })).toBe('partial');
  expect(jobStatusLabel({ kind: 'song', lifecycleState: 'Running', activityPhase: 'Downloading' })).toBe('Downloading');
  expect(jobStatusLabel({ lifecycleState: 'AwaitingSelection' })).toBe('Awaiting selection');
  expect(jobTransferTone({ lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' })).toBe('complete');
  expect(jobTransferTone({ lifecycleState: 'Terminal', terminalOutcome: 'Cancelled' })).toBe('cancelled');
  expect(jobTransferTone({ lifecycleState: 'Terminal', terminalOutcome: 'Failed' })).toBe('failed');
  expect(jobTransferTone({ lifecycleState: 'Terminal', terminalOutcome: 'PartialSuccess' })).toBe('partial');
  expect(jobTransferTone({ lifecycleState: 'Terminal', terminalOutcome: 'Skipped' })).toBeUndefined();
  expect(jobTransferTone({ lifecycleState: 'Running' })).toBe('active');
});

it('only adds information to successful results when context is missing', () => {
  for (const kind of ['song', 'album', 'remote-file', 'remote-directory', 'job-list', 'song-groups', 'album-groups'] as const) {
    expect(jobDetailInfo({ kind, lifecycleState: 'Terminal', terminalOutcome: 'Succeeded',
      stats: { memberCount: 10, completedMemberCount: 10, failedMemberCount: 0, skippedMemberCount: 0 },
    }, null, true)).toBeNull();
  }
  expect(jobDetailInfo({ kind: 'song', lifecycleState: 'Terminal', terminalOutcome: 'Skipped' }, null, true)).toBeNull();
  expect(jobDetailInfo({ kind: 'song', lifecycleState: 'Terminal', terminalOutcome: 'Failed', failureDetail: 'Diagnostics' }, null, true)).toBeNull();
  const song = { kind: 'song', query: { artistMaybeWrong: false }, candidateCount: 0,
    file: { reused: false, bytesTransferred: 0 } } satisfies Schema<'JobPayloadDto'>;
  expect(jobDetailInfo({ kind: 'song', lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' }, song, false)).not.toBeNull();
});

it('does not turn skipped children or missing totals into a warning for a successful collection', () => {
  for (const kind of ['job-list', 'song-groups', 'album-groups'] as const) {
    for (const stats of [undefined, { memberCount: 6, completedMemberCount: 6, failedMemberCount: 0, skippedMemberCount: 1 }]) {
      expect(jobDetailInfo({ kind, lifecycleState: 'Terminal', terminalOutcome: 'Succeeded', stats }, null, false)).toBeNull();
    }
  }
});

it('uses authoritative collection totals for progress and stops showing a bar after termination', () => {
  for (const kind of ['job-list', 'song-groups', 'album-groups'] as const) {
    const job: JobSummaryDto = { kind, lifecycleState: 'Running', activityPhase: 'RunningChildren',
      stats: { memberCount: 100, completedMemberCount: 90, failedMemberCount: 5, skippedMemberCount: 2 } };
    const active = jobDetailInfo(job, null, false)!;
    expect(active.progress).toEqual({ total: 100, completed: 90, percent: 90 });
    expect(active.busy).toBe(true);
    const stopped = jobDetailInfo({ ...job, lifecycleState: 'Terminal', terminalOutcome: 'PartialSuccess',
      failureReason: 'ChildJobsFailed', failureMessage: 'Some entries did not finish successfully.',
    }, null, false)!;
    expect(stopped.progress).toBeUndefined();
    expect(stopped.busy).toBe(false);
    expect(stopped.message).toBe(''); // Authoritative outcome counts already explain this.
    expect(stopped.counts).toContain('83 succeeded');
    expect(stopped.counts).toContain('5 unsuccessful');
  }
});

it('does not infer missing outcomes or progress from absent totals', () => {
  const info = jobDetailInfo({ kind: 'job-list', lifecycleState: 'Terminal', terminalOutcome: 'PartialSuccess',
    stats: { memberCount: 100 } }, null, false)!;
  expect(info.progress).toBeUndefined();
  expect(info.note).toBeTruthy();
  expect(info.counts).toEqual(['100 jobs']);
});

it('keeps skip provenance and the existing path together', () => {
  const info = jobDetailInfo({ kind: 'song', lifecycleState: 'Terminal', terminalOutcome: 'Skipped', skipReason: 'AlreadyExists' }, {
    kind: 'song', query: { artistMaybeWrong: false }, candidateCount: 0,
    file: { reused: false, bytesTransferred: 0, existingFileSource: 'Library', downloadPath: '/music/song.flac' },
  }, false)!;
  expect(info.facts).toEqual([{ label: 'Existing path', value: '/music/song.flac' }]);
  expect(info.message).toBeTruthy();
  expect(info.busy).toBe(false);
  expect(info.progress).toBeUndefined();
});

it('preserves the import failure and input without adding generic failure explanations', () => {
  const info = jobDetailInfo({ kind: 'extract', lifecycleState: 'Terminal', terminalOutcome: 'Failed',
    failureMessage: 'Invalid row 4', failureDetail: 'Diagnostic context' },
  { kind: 'extract', inputType: 'csv', input: '/lists/input.csv' }, false)!;
  expect(info.message).toBe('Invalid row 4');
  expect(info.facts).toEqual([{ label: 'Input', value: '/lists/input.csv' }]);
  expect(info.counts).toEqual([]);
});

it('leaves active transfer progress to the content card, but shows discovery without a selected target', () => {
  const job: JobSummaryDto = { kind: 'song', lifecycleState: 'Running', activityPhase: 'Downloading' };
  expect(jobDetailInfo(job, null, true)).toBeNull();
  expect(jobDetailInfo({ ...job, activityPhase: 'Searching' }, null, false)?.busy).toBe(true);
});
