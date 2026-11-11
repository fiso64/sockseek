export interface CommonSearchConditions {
  formats: string[];
  minBitrate: string;
  maxBitrate: string;
  minSampleRate: string;
  maxSampleRate: string;
  minBitDepth: string;
  maxBitDepth: string;
  rejectUnknownMetadata: boolean;
  strictArtist: boolean;
  excludeLocked: boolean;
  allowedUsers: string;
  bannedUsers: string;
}

export interface TrackSearchConditions {
  strictTitle: boolean;
  expectedLength: string;
  lengthTolerance: string;
  acceptNoLength: boolean;
}

export interface AlbumSearchConditions {
  strictAlbum: boolean;
  minTrackCount: string;
  maxTrackCount: string;
  requiredTrackTitles: string[];
  strictAlbumQuality: boolean;
}

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

export interface SearchRankingPreferences {
  common: CommonSearchRanking;
  track: TrackSearchRanking;
  album: AlbumSearchRanking;
}

export interface SearchConditions {
  common: CommonSearchConditions;
  track: TrackSearchConditions;
  album: AlbumSearchConditions;
  aggregate: {
    minShares: string;
    lengthTolerance: string;
  };
  ranking: SearchRankingPreferences;
}

export function createEmptySearchConditions(): SearchConditions {
  return {
    common: {
      formats: [],
      minBitrate: '',
      maxBitrate: '',
      minSampleRate: '',
      maxSampleRate: '',
      minBitDepth: '',
      maxBitDepth: '',
      rejectUnknownMetadata: false,
      strictArtist: false,
      excludeLocked: false,
      allowedUsers: '',
      bannedUsers: '',
    },
    track: {
      strictTitle: false,
      expectedLength: '',
      lengthTolerance: '',
      acceptNoLength: true,
    },
    album: {
      strictAlbum: false,
      minTrackCount: '',
      maxTrackCount: '',
      requiredTrackTitles: [],
      strictAlbumQuality: false,
    },
    aggregate: {
      minShares: '',
      lengthTolerance: '',
    },
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
