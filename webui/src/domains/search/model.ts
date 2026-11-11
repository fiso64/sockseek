import type { Schema } from '../../api/types';

export type SearchResultMode = 'generic' | 'track' | 'album' | 'song-groups' | 'album-groups';
export type SearchInputMode = 'simple' | 'split';
export type SearchModeFamily = 'generic' | 'track' | 'album';

const resultModes: readonly SearchResultMode[] = [
  'generic', 'track', 'album', 'song-groups', 'album-groups',
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
  return mode === 'generic' ? 'generic' : mode === 'album' || mode === 'album-groups' ? 'album' : 'track';
}

export function isGroupSearchMode(mode: SearchResultMode): boolean {
  return mode === 'song-groups' || mode === 'album-groups';
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

/** Retained query metadata belongs to the original input, not a newly edited search. */
export function editableSearchDraft(draft: SearchDraft): SearchDraft {
  const { albumSearchHint, albumContext, sourceUri, artistMaybeWrong, ...editable } = draft;
  return editable;
}

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
    'song-groups': 'Song Groups',
    'album-groups': 'Album Groups',
  }[mode];
}

export function searchViewKind(mode: SearchResultMode): Schema<'ServerSearchViewProjectionKind'> {
  const kinds: Record<SearchResultMode, Exclude<Schema<'ServerSearchViewProjectionKind'>, null>> = {
    generic: 'GenericDirectories',
    track: 'Files',
    album: 'AlbumDirectories',
    'song-groups': 'TrackGroups',
    'album-groups': 'AlbumGroups',
  };
  return kinds[mode];
}

export function searchDelimiter(input: string): { index: number; length: number } | null {
  return [' — ', ' - '].map(value => ({ index: input.indexOf(value), length: value.length }))
    .filter(item => item.index >= 0).sort((a, b) => a.index - b.index)[0] ?? null;
}

// Merging two group fields keeps their meaning when submitted again.
function groupInput(query: string): { artist: string | null; title: string | null } {
  const delimiter = searchDelimiter(query);
  return delimiter
    ? { artist: query.slice(0, delimiter.index).trim() || null, title: query.slice(delimiter.index + delimiter.length).trim() || null }
    : { artist: query.trim() || null, title: null };
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
  const group = draft.resultMode === 'song-groups' ? groupInput(draft.query) : null;
  return {
    artist: group?.artist ?? null,
    title: group ? group.title : draft.query.trim() || null,
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
  const group = draft.resultMode === 'album-groups' ? groupInput(draft.query) : null;
  return {
    artist: group?.artist ?? null,
    album: group ? group.title : draft.query.trim() || null,
    searchHint: draft.albumSearchHint ?? null,
    uri: draft.sourceUri ?? null,
    artistMaybeWrong: draft.artistMaybeWrong ?? false,
  };
}
