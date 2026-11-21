import type { JobDraft, Schema } from '../../api/types';

export type SearchInputMode = 'simple' | 'split';
export type SearchModeFamily = 'generic' | 'track' | 'album';

const searchModes = {
  'generic': {
    label: 'File Search', family: 'generic', kind: 'GenericDirectories',
    description: 'Standard Soulseek file search.',
  },
  'track': {
    label: 'Track Search', family: 'track', kind: 'Files',
    description: 'Shows results as tracks. Uses music-oriented default filters and ranking.',
  },
  'album': {
    label: 'Album Search', family: 'album', kind: 'AlbumDirectories',
    description: 'Shows results as albums. Uses music-oriented default filters and ranking.',
  },
  'song-groups': {
    label: 'Song Groups', family: 'track', kind: 'TrackGroups',
    description: 'Like Track Search, but combines matching copies from different peers into one result per song. Choose a copy within each result. Supports artist-only searches to show all distinct songs by an artist from Soulseek.',
  },
  'album-groups': {
    label: 'Album Groups', family: 'album', kind: 'AlbumGroups',
    description: 'Like Album Search, but combines matching album folders from different peers into one result per album. Choose a folder within each result. Supports artist-only searches to show all distinct songs by an artist from Soulseek.',
  },
} as const satisfies Record<string, {
  label: string; family: SearchModeFamily; kind: Schema<'ServerSearchViewProjectionKind'>; description: string;
}>;

export type SearchResultMode = keyof typeof searchModes;

export const searchModeOptions = (Object.keys(searchModes) as SearchResultMode[]).map(value => ({
  ...searchModes[value], value, icon: value,
}));

export function modeForProjection(kind: Schema<'ServerSearchViewProjectionKind'>): SearchResultMode {
  const option = searchModeOptions.find(option => option.kind === kind);
  if (!option) throw new Error(`Unknown search projection ${kind}`);
  return option.value;
}

export function parseSearchResultMode(value: string | null): SearchResultMode | null {
  return searchModeOptions.find(option => option.value === value)?.value ?? null;
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
  return searchModes[mode].family;
}

export function isGroupSearchMode(mode: SearchResultMode): boolean {
  return mode === 'song-groups' || mode === 'album-groups';
}

export function searchJobDraft(draft: SearchDraft): JobDraft {
  if (draft.resultMode === 'generic') return { kind: 'search-generic', queryText: networkQuery(draft) };
  if (searchModeFamily(draft.resultMode) === 'track') return { kind: 'search-track', songQuery: songQuery(draft), groupResults: isGroupSearchMode(draft.resultMode) };
  return { kind: 'search-album', albumQuery: albumQuery(draft), groupResults: isGroupSearchMode(draft.resultMode) };
}

export interface SearchDraft {
  mode: SearchInputMode;
  resultMode: SearchResultMode;
  query: string;
  artist: string;
  title: string;
  profileNames?: string[];
  searchSettings?: Schema<'SearchSettingsPatchDto'>;
  expectedLength?: number;
  albumContext?: string;
  sourceUri?: string;
  artistMaybeWrong?: boolean;
  albumSearchHint?: string;
}

export function searchSubmissionOptions({ profileNames, searchSettings }: SearchDraft): Schema<'SubmissionOptionsDto'> | undefined {
  if (!profileNames?.length && !searchSettings) return undefined;
  return { profileNames, downloadSettings: searchSettings ? { search: searchSettings } : undefined };
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
  return searchModes[mode].label;
}

export function searchViewKind(mode: SearchResultMode): Schema<'ServerSearchViewProjectionKind'> {
  return searchModes[mode].kind;
}

export function searchDelimiter(input: string): { index: number; length: number } | null {
  return [' — ', ' – ', ' - '].map(value => ({ index: input.indexOf(value), length: value.length }))
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
