export interface CommonSearchRanking {
  formats: string[];
  minBitrate: number | null;
  maxBitrate: number | null;
  minSampleRate: number | null;
  maxSampleRate: number | null;
  minBitDepth: number | null;
  maxBitDepth: number | null;
  strictArtist: boolean;
  excludeLocked: boolean;
  allowedUsers: string;
  bannedUsers: string;
}

export interface TrackSearchRanking {
  strictTitle: boolean;
  lengthTolerance: number | null;
}

export interface AlbumSearchRanking {
  strictAlbum: boolean;
}

export interface CommonSearchConditions extends CommonSearchRanking {
  rejectUnknownMetadata: boolean;
}

export interface TrackSearchConditions extends TrackSearchRanking {
  expectedLength: number | null;
  acceptNoLength: boolean;
}

export interface AlbumSearchConditions extends AlbumSearchRanking {
  minTrackCount: number | null;
  maxTrackCount: number | null;
  requiredTrackTitles: string[];
  strictAlbumQuality: boolean;
}

export interface SearchRankingPreferences {
  common: CommonSearchRanking;
  track: TrackSearchRanking;
  album: AlbumSearchRanking;
  /** Ranking only: there is no matching filter. */
  preferCommonTrackCount: boolean;
}

/** Numeric conditions are non-negative integers; null leaves the bound unset. */
export interface SearchConditions {
  common: CommonSearchConditions;
  track: TrackSearchConditions;
  album: AlbumSearchConditions;
  group: {
    minShares: number | null;
    lengthTolerance: number | null;
  };
  ranking: SearchRankingPreferences;
}

export function createEmptySearchConditions(): SearchConditions {
  const { common, track, album } = createEmptySearchRanking();
  return {
    common: { ...common, rejectUnknownMetadata: false },
    track: { ...track, expectedLength: null, acceptNoLength: true },
    album: {
      ...album,
      minTrackCount: null,
      maxTrackCount: null,
      requiredTrackTitles: [],
      strictAlbumQuality: false,
    },
    group: { minShares: null, lengthTolerance: null },
    ranking: createEmptySearchRanking(),
  };
}

export function createEmptySearchRanking(): SearchRankingPreferences {
  return {
    common: {
      formats: [],
      minBitrate: null,
      maxBitrate: null,
      minSampleRate: null,
      maxSampleRate: null,
      minBitDepth: null,
      maxBitDepth: null,
      strictArtist: false,
      excludeLocked: false,
      allowedUsers: '',
      bannedUsers: '',
    },
    track: {
      strictTitle: false,
      lengthTolerance: null,
    },
    album: {
      strictAlbum: false,
    },
    preferCommonTrackCount: false,
  };
}
