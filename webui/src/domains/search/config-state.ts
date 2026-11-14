export interface CommonSearchRanking {
  formats: string[];
  minBitrate: string;
  maxBitrate: string;
  minSampleRate: string;
  maxSampleRate: string;
  minBitDepth: string;
  maxBitDepth: string;
  strictArtist: boolean;
  excludeLocked: boolean;
  allowedUsers: string;
  bannedUsers: string;
}

export interface TrackSearchRanking {
  strictTitle: boolean;
  lengthTolerance: string;
}

export interface AlbumSearchRanking {
  strictAlbum: boolean;
}

export interface CommonSearchConditions extends CommonSearchRanking {
  rejectUnknownMetadata: boolean;
}

export interface TrackSearchConditions extends TrackSearchRanking {
  expectedLength: string;
  acceptNoLength: boolean;
}

export interface AlbumSearchConditions extends AlbumSearchRanking {
  minTrackCount: string;
  maxTrackCount: string;
  requiredTrackTitles: string[];
  strictAlbumQuality: boolean;
}

export interface SearchRankingPreferences {
  common: CommonSearchRanking;
  track: TrackSearchRanking;
  album: AlbumSearchRanking;
}

export interface SearchConditions {
  common: CommonSearchConditions;
  track: TrackSearchConditions;
  album: AlbumSearchConditions;
  group: {
    minShares: string;
    lengthTolerance: string;
  };
  ranking: SearchRankingPreferences;
}

export function createEmptySearchConditions(): SearchConditions {
  const { common, track, album } = createEmptySearchRanking();
  return {
    common: { ...common, rejectUnknownMetadata: false },
    track: { ...track, expectedLength: '', acceptNoLength: true },
    album: {
      ...album,
      minTrackCount: '',
      maxTrackCount: '',
      requiredTrackTitles: [],
      strictAlbumQuality: false,
    },
    group: { minShares: '', lengthTolerance: '' },
    ranking: createEmptySearchRanking(),
  };
}

export function createEmptySearchRanking(): SearchRankingPreferences {
  return {
    common: {
      formats: [],
      minBitrate: '',
      maxBitrate: '',
      minSampleRate: '',
      maxSampleRate: '',
      minBitDepth: '',
      maxBitDepth: '',
      strictArtist: false,
      excludeLocked: false,
      allowedUsers: '',
      bannedUsers: '',
    },
    track: {
      strictTitle: false,
      lengthTolerance: '',
    },
    album: {
      strictAlbum: false,
    },
  };
}
