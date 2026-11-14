import { expect, it } from 'vitest';
import type { Schema } from '../src/api/types';
import { directoryFileRow, directoryKnownSize, directoryManifestFileRow } from '../src/domains/jobs/directory-files';
import { readJobSummaries, jobSummaryRetryIds } from '../src/domains/jobs/details';

it('keeps missing outcomes and unknown sizes distinct from success and empty files', () => {
  const supplemental = { ordinal: 1, target: null, relativeDirectoryComponents: [], childJobId: 'pruned' } satisfies Schema<'DirectoryManifestEntryDto'>;
  expect(directoryManifestFileRow(supplemental, { ownerTerminal: true })).toMatchObject({
    relativePath: 'File details unavailable', sizeUnknown: true,
    transfer: { state: 'Outcome unavailable' },
  });
  expect(directoryManifestFileRow({ ...supplemental, target: {
    username: 'OtherPeer', filename: 'Other\\Art\\Cover.jpg', size: 128, extension: '.jpg',
  } }, { ownerTerminal: true, child: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' } }))
    .toMatchObject({ relativePath: 'Cover.jpg', sourcePath: 'Other\\Art\\Cover.jpg', transfer: { state: 'Complete' } });

  const row = directoryFileRow({
    id: 'one', relativePath: 'Cover.jpg',
    target: { username: 'Peer', filename: 'Art\\Cover.jpg', size: null, extension: '.jpg' },
    childId: 'pruned', ownerTerminal: true,
  });
  expect(row.sizeUnknown).toBe(true);
  expect(row.transfer?.state).toBe('Outcome unavailable');
  expect(directoryFileRow({
    id: 'two', relativePath: 'Empty.txt',
    target: { username: 'Peer', filename: 'Empty.txt', size: 0, extension: '.txt' }, ownerTerminal: true,
  }).sizeUnknown).toBe(false);
  expect(directoryFileRow({ id: 'three', relativePath: 'Not started', ownerTerminal: true }).transfer?.state).toBe('Not started');
  expect(directoryFileRow({
    id: 'four', relativePath: 'Cancelled.jpg', ownerTerminal: true,
    child: { lifecycleState: 'Terminal', terminalOutcome: 'Cancelled' },
    file: { reused: false, downloadPath: null, bytesTransferred: 64, fileSize: 128, progressPercent: 50 },
  }).transfer).toMatchObject({ state: 'Cancelled', progressPercent: 50, tone: 'cancelled' });
  expect(directoryFileRow({
    id: 'five', relativePath: 'Existing.jpg', ownerTerminal: true,
    child: { lifecycleState: 'Terminal', terminalOutcome: 'Skipped', skipReason: 'AlreadyExists' },
    file: { reused: false, downloadPath: '/downloads/Existing.jpg', bytesTransferred: 0, fileSize: 128, progressPercent: 0 },
  }).transfer).toMatchObject({ state: 'Skipped', progressPercent: undefined,
    job: { skipReason: 'AlreadyExists' }, file: { reused: false, downloadPath: '/downloads/Existing.jpg' } });
  const reused = directoryFileRow({
    id: 'reused', relativePath: 'Copy.jpg', ownerTerminal: true,
    child: { lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' },
    file: { reused: true, downloadPath: '/downloads/Copy.jpg', bytesTransferred: 0, fileSize: 128, progressPercent: 0 },
  }).transfer;
  expect(reused?.file?.reused).toBe(true);
  expect(reused?.file?.bytesTransferred).toBe(0);
  expect(reused?.progressPercent).toBeUndefined();
});

it('shows a directory zero only when its owner confirms every planned size is known', () => {
  const base = {
    phase: 'planned', attemptNumber: 1, downloadPath: null, fileCount: 1,
    terminalFileCount: 0, successfulFileCount: 0, failedFileCount: 0,
    skippedFileCount: 0, downloadedFileCount: 0, reusedFileCount: 0,
    bytesTransferred: 0, totalKnownBytes: 0, progressPercent: null, unknownFileSizeCount: 1,
  } satisfies Schema<'DirectoryDownloadStateDto'>;
  expect(directoryKnownSize({ ...base, unknownFileSizeCount: 1 })).toBeNull();
  expect(directoryKnownSize({ ...base, unknownFileSizeCount: 0 })).toBe(0);
  expect(directoryKnownSize(base)).toBeNull();
  expect(directoryKnownSize({ ...base, totalKnownBytes: 128, unknownFileSizeCount: 1 })).toBe(128);
  expect(directoryKnownSize({ ...base, attemptNumber: null, unknownFileSizeCount: 0 })).toBeNull();
});

it('reads linked summaries once and keeps missing siblings independent', async () => {
  const signal = new AbortController().signal;
  const ids = ['one', 'missing', 'two', 'one'];
  let calls = 0;
  const reads = await readJobSummaries(ids, async (requested, receivedSignal) => {
    calls++;
    expect(requested).toEqual(['one', 'missing', 'two']);
    expect(receivedSignal).toBe(signal);
    return [{ jobId: 'two' }, { jobId: 'one' }, { jobId: 'unrequested' }];
  }, signal);
  expect(calls).toBe(1);
  expect([...reads.keys()]).toEqual(['one', 'missing', 'two']);
  expect(reads.get('one')?.summary?.jobId).toBe('one');
  expect(reads.get('missing')).toEqual({ summary: null, error: null });
  expect(jobSummaryRetryIds(reads, 'missing')).toEqual(['missing']);
  expect(reads.get('two')?.summary?.jobId).toBe('two');
});

it('keeps transport failure distinct from missing summaries and ignores cancelled reads', async () => {
  const controller = new AbortController();
  const failed = await readJobSummaries(['one', 'two'], async () => {
    throw new Error('Transport unavailable');
  }, controller.signal);
  expect([...failed.values()].map(row => row.error?.message)).toEqual(['Transport unavailable', 'Transport unavailable']);
  expect(jobSummaryRetryIds(failed, 'one')).toEqual(['one', 'two']);
  const cancelled = await readJobSummaries(['one', 'two'], async () => {
    controller.abort();
    return [{ jobId: 'one' }];
  }, controller.signal);
  expect(cancelled.size).toBe(0);
  let requested = false;
  await readJobSummaries(['one'], async () => { requested = true; return []; }, controller.signal);
  expect(requested).toBe(false);
});
