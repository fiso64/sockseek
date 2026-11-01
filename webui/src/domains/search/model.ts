import type { Schema } from '../../api/types';

export type SearchResultMode = 'generic' | 'track' | 'album' | 'song-aggregate' | 'album-aggregate';
export type SearchInputMode = 'simple' | 'split';
export type SearchModeFamily = 'generic' | 'track' | 'album';

const resultModes: readonly SearchResultMode[] = [
  'generic', 'track', 'album', 'song-aggregate', 'album-aggregate',
];

export function parseSearchResultMode(value: string | null): SearchResultMode | null {
  return resultModes.find((mode) => mode === value) ?? null;
}

export function searchJobPath(jobId: string, mode: SearchResultMode): string {
  return `/jobs/${encodeURIComponent(jobId)}?results=${encodeURIComponent(mode)}`;
}

export function directoryTotals(directory: Pick<Schema<'SearchViewDirectoryDto'>,
  'retrievedFileCount' | 'retrievedBytes' | 'publicMatchingFileCount' | 'lockedMatchingFileCount' | 'publicMatchingBytes' | 'lockedMatchingBytes'>): { fileCount: number; bytes: number } {
  return {
    fileCount: directory.retrievedFileCount == null
      ? Number(directory.publicMatchingFileCount) + Number(directory.lockedMatchingFileCount)
      : Number(directory.retrievedFileCount),
    bytes: directory.retrievedBytes == null
      ? Number(directory.publicMatchingBytes) + Number(directory.lockedMatchingBytes)
      : Number(directory.retrievedBytes),
  };
}

export function searchModeFamily(mode: SearchResultMode): SearchModeFamily {
  return mode === 'generic' ? 'generic' : mode === 'album' || mode === 'album-aggregate' ? 'album' : 'track';
}

export function isAggregateSearchMode(mode: SearchResultMode): boolean {
  return mode === 'song-aggregate' || mode === 'album-aggregate';
}

export function searchJobDraft(draft: SearchDraft): Schema<'JobDraftDto'> {
  if (draft.resultMode === 'generic') return { kind: 'search-generic', queryText: networkQuery(draft) };
  if (searchModeFamily(draft.resultMode) === 'track') return { kind: 'search-track', songQuery: songQuery(draft), includeFullResults: true };
  return { kind: 'search-album', albumQuery: albumQuery(draft) };
}

export interface SearchDraft {
  mode: SearchInputMode;
  resultMode: SearchResultMode;
  query: string;
  artist: string;
  title: string;
  searchSettings?: Schema<'SearchSettingsPatchDto'>;
  expectedLength?: number;
  albumContext?: string;
  sourceUri?: string;
  artistMaybeWrong?: boolean;
  albumSearchHint?: string;
}

export const emptySearchDraft: SearchDraft = {
  mode: 'simple',
  resultMode: 'album',
  query: '',
  artist: '',
  title: '',
};

export function networkQuery(draft: SearchDraft): string {
  return draft.mode === 'split'
    ? [draft.artist.trim(), draft.title.trim()].filter(Boolean).join(' ')
    : draft.query.trim();
}

export function searchModeLabel(mode: SearchResultMode): string {
  return {
    generic: 'File Search',
    track: 'Track Search',
    album: 'Album Search',
    'song-aggregate': 'Song Aggregate',
    'album-aggregate': 'Album Aggregate',
  }[mode];
}

export function searchViewKind(mode: SearchResultMode): Schema<'ServerSearchViewProjectionKind'> {
  const kinds: Record<SearchResultMode, Exclude<Schema<'ServerSearchViewProjectionKind'>, null>> = {
    generic: 'GenericDirectories',
    track: 'Files',
    album: 'AlbumDirectories',
    'song-aggregate': 'AggregateTracks',
    'album-aggregate': 'AggregateAlbums',
  };
  return kinds[mode];
}

export function songQuery(draft: SearchDraft): Schema<'SongQueryDto'> {
  if (draft.mode === 'split') {
    return {
      artist: draft.artist.trim() || null,
      title: draft.title.trim() || null,
      album: draft.albumContext ?? null,
      uri: draft.sourceUri ?? null,
      length: draft.expectedLength ?? null,
      artistMaybeWrong: draft.artistMaybeWrong ?? false,
    };
  }
  return {
    artist: null,
    title: draft.query.trim() || null,
    album: draft.albumContext ?? null,
    uri: draft.sourceUri ?? null,
    length: draft.expectedLength ?? null,
    artistMaybeWrong: draft.artistMaybeWrong ?? false,
  };
}

export function albumQuery(draft: SearchDraft): Schema<'AlbumQueryDto'> {
  if (draft.mode === 'split') {
    return {
      artist: draft.artist.trim() || null,
      album: draft.title.trim() || null,
      searchHint: draft.albumSearchHint ?? null,
      uri: draft.sourceUri ?? null,
      artistMaybeWrong: draft.artistMaybeWrong ?? false,
    };
  }
  return {
    artist: null,
    album: null,
    searchHint: draft.query.trim() || null,
    uri: draft.sourceUri ?? null,
    artistMaybeWrong: draft.artistMaybeWrong ?? false,
  };
}
