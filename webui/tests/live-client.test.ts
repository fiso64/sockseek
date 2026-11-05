import { afterEach, expect, it, vi } from 'vitest';
import type { DaemonApiClient } from '../src/api/client';
import { DaemonApiError } from '../src/api/http';
import { DaemonLiveClient, type LiveConnectionStatus } from '../src/api/live';

afterEach(() => vi.useRealTimers());

it('reports an unreachable daemon as offline and retries startup until stopped', async () => {
  vi.useFakeTimers();
  const getServerInfo = vi.fn().mockRejectedValue(new TypeError('Failed to fetch'));
  const client = new DaemonLiveClient({ getServerInfo } as unknown as DaemonApiClient);
  const statuses: LiveConnectionStatus[] = [];
  client.onStatus((status) => statuses.push(status));

  await expect(client.startDaemon()).rejects.toThrow('Failed to fetch');
  expect(statuses).toEqual(['connecting', 'offline']);
  await vi.advanceTimersByTimeAsync(1_000);
  expect(getServerInfo).toHaveBeenCalledTimes(2);
  expect(statuses).toEqual(['connecting', 'offline', 'connecting', 'offline']);

  await client.stop();
  await vi.advanceTimersByTimeAsync(30_000);
  expect(getServerInfo).toHaveBeenCalledTimes(2);
});

it('distinguishes a responding daemon with a failed preflight from offline transport', async () => {
  vi.useFakeTimers();
  const client = new DaemonLiveClient({
    getServerInfo: () => Promise.reject(new DaemonApiError('Server info failed', 500)),
  } as unknown as DaemonApiClient);
  const statuses: LiveConnectionStatus[] = [];
  client.onStatus((status) => statuses.push(status));

  await expect(client.startDaemon()).rejects.toThrow('Server info failed');
  expect(statuses).toEqual(['connecting', 'failed']);
  await client.stop();
});

it('does not report a late preflight failure after the client has stopped', async () => {
  let rejectPreflight: (error: Error) => void = () => undefined;
  const getServerInfo = vi.fn().mockImplementation(() => new Promise((_, reject) => {
    rejectPreflight = reject;
  }));
  const client = new DaemonLiveClient({ getServerInfo } as unknown as DaemonApiClient);
  const statuses: LiveConnectionStatus[] = [];
  client.onStatus((status) => statuses.push(status));

  const startup = client.startDaemon();
  await client.stop();
  rejectPreflight(new TypeError('Failed to fetch'));
  await startup;

  expect(statuses).toEqual(['connecting', 'offline']);
  expect(getServerInfo).toHaveBeenCalledTimes(1);
});
