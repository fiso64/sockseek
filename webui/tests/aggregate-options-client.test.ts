import { expect, it } from 'vitest';
import { DaemonApiClient } from '../src/api/client';
import { DaemonHttpClient } from '../src/api/http';

it.each(['track', 'album'] as const)('pages %s alternatives with exact refs, revision, cursor, and cancellation', async (kind) => {
  let requestedPath = '';
  let requestedSignal: AbortSignal | undefined;
  class Http extends DaemonHttpClient {
    override async get<T>(path: string, signal?: AbortSignal): Promise<T> {
      requestedPath = path;
      requestedSignal = signal;
      throw new Error('Transport unavailable');
    }
  }
  const api = new DaemonApiClient(new Http());
  const signal = new AbortController().signal;
  const request = kind === 'track'
    ? api.getSearchViewAggregateTrackOptions('view', 'Exact/Ref+?', '9007199254740993', 'next+cursor', 25, signal)
    : api.getSearchViewAggregateAlbumOptions('view', 'Exact/Ref+?', '9007199254740993', 'next+cursor', 25, signal);
  await expect(request).rejects.toThrow('Transport unavailable');
  const url = new URL(requestedPath, 'http://localhost');
  expect(url.pathname).toBe(`/search-views/view/aggregate-${kind}s/Exact%2FRef%2B%3F/alternatives`);
  expect(url.searchParams.get('revision')).toBe('9007199254740993');
  expect(url.searchParams.get('cursor')).toBe('next+cursor');
  expect(url.searchParams.get('limit')).toBe('25');
  expect(requestedSignal).toBe(signal);
});
