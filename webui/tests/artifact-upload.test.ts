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
  expect(fetchMock).toHaveBeenCalledWith('/api/input-artifacts?fileName=music+%26+favorites.csv', {
    method: 'POST', body: file, headers: { 'content-type': 'application/octet-stream' }, signal: abort.signal,
  });
});
