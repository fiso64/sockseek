import { expect, it } from 'vitest';
import { jobHeaderStats } from '../src/domains/jobs/header-stats';
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
  expect(jobStatusLabel({ kind: 'song', lifecycleState: 'Running', activityPhase: 'Downloading' })).toBe('Running');
  expect(jobStatusLabel({ lifecycleState: 'AwaitingSelection' })).toBe('Awaiting selection');
  expect(jobTransferTone({ lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' })).toBe('complete');
  expect(jobTransferTone({ lifecycleState: 'Terminal', terminalOutcome: 'Cancelled' })).toBe('cancelled');
  expect(jobTransferTone({ lifecycleState: 'Terminal', terminalOutcome: 'Failed' })).toBe('failed');
  expect(jobTransferTone({ lifecycleState: 'Terminal', terminalOutcome: 'Skipped' })).toBeUndefined();
  expect(jobTransferTone({ lifecycleState: 'Running' })).toBeUndefined();
});

it('shows authoritative outcome totals without counting loaded children', () => {
  expect(jobHeaderStats({ kind: 'job-list', count: 100, activeJobCount: 10,
    completedJobCount: 90, succeededJobCount: 85, failedJobCount: 5,
  })).toEqual(['100 jobs', '85 complete', '5 failed']);
  expect(jobHeaderStats({ kind: 'song-groups', query: { artistMaybeWrong: false },
    songCount: 20, completedSongCount: 20, succeededSongCount: 20, failedSongCount: 0,
  })).toEqual(['20 songs', '20 complete']);
});

it('preserves unknown file sizes and displays authoritative container outcomes', () => {
  const directory = {
    phase: 'resolving', attemptNumber: null, downloadPath: null, fileCount: 0,
    terminalFileCount: 0, successfulFileCount: 0, failedFileCount: 0,
    bytesTransferred: 0, totalKnownBytes: 0, progressPercent: null, unknownFileSizeCount: 0,
  };
  expect(jobHeaderStats({ kind: 'remote-directory', sourceKind: 'PeerDirectory',
    sourceUsername: 'peer', sourceFolderPath: 'Music', directory })).toEqual([]);
  expect(jobHeaderStats({ kind: 'remote-directory', sourceKind: 'PeerDirectory',
    sourceUsername: 'peer', sourceFolderPath: 'Music', directory: { ...directory, attemptNumber: 1 } }))
    .toEqual(['0 files']);
  expect(jobHeaderStats({ kind: 'remote-directory', sourceKind: 'PeerDirectory',
    sourceUsername: 'peer', sourceFolderPath: 'Music', directory: { ...directory, attemptNumber: 1, fileCount: 11 } }))
    .toEqual(['11 files']);
  expect(jobHeaderStats({ kind: 'remote-directory', sourceKind: 'PeerDirectory',
    sourceUsername: 'peer', sourceFolderPath: 'Music', directory: { ...directory, fileCount: 11 } }))
    .toEqual(['11 files']);
  expect(jobHeaderStats({ kind: 'remote-file',
    target: { username: 'peer', filename: 'file', size: null, extension: null },
    outputPathComponents: [], file: { downloadPath: null, fileSize: null, bytesTransferred: 0, progressPercent: null },
  })).toEqual([]);
  expect(jobHeaderStats({ kind: 'album-groups', query: { artistMaybeWrong: false }, resultCount: 12,
    completedAlbumCount: 0, succeededAlbumCount: 0, failedAlbumCount: 0 }))
    .toEqual(['12 albums', '0 complete']);
  expect(jobHeaderStats({ kind: 'album-groups', query: { artistMaybeWrong: false },
    resultCount: 12, completedAlbumCount: 9, succeededAlbumCount: 8, failedAlbumCount: 1 }))
    .toEqual(['12 albums', '8 complete', '1 failed']);
  expect(jobHeaderStats({ kind: 'song-groups', query: { artistMaybeWrong: false }, songCount: 7,
    completedSongCount: 0, succeededSongCount: 0, failedSongCount: 0 }))
    .toEqual(['7 songs', '0 complete']);
  expect(jobHeaderStats({ kind: 'job-list', count: 3, activeJobCount: 3,
    completedJobCount: 0, succeededJobCount: 0, failedJobCount: 0 }))
    .toEqual(['3 jobs', '0 complete']);
});
