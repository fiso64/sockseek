import type { SearchConditions } from './config-state';
import { isGroupSearchMode, searchModeFamily, type SearchResultMode } from './model';

// Matches Core's SearchDefaults.Formats, not the shorter format-button suggestions.
// The real-daemon browser journey verifies that its music defaults stay hidden.
const defaultMusicFormats = new Set(['mp3', 'flac', 'ogg', 'm4a', 'opus', 'wav', 'aac', 'alac']);
const sampleRates: Record<string, string> = {
  '44100': '44.1 kHz', '48000': '48 kHz', '88200': '88.2 kHz',
  '96000': '96 kHz', '176400': '176.4 kHz', '192000': '192 kHz',
};

export function visibleFormatPills(mode: SearchResultMode, formats: string[]): string[] {
  if (mode === 'generic') return formats;
  const selected = new Set(formats.map(format => format.toLowerCase()));
  return selected.size === defaultMusicFormats.size && [...selected].every(format => defaultMusicFormats.has(format))
    ? [] : formats;
}

export interface ConditionPill {
  label: string;
  removeLabel: string;
  remove: () => void;
}

/** One list owns visibility, presentation and removal; ranking preferences aren't filter pills. */
export function conditionPills(mode: SearchResultMode, conditions: SearchConditions): ConditionPill[] {
  const { common, track, album, group } = conditions;
  const family = searchModeFamily(mode);
  const pills: ConditionPill[] = [];
  function add(label: string, remove: () => void, removeLabel = `Remove ${label} filter`): void {
    pills.push({ label, removeLabel, remove });
  }

  if (isGroupSearchMode(mode)) {
    if (group.minShares) add(`≥${group.minShares} sharers`, () => { group.minShares = ''; }, 'Remove minimum sharers filter');
    if (group.lengthTolerance && group.lengthTolerance !== '3')
      add(`group ±${group.lengthTolerance}s`, () => { group.lengthTolerance = '3'; }, 'Reset grouping tolerance');
  }
  for (const format of visibleFormatPills(mode, common.formats))
    add(`format: ${format}`, () => { common.formats = common.formats.filter(item => item !== format); }, `Remove ${format} format`);

  if (common.minBitrate) add(`bitrate ≥ ${common.minBitrate} kbps`, () => { common.minBitrate = ''; });
  if (common.maxBitrate) add(`bitrate ≤ ${common.maxBitrate} kbps`, () => { common.maxBitrate = ''; });
  if (common.minSampleRate) add(`sample rate ≥ ${sampleRates[common.minSampleRate] ?? common.minSampleRate}`, () => { common.minSampleRate = ''; });
  if (common.maxSampleRate) add(`sample rate ≤ ${sampleRates[common.maxSampleRate] ?? common.maxSampleRate}`, () => { common.maxSampleRate = ''; });
  if (common.minBitDepth) add(`bit depth ≥ ${common.minBitDepth}-bit`, () => { common.minBitDepth = ''; });
  if (common.maxBitDepth) add(`bit depth ≤ ${common.maxBitDepth}-bit`, () => { common.maxBitDepth = ''; });
  if (family !== 'generic' && common.strictArtist) add('strict artist', () => { common.strictArtist = false; });
  if (common.rejectUnknownMetadata) add('reject unknown metadata', () => { common.rejectUnknownMetadata = false; });

  if (family === 'track') {
    if (track.strictTitle) add('strict title', () => { track.strictTitle = false; });
    if (track.expectedLength) add(`length ${track.expectedLength}s ±${track.lengthTolerance || '0'}s`, () => { track.expectedLength = ''; });
  } else if (family === 'album') {
    if (album.strictAlbum) add('strict album', () => { album.strictAlbum = false; });
    if (album.minTrackCount) add(`tracks ≥ ${album.minTrackCount}`, () => { album.minTrackCount = ''; });
    if (album.maxTrackCount) add(`tracks ≤ ${album.maxTrackCount}`, () => { album.maxTrackCount = ''; });
    for (const title of album.requiredTrackTitles)
      add(`contains: ${title}`, () => { album.requiredTrackTitles = album.requiredTrackTitles.filter(item => item !== title); });
    if (album.strictAlbumQuality) add('all tracks meet quality', () => { album.strictAlbumQuality = false; });
  }

  if (common.excludeLocked) add('hide locked', () => { common.excludeLocked = false; }, 'Remove locked files filter');
  if (common.allowedUsers.trim()) add(`allow: ${common.allowedUsers.trim()}`, () => { common.allowedUsers = ''; });
  if (common.bannedUsers.trim()) add(`ban: ${common.bannedUsers.trim()}`, () => { common.bannedUsers = ''; });
  return pills;
}
