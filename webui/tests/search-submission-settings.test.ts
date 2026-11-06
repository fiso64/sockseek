import { expect, it, vi } from 'vitest';
import { DaemonApiClient } from '../src/api/client';
import { DaemonHttpClient } from '../src/api/http';
import { albumQuery, editableSearchDraft, emptySearchDraft, parseSearchResultMode, searchJobPath, songQuery, type SearchResultMode } from '../src/domains/search/model';

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
  const edited = { ...editableSearchDraft(opened.draft), artist: 'New artist', title: '' };
  expect(albumQuery(edited)).toEqual({ artist: 'New artist', album: null, searchHint: null, uri: null, artistMaybeWrong: false });
  expect(albumQuery(opened.draft)).toEqual(query);
});


it.each<SearchResultMode>(['album'])(
  'submits unsplit %s input as an album and restores it when reopened', async (resultMode) => {
    const http = new DaemonHttpClient();
    const post = vi.spyOn(http, 'post').mockResolvedValueOnce({ jobId: 'job' })
      .mockResolvedValueOnce({ viewId: 'view' })
      .mockResolvedValueOnce({ kind: resultMode === 'album' ? 'AlbumDirectories' : 'AggregateAlbums' });
    const client = new DaemonApiClient(http);
    await client.submitSearch({ ...emptySearchDraft, mode: 'simple', resultMode, query: '  Mint Jams  ' });
    const expected = { artist: null, album: 'Mint Jams', searchHint: null };
    expect(post.mock.calls[0]?.[1]).toMatchObject({ albumQuery: expected });
    expect(post.mock.calls[1]?.[1]).toMatchObject({ albumQuery: expected });
    vi.spyOn(http, 'get').mockResolvedValue({ summary: { jobId: 'job' }, payload: {
      kind: 'search', defaultProjection: 'Album', aggregatePresentation: resultMode === 'album-aggregate',
      albumQuery: expected,
    } });
    const opened = await client.openSearchJob({ jobId: 'job' });
    expect(opened.draft).toMatchObject({ mode: 'simple', query: 'Mint Jams', resultMode });
    expect(albumQuery(opened.draft)).toMatchObject(expected);
  },
);

it.each<SearchResultMode>(['song-aggregate', 'album-aggregate'])(
  'submits unsplit %s as an artist and restores it on reopen', async resultMode => {
    const http = new DaemonHttpClient();
    const kind = resultMode === 'song-aggregate' ? 'AggregateTracks' : 'AggregateAlbums';
    const field = resultMode === 'song-aggregate' ? 'songQuery' : 'albumQuery';
    const post = vi.spyOn(http, 'post').mockResolvedValueOnce({ jobId: 'job' })
      .mockResolvedValueOnce({ viewId: 'view' }).mockResolvedValueOnce({ kind });
    const client = new DaemonApiClient(http);
    await client.submitSearch({ ...emptySearchDraft, mode: 'simple', resultMode, query: '  Casiopea  ' });
    const query = resultMode === 'song-aggregate' ? { artist: 'Casiopea', title: null } : { artist: 'Casiopea', album: null, searchHint: null };
    expect(post.mock.calls[0]?.[1]).toMatchObject({ [field]: query });
    expect(post.mock.calls[1]?.[1]).toMatchObject({ [field]: query });
    vi.spyOn(http, 'get').mockResolvedValue({ summary: { jobId: 'job' }, payload: {
      kind: 'search', defaultProjection: resultMode === 'song-aggregate' ? 'Track' : 'Album',
      aggregatePresentation: true, [field]: query,
    } });
    const opened = await client.openSearchJob({ jobId: 'job' });
    expect(opened.draft).toMatchObject({ mode: 'simple', query: 'Casiopea', resultMode });
    const merged = { ...opened.draft, query: 'Casiopea - Take Me' };
    expect(resultMode === 'song-aggregate' ? songQuery(merged) : albumQuery(merged))
      .toMatchObject(resultMode === 'song-aggregate' ? { artist: 'Casiopea', title: 'Take Me' } : { artist: 'Casiopea', album: 'Take Me' });
  },
);

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
  expect(songQuery({ ...editableSearchDraft(opened.draft), title: 'New track' })).toMatchObject({
    title: 'New track', length: 180, album: null, uri: null, artistMaybeWrong: false,
  });
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
