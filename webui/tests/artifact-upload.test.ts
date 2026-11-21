import { afterEach, expect, it, vi } from 'vitest';
import { DaemonApiClient } from '../src/api/client';

afterEach(() => vi.unstubAllGlobals());

it('uploads the original file body without materializing text or treating its name as a path', async () => {
  const file = new File(['artist,title\nArtist,Track'], 'music & favorites.csv', { type: 'text/csv' });
  const abort = new AbortController();
  const fetchMock = vi.fn(async () => new Response(JSON.stringify({ artifactId: 'artifact' }), {
    status: 201, headers: { 'content-type': 'application/json' },
  }));
  vi.stubGlobal('fetch', fetchMock);
  const result = await new DaemonApiClient().uploadInputArtifact(file, abort.signal);
  expect(result.artifactId).toBe('artifact');
  expect(fetchMock).toHaveBeenCalledWith('/api/input-artifacts?fileName=music+%26+favorites.csv',
    expect.objectContaining({ method: 'POST', body: file, credentials: 'same-origin' }));
  const request = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
  expect(new Headers(request.headers).get('content-type')).toBe('application/octet-stream');
  abort.abort();
  expect(request.signal?.aborted).toBe(true);
});
