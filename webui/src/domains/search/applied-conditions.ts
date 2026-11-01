import type { SearchConditions } from './config-state';
import { searchModeFamily, type SearchResultMode } from './model';
export function hasAppliedConditions(mode: SearchResultMode, conditions: SearchConditions): boolean {
  const family = searchModeFamily(mode);
  const common = Boolean(
    conditions.common.formats.length
      || conditions.common.minBitrate
      || conditions.common.maxBitrate
      || conditions.common.minSampleRate
      || conditions.common.maxSampleRate
      || conditions.common.minBitDepth
      || conditions.common.maxBitDepth
      || conditions.common.rejectUnknownMetadata
      || conditions.common.allowedUsers.trim()
      || conditions.common.bannedUsers.trim(),
  );
  if (family === 'generic') return common;
  const aggregate = mode === 'song-aggregate' || mode === 'album-aggregate'
    ? Boolean(conditions.aggregate.minShares || (conditions.aggregate.lengthTolerance && conditions.aggregate.lengthTolerance !== '3'))
    : false;

  return common || aggregate || Boolean(
    conditions.common.strictArtist
      || (family === 'track'
        ? conditions.track.strictTitle || conditions.track.expectedLength
        : conditions.album.strictAlbum
          || conditions.album.minTrackCount
          || conditions.album.maxTrackCount
          || conditions.album.requiredTrackTitles.length
          || conditions.album.strictAlbumQuality),
  );
}
