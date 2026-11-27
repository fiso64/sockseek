import type { Schema } from '../../api/types';
import type { SearchConditions } from './config-state';
import { SEARCH_SAMPLE_RATES } from './config-schema';
import { isGroupSearchMode, searchModeFamily, type SearchResultMode } from './model';

// Matches Core's SearchDefaults.Formats, not the shorter format-button suggestions.
// The real-daemon browser journey verifies that its music defaults stay hidden.
const defaultMusicFormats = new Set(['mp3', 'flac', 'ogg', 'm4a', 'opus', 'wav', 'aac', 'alac']);
const sampleRates = new Map<number, string>(SEARCH_SAMPLE_RATES.map(rate => [rate.value, rate.label]));

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
  /** Where the pill's setting is edited: a panel tab and its field ids. */
  tab: 'conditions' | 'ranking';
  fields: string[];
}

/** One list owns visibility, presentation and removal; ranking preferences aren't filter pills. */
export function conditionPills(mode: SearchResultMode, conditions: SearchConditions): ConditionPill[] {
  const { common, track, album, group } = conditions;
  const family = searchModeFamily(mode);
  const pills: ConditionPill[] = [];
  function add(fields: string[], label: string, remove: () => void, removeLabel = `Remove ${label} filter`): void {
    pills.push({ label, removeLabel, remove, tab: 'conditions', fields });
  }

  if (isGroupSearchMode(mode)) {
    if (group.minShares) add(['minShares'], `≥${group.minShares} sharers`, () => { group.minShares = null; }, 'Remove minimum sharers filter');
    if (group.lengthTolerance && group.lengthTolerance !== 3)
      add(['groupLengthTolerance'], `group ±${group.lengthTolerance}s`, () => { group.lengthTolerance = 3; }, 'Reset grouping tolerance');
  }
  for (const format of visibleFormatPills(mode, common.formats))
    add(['formats'], `format: ${format}`, () => { common.formats = common.formats.filter(item => item !== format); }, `Remove ${format} format`);

  if (common.minBitrate) add(['minBitrate'], `bitrate ≥ ${common.minBitrate} kbps`, () => { common.minBitrate = null; });
  if (common.maxBitrate) add(['maxBitrate'], `bitrate ≤ ${common.maxBitrate} kbps`, () => { common.maxBitrate = null; });
  if (common.minSampleRate) add(['minSampleRate'], `sample rate ≥ ${sampleRates.get(common.minSampleRate) ?? common.minSampleRate}`, () => { common.minSampleRate = null; });
  if (common.maxSampleRate) add(['maxSampleRate'], `sample rate ≤ ${sampleRates.get(common.maxSampleRate) ?? common.maxSampleRate}`, () => { common.maxSampleRate = null; });
  if (common.minBitDepth) add(['minBitDepth'], `bit depth ≥ ${common.minBitDepth}-bit`, () => { common.minBitDepth = null; });
  if (common.maxBitDepth) add(['maxBitDepth'], `bit depth ≤ ${common.maxBitDepth}-bit`, () => { common.maxBitDepth = null; });
  if (family !== 'generic' && common.strictArtist) add(['strictArtist'], 'exact artist name', () => { common.strictArtist = false; });
  if (common.rejectUnknownMetadata) add(['rejectUnknownMetadata'], 'reject unknown metadata', () => { common.rejectUnknownMetadata = false; });

  if (family === 'track') {
    if (track.strictTitle) add(['strictTitle'], 'exact title', () => { track.strictTitle = false; });
    if (track.expectedLength) add(['expectedLength', 'lengthTolerance'], `length ${track.expectedLength}s ±${track.lengthTolerance ?? 0}s`, () => { track.expectedLength = null; });
  } else if (family === 'album') {
    if (album.strictAlbum) add(['strictAlbum'], 'exact album name', () => { album.strictAlbum = false; });
    if (album.minTrackCount != null && album.minTrackCount === album.maxTrackCount)
      add(['trackCount'], `tracks = ${album.minTrackCount}`, () => { album.minTrackCount = null; album.maxTrackCount = null; });
    else {
      if (album.minTrackCount) add(['trackCount'], `tracks ≥ ${album.minTrackCount}`, () => { album.minTrackCount = null; });
      if (album.maxTrackCount) add(['trackCount'], `tracks ≤ ${album.maxTrackCount}`, () => { album.maxTrackCount = null; });
    }
    for (const title of album.requiredTrackTitles)
      add(['requiredTrackTitle'], `contains: ${title}`, () => { album.requiredTrackTitles = album.requiredTrackTitles.filter(item => item !== title); });
    if (album.strictAlbumQuality) add(['strictAlbumQuality'], 'all tracks meet quality', () => { album.strictAlbumQuality = false; });
  }

  if (common.excludeLocked) add(['excludeLocked'], 'hide locked', () => { common.excludeLocked = false; }, 'Remove locked files filter');
  if (common.allowedUsers.trim()) add(['allowedUsers'], `allow: ${common.allowedUsers.trim()}`, () => { common.allowedUsers = ''; });
  if (common.bannedUsers.trim()) add(['bannedUsers'], `ban: ${common.bannedUsers.trim()}`, () => { common.bannedUsers = ''; });
  return pills;
}

export type PreferenceCondition = Schema<'SearchPreferenceCondition'>;

// The daemon's ranking order. One Username condition covers both user lists,
// which rank at different places: downranked users first, preferred users above audio quality.
type RankingSlot = PreferenceCondition | 'PreferredUsers';
const rankingOrder: RankingSlot[] = ['Username', 'Title', 'Album', 'Artist', 'Length', 'PreferredUsers', 'Format', 'Bitrate', 'SampleRate', 'BitDepth', 'ExcludeLocked'];

function range(min: number | null, max: number | null, label: (value: number) => string, unit = ''): string {
  if (min != null && max != null) return `${label(min)}–${label(max)}${unit}`;
  return min != null ? `≥ ${label(min)}${unit}` : `≤ ${label(max ?? 0)}${unit}`;
}

/** What a configured preferred condition is called, what missing it means, and how to turn it off. */
function rankingPreference(condition: RankingSlot, conditions: SearchConditions): { pill: string; missed: string; remove: () => void; fields: string[] } {
  const { common, track, album } = conditions.ranking;
  const rate = (value: number) => sampleRates.get(value) ?? String(value);
  switch (condition) {
    case 'Album': return { fields: ['strictAlbum'], pill: 'prefer exact album name', missed: 'no exact album name', remove: () => { album.strictAlbum = false; } };
    case 'Artist': return { fields: ['strictArtist'], pill: 'prefer exact artist name', missed: 'no exact artist name', remove: () => { common.strictArtist = false; } };
    case 'Title': return { fields: ['strictTitle'], pill: 'prefer exact title', missed: 'no exact title', remove: () => { track.strictTitle = false; } };
    case 'Format': {
      const formats = common.formats.join('/');
      return { fields: ['formats'], pill: `prefer ${formats}`, missed: `not ${formats}`, remove: () => { common.formats = []; } };
    }
    case 'Bitrate': return { fields: ['minBitrate', 'maxBitrate'], pill: `prefer ${range(common.minBitrate, common.maxBitrate, String, ' kbps')}`, missed: 'other bitrate',
      remove: () => { common.minBitrate = null; common.maxBitrate = null; } };
    case 'SampleRate': return { fields: ['minSampleRate', 'maxSampleRate'], pill: `prefer ${range(common.minSampleRate, common.maxSampleRate, rate)}`, missed: 'other sample rate',
      remove: () => { common.minSampleRate = null; common.maxSampleRate = null; } };
    case 'BitDepth': return { fields: ['minBitDepth', 'maxBitDepth'], pill: `prefer ${range(common.minBitDepth, common.maxBitDepth, String, '-bit')}`, missed: 'other bit depth',
      remove: () => { common.minBitDepth = null; common.maxBitDepth = null; } };
    case 'Length': return { fields: ['lengthTolerance'], pill: `prefer length ±${track.lengthTolerance ?? 0}s`, missed: 'other length', remove: () => { track.lengthTolerance = null; } };
    case 'ExcludeLocked': return { fields: ['excludeLocked'], pill: 'prefer unlocked', missed: 'locked', remove: () => { common.excludeLocked = false; } };
    case 'Username': return { fields: ['bannedUsers'], pill: `avoid: ${common.bannedUsers.trim()}`, missed: 'other users', remove: () => { common.bannedUsers = ''; } };
    case 'PreferredUsers': return { fields: ['allowedUsers'], pill: `prefer: ${common.allowedUsers.trim()}`, missed: 'other users', remove: () => { common.allowedUsers = ''; } };
  }
}

/** Ranking preferences, such as those some results miss, as removable pills in ranking order. */
export function rankingPills(configured: readonly PreferenceCondition[], conditions: SearchConditions): ConditionPill[] {
  const { common } = conditions.ranking;
  return rankingOrder.filter(slot => slot === 'Username' ? configured.includes(slot) && common.bannedUsers.trim()
    : slot === 'PreferredUsers' ? configured.includes('Username') && common.allowedUsers.trim()
    : configured.includes(slot)).map(condition => {
    const { pill, remove, fields } = rankingPreference(condition, conditions);
    return { label: pill, removeLabel: `Stop ranking by: ${pill.replace(/^prefer:? /, '')}`, remove, tab: 'ranking' as const, fields };
  });
}

/** The daemon's per-preference miss counts, leaving out preferences no result misses. */
export function missCounts(misses: Schema<'SearchPreferenceMissesDto'>): { condition: PreferenceCondition; count: number }[] {
  return Object.entries(misses)
    .map(([key, count]) => ({ condition: (key.charAt(0).toUpperCase() + key.slice(1)) as PreferenceCondition, count: Number(count) }))
    .filter(miss => miss.count > 0);
}

/** Why results aren't Preferred: how many miss each preference, most common first. */
export function missedPreferences(misses: readonly { condition: PreferenceCondition; count: number | string }[], conditions: SearchConditions): string[] {
  return [...misses].sort((a, b) => Number(b.count) - Number(a.count))
    .map(miss => `${Number(miss.count).toLocaleString()} ${rankingPreference(miss.condition, conditions).missed}`);
}
