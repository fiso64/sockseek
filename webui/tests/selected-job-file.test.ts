import { expect, it } from 'vitest';
import type { Schema, TransferStateDto } from '../src/api/types';
import { selectedJobFile, selectedJobFileTransfer } from '../src/domains/jobs/selected-file';

const file: Schema<'FileDownloadStateDto'> = {
  downloadPath: null, bytesTransferred: 0, fileSize: null, progressPercent: null,
};

it('keeps the last attempted source without treating it as a resolved pin', () => {
  const target: Schema<'PeerFileTargetDto'> = {
    username: ' Peer CASE ', filename: 'Music\\Mix CASE.flac', size: 128,
    bitRate: 320, bitDepth: 24, sampleRate: 48000, extension: '.flac',
  };
  const payload: Schema<'JobPayloadDtoSongJobPayloadDto'> = {
    kind: 'song', query: { artistMaybeWrong: false }, candidateCount: 2,
    file: { downloadPath: null, bytesTransferred: 64, fileSize: 128, progressPercent: 50 },
    lastAttemptedFile: { target, peer: { username: target.username, hasFreeUploadSlot: false, uploadSpeed: 1000 } },
  };
  expect(selectedJobFile(payload)).toEqual({ target, file: payload.file, peer: payload.lastAttemptedFile!.peer });
  const exact = { ...target, username: 'Exact pin', filename: 'Exact.bin', size: 0 };
  expect(selectedJobFile({ ...payload, exactTarget: exact })).toEqual({ target: exact, file: payload.file });
  expect(selectedJobFile({ ...payload, resolvedUsername: 'Pinned', resolvedFilename: 'Pinned.flac' })?.target.username)
    .toBe('Pinned');
  expect(payload.resolvedUsername).toBeUndefined();
});

it('preserves exact song identities and resolves available audio attributes without inventing missing values', () => {
  const payload: Schema<'JobPayloadDtoSongJobPayloadDto'> = {
    kind: 'song', query: { artistMaybeWrong: false }, candidateCount: 2, file,
    resolvedUsername: 'Peer Name', resolvedFilename: 'Music\\Mix CASE.flac',
    resolvedAttributes: [{ type: 'BitDepth', value: 24 }, { type: 'SampleRate', value: 48000 }],
  };
  expect(selectedJobFile(payload)).toEqual({ file, target: {
    username: 'Peer Name', filename: 'Music\\Mix CASE.flac', size: null, extension: null,
    bitRate: null, bitDepth: 24, sampleRate: 48000, length: null,
    attributes: payload.resolvedAttributes,
  } });
  expect(selectedJobFile({ ...payload, resolvedSampleRate: 96000 })?.target.sampleRate).toBe(96000);
  expect(selectedJobFile({ ...payload, resolvedFilename: null })).toBeNull();
});

it('uses the canonical remote target unchanged, including zero-length files', () => {
  const target: Schema<'PeerFileTargetDto'> = { username: 'peer', filename: 'Empty', size: 0, extension: null };
  const selected = selectedJobFile({ kind: 'remote-file', target, file, outputPathComponents: [] });
  expect(selected?.target).toBe(target);
  expect(selected?.file).toBe(file);
  expect(selectedJobFile(null)).toBeNull();
});

it('uses live size evidence for an exact remote file without changing its identity', () => {
  const target: Schema<'PeerFileTargetDto'> = {
    username: 'Exact Peer', filename: 'Folder\\Unknown.bin', size: null, extension: '.bin',
  };
  const payload: Schema<'JobPayloadDtoRemoteFileJobPayloadDto'> = {
    kind: 'remote-file', target, file, outputPathComponents: [],
  };
  const live: TransferStateDto = {
    transferId: 'transfer', revision: 1, createdAtUtc: new Date(0).toISOString(),
    identity: { jobId: 'child', workflowId: null, direction: 'Download', source: 'RemoteFile',
      username: target.username, remotePath: target.filename, candidateKey: null },
    status: { state: 'InProgress', localPath: null, attemptCount: 1, isTerminal: false },
    progress: { bytesTransferred: 64, totalBytes: 128 },
  };
  expect(selectedJobFile(payload)?.target).toBe(target);
  expect(selectedJobFile(payload, live)?.target).toEqual({ ...target, size: 128 });
  expect(selectedJobFile(payload, { ...live, identity: { ...live.identity, remotePath: 'Other\\File.bin' } })?.target).toBe(target);
  expect(selectedJobFile(payload, { ...live, progress: { ...live.progress, totalBytes: -1 } })?.target).toBe(target);
  expect(selectedJobFile({ ...payload, target: { ...target, size: 0 } }, live)?.target.size).toBe(0);
});

it('uses available live identity and progress while the child payload is not queryable', () => {
  const live: TransferStateDto = {
    transferId: 'transfer', revision: 1, createdAtUtc: new Date(0).toISOString(),
    identity: { jobId: 'child', workflowId: null, direction: 'Download', source: 'RemoteFile',
      username: 'Exact Peer', remotePath: 'Other\\Mix CASE.jpg', candidateKey: null },
    status: { state: 'InProgress', localPath: '/downloads/cover.jpg', attemptCount: 1, isTerminal: false },
    progress: { bytesTransferred: 64, totalBytes: 128 },
  };
  const selected = selectedJobFile(null, live);
  expect(selected?.target).toMatchObject({ username: 'Exact Peer', filename: 'Other\\Mix CASE.jpg', size: 128 });
  expect(selected?.file).toEqual({ downloadPath: '/downloads/cover.jpg', fileSize: 128, bytesTransferred: 64, progressPercent: 50 });
  expect(selectedJobFile(null, { ...live, progress: { bytesTransferred: 64, totalBytes: -1 } })?.target.size).toBeNull();
  expect(selectedJobFile(null, { ...live, progress: { bytesTransferred: 0, totalBytes: 0 } })?.target.size).toBe(0);
  expect(selectedJobFileTransfer(live)).toMatchObject({ state: 'Downloading', progressPercent: 50, progressText: '50%' });
  expect(selectedJobFileTransfer({ ...live, progress: { ...live.progress, bytesPerSecond: 32 } }))
    .toMatchObject({ speed: '32 B/s', eta: '2s' });
  expect(selectedJobFileTransfer({ ...live, status: { ...live.status, state: 'Queued' },
    progress: { bytesTransferred: 0, totalBytes: 128 } })).toMatchObject({ state: 'Queued', progressText: 'Waiting for peer' });
});
