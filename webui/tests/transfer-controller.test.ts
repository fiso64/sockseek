import { expect, it, vi } from 'vitest';
import type { DaemonLiveClient } from '../src/api/live';
import type { Schema } from '../src/api/types';
import { TransferTimelineController } from '../src/domains/transfers/controller.svelte';

const coverage = { state: 'Available' } as const;
const position = (sequence: number) => ({ epoch: 'transfer-facts', sequence });
const row = (id: string, groupRef: string | null = null) => ({
  transferId: id, revision: 1, direction: 'Download', username: 'Peer', groupRef,
  createdAtUtc: `2026-10-26T00:00:0${id}Z`,
}) as Schema<'TransferTimelineUpdateDto'>['items'][number];
const totals = (groupRef: string, fileCount = 200) => ({
  key: { direction: 'Download', username: 'Peer', groupRef }, fileCount,
}) as Schema<'TransferGroupSummaryDto'>;
const groups = (items: Schema<'TransferGroupSummaryDto'>[], sequence = 10) => ({
  items, retainedCoverage: coverage, readPosition: position(sequence),
});

function fixture(rows = [row('2'), row('1')]) {
  let publish: Parameters<DaemonLiveClient['observeTransferTimeline']>[1] = () => undefined;
  const getTransferTimeline = vi.fn().mockResolvedValue({ items: rows, windowTransferIds: rows.map(row => row.transferId), removedTransferIds: [],
    boundaryTransferId: rows.at(-1)?.transferId, nextCursor: null, retainedCoverage: coverage,
    groupSummaries: groups(rows.filter(row => row.groupRef).map(row => totals(row.groupRef!))),
  });
  const cancel = vi.fn();
  const observe = vi.fn<DaemonLiveClient['observeTransferTimeline']>((_, next) => {
    publish = next;
    return cancel;
  });
  // The observation owns totals too; there is no separate summary-read dependency.
  const controller = new TransferTimelineController({ getTransferTimeline }, observe);
  controller.connected = false;
  const update = (value: Partial<Schema<'TransferTimelineUpdateDto'>>) => publish({
    items: [], removedTransferIds: [], retainedCoverage: coverage,
    nextCursor: 'older', boundaryTransferId: '1', readPosition: position(10), ...value,
  });
  return { controller, getTransferTimeline, observe, cancel, update };
}

it('applies moving membership, preserves metadata-only row identity and expands through observation', async () => {
  const f = fixture();
  await f.controller.load('download', true);
  f.controller.connected = true;
  const stop = f.controller.observe('download')!;
  f.update({ windowTransferIds: ['2', '1'], groupSummaries: groups([]) });
  const before = f.controller.history.download.items;
  f.update({ readPosition: position(11) });
  expect(f.controller.history.download.items).toBe(before);
  f.update({ items: [row('3')], removedTransferIds: ['1'], boundaryTransferId: '2' });
  expect(f.controller.history.download.items.map(r => r.transferId)).toEqual(['3', '2']);
  await f.controller.load('download');
  stop();
  const expanded = f.controller.observe('download')!;
  expect(f.observe.mock.lastCall?.[0]).toMatchObject({ limit: 1000, knownRevisions: { '3': 1, '2': 1 }, includeGroupSummaries: true });
  expect(f.getTransferTimeline).toHaveBeenCalledOnce();
  expanded();
});

it('uses the initial live snapshot for connected navigation, expansion and Retry', async () => {
  const f = fixture();
  f.controller.connected = true;
  await f.controller.load('download', true);
  expect(f.controller.history.download.loading).toBe(true);
  f.controller.observe('download');
  expect(f.observe.mock.lastCall?.[0]).toMatchObject({ limit: 500, knownRevisions: {}, includeGroupSummaries: true });
  f.update({ items: [row('2', 'Folder'), row('1', 'Folder')], windowTransferIds: ['2', '1'],
    groupSummaries: groups([totals('Folder')]) });
  expect(f.controller.history.download.items).toHaveLength(2);
  expect(f.controller.history.download.summaries[0]?.fileCount).toBe(200);
  expect(f.controller.history.download.loading).toBe(false);
  await f.controller.load('download');
  f.controller.observe('download');
  expect(f.observe.mock.lastCall?.[0].limit).toBe(1000);
  f.update({ windowTransferIds: ['2', '1'] });
  await f.controller.load('download', true);
  f.controller.observe('download');
  expect(f.observe.mock.lastCall?.[0]).toMatchObject({ limit: 1000, knownRevisions: { '2': 1, '1': 1 } });
  expect(f.getTransferTimeline).not.toHaveBeenCalled();
  f.controller.stop();
});

it('falls back to the shared HTTP snapshot after live observation fails', async () => {
  const f = fixture();
  f.controller.connected = true;
  await f.controller.load('download', true);
  f.controller.observe('download');
  f.observe.mock.lastCall![2](new Error('Live delivery unavailable'));
  expect(f.controller.history.download.error?.message).toBe('Live delivery unavailable');
  await f.controller.load('download', true);
  expect(f.getTransferTimeline).toHaveBeenCalledOnce();
  expect(f.cancel).toHaveBeenCalledOnce();
  expect(f.controller.history.download.items).toHaveLength(2);
  expect(f.controller.history.download.error).toBeNull();
  f.controller.observe('download');
  expect(f.observe.mock.lastCall?.[0]).toMatchObject({ knownRevisions: { '2': 1, '1': 1 } });
  f.controller.stop();
});

it('displays changed whole-folder totals in the same update without another request', async () => {
  const f = fixture([row('1', 'Folder')]);
  await f.controller.load('download', true);
  f.controller.observe('download');
  f.update({ windowTransferIds: ['1'], groupSummaries: groups([totals('Folder')]) });
  const before = f.controller.history.download.items;
  f.update({ groupSummaries: groups([{ ...totals('Folder'), succeededCount: 200 }], 11) });
  expect(f.controller.history.download.items).toBe(before);
  expect(f.controller.history.download.summaries[0]?.succeededCount).toBe(200);
  expect(f.getTransferTimeline).toHaveBeenCalledOnce();
  f.controller.stop();
});

it('moves new folders and their exact totals together and removes departed totals', async () => {
  const f = fixture([row('1', 'First')]);
  await f.controller.load('download', true);
  f.controller.observe('download');
  f.update({ windowTransferIds: ['1'], groupSummaries: groups([totals('First')]) });
  f.update({ items: [row('2', 'Second')], removedTransferIds: ['1'], groupSummaries: groups([totals('Second', 300)], 11) });
  expect(f.controller.history.download.items.map(row => row.groupRef)).toEqual(['Second']);
  expect(f.controller.history.download.summaries.map(summary => [summary.key.groupRef, summary.fileCount])).toEqual([['Second', 300]]);
  expect(f.getTransferTimeline).toHaveBeenCalledOnce();
  f.controller.stop();
});

it('keeps valid rows when totals fail and recovers with the next authoritative totals', async () => {
  const f = fixture([row('1', 'Folder')]);
  await f.controller.load('download', true);
  f.controller.observe('download');
  f.update({ windowTransferIds: ['1'], groupSummaries: { items: [], retainedCoverage: { state: 'Unavailable', reason: 'storage failure' } } });
  expect(f.controller.history.download.items).toHaveLength(1);
  expect(f.controller.history.download.summaryError?.message).toBe('storage failure');
  expect(f.controller.history.download.summaryCoverage?.state).toBe('Unavailable');
  f.update({ groupSummaries: groups([totals('Folder')], 11) });
  expect(f.controller.history.download.summaryError).toBeNull();
  expect(f.controller.history.download.summaries[0]?.fileCount).toBe(200);
  f.controller.stop();
});

it('ends observation when hidden and ignores later row and summary arrivals', async () => {
  const f = fixture([row('1', 'Folder')]);
  await f.controller.load('download', true);
  const stop = f.controller.observe('download')!;
  f.update({ windowTransferIds: ['1'], groupSummaries: groups([totals('Folder')]) });
  f.controller.stop();
  stop();
  f.update({ items: [row('2', 'Other')], groupSummaries: groups([totals('Other')], 11) });
  expect(f.controller.history.download.items.map(row => row.transferId)).toEqual(['1']);
  expect(f.controller.history.download.summaries[0]?.key.groupRef).toBe('Folder');
  expect(f.cancel).toHaveBeenCalledOnce();
  expect(f.getTransferTimeline).toHaveBeenCalledOnce();
});

it('updates scoped progress without fetching history and retains history after live eviction', async () => {
  const f = fixture();
  await f.controller.load('download', true);
  const stop = f.controller.observe('download')!;
  const live = {
    transferId: '2', revision: 1,
    identity: { direction: 'Download', source: 'RemoteFile' },
    status: { state: 'InProgress', isTerminal: false, attemptCount: 1, terminalOutcome: 'None', failureReason: 'None', cancellationSource: 'None' },
    progress: { bytesTransferred: 10, totalBytes: 100 },
  } satisfies Schema<'TransferStateDto'>;
  f.update({ windowTransferIds: ['2', '1'], liveTransfers: [{ transferId: '2', revision: 1, added: live }] });
  const history = f.controller.history.download.items;
  f.update({ liveTransfers: [{ transferId: '2', revision: 2, progress: { bytesTransferred: 50, totalBytes: 100 } }] });
  expect(f.controller.live.read('2')?.progress.bytesTransferred).toBe(50);
  expect(f.controller.history.download.items).toBe(history);
  expect(f.getTransferTimeline).toHaveBeenCalledOnce();
  f.update({ liveTransfers: [{ transferId: '2', revision: 1, added: live }] });
  expect(f.controller.live.read('2')?.progress.bytesTransferred).toBe(50);
  f.update({ removedLiveTransferIds: ['2'] });
  expect(f.controller.live.read('2')).toBeUndefined();
  expect(f.controller.history.download.items).toBe(history);
  stop();
  f.update({ liveTransfers: [{ transferId: '2', revision: 3, added: live }] });
  expect(f.controller.live.size).toBe(0);
});

it('stops old observation before a snapshot and reconciles the gap through a fresh scope', async () => {
  const f = fixture([row('2', 'Folder'), row('1', 'Folder')]);
  await f.controller.load('download', true);
  f.controller.observe('download');
  f.update({ windowTransferIds: ['2', '1'] });
  let resolve!: (page: unknown) => void;
  f.getTransferTimeline.mockReturnValueOnce(new Promise(done => { resolve = done; }));
  const reset = f.controller.load('download', true);
  expect(f.cancel).toHaveBeenCalledOnce();
  f.update({ items: [row('3', 'Folder')], removedTransferIds: ['1'], groupSummaries: groups([totals('Folder', 99)], 11) });
  expect(f.controller.observe('download')).toBeUndefined();
  resolve({ items: [row('2', 'Folder'), row('1', 'Folder')], windowTransferIds: ['2', '1'], removedTransferIds: [], nextCursor: null, retainedCoverage: coverage, groupSummaries: groups([totals('Folder', 2)]) });
  await reset;
  expect(f.controller.history.download.summaries[0]?.fileCount).toBe(2);
  f.controller.observe('download');
  f.update({ windowTransferIds: ['3', '2'], items: [row('3', 'Folder')], removedTransferIds: ['1'], groupSummaries: groups([totals('Folder', 3)], 12) });
  expect(f.controller.history.download.items.map(r => r.transferId)).toEqual(['3', '2']);
  expect(f.controller.history.download.summaries[0]?.fileCount).toBe(3);
  f.controller.stop();
});

it('applies snapshot progress through the same live overlay as observation', async () => {
  const f = fixture();
  const live = {
    transferId: '2', revision: 2, identity: { direction: 'Download', source: 'RemoteFile' },
    status: { state: 'InProgress', isTerminal: false, attemptCount: 1, terminalOutcome: 'None', failureReason: 'None', cancellationSource: 'None' },
    progress: { bytesTransferred: 50, totalBytes: 100 },
  } satisfies Schema<'TransferStateDto'>;
  f.getTransferTimeline.mockResolvedValueOnce({ items: [row('2')], windowTransferIds: ['2'], removedTransferIds: [],
    retainedCoverage: coverage, groupSummaries: groups([]), liveTransfers: [{ transferId: '2', revision: 2, added: live }] });
  await f.controller.load('download', true);
  expect(f.controller.live.read('2')?.progress.bytesTransferred).toBe(50);
  expect(f.controller.history.download.items).toHaveLength(1);
});

it('keeps the visible upload observation intact when a hidden download refresh finishes', async () => {
  const f = fixture([{ ...row('7'), direction: 'Upload' }]);
  await f.controller.load('upload', true);
  f.controller.observe('upload');
  const live = {
    transferId: '7', revision: 1, identity: { direction: 'Upload', source: 'RemoteFile' },
    status: { state: 'InProgress', isTerminal: false, attemptCount: 1, terminalOutcome: 'None', failureReason: 'None', cancellationSource: 'None' },
    progress: { bytesTransferred: 50, totalBytes: 100 },
  } satisfies Schema<'TransferStateDto'>;
  f.update({ windowTransferIds: ['7'], liveTransfers: [{ transferId: '7', revision: 1, added: live }] });
  f.getTransferTimeline.mockResolvedValueOnce({ items: [row('1')], windowTransferIds: ['1'], removedTransferIds: [], retainedCoverage: coverage, groupSummaries: groups([]) });
  await f.controller.load('download', true);
  expect(f.cancel).not.toHaveBeenCalled();
  expect(f.controller.live.read('7')?.progress.bytesTransferred).toBe(50);
  expect(f.controller.history.upload.items.map(row => row.transferId)).toEqual(['7']);
  f.controller.stop();
});

it('retains newly fetched folder members when an older scoped snapshot finishes', async () => {
  const f = fixture([row('2', 'Folder'), row('1', 'Folder')]);
  await f.controller.load('download', true);
  let resolve!: (page: unknown) => void;
  f.getTransferTimeline.mockReturnValueOnce(new Promise(done => { resolve = done; }));
  const reset = f.controller.load('download', true);
  f.controller.registerMembers('download', [row('0', 'Folder')]);
  resolve({ items: [row('2', 'Folder')], windowTransferIds: ['2'], removedTransferIds: [], retainedCoverage: coverage,
    groupSummaries: groups([totals('Folder')]) });
  await reset;
  expect(f.controller.history.download.items.map(row => row.transferId)).toEqual(['2', '0']);
  expect(f.controller.missingMembers.download).toEqual([]);
  f.controller.observe('download');
  expect(f.observe.mock.lastCall?.[0].additionalTransferIds).toEqual(['0']);
  f.controller.stop();
});
