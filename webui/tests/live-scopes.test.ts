import { afterEach, expect, it, vi } from 'vitest';
import type { DaemonApiClient } from '../src/api/client';
import type { StateSnapshotDto, StateUpdateBatchDto, UserBrowseDto } from '../src/api/types';
import { DaemonLiveClient, type LiveConnectionStatus } from '../src/api/live';

interface Hub {
  state: string;
  invoke: ReturnType<typeof vi.fn>;
  receive: (batch: StateUpdateBatchDto) => void;
  reconnecting: () => void;
  reconnected: () => Promise<void>;
  closed: () => void;
}
const hubs = vi.hoisted(() => [] as Hub[]);
vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: { Connected: 'Connected' },
  LogLevel: { Warning: 3, Error: 4 },
  HubConnectionBuilder: class {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() {
      const hub: Hub = {
        state: 'Disconnected', invoke: vi.fn().mockResolvedValue(undefined),
        receive: () => undefined, reconnecting: () => undefined,
        reconnected: async () => undefined, closed: () => undefined,
      };
      hubs.push(hub);
      return Object.assign(hub, {
        on: (_: string, receive: Hub['receive']) => { hub.receive = receive; },
        onreconnecting: (callback: Hub['reconnecting']) => { hub.reconnecting = () => { hub.state = 'Reconnecting'; callback(); }; },
        onreconnected: (callback: Hub['reconnected']) => { hub.reconnected = async () => { hub.state = 'Connected'; await callback(); }; },
        onclose: (callback: Hub['closed']) => { hub.closed = callback; },
        start: async () => { hub.state = 'Connected'; },
        stop: async () => { hub.state = 'Disconnected'; hub.closed(); },
      });
    }
  },
}));

const firstId = '10000000-0000-0000-0000-000000000001';
const secondId = '10000000-0000-0000-0000-000000000002';
const epoch = '20000000-0000-0000-0000-000000000001';
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(complete => { resolve = complete; });
  return { promise, resolve };
}
function resource(browseId: string, revision = 1): UserBrowseDto {
  return {
    browseId, username: 'ExactPeer', state: 'running', phase: 'receiving',
    compressedBytesReceived: 0, compressedBytesExpected: null, directoryCount: 0,
    fileCount: revision, totalFileBytes: revision * 128, revision,
    createdAt: '2026-10-22T00:00:00Z', updatedAt: '2026-10-22T00:00:00Z',
    expiresAt: null, failure: null,
  };
}
function snapshot(browseId?: string, sequence = 10, revision = 1): StateSnapshotDto {
  return {
    scope: browseId ? { kind: 'userBrowse', userBrowseId: browseId } : { kind: 'daemon' },
    position: { epoch, sequence }, capturedAtUtc: '2026-10-22T00:00:00Z',
    daemon: null, workflows: [], jobs: [], searches: [], transfers: [], chatTarget: null,
    userBrowse: browseId ? resource(browseId, revision) : null,
  };
}
function batch(browseId: string, sequence: number, previousSequence: number, revision: number): StateUpdateBatchDto {
  return {
    scope: { kind: 'userBrowse', userBrowseId: browseId }, epoch, sequence, previousSequence,
    occurredAtUtc: '2026-10-22T00:00:00Z', activity: [],
    state: { workflows: [], jobs: [], searches: [], transfers: [], removedWorkflowIds: [],
      removedJobIds: [], removedSearchJobIds: [], removedTransferIds: [], userBrowse: resource(browseId, revision) },
  };
}
function fixture() {
  const api = {
    getServerInfo: vi.fn().mockResolvedValue({ liveProtocolVersion: 18 }),
    getDaemonSnapshot: vi.fn().mockResolvedValue(snapshot()),
    getUserBrowseSnapshot: vi.fn((id: string) => Promise.resolve(snapshot(id))),
  };
  const client = new DaemonLiveClient(api as unknown as DaemonApiClient);
  return { api, client };
}
const clients: DaemonLiveClient[] = [];
afterEach(async () => {
  for (const client of clients.splice(0)) await client.stop();
  hubs.length = 0;
});

it('keeps global transfer details opt-in and preserves the mode after reconnect', async () => {
  const { api, client } = fixture();
  clients.push(client);
  await client.startDaemon();
  expect(hubs[0]!.invoke).toHaveBeenLastCalledWith('SubscribeAll', 'Overview');
  expect(api.getDaemonSnapshot).toHaveBeenLastCalledWith(undefined, 'Overview');
  hubs[0]!.reconnecting();
  await hubs[0]!.reconnected();
  expect(hubs[0]!.invoke).toHaveBeenLastCalledWith('SubscribeAll', 'Overview');
  expect(api.getDaemonSnapshot).toHaveBeenLastCalledWith(undefined, 'Overview');
});

it('shares one subscription/snapshot, replays a buffered update, and releases only its own listeners', async () => {
  const { api, client } = fixture();
  clients.push(client);
  await client.startDaemon();
  const started = deferred<void>();
  const initial = deferred<StateSnapshotDto>();
  api.getUserBrowseSnapshot.mockImplementationOnce(() => { started.resolve(); return initial.promise; });
  const first = vi.fn();
  const second = vi.fn();
  const firstSubscription = client.subscribeUserBrowse(firstId, first, vi.fn());
  const secondSubscription = client.subscribeUserBrowse(firstId, second, vi.fn());
  await started.promise;
  hubs[0]!.receive(batch(firstId, 11, 10, 2));
  initial.resolve(snapshot(firstId));
  const releaseFirst = await firstSubscription;
  const releaseSecond = await secondSubscription;
  expect(api.getUserBrowseSnapshot).toHaveBeenCalledTimes(1);
  expect(hubs[0]!.invoke.mock.calls.filter(call => call[0] === 'SubscribeUserBrowse')).toHaveLength(1);
  expect(first.mock.calls.map(call => call[0].revision)).toEqual([2]);
  expect(second.mock.calls.map(call => call[0].revision)).toEqual([2]);
  await releaseFirst();
  hubs[0]!.receive(batch(firstId, 12, 11, 3));
  expect(first).toHaveBeenCalledTimes(1);
  expect(second.mock.calls.at(-1)?.[0].revision).toBe(3);
  expect(hubs[0]!.invoke.mock.calls.filter(call => call[0] === 'UnsubscribeUserBrowse')).toHaveLength(0);
  await releaseSecond();
  expect(hubs[0]!.invoke.mock.calls.filter(call => call[0] === 'UnsubscribeUserBrowse')).toHaveLength(1);

  const replacement = vi.fn();
  const releaseReplacement = await client.subscribeUserBrowse(firstId, replacement, vi.fn());
  await releaseFirst();
  await releaseSecond();
  hubs[0]!.receive(batch(firstId, 11, 10, 2));
  expect(replacement.mock.calls.map(call => call[0].revision)).toEqual([1, 2]);
  await releaseReplacement();
});

it('recovers only a gapped scope, drains updates received during recovery, and isolates a failed scope', async () => {
  const { api, client } = fixture();
  clients.push(client);
  const statuses: LiveConnectionStatus[] = [];
  client.onStatus(status => statuses.push(status));
  await client.startDaemon();
  const recovered = deferred<UserBrowseDto>();
  const failed = deferred<Error>();
  const first = vi.fn((value: UserBrowseDto) => { if (Number(value.revision) === 4) recovered.resolve(value); });
  const second = vi.fn();
  await client.subscribeUserBrowse(firstId, first, failed.resolve);
  await client.subscribeUserBrowse(secondId, second, vi.fn());
  const started = deferred<void>();
  const recovery = deferred<StateSnapshotDto>();
  api.getUserBrowseSnapshot.mockImplementationOnce(() => { started.resolve(); return recovery.promise; });
  hubs[0]!.receive(batch(firstId, 12, 11, 3));
  await started.promise;
  hubs[0]!.receive(batch(firstId, 13, 12, 4));
  hubs[0]!.receive(batch(secondId, 11, 10, 2));
  expect(second.mock.calls.at(-1)?.[0].revision).toBe(2);
  recovery.resolve(snapshot(firstId, 12, 3));
  expect((await recovered.promise).revision).toBe(4);
  expect(first.mock.calls.map(call => call[0].revision)).toEqual([1, 4]);
  expect(api.getUserBrowseSnapshot).toHaveBeenCalledTimes(3);
  api.getUserBrowseSnapshot.mockRejectedValueOnce(new Error('This browse is unavailable'));
  hubs[0]!.receive(batch(firstId, 15, 14, 5));
  expect(api.getUserBrowseSnapshot).toHaveBeenCalledTimes(4);
  expect((await failed.promise).message).toBe('This browse is unavailable');
  hubs[0]!.receive(batch(secondId, 12, 11, 3));
  expect(second.mock.calls.at(-1)?.[0].revision).toBe(3);
  expect(statuses).toEqual(['connecting', 'connected']);
  expect(hubs[0]!.invoke.mock.calls.filter(call => call[0] === 'UnsubscribeUserBrowse' && call[1] === firstId)).toHaveLength(1);
});

it('rejects snapshots and deltas belonging to another resource without poisoning the daemon connection', async () => {
  const { api, client } = fixture();
  clients.push(client);
  const statuses: LiveConnectionStatus[] = [];
  client.onStatus(status => statuses.push(status));
  await client.startDaemon();
  const listener = vi.fn();
  const errors = vi.fn();
  api.getUserBrowseSnapshot.mockResolvedValueOnce(snapshot(secondId));
  await expect(client.subscribeUserBrowse(firstId, listener, errors)).rejects.toThrow('different resource');
  expect(listener).not.toHaveBeenCalled();
  await client.subscribeUserBrowse(firstId, listener, errors);
  const invalid = batch(firstId, 11, 10, 2);
  hubs[0]!.receive({ ...invalid, state: { ...invalid.state, userBrowse: resource(secondId, 2) } });
  expect(errors).toHaveBeenCalledOnce();
  expect(errors.mock.calls[0]?.[0].message).toContain('different resource');
  expect(listener).toHaveBeenCalledOnce();
  expect(statuses).toEqual(['connecting', 'connected']);
});

it('starts a fresh daemon recovery for a second gap immediately after snapshot publication', async () => {
  const { api, client } = fixture();
  clients.push(client);
  const recovered = deferred<void>();
  const recoveredAgain = deferred<void>();
  let publications = 0;
  let replacedSnapshot = false;
  client.onState((_, changes) => {
    replacedSnapshot = changes.snapshot;
    if (++publications === 2) recovered.resolve();
    if (publications === 3) recoveredAgain.resolve();
  });
  await client.startDaemon();
  const update = (sequence: number, previousSequence: number): StateUpdateBatchDto => ({
    ...batch(firstId, sequence, previousSequence, 1), scope: { kind: 'daemon' },
    state: { workflows: [], jobs: [], searches: [], transfers: [], removedWorkflowIds: [],
      removedJobIds: [firstId], removedSearchJobIds: [], removedTransferIds: [secondId] },
  });
  api.getDaemonSnapshot.mockResolvedValueOnce(snapshot(undefined, 12));
  hubs[0]!.receive(update(12, 11));
  await recovered.promise;
  api.getDaemonSnapshot.mockResolvedValueOnce(snapshot(undefined, 14));
  hubs[0]!.receive(update(14, 13));
  expect(api.getDaemonSnapshot).toHaveBeenCalledTimes(3);
  await recoveredAgain.promise;
  expect(replacedSnapshot).toBe(true);
  await client.stop();
  await client.startDaemon();
  const before = publications;
  hubs[0]!.reconnecting();
  hubs[1]!.receive(update(11, 10));
  expect(publications).toBe(before + 1);
  expect(replacedSnapshot).toBe(false);
  hubs[0]!.receive(update(12, 11));
  expect(publications).toBe(before + 1);
});

it('does not publish a pending recovery or subscribe result after the client stops', async () => {
  const { api, client } = fixture();
  clients.push(client);
  const sequences: number[] = [];
  client.onState(state => sequences.push(Number(state.sequence)));
  await client.startDaemon();
  const pendingDaemon = deferred<StateSnapshotDto>();
  api.getDaemonSnapshot.mockReturnValueOnce(pendingDaemon.promise);
  const update = batch(firstId, 12, 11, 2);
  hubs[0]!.receive({ ...update, scope: { kind: 'daemon' }, state: { ...update.state, userBrowse: null } });
  await client.stop();
  pendingDaemon.resolve(snapshot(undefined, 12));
  await pendingDaemon.promise;
  expect(sequences).toEqual([10]);
  await client.startDaemon();
  const started = deferred<void>();
  const pendingBrowse = deferred<StateSnapshotDto>();
  api.getUserBrowseSnapshot.mockImplementationOnce(() => { started.resolve(); return pendingBrowse.promise; });
  const observer = vi.fn();
  const subscribing = client.subscribeUserBrowse(firstId, observer, vi.fn());
  await started.promise;
  await client.stop();
  pendingBrowse.resolve(snapshot(firstId));
  await expect(subscribing).rejects.toThrow('interrupted');
  expect(observer).not.toHaveBeenCalled();
});

it('takes a fresh snapshot after rejoining instead of reusing a read from before the connection loss', async () => {
  const { api, client } = fixture();
  clients.push(client);
  const sequences: number[] = [];
  client.onState(state => sequences.push(Number(state.sequence)));
  await client.startDaemon();
  const oldRead = deferred<StateSnapshotDto>();
  api.getDaemonSnapshot.mockReturnValueOnce(oldRead.promise);
  const update = batch(firstId, 12, 11, 2);
  hubs[0]!.receive({ ...update, scope: { kind: 'daemon' }, state: { ...update.state, userBrowse: null } });
  hubs[0]!.reconnecting();
  api.getDaemonSnapshot.mockResolvedValueOnce(snapshot(undefined, 20));
  const rejoining = hubs[0]!.reconnected();
  oldRead.resolve(snapshot(undefined, 12));
  await rejoining;
  expect(sequences).toEqual([10, 20]);
  const revisions: number[] = [];
  await client.subscribeUserBrowse(firstId, value => revisions.push(Number(value.revision)), vi.fn());
  const oldBrowseRead = deferred<StateSnapshotDto>();
  api.getUserBrowseSnapshot.mockReturnValueOnce(oldBrowseRead.promise);
  hubs[0]!.receive(batch(firstId, 12, 11, 2));
  hubs[0]!.reconnecting();
  api.getDaemonSnapshot.mockResolvedValueOnce(snapshot(undefined, 30));
  api.getUserBrowseSnapshot.mockResolvedValueOnce(snapshot(firstId, 20, 3));
  const rejoiningBrowse = hubs[0]!.reconnected();
  oldBrowseRead.resolve(snapshot(firstId, 12, 2));
  await rejoiningBrowse;
  expect(revisions).toEqual([1, 3]);
});

it('rejoins healthy scopes after reconnect even when another retained browse has expired', async () => {
  const { api, client } = fixture();
  clients.push(client);
  const statuses: LiveConnectionStatus[] = [];
  client.onStatus(status => statuses.push(status));
  await client.startDaemon();
  const unavailable = vi.fn();
  const healthy = vi.fn();
  await client.subscribeUserBrowse(firstId, vi.fn(), unavailable);
  await client.subscribeUserBrowse(secondId, healthy, vi.fn());
  hubs[0]!.reconnecting();
  api.getUserBrowseSnapshot.mockImplementation(id => id === firstId
    ? Promise.reject(new Error('Browse expired')) : Promise.resolve(snapshot(id, 20, 3)));
  await hubs[0]!.reconnected();
  expect(unavailable).toHaveBeenCalledOnce();
  expect(statuses).toEqual(['connecting', 'connected', 'reconnecting', 'connected']);
  hubs[0]!.receive(batch(secondId, 21, 20, 4));
  expect(healthy.mock.calls.at(-1)?.[0].revision).toBe(4);
});
