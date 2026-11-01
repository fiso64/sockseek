import { expect, it, vi } from 'vitest';
import { DaemonApiClient } from '../src/api/client';
import { DaemonHttpClient } from '../src/api/http';
import { albumQuery, emptySearchDraft, parseSearchResultMode, searchJobPath, songQuery, type SearchResultMode } from '../src/domains/search/model';

it('keeps only known result modes in search-detail URLs', () => {
  expect(searchJobPath('a/b', 'song-aggregate')).toBe('/jobs/a%2Fb?results=song-aggregate');
  expect(parseSearchResultMode('song-aggregate')).toBe('song-aggregate');
  expect(parseSearchResultMode('album-aggregate')).toBe('album-aggregate');
  expect(parseSearchResultMode('unknown')).toBeNull();
});

it('restores album context and search hints independently on reopening', async () => {
  const http = new DaemonHttpClient();
  vi.spyOn(http, 'post').mockResolvedValue({ kind: 'AlbumDirectories' });
  const query = { artist: 'Artist', album: 'Album', searchHint: 'edition 2026', uri: 'source:album', artistMaybeWrong: true };
  vi.spyOn(http, 'get').mockResolvedValue({ payload: { kind: 'search', albumQuery: query } });
  const opened = await new DaemonApiClient(http).openSearchJob({ jobId: 'job' });
  expect(opened.draft.mode).toBe('split');
  expect(albumQuery(opened.draft)).toEqual(query);
});

it('keeps expected length in the song query for both input layouts', () => {
  const draft = { ...emptySearchDraft, query: 'Track', expectedLength: 180 };
  expect(songQuery(draft).length).toBe(180);
  expect(songQuery({ ...draft, mode: 'split', title: 'Track', artist: 'Artist' }).length).toBe(180);
  expect(songQuery(emptySearchDraft).length).toBeNull();
});

it('restores structured track fields from retained detail instead of parsing display text', async () => {
  const http = new DaemonHttpClient();
  vi.spyOn(http, 'post').mockResolvedValue({ kind: 'Files' });
  vi.spyOn(http, 'get').mockResolvedValue({ payload: {
    kind: 'search', songQuery: { artist: 'Artist — Name', title: 'Track', length: 180,
      album: 'Album context', uri: 'source:track', artistMaybeWrong: true },
  } });
  const opened = await new DaemonApiClient(http).openSearchJob({ jobId: 'job', queryText: 'Not structured metadata' });
  expect(opened.draft).toMatchObject({ mode: 'split', artist: 'Artist — Name', title: 'Track', expectedLength: 180 });
  expect(songQuery(opened.draft)).toMatchObject({ artist: 'Artist — Name', title: 'Track', length: 180,
    album: 'Album context', uri: 'source:track', artistMaybeWrong: true });
});

it('uses only a compatible result mode when reopening a retained search', async () => {
  const http = new DaemonHttpClient();
  const post = vi.spyOn(http, 'post')
    .mockResolvedValueOnce({ kind: 'Files' })
    .mockResolvedValueOnce({ kind: 'AggregateTracks' });
  vi.spyOn(http, 'get').mockResolvedValue({
    summary: { jobId: 'job' },
    payload: { kind: 'search', defaultProjection: 'Track', includeFullResults: true,
      songQuery: { title: 'Track', artistMaybeWrong: false } },
  });
  const client = new DaemonApiClient(http);
  await client.openSearchJob({ jobId: 'job' }, undefined, undefined, 'album-aggregate');
  await client.openSearchJob({ jobId: 'job' }, undefined, undefined, 'song-aggregate');
  expect(post.mock.calls[0]?.[1]).toMatchObject({ kind: 'Files', includeFullResults: true });
  expect(post.mock.calls[1]?.[1]).toMatchObject({ kind: 'AggregateTracks', includeFullResults: true });
});

it('retains the submitted settings when the caller edits its draft during submission', async () => {
  const http = new DaemonHttpClient();
  let accept!: (value: { jobId: string }) => void;
  vi.spyOn(http, 'post').mockImplementationOnce(() => new Promise((resolve) => { accept = resolve; }))
    .mockResolvedValueOnce({ viewId: 'view' });
  const draft = new Proxy({ ...emptySearchDraft, query: 'Original', searchSettings: { searchTimeout: 1234 } }, {});
  const pending = new DaemonApiClient(http).submitSearch(draft);
  draft.query = 'Edited';
  draft.searchSettings.searchTimeout = 4321;
  accept({ jobId: 'job' });
  const submitted = await pending;
  expect(submitted.draft.query).toBe('Original');
  expect(submitted.draft.searchSettings?.searchTimeout).toBe(1234);
});

it.each<SearchResultMode>(['generic', 'track', 'album', 'song-aggregate', 'album-aggregate'])(
  'passes canonical search overrides on %s submissions', async (resultMode) => {
    const http = new DaemonHttpClient();
    const post = vi.spyOn(http, 'post').mockResolvedValueOnce({ jobId: 'job' }).mockResolvedValueOnce({ viewId: 'view' });
    const searchSettings = { searchTimeout: 1234, necessaryCond: { strictTitle: true } };
    await new DaemonApiClient(http).submitSearch({ ...emptySearchDraft, query: 'Track', resultMode, searchSettings });
    expect(post.mock.calls[0]?.[1]).toMatchObject({ options: { downloadSettings: { search: searchSettings } } });
  },
);
