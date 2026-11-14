import { expect, it, vi } from 'vitest';
import type { TransferStateDto, Schema } from '../src/api/types';
import { transferGroupMemberScope, transferGroupKeys, mergeTransferHistory, transferGroupCursor } from '../src/domains/transfers/history';
import { arrangeTransferGroups, presentTransferEntry, transferEntryActions, liveTransferPresentation, TransferProjection } from '../src/domains/transfers/model';
import { runTransferCommands } from '../src/domains/transfers/commands';

// Tests supply the same authoritative timeline membership as HTTP/observation.
// Changed records use the production per-card reader without rearranging the page.
function buildTransferGroups(
  history: TransferStateDto[], live: TransferStateDto[], direction: 'download' | 'upload',
  summaries: Schema<'TransferGroupSummaryDto'>[] = [], projection = new TransferProjection(),
) {
  const rows = [...history];
  const ids = new Set(rows.map(row => row.transferId));
  const current = new Map(live.filter(row => row.identity.direction.toLowerCase() === direction)
    .map(row => [row.transferId, row]));
  for (const row of current.values()) if (!ids.has(row.transferId)) rows.push(row);
  return arrangeTransferGroups(rows, direction, summaries, projection).map(group => ({
    ...group, items: group.items.map(entry => presentTransferEntry(entry, projection, id => current.get(id))),
  }));
}

it('collects exact group identities once across live/retained overlap and isolates direction', () => {
  const history = ['Peer', 'Peer', 'peer'].map(username => ({ revision: 1, transferId: 'fixture', createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', username: username, direction: 'Download', groupRef: 'Group' },
    status: { attemptCount: 0, isTerminal: false, terminalOutcome: 'None', state: 'Queued', availableActions: [] },
    progress: { totalBytes: -1, bytesTransferred: 0 }
  }) as TransferStateDto);
  const live = [
    { direction: 'Download', username: 'Peer', groupRef: 'Group' },
    { direction: 'Download', username: 'Peer', groupRef: 'group' },
    { direction: 'Upload', username: 'Other', groupRef: 'Group' },
    { direction: 'Download', username: 'Standalone', groupRef: null },
  ].map((identity, index) => ({ transferId: String(index), revision: 1, identity }) as TransferStateDto);
  expect(transferGroupKeys('download', [...history, ...live])).toEqual([
    { direction: 'Download', username: 'Peer', groupRef: 'Group' },
    { direction: 'Download', username: 'peer', groupRef: 'Group' },
    { direction: 'Download', username: 'Peer', groupRef: 'group' },
  ]);
  expect(transferGroupKeys('upload', [...history, ...live])).toEqual([
    { direction: 'Upload', username: 'Other', groupRef: 'Group' },
  ]);

});

it('keeps an expanded folder valid when unrelated arrivals displace its global-page suffix', () => {
  const row = (id: string, seconds: number, username = 'Peer') => ({ revision: 1,
    transferId: id,
    createdAtUtc: new Date(seconds * 1000).toISOString(),
    identity: { source: 'SoulseekPeer', direction: 'Download', username: username, groupRef: 'group' },
    status: { attemptCount: 0, isTerminal: false, terminalOutcome: 'None', state: 'Queued', availableActions: [] },
    progress: { totalBytes: -1, bytesTransferred: 0 }
  }) as TransferStateDto;
  const summary = { key: { direction: 'Download', username: 'Peer', groupRef: 'group' },
    fileCount: 4, knownTotalBytes: 40, unknownTotalCount: 0, queuedCount: 0, inProgressCount: 0,
    succeededCount: 4, failedCount: 0, cancelledCount: 0, interruptedCount: 0,
  } as Schema<'TransferGroupSummaryDto'>;
  const cached = [row('d', 4), row('c', 3), row('b', 2), row('a', 1)];
  expect(transferGroupCursor(cached.slice(0, 2), 'member', cached[2]!, 'global')).toBe('global');
  expect(transferGroupCursor(cached, 'member', cached[2]!, 'global')).toBe('member');
  expect(transferGroupCursor([], null, cached[2]!, 'global')).toBe('global');
  const prefix = cached.slice(0, 2);
  expect(transferGroupMemberScope(summary, cached, prefix))
    .toBe(transferGroupMemberScope(summary, prefix, prefix));
  const scope = transferGroupMemberScope(summary, prefix, cached);
  expect(transferGroupMemberScope(summary, [row('other', 5, 'Other'), ...prefix], cached)).toBe(scope);
  // A known member above the fresh boundary was removed, not simply displaced.
  expect(transferGroupMemberScope(summary, [cached[0]!, cached[2]!], cached)).not.toBe(scope);
  expect(transferGroupMemberScope({ ...summary, failedCount: 1 }, prefix, cached)).not.toBe(scope);
});

it('groups interleaved transfers by run identity independently of optional totals', () => {
  const rows = ['A', 'B', 'A'].map((groupRef, index) => ({ revision: 1,
    transferId: `transfer-${index}`,
    createdAtUtc: `2026-09-10T00:00:0${3 - index}Z`,
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: `${groupRef}\\file-${index}`, groupRef: groupRef, groupDisplayPath: groupRef },
    status: { attemptCount: 0, availableActions: [], state: 'Queued', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: 100, bytesTransferred: 0 }
  }) as TransferStateDto);
  for (const direction of ['download', 'upload'] as const) {
    const transfers = rows.map(row => ({
    ...row,
    identity: { ...row.identity, direction: direction === 'download' ? 'Download' : 'Upload' }
  }));
    const groups = buildTransferGroups(transfers, [], direction);
    expect(groups).toHaveLength(1);
    expect(groups[0]!.items.map(item => item.path)).toEqual(['A', 'B']);
    expect(groups[0]!.items.flatMap(item => item.sourceTransferIds)).toEqual(['transfer-0', 'transfer-2', 'transfer-1']);
    const separate = buildTransferGroups(transfers.map((row, index) => ({
    ...row,
    identity: { ...row.identity, groupRef: `run-${index}` }
  })), [], direction)[0]!.items;
    expect(separate.map(item => item.path)).toEqual(['A', 'B', 'A']);
    expect(new Set(separate.map(item => item.id)).size).toBe(3);
  }
  const summary = (groupRef: string, fileCount: number) => ({
    key: { direction: 'Download' as const, username: 'Peer', groupRef },
    fileCount, knownTotalBytes: fileCount * 100, transferredBytes: 0, speedBytesPerSecond: 0, unknownTotalCount: 0,
    knownRemainingActiveBytes: fileCount * 100, unknownActiveTotalCount: 0,
    queuedCount: fileCount, inProgressCount: 0, succeededCount: 0, failedCount: 0,
    cancelledCount: 0, interruptedCount: 0,
  } satisfies Schema<'TransferGroupSummaryDto'>);
  const summarized = buildTransferGroups(rows, [], 'download', [summary('A', 2), summary('B', 1)]);
  expect(summarized[0]!.transferCount).toBe(3);
  expect(summarized[0]!.items.map((item) => item.path)).toEqual(['A', 'B']);
  expect(summarized[0]!.items[0]?.sourceTransferIds).toEqual(['transfer-0', 'transfer-2']);
  expect(summarized[0]!.items[0]?.kind === 'folder' && summarized[0]!.items[0].filesComplete).toBe(true);
});

it.each(['download', 'upload'] as const)('sorts %s folder files by name while keeping cards and progress stable', direction => {
  const rows = ['one', 'two', 'other'].map((transferId, index) => ({ revision: 1,
    transferId,
    createdAtUtc: `2026-10-26T00:00:0${3 - index}Z`,
    identity: { source: 'SoulseekPeer', direction: direction === 'download' ? 'Download' : 'Upload', username: index === 2 ? 'Other' : 'Peer', remotePath: `Folder\\${index === 0 ? "10" : "2"} Track.flac`, groupRef: index === 2 ? null : 'folder' },
    status: { attemptCount: 0, availableActions: [], state: 'Queued', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: 100, bytesTransferred: 0 }
  }) as TransferStateDto);
  const projection = new TransferProjection();
  const groups = arrangeTransferGroups(rows, direction, [], projection);
  const layout = groups[0]!.items[0]!;
  const live = { createdAtUtc: '2026-09-10T00:00:00Z',
    transferId: 'one', revision: 2,
    identity: { source: 'SoulseekPeer', direction: direction === 'download' ? 'Download' : 'Upload', username: 'Peer', remotePath: 'Folder\\10 Track.flac', groupRef: 'folder' },
    status: { attemptCount: 0, state: 'InProgress', isTerminal: false, terminalOutcome: 'None', availableActions: [{ kind: 'cancel' }] },
    progress: { bytesTransferred: 50, totalBytes: 100, bytesPerSecond: 10 },
  } as TransferStateDto;
  const read = vi.fn((id: string) => id === 'one' ? live : undefined);
  const card = presentTransferEntry(layout, projection, read);
  expect(card.kind).toBe('folder');
  if (card.kind !== 'folder') throw new Error('Expected a folder');
  expect(card.files.map(file => file.id)).toEqual(['two', 'one']);
  expect(card.files[1]!.transfer).toMatchObject({ tone: 'active', progressPercent: 50 });
  expect(card.transfer).toMatchObject({ tone: 'active', progressPercent: 25 });
  expect(read.mock.calls.flat()).toEqual(['one', 'two']);
  expect([...transferEntryActions(layout, projection, read)].some(action => action.cancellable)).toBe(true);
  // A later HTTP snapshot changes outcomes without changing creation order.
  const finished = rows.map(row => ({
    ...row,
    revision: 3,
    status: { ...row.status, state: 'Completed', terminalOutcome: 'Succeeded' as const, isTerminal: true },
    progress: { ...row.progress, bytesTransferred: 100 }
  }));
  const after = arrangeTransferGroups([...finished].reverse(), direction, [], projection);
  expect(after.map(group => group.key)).toEqual(groups.map(group => group.key));
  expect(presentTransferEntry(after[0]!.items[0]!, projection).transfer.tone).toBe('complete');
  expect(presentTransferEntry(after[0]!.items[0]!, projection, read).transfer.tone).toBe('complete');
  expect(groups[1]!.items[0]!.id).toBe('other');
});

it('does not merge an unknown peer with a literal peer named Unknown peer', () => {
  const rows = [null, 'Unknown peer'].map((username, index) => ({ revision: 1,
    transferId: `identity-${index}`,
    createdAtUtc: `2026-09-10T00:00:0${2 - index}Z`,
    identity: { source: 'SoulseekPeer', direction: 'Download', username: username, remotePath: `file-${index}.bin` },
    status: { attemptCount: 0, availableActions: [], state: 'Completed', terminalOutcome: 'Succeeded' as const, isTerminal: true },
    progress: { totalBytes: 1, bytesTransferred: 1 }
  })) as TransferStateDto[];
  const groups = buildTransferGroups(rows, [], 'download');
  expect(groups).toHaveLength(2);
  expect(groups.map((group) => group.username)).toEqual([null, 'Unknown peer']);
});

it.each(['download', 'upload'] as const)('uses whole-group totals for single-file %s groups', (direction) => {
  const wireDirection = direction === 'download' ? 'Download' : 'Upload';
  const uploaded = { revision: 1,
    transferId: 'single',
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: wireDirection, username: 'Peer', remotePath: 'Shared\\Solo.txt', groupKind: 'Files', groupRef: 'shared-folder', groupDisplayPath: 'Shared' },
    status: { attemptCount: 0, availableActions: [], state: 'InProgress', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: 100, bytesTransferred: 50 }
  } as TransferStateDto;
  const summary = (fileCount: number) => ({
    key: { direction: wireDirection, username: 'Peer', groupRef: 'shared-folder' },
    fileCount, knownTotalBytes: fileCount * 100, transferredBytes: 50, speedBytesPerSecond: 0,
    unknownTotalCount: 0, knownRemainingActiveBytes: 50, unknownActiveTotalCount: 0,
    queuedCount: fileCount - 1, inProgressCount: 1, succeededCount: 0, failedCount: 0,
    cancelledCount: 0, interruptedCount: 0,
  } satisfies Schema<'TransferGroupSummaryDto'>);
  const single = buildTransferGroups([uploaded], [], direction, [summary(1)])[0]!.items[0]!;
  const explicitDirectory = { ...uploaded, identity: { ...uploaded.identity, groupKind: 'Directory' as const } };
  expect(buildTransferGroups([explicitDirectory], [], direction, [summary(1)])[0]!.items[0]!.kind).toBe('folder');
  expect(single.kind).toBe('file');
  expect(single.sourceTransferIds).toEqual(['single']);
  expect(buildTransferGroups([uploaded], [], direction, [summary(2)])[0]!.items[0]!.kind).toBe('folder');
  expect(buildTransferGroups([uploaded], [], direction)[0]!.items[0]!.kind).toBe('folder');
});

it('shows transfer speed only while a file is active, even if a terminal snapshot retains its last sample', () => {
  const live = { revision: 1, createdAtUtc: '2026-09-10T00:00:00Z',
    transferId: 'speed',
    identity: { source: 'SoulseekPeer', direction: 'Upload', username: 'Peer', remotePath: 'Shared\\File.txt' },
    progress: { totalBytes: 128, bytesTransferred: 64, bytesPerSecond: 128 },
    status: { attemptCount: 0, state: 'InProgress', isTerminal: false, terminalOutcome: 'None' },
    scheduling: { requestedAtUtc: '2026-09-10T00:00:00Z', startedAtUtc: '2026-09-10T00:00:00Z' },
  } as unknown as TransferStateDto;
  expect(liveTransferPresentation(live).speed).toBe('128 B/s');
  expect(liveTransferPresentation(live)).toMatchObject({ state: 'Uploading', progressText: '50%' });
  const finalizing = liveTransferPresentation({ ...live,
    identity: { ...live.identity, direction: 'Download' },
    status: { ...live.status, state: 'AttemptCompleted' },
    progress: { ...live.progress, bytesTransferred: 128 },
  });
  expect(finalizing.state).toBe('Finalizing');
  expect(finalizing.speed).toBeUndefined();
  expect(finalizing.eta).toBeUndefined();

  const completed = { ...live,
    progress: { ...live.progress, bytesTransferred: 128 },
    status: { ...live.status, state: 'Completed', isTerminal: true, terminalOutcome: 'Succeeded' },
  } as TransferStateDto;
  expect(liveTransferPresentation(completed).speed).toBeUndefined();

  const retained = { revision: 1,
    transferId: 'speed',
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Upload', username: 'Peer', remotePath: 'Shared\\File.txt' },
    status: { attemptCount: 0, availableActions: [], state: 'InProgress', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: 128, bytesTransferred: 64, bytesPerSecond: 128 },
    scheduling: { requestedAtUtc: '2026-09-10T00:00:00Z', startedAtUtc: '2026-09-10T00:00:00Z' }
  } as TransferStateDto;
  expect(buildTransferGroups([retained], [], 'upload')[0]!.items[0]!.transfer.speed).toBe('128 B/s');
  expect(buildTransferGroups([retained], [], 'upload')[0]!.items[0]!.transfer).toMatchObject({ state: 'Uploading', progressText: '50%' });
  const historical = {
    ...retained,
    status: { ...retained.status, state: 'Completed', terminalOutcome: 'Succeeded' as const, isTerminal: true, completedAtUtc: '2026-09-10T00:00:01Z' },
    progress: { ...retained.progress, bytesTransferred: 128 }
  } as TransferStateDto;
  expect(buildTransferGroups([historical], [], 'upload')[0]!.items[0]!.transfer.speed).toBeUndefined();
  expect(buildTransferGroups([historical], [], 'upload')[0]!.items[0]!.transfer.progressText).toBe('100%');
});

it('uses one live and retained active-download label and byte-plus-percent footer', () => {
  const live = { revision: 1, createdAtUtc: '2026-09-10T00:00:00Z',
    transferId: 'download',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\Track.flac' },
    progress: { totalBytes: 1_000_000, bytesTransferred: 500_000, bytesPerSecond: 250_000 },
    status: { attemptCount: 0, state: 'InProgress', isTerminal: false, terminalOutcome: 'None' },
    scheduling: { requestedAtUtc: '2026-09-10T00:00:00Z', startedAtUtc: '2026-09-10T00:00:00Z' },
  } as unknown as TransferStateDto;
  expect(liveTransferPresentation(live)).toMatchObject({
    state: 'Downloading', tone: 'active', progressPercent: 50, progressText: '500.0 KB · 50%',
  });
  const retained = { revision: 1,
    transferId: 'download',
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\Track.flac' },
    status: { attemptCount: 0, availableActions: [], state: 'InProgress', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: 1_000_000, bytesTransferred: 500_000 },
    scheduling: { requestedAtUtc: '2026-09-10T00:00:00Z', startedAtUtc: '2026-09-10T00:00:00Z' }
  } as TransferStateDto;
  expect(buildTransferGroups([retained], [], 'download')[0]!.items[0]!.transfer).toMatchObject({
    state: 'Downloading', tone: 'active', progressPercent: 50, progressText: '500.0 KB · 50%',
  });
});

it('keeps a resumed partially transferred file queued when its current owner state is queued', () => {
  const live = { revision: 1, createdAtUtc: '2026-09-10T00:00:00Z',
    transferId: 'retry', identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\Track.flac' },
    progress: { totalBytes: 1_000_000, bytesTransferred: 500_000, bytesPerSecond: 250_000 },
    status: { attemptCount: 0, state: 'Queued (R)', isTerminal: false, terminalOutcome: 'None' },
    scheduling: { requestedAtUtc: '2026-09-10T00:00:00Z', startedAtUtc: '2026-09-10T00:00:00Z' },
  } as unknown as TransferStateDto;
  expect(liveTransferPresentation(live)).toMatchObject({ state: 'Queued (R)', tone: 'queued' });
  expect(liveTransferPresentation(live).speed).toBeUndefined();
  const retained = { revision: 1,
    transferId: 'retry',
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\Track.flac' },
    status: { attemptCount: 0, availableActions: [], state: 'Queued (R)', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: 1_000_000, bytesTransferred: 500_000, bytesPerSecond: 250_000 },
    scheduling: { requestedAtUtc: '2026-09-10T00:00:00Z', startedAtUtc: '2026-09-10T00:00:00Z' }
  } as TransferStateDto;
  expect(buildTransferGroups([retained], [], 'download')[0]!.items[0]!.transfer).toMatchObject({ state: 'Queued (R)', tone: 'queued' });
  expect(buildTransferGroups([retained], [], 'download')[0]!.items[0]!.transfer.speed).toBeUndefined();
  expect(buildTransferGroups([retained], [], 'download')[0]!.items[0]!.transfer.progressText).toBe('Waiting for peer');
});

it('shows the same standalone completion time before and after persistence without repeating its byte total', () => {
  const row = { revision: 1,
    transferId: 'done',
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\Track.flac' },
    status: { attemptCount: 0, availableActions: [], completedAtUtc: '2026-09-10T00:01:00Z', state: 'Completed', terminalOutcome: 'Succeeded' as const, isTerminal: true },
    progress: { totalBytes: 1_000_000, bytesTransferred: 1_000_000 }
  } as TransferStateDto;
  expect(buildTransferGroups([row], [], 'download')[0]!.items[0]!.transfer).toMatchObject({
    state: 'Complete', tone: 'complete', progressPercent: 100,
    completedAt: '2026-09-10T00:01:00Z',
  });
  expect(buildTransferGroups([row], [], 'download')[0]!.items[0]!.transfer.progressText).toBeUndefined();
  const live = { revision: 1, createdAtUtc: '2026-09-10T00:00:00Z',
    transferId: 'done', identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\Track.flac' },
    progress: { totalBytes: 1_000_000, bytesTransferred: 1_000_000, bytesPerSecond: 0 },
    status: { attemptCount: 0, state: 'Completed', isTerminal: true, terminalOutcome: 'Succeeded' },
  } as unknown as TransferStateDto;
  expect(liveTransferPresentation(live).progressText).toBe('Complete');
  const terminal = { ...live, status: { ...live.status, completedAtUtc: row.status.completedAtUtc } };
  expect(buildTransferGroups([], [terminal], 'download')[0]!.items[0]!.transfer)
    .toMatchObject({ completedAt: row.status.completedAtUtc, progressPercent: 100, tone: 'complete' });
  expect(liveTransferPresentation(terminal).progressText).toBeUndefined();
  const failed = { ...live, status: { ...live.status, state: 'Failed', terminalOutcome: 'Failed' as const,
    failureMessage: 'Peer connection closed.' } };
  expect(liveTransferPresentation(failed)).toMatchObject({ tone: 'failed', detail: 'Peer connection closed.' });
});

it('bounds per-ID requests and continues independent transfers after a failure', async () => {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => release = resolve);
  const visited: string[] = [];
  let active = 0, peak = 0;
  const result = runTransferCommands(['a', 'b', 'c', 'd', 'e', 'f'], async (id) => {
    visited.push(id);
    peak = Math.max(peak, ++active);
    try {
      await gate;
      if (id === 'b') throw new Error('peer unavailable');
    } finally { active--; }
  });
  expect(visited).toEqual(['a', 'b', 'c', 'd']);
  release();
  expect(await result).toEqual({ succeeded: 5, failed: 1, firstError: 'peer unavailable' });
  expect(visited).toEqual(['a', 'b', 'c', 'd', 'e', 'f']);
  expect(peak).toBe(4);
});

it('weights grouped transfer progress by bytes and leaves it unknown when a file total is unknown', () => {
  const file = (transferId: string, totalBytes: number | null, transferredBytes: number) => ({ revision: 1,
    transferId,
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: `Folder\\${transferId}`, groupRef: 'directory', groupDisplayPath: 'Folder' },
    status: { attemptCount: 0, availableActions: [], state: 'InProgress', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: (totalBytes) ?? -1, bytesTransferred: transferredBytes }
  }) as TransferStateDto;
  const small = file('small', 100, 100);
  const large = file('large', 900, 0);
  const grouped = buildTransferGroups([small, large], [], 'download')[0]!.items[0]!;
  expect(grouped.kind).toBe('folder');
  expect(grouped.transfer.progressPercent).toBe(10);
  const unknown = buildTransferGroups([small, file('unknown', null, 0)], [], 'download')[0]!.items[0]!;
  expect(unknown.transfer.progressPercent).toBeUndefined();
  expect(unknown.kind === 'folder' && unknown.sizeBytes).toBe(100);
});

it('uses whole-group totals and status when only one folder member is loaded', () => {
  const loaded = { revision: 1,
    transferId: 'finished',
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\finished.bin', groupRef: 'directory', groupDisplayPath: 'Folder' },
    status: { attemptCount: 0, availableActions: [], state: 'Completed', terminalOutcome: 'Succeeded' as const, isTerminal: true },
    progress: { totalBytes: 100, bytesTransferred: 100 }
  } as TransferStateDto;
  const summary = {
    key: { direction: 'Download', username: 'Peer', groupRef: 'directory' },
    fileCount: 2, knownTotalBytes: 1000, transferredBytes: 100, speedBytesPerSecond: 0, unknownTotalCount: 0,
    knownRemainingActiveBytes: 900, unknownActiveTotalCount: 0,
    queuedCount: 1, inProgressCount: 0, succeededCount: 1, failedCount: 0,
    cancelledCount: 0, interruptedCount: 0,
  } satisfies Schema<'TransferGroupSummaryDto'>;
  const item = buildTransferGroups([loaded], [], 'download', [summary])[0]!.items[0]!;
  expect(item.kind).toBe('folder');
  if (item.kind !== 'folder') throw new Error('Expected a folder entry.');
  expect(item.totalFileCount).toBe(2);
  expect(item.filesComplete).toBe(false);
  expect(item.sizeBytes).toBe(1000);
  expect(item.transfer.state).toBe('Queued');
  expect(item.transfer.progressPercent).toBe(10);
  expect(item.transfer.cancellable).toBe(true);
});

it('uses the exact completed-folder count even when its member page is partial', () => {
  const loaded = { revision: 1,
    transferId: 'finished',
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\finished.bin', groupRef: 'directory', groupDisplayPath: 'Folder' },
    status: { attemptCount: 0, availableActions: [], completedAtUtc: '2026-09-10T00:01:00Z', state: 'Completed', terminalOutcome: 'Succeeded' as const, isTerminal: true },
    progress: { totalBytes: 100, bytesTransferred: 100 }
  } as TransferStateDto;
  const summary = {
    key: { direction: 'Download', username: 'Peer', groupRef: 'directory' },
    fileCount: 2, knownTotalBytes: 200, transferredBytes: 200, speedBytesPerSecond: 0,
    unknownTotalCount: 0, knownRemainingActiveBytes: 0, unknownActiveTotalCount: 0,
    queuedCount: 0, inProgressCount: 0, succeededCount: 2, failedCount: 0,
    cancelledCount: 0, interruptedCount: 0,
  } satisfies Schema<'TransferGroupSummaryDto'>;
  const item = buildTransferGroups([loaded], [], 'download', [summary])[0]!.items[0]!;
  expect(item.kind).toBe('folder');
  if (item.kind !== 'folder') throw new Error('Expected a folder entry.');
  expect(item.filesComplete).toBe(false);
  expect(item.transfer.progressText).toBe('2 files complete');
  expect(item.transfer.completedAt).toBeUndefined();
});

it('estimates folder time from unfinished active bytes, excluding failed members', () => {
  const loaded = { revision: 1,
    transferId: 'failed',
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\failed.bin', groupRef: 'directory', groupDisplayPath: 'Folder' },
    status: { attemptCount: 0, availableActions: [], state: 'Failed', terminalOutcome: 'Failed', isTerminal: true },
    progress: { totalBytes: 100, bytesTransferred: 0 }
  } as TransferStateDto;
  const summary = {
    key: { direction: 'Download', username: 'Peer', groupRef: 'directory' },
    fileCount: 2, knownTotalBytes: 200, transferredBytes: 0, speedBytesPerSecond: 10, unknownTotalCount: 0,
    knownRemainingActiveBytes: 100, unknownActiveTotalCount: 0,
    queuedCount: 0, inProgressCount: 1, succeededCount: 0, failedCount: 1,
    cancelledCount: 0, interruptedCount: 0,
  } satisfies Schema<'TransferGroupSummaryDto'>;
  const item = buildTransferGroups([loaded], [], 'download', [summary])[0]!.items[0]!;
  expect(item.transfer.eta).toBe('10s remaining');
  const unknownFinished = { ...summary, fileCount: 3, failedCount: 2, unknownTotalCount: 1 };
  expect(buildTransferGroups([loaded], [], 'download', [unknownFinished])[0]!.items[0]!.transfer.eta).toBe('10s remaining');
  const unknownActive = { ...unknownFinished, unknownActiveTotalCount: 1 };
  expect(buildTransferGroups([loaded], [], 'download', [unknownActive])[0]!.items[0]!.transfer.eta).toBeUndefined();
});

it('does not invent a timestamp or audio properties for a live transfer with missing metadata', () => {
  const live = { revision: 1, createdAtUtc: '2026-09-10T00:00:00Z',
    transferId: 'live',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\File.txt' },
    progress: { totalBytes: 128, bytesTransferred: 0, bytesPerSecond: 0 },
    status: { attemptCount: 0, state: 'Queued', isTerminal: false },
    scheduling: null,
    file: { name: 'File.txt', size: 128, bitRate: null, bitDepth: null, sampleRate: null, length: null },
  } as unknown as TransferStateDto;
  const item = buildTransferGroups([], [live], 'download')[0]!.items[0]!;
  expect(item.transfer.createdAt).toBeUndefined();
  expect(item.kind === 'file' && item.audio).toBeUndefined();
  const retained = { revision: 1,
    transferId: 'live',
    createdAtUtc: '2026-09-10T00:00:00Z',
    scheduling: { requestedAtUtc: '2026-09-10T00:00:00Z', startedAtUtc: null },
    identity: { source: 'SoulseekPeer', direction: 'Download' },
    status: { attemptCount: 0, availableActions: [], state: 'Queued', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: -1, bytesTransferred: 0 }
  } as TransferStateDto;
  expect(buildTransferGroups([retained], [], 'download')[0]!.items[0]!.transfer.createdAt).toBe(retained.createdAtUtc);
});

it('keeps unknown transfer file sizes absent while preserving valid zero and known metadata', () => {
  const live = { revision: 1, createdAtUtc: '2026-09-10T00:00:00Z',
    transferId: 'unknown-size',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\Unknown.txt' },
    progress: { totalBytes: -1, bytesTransferred: 64, bytesPerSecond: 0 },
    status: { attemptCount: 0, state: 'InProgress', isTerminal: false },
    file: { name: 'Unknown.txt', size: null },
  } as unknown as TransferStateDto;
  const unknown = buildTransferGroups([], [live], 'download')[0]!.items[0]!;
  expect(unknown.kind).toBe('file');
  if (unknown.kind !== 'file') throw new Error('Expected a file entry');
  expect(unknown.sizeBytes).toBeNull();
  const known = buildTransferGroups([], [{ ...live, file: { ...live.file!, size: 128 } }], 'download')[0]!.items[0]!;
  expect(known.kind === 'file' && known.sizeBytes).toBe(128);
  const grouped = buildTransferGroups([], [{ ...live, identity: {
    ...live.identity, groupRef: 'directory', groupDisplayPath: 'Folder',
  } }], 'download')[0]!.items[0]!;
  expect(grouped.kind).toBe('folder');
  if (grouped.kind !== 'folder') throw new Error('Expected a folder entry');
  expect(grouped.sizeBytes).toBeNull();
  expect(grouped.files[0]?.sizeUnknown).toBe(true);
  const summary = {
    key: { direction: 'Download' as const, username: 'Peer', groupRef: 'directory' },
    fileCount: 1, knownTotalBytes: 0, transferredBytes: 64, speedBytesPerSecond: 0,
    unknownTotalCount: 1, knownRemainingActiveBytes: 0, unknownActiveTotalCount: 1,
    queuedCount: 0, inProgressCount: 1, succeededCount: 0, failedCount: 0,
    cancelledCount: 0, interruptedCount: 0,
  } satisfies Schema<'TransferGroupSummaryDto'>;
  const summarized = buildTransferGroups([], [{ ...live, identity: {
    ...live.identity, groupRef: 'directory', groupDisplayPath: 'Folder',
  } }], 'download', [summary])[0]!.items[0]!;
  expect(summarized.kind === 'folder' && summarized.sizeBytes).toBeNull();

  const retained = { revision: 1,
    transferId: 'retained-unknown',
    createdAtUtc: '2026-09-10T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Folder\\Retained.txt' },
    status: { attemptCount: 0, availableActions: [], state: 'Queued', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: -1, bytesTransferred: 0 }
  } as TransferStateDto;
  const historical = buildTransferGroups([retained], [], 'download')[0]!.items[0]!;
  expect(historical.kind === 'file' && historical.sizeBytes).toBeNull();
  const zero = buildTransferGroups([{
    ...retained,
    progress: { ...retained.progress, totalBytes: 0 }
  }], [], 'download')[0]!.items[0]!;
  expect(zero.kind === 'file' && zero.sizeBytes).toBe(0);
});


it('projects fresh immutable snapshots even when a replacement keeps its revision', () => {
  const projection = new TransferProjection();
  const one = {
    transferId: 'one',
    revision: 1,
    createdAtUtc: '2026-10-24T02:00:00Z',
    identity: { source: 'SoulseekPeer', username: 'Peer', direction: 'Download', groupRef: 'group', groupDisplayPath: 'Folder', remotePath: 'Folder\\one.bin' },
    status: { attemptCount: 0, availableActions: [], state: 'InProgress', terminalOutcome: 'None', isTerminal: false },
    progress: { totalBytes: 100, bytesTransferred: 25 }
  } as TransferStateDto;
  const two = {
    ...one,
    transferId: 'two',
    identity: { ...one.identity, remotePath: 'Folder\\two.bin' },
    progress: { ...one.progress, bytesTransferred: 0 }
  };
  const summary = { key: { direction: 'Download', username: 'Peer', groupRef: 'group' },
    fileCount: 2, knownTotalBytes: 200, transferredBytes: 25, speedBytesPerSecond: 0,
    unknownTotalCount: 0, knownRemainingActiveBytes: 175, unknownActiveTotalCount: 0,
    queuedCount: 0, inProgressCount: 2, succeededCount: 0, failedCount: 0,
    cancelledCount: 0, interruptedCount: 0,
  } satisfies Schema<'TransferGroupSummaryDto'>;
  const folder = (rows: TransferStateDto[], totals = summary) => {
    const entry = buildTransferGroups(rows, [], 'download', [totals], projection)[0]!.items[0]!;
    if (entry.kind !== 'folder') throw new Error('Expected a folder.');
    return entry;
  };
  const initial = folder([one, two]);
  expect(folder([one, two])).toEqual(initial);
  const changed = folder([{
    ...one,
    status: { ...one.status, availableActions: [{ kind: 'cancel', method: 'POST', href: '/api/transfers/one/cancel' }] },
    progress: { ...one.progress, bytesTransferred: 75 }
  }, two]);
  expect(changed.files.find(file => file.id === 'one')?.transfer)
    .toMatchObject({ progressPercent: 75, cancellable: true });
  expect(changed.files.find(file => file.id === 'two')?.transfer?.progressPercent).toBe(0);
  expect(folder([one, two]).files.find(file => file.id === 'one')?.transfer)
    .toMatchObject({ progressPercent: 25, cancellable: false });
  expect(folder([one, two], { ...summary, transferredBytes: 100 }).transfer.progressPercent).toBe(50);
});

it('orders transfer cards by time when UTC timestamps have different fractional precision', () => {
  const rows = [
    ['newer', '2026-10-24T02:00:00.200+00:00'],
    ['fraction', '2026-10-24T02:00:00.100+00:00'],
    ['whole', '2026-10-24T02:00:00+00:00'],
  ].map(([transferId, createdAtUtc]) => ({ revision: 1,
    transferId,
    createdAtUtc,
    identity: { source: 'SoulseekPeer', username: 'Peer', direction: 'Download', remotePath: `${transferId}.bin` },
    status: { attemptCount: 0, availableActions: [], state: 'Completed', terminalOutcome: 'Succeeded' as const, isTerminal: true },
    progress: { totalBytes: 1, bytesTransferred: 1 }
  })) as TransferStateDto[];
  expect(buildTransferGroups(rows, [], 'download')[0]!.items.map(item => item.path))
    .toEqual(['newer.bin', 'fraction.bin', 'whole.bin']);
});

it('keeps peer sections stable when arrivals change their first card or position', () => {
  const projection = new TransferProjection();
  const row = (transferId: string, username: string | null, second: number) => ({ revision: 1,
    transferId,
    createdAtUtc: new Date(second * 1000).toISOString(),
    identity: { source: 'SoulseekPeer', username: username, direction: 'Download', remotePath: `${transferId}.bin` },
    status: { attemptCount: 0, availableActions: [], state: 'Completed', terminalOutcome: 'Succeeded' as const, isTerminal: true },
    progress: { totalBytes: -1, bytesTransferred: 0 }
  }) as TransferStateDto;
  const old = row('old', 'Peer', 1);
  const original = arrangeTransferGroups([old], 'download', [], projection)[0]!;
  const arrived = row('arrived', 'Peer', 2);
  const next = arrangeTransferGroups([old, arrived], 'download', [], projection)[0]!;
  expect(next.key).toBe(original.key);
  expect(next.items.map(item => item.id)).toEqual(['arrived', 'old']);
  const shifted = arrangeTransferGroups([old, arrived, row('other', 'Other', 3)], 'download', [], projection);
  expect(shifted[1]!.key).toBe(original.key);
  const split = arrangeTransferGroups([old, row('other', 'Other', 3), row('head', 'Peer', 4)], 'download', [], projection);
  expect(split.map(group => group.username)).toEqual(['Peer', 'Other', 'Peer']);
  expect(new Set(split.map(group => group.key)).size).toBe(3);
  const exact = arrangeTransferGroups([row('a', 'Peer', 4), row('b', 'peer', 3),
    row('c', null, 2), row('d', 'Unknown peer', 1)], 'download', [], projection);
  expect(new Set(exact.map(group => group.key)).size).toBe(4);
});

it('merges exact changes without hiding fetched rows, losing order or regressing revisions', () => {
  const row = (id: string, seconds: number, revision = 1) => ({
    transferId: id,
    createdAtUtc: new Date(seconds * 1000).toISOString(),
    revision,
    identity: {direction: 'Download',  source: 'SoulseekPeer' },
    status: { attemptCount: 0, isTerminal: false, terminalOutcome: 'None', state: 'Queued', availableActions: [] },
    progress: { totalBytes: -1, bytesTransferred: 0 }
  }) as TransferStateDto;
  const previous = [row('recent', 4), row('changed', 3, 4), row('removed', 2)];
  const merged = mergeTransferHistory(previous, [row('old-retirement', 0), row('changed', 3, 3), row('new', 5)], ['removed']);
  expect(merged.map(item => item.transferId)).toEqual(['new', 'recent', 'changed', 'old-retirement']);
  expect(merged.find(item => item.transferId === 'changed')?.revision).toBe(4);
  const continued = mergeTransferHistory(merged, [row('older-page', 1), row('changed', 3, 5)]);
  expect(continued.map(item => item.transferId)).toEqual(['new', 'recent', 'changed', 'older-page', 'old-retirement']);
  expect(continued.find(item => item.transferId === 'changed')?.revision).toBe(5);
  // An older HTTP continuation may finish after the stream moves its window.
  // Apply the daemon's membership instead of resurrecting displaced rows.
  const window = mergeTransferHistory(continued, [row('outside', 6)], [], ['new', 'changed']);
  expect(window.map(item => item.transferId)).toEqual(['new', 'changed']);
  expect(window[1]?.revision).toBe(5);
});

it.each(['live', 'retained'])('renders planned nested paths for a custom directory label (%s)', source => {
  const history = {
    transferId: 'nested',
    revision: 1,
    createdAtUtc: '2026-10-25T00:00:00Z',
    identity: { source: 'SoulseekPeer', direction: 'Download', username: 'Peer', remotePath: 'Shares\\Album\\Artwork\\cover.jpg', groupRef: 'selection', groupDisplayPath: 'My selection', groupRelativePath: 'Artwork/cover.jpg' },
    status: { attemptCount: 0, availableActions: [], state: 'Completed', terminalOutcome: 'Succeeded' as const, isTerminal: true },
    progress: { totalBytes: 10, bytesTransferred: 10 }
  } as TransferStateDto;
  const live = { createdAtUtc: '2026-09-10T00:00:00Z',  transferId: history.transferId, revision: 1,
    identity: { direction: history.identity.direction, source: "SoulseekPeer", username: history.identity.username, remotePath: history.identity.remotePath,
      groupRef: history.identity.groupRef, groupDisplayPath: history.identity.groupDisplayPath, groupRelativePath: history.identity.groupRelativePath },
    status: { state: 'Completed', attemptCount: 1, isTerminal: true, terminalOutcome: 'Succeeded', availableActions: [] },
    progress: { totalBytes: 10, bytesTransferred: 10 }, scheduling: { requestedAtUtc: history.createdAtUtc },
  } as TransferStateDto;
  const folder = buildTransferGroups(source === 'retained' ? [history] : [], source === 'live' ? [live] : [], 'download')[0]!.items[0]!;
  expect(folder.kind).toBe('folder');
  if (folder.kind === 'folder') {
    expect(folder.path).toBe('My selection');
    expect(folder.files[0]!.relativePath).toBe('Artwork/cover.jpg');
    expect(folder.sourceTransferIds).toEqual(['nested']);
  }
});


it('groups fallback providers independently and presents their titles without inventing peers', () => {
  const rows = ['Provider A', 'Provider B'].map((provider, index): TransferStateDto => ({
    transferId: String(index), revision: 1, createdAtUtc: new Date(index).toISOString(),
    identity: { direction: 'Download', source: 'Fallback', username: null, remotePath: 'opaque-id',
      fallback: { provider, title: 'Source title', reference: 'opaque-id' } },
    status: { state: 'AttemptStarted', isTerminal: false, attemptCount: 1 },
    scheduling: { requestedAtUtc: new Date(0).toISOString(), startedAtUtc: new Date(0).toISOString() },
    progress: { bytesTransferred: 0, totalBytes: 0 },
  }));
  const groups = buildTransferGroups(rows, [], 'download');
  expect(groups).toHaveLength(2);
  expect(new Set(groups.map(group => group.peer))).toEqual(new Set(['Provider A', 'Provider B']));
  for (const group of groups) {
    expect(group.username).toBeNull();
    expect(group.items[0]).toMatchObject({ path: 'Source title', transfer: { state: 'Running fallback' } });
  }
  expect(liveTransferPresentation({ ...rows[0]!, status: { ...rows[0]!.status, state: 'AttemptCompleted' } }).state)
    .toBe('Finalizing');
});
