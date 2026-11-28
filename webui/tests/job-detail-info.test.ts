import { jobSummary } from './job-fixture';
import { expect, it } from 'vitest';
import { jobDetailInfo } from '../src/domains/jobs/detail-info';
import type { JobSummaryDto, Schema } from '../src/api/types';
import { jobOutcomeDetail, jobStatusClass, jobStatusLabel, jobTransferTone } from '../src/domains/jobs/status';
import { searchJobStats } from '../src/domains/jobs/list-stats';

it('keeps unknown search counts absent while preserving explicit zero and public counts', () => {
  expect(searchJobStats(jobSummary())).toEqual([]);
  expect(searchJobStats(jobSummary({ discovery: { rawResultCount: 8, publicFileCount: 5, lockedFileCount: 0 } }))).toEqual([{ count: 5, label: 'files' }, { count: 0, label: 'locked' }]);
  expect(searchJobStats(jobSummary({ discovery: { observedPeerCount: 2 } }))).toEqual([{ count: 2, label: 'peers' }]);
  expect(searchJobStats(jobSummary({ discovery: { publicFileCount: '1', lockedFileCount: 0, observedPeerCount: 1 } })))
    .toEqual([{ count: '1', label: 'file' }, { count: 0, label: 'locked' }, { count: 1, label: 'peer' }]);
  for (const projection of ['Track', 'Album'] as const) {
    expect(searchJobStats(jobSummary({ display: { kind: 'search', searchDefaultProjection: projection, searchGroupResults: true }, discovery: { publicFileCount: 5, observedPeerCount: 2 } })))
      .toEqual([{ count: 5, label: 'files' }, { count: 2, label: 'peers' }]);
  }
});

it('shares prototype lifecycle labels without presenting skipped jobs as failed', () => {
  expect(jobStatusLabel(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' } }))).toBe('Complete');
  expect(jobStatusClass(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Skipped' } }))).toBe('skipped');
  expect(jobStatusClass(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'PartialSuccess' } }))).toBe('partial');
  expect(jobStatusLabel(jobSummary({ display: { kind: 'song' }, lifecycle: { lifecycleState: 'Running', activityPhase: 'Downloading' } }))).toBe('Downloading');
  expect(jobStatusLabel(jobSummary({ lifecycle: { lifecycleState: 'AwaitingSelection' } }))).toBe('Awaiting selection');
  expect(jobTransferTone(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' } }))).toBe('complete');
  expect(jobTransferTone(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Cancelled' } }))).toBe('cancelled');
  expect(jobTransferTone(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Failed' } }))).toBe('failed');
  expect(jobTransferTone(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'PartialSuccess' } }))).toBe('partial');
  expect(jobTransferTone(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Skipped' } }))).toBeUndefined();
  expect(jobTransferTone(jobSummary({ lifecycle: { lifecycleState: 'Running' } }))).toBe('active');
});

it('only adds information to successful results when context is missing', () => {
  for (const kind of ['song', 'album', 'remote-file', 'remote-directory', 'job-list', 'song-groups', 'album-groups'] as const) {
    expect(jobDetailInfo(jobSummary({ stats: { memberCount: 10, completedMemberCount: 10, failedMemberCount: 0, skippedMemberCount: 0 }, display: { kind }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' } }), null, true)).toBeNull();
  }
  expect(jobDetailInfo(jobSummary({ display: { kind: 'song' }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Skipped' } }), null, true)).toBeNull();
  expect(jobDetailInfo(jobSummary({ display: { kind: 'song' }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Failed', hasFailureDetail: true } }), null, true)).toBeNull();
  const song = { kind: 'song', query: { artistMaybeWrong: false }, candidateCount: 0,
    file: { reused: false, bytesTransferred: 0 } } satisfies Schema<'JobPayloadDto'>;
  expect(jobDetailInfo(jobSummary({ display: { kind: 'song' }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' } }), song, false)).not.toBeNull();
});

it('explains a job that matched nothing with the recorded reason and what each condition rejected', () => {
  const explanation = '58 results found, but none matched.';
  const failed = jobSummary({ display: { kind: 'song' }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Failed', failureReason: 'NoMatchingResults', failureMessage: explanation } });
  const song = { kind: 'song', query: { artistMaybeWrong: false }, candidateCount: 0, file: { reused: false, bytesTransferred: 0 },
    rejections: [{ reason: 'Format', count: 41 }, { reason: 'FolderBelowMinimumTracks', count: 1 }] } satisfies Schema<'JobPayloadDto'>;
  const info = jobDetailInfo(failed, song, false)!;
  expect(info.message).toBe(explanation);
  expect(info.rejections).toHaveLength(2);
  // The list entry keeps its short reason.
  expect(jobOutcomeDetail(failed)).not.toBe(explanation);
  expect(jobDetailInfo(failed, { ...song, rejections: null }, false)!.rejections).toEqual([]);
});

it('does not turn skipped children or missing totals into a warning for a successful collection', () => {
  for (const kind of ['job-list', 'song-groups', 'album-groups'] as const) {
    for (const stats of [undefined, { memberCount: 6, completedMemberCount: 6, failedMemberCount: 0, skippedMemberCount: 1 }]) {
      expect(jobDetailInfo(jobSummary({ stats, display: { kind }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' } }), null, false)).toBeNull();
    }
  }
});

it('uses authoritative collection totals for progress and stops showing a bar after termination', () => {
  for (const kind of ['job-list', 'song-groups', 'album-groups'] as const) {
    const job: JobSummaryDto = jobSummary({ stats: { memberCount: 100, completedMemberCount: 90, failedMemberCount: 5, skippedMemberCount: 2 }, display: { kind }, lifecycle: { lifecycleState: 'Running', activityPhase: 'RunningChildren' } });
    const active = jobDetailInfo(job, null, false)!;
    expect(active.progress).toEqual({ total: 100, completed: 90, percent: 90 });
    const stopped = jobDetailInfo(jobSummary({ lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'PartialSuccess', failureReason: 'ChildJobsFailed', failureMessage: 'Some entries did not finish successfully.' } }, job), null, false)!;
    expect(stopped.progress).toBeUndefined();
    expect(stopped.message).toBe(''); // Authoritative outcome counts already explain this.
    expect(stopped.counts).toContain('83 succeeded');
    expect(stopped.counts).toContain('5 unsuccessful');
  }
});

it('does not infer missing outcomes or progress from absent totals', () => {
  const info = jobDetailInfo(jobSummary({ stats: { memberCount: 100 }, display: { kind: 'job-list' }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'PartialSuccess' } }), null, false)!;
  expect(info.progress).toBeUndefined();
  expect(info.note).toBeTruthy();
  expect(info.counts).toEqual(['100 jobs']);
});

it('keeps skip provenance and the existing path together', () => {
  const info = jobDetailInfo(jobSummary({ display: { kind: 'song' }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Skipped', skipReason: 'AlreadyExists' } }), {
    kind: 'song', query: { artistMaybeWrong: false }, candidateCount: 0,
    file: { reused: false, bytesTransferred: 0, existingFileSource: 'Library', downloadPath: '/music/song.flac' },
  }, false)!;
  expect(info.facts).toEqual([{ label: 'Existing path', value: '/music/song.flac' }]);
  expect(info.message).toBeTruthy();
  expect(info.progress).toBeUndefined();
});

it('preserves the import failure and input without adding generic failure explanations', () => {
  const info = jobDetailInfo(jobSummary({ display: { kind: 'extract' }, lifecycle: { lifecycleState: 'Terminal', terminalOutcome: 'Failed', failureMessage: 'Invalid row 4', hasFailureDetail: true } }),
  { kind: 'extract', inputType: 'csv', input: '/lists/input.csv' }, false)!;
  expect(info.message).toBe('Invalid row 4');
  expect(info.facts).toEqual([{ label: 'Input', value: '/lists/input.csv' }]);
  expect(info.counts).toEqual([]);
});

it('leaves active transfer progress to the content card, but shows discovery without a selected target', () => {
  const job: JobSummaryDto = jobSummary({ display: { kind: 'song' }, lifecycle: { lifecycleState: 'Running', activityPhase: 'Downloading' } });
  expect(jobDetailInfo(job, null, true)).toBeNull();
  expect(jobDetailInfo(jobSummary({ lifecycle: { activityPhase: 'Searching' } }, job), null, false)).not.toBeNull();
  expect(jobDetailInfo(jobSummary({ lifecycle: { lifecycleState: 'Pending' } }, job), null, false)).not.toBeNull();
});
