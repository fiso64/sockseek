import { expect, test } from 'vitest';
import type { JobSummaryDto } from '../src/api/types';
import { automaticJobListStats, automaticJobProgressPercent } from '../src/domains/jobs/list-stats';

function row(kind: JobSummaryDto['kind'], stats?: JobSummaryDto['stats'], lifecycleState?: JobSummaryDto['lifecycleState']): JobSummaryDto {
  return { kind, stats, lifecycleState };
}

test('shows daemon-owned container progress and failure counts without reading children', () => {
  expect(automaticJobListStats(row('job-list', {
    memberCount: 12, completedMemberCount: 8, failedMemberCount: 1,
  }))).toEqual(['8/12 complete', '1 failed']);
  expect(automaticJobListStats(row('album-aggregate', {
    memberCount: 5, completedMemberCount: 2, failedMemberCount: 0,
  }))).toEqual(['2/5 complete']);
  expect(automaticJobListStats(row('album', {
    memberCount: 9, completedMemberCount: 3, failedMemberCount: 0,
    folderCount: 7, fileCount: 9,
  }))).toEqual(['3/9 complete', '7 folders']);
});

test('does not turn missing legacy outcome counts into zero progress', () => {
  expect(automaticJobListStats(row('aggregate', {
    memberCount: 3, completedMemberCount: null, failedMemberCount: null,
  }))).toEqual(['3 songs']);
  expect(automaticJobListStats(row('job-list'))).toEqual([]);
});

test('keeps kind-specific single-job stats', () => {
  expect(automaticJobListStats(row('song', { candidateCount: 2 }))).toEqual(['2 candidates']);
  expect(automaticJobListStats(row('remote-file', { knownBytes: 12_000_000 }))).toEqual(['12.0 MB']);
  expect(automaticJobListStats(row('remote-directory', { fileCount: 4 }))).toEqual(['4 files']);
  expect(automaticJobListStats(row('retrieve-folder', { newFilesFoundCount: 3 }))).toEqual(['3 new files']);
});

test('uses byte progress for directories and count progress for running containers', () => {
  expect(automaticJobProgressPercent(row('album', {
    memberCount: 4, completedMemberCount: 1, progressPercent: 45.5,
  }, 'Running'))).toBe(45.5);
  expect(automaticJobProgressPercent(row('aggregate', {
    memberCount: 5, completedMemberCount: 2,
  }, 'Running'))).toBe(40);
  expect(automaticJobProgressPercent(row('album-aggregate', {
    memberCount: 0, completedMemberCount: 0,
  }, 'Running'))).toBeUndefined();
  expect(automaticJobProgressPercent(row('job-list', {
    memberCount: 5, completedMemberCount: null,
  }, 'Running'))).toBeUndefined();
  expect(automaticJobProgressPercent(row('album', {
    memberCount: 4, completedMemberCount: 4, progressPercent: 100,
  }, 'Terminal'))).toBeUndefined();
});
