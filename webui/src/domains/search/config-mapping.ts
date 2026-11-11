import type { Schema } from '../../api/types';
import { createEmptySearchConditions, type SearchConditions } from './config-state';
import { isAggregateSearchMode, searchModeFamily, type SearchModeFamily, type SearchResultMode } from './model';

type Settings = Schema<'SearchSettingsPatchDto'>;
type FileConditions = Schema<'FileConditionsPatchDto'>;
const bounds = ['minBitrate', 'maxBitrate', 'minSampleRate', 'maxSampleRate', 'minBitDepth', 'maxBitDepth'] as const;
const clearBound = {
  minBitrate: 'clearMinBitrate', maxBitrate: 'clearMaxBitrate',
  minSampleRate: 'clearMinSampleRate', maxSampleRate: 'clearMaxSampleRate',
  minBitDepth: 'clearMinBitDepth', maxBitDepth: 'clearMaxBitDepth',
} as const;
const numberText = (value: number | string | null | undefined) => value == null ? '' : String(value);
const csv = (value: string) => value.split(',').map((item) => item.trim()).filter(Boolean);

/** Read an effective (fully resolved) snapshot, not a sparse request patch. */
export function readSearchConditions(settings: Settings, expectedLength?: number): SearchConditions {
  const result = createEmptySearchConditions();
  function readCommon(source: FileConditions, target: SearchConditions['ranking']['common']) {
    for (const key of bounds) target[key] = numberText(source[key]);
    target.formats = (source.formats?.replace ?? []).map((format) => format.toUpperCase());
    target.allowedUsers = (source.allowedUsers?.replace ?? []).join(', ');
    target.bannedUsers = (source.bannedUsers?.replace ?? []).join(', ');
    target.strictArtist = source.strictArtist ?? false;
    target.excludeLocked = source.excludeLocked ?? false;
  }
  const required = settings.necessaryCond ?? {};
  const preferred = settings.preferredCond ?? {};
  readCommon(required, result.common);
  readCommon(preferred, result.ranking.common);
  result.common.rejectUnknownMetadata = required.acceptMissingProps === false;
  result.track.strictTitle = required.strictTitle ?? false;
  result.track.acceptNoLength = required.acceptNoLength ?? true;
  result.track.expectedLength = numberText(expectedLength);
  result.track.lengthTolerance = Number(required.lengthTolerance) < 0 ? '' : numberText(required.lengthTolerance);
  result.ranking.track.strictTitle = preferred.strictTitle ?? false;
  result.ranking.track.lengthTolerance = Number(preferred.lengthTolerance) < 0 ? '' : numberText(preferred.lengthTolerance);
  result.album.strictAlbum = required.strictAlbum ?? false;
  result.ranking.album.strictAlbum = preferred.strictAlbum ?? false;
  result.album.strictAlbumQuality = settings.strictAlbumQuality ?? false;
  result.album.minTrackCount = numberText(settings.necessaryFolderCond?.minTrackCount);
  result.album.maxTrackCount = numberText(settings.necessaryFolderCond?.maxTrackCount);
  result.album.requiredTrackTitles = [...(settings.necessaryFolderCond?.requiredTrackTitles?.replace ?? [])];
  result.aggregate.minShares = numberText(settings.minSharesAggregate);
  result.aggregate.lengthTolerance = numberText(settings.aggregateLengthTol);
  return result;
}

function changedNumber(before: unknown, after: unknown, label: string, clearValue?: number): number | undefined {
  const text = after == null ? '' : String(after).trim();
  if (text === (before == null ? '' : String(before).trim())) return undefined;
  if (!text) {
    if (clearValue !== undefined) return clearValue;
    throw new Error(`The daemon cannot yet clear an inherited ${label}. Restore its value to continue.`);
  }
  const value = Number(text);
  if (!Number.isSafeInteger(value) || value < 0 || value > 2147483647) throw new Error(`${label} must be a non-negative 32-bit integer.`);
  return value;
}

/** Only edited fields become overrides; all other fields retain daemon inheritance. */
export function searchConditionsPatch(mode: SearchResultMode, before: SearchConditions, after: SearchConditions): Settings {
  const patch = searchConditionsPatchForFamily(searchModeFamily(mode), before, after);
  if (isAggregateSearchMode(mode)) {
    const shares = changedNumber(before.aggregate.minShares, after.aggregate.minShares, 'minimum sharers', 1);
    // Unlike a file-condition bound, grouping tolerance has no disabled state.
    // An empty field means inherit the resolved/default value.
    const tolerance = String(after.aggregate.lengthTolerance ?? '').trim()
      ? changedNumber(before.aggregate.lengthTolerance, after.aggregate.lengthTolerance, 'grouping tolerance')
      : undefined;
    if (shares !== undefined) patch.minSharesAggregate = shares;
    if (tolerance !== undefined) patch.aggregateLengthTol = tolerance;
  }
  return patch;
}

/** Shared settings mapping for search drafts and mixed-source New Job drafts. */
export function searchConditionsPatchForFamily(
  family: SearchModeFamily | 'mixed', before: SearchConditions, after: SearchConditions,
): Settings {
  function filePatch(ranking: boolean): FileConditions {
    const a = ranking ? before.ranking : before;
    const b = ranking ? after.ranking : after;
    const patch: FileConditions = {};
    for (const key of bounds) {
      if (String(a.common[key] ?? '').trim() && !String(b.common[key] ?? '').trim()) patch[clearBound[key]] = true;
      else {
        const value = changedNumber(a.common[key], b.common[key], key);
        if (value !== undefined) patch[key] = value;
      }
    }
    for (const key of ['formats', 'allowedUsers', 'bannedUsers'] as const) {
      const first = key === 'formats' ? a.common[key] : csv(a.common[key]);
      const second = key === 'formats' ? b.common[key] : csv(b.common[key]);
      if (JSON.stringify(first) !== JSON.stringify(second)) patch[key] = { replace: key === 'formats' ? second.map((format) => format.toLowerCase()) : [...second] };
    }
    if (a.common.excludeLocked !== b.common.excludeLocked) patch.excludeLocked = b.common.excludeLocked;
    if (family !== 'generic' && a.common.strictArtist !== b.common.strictArtist) patch.strictArtist = b.common.strictArtist;
    if (family === 'track' || family === 'mixed') {
      if (a.track.strictTitle !== b.track.strictTitle) patch.strictTitle = b.track.strictTitle;
      const tolerance = changedNumber(a.track.lengthTolerance, b.track.lengthTolerance, 'length tolerance', -1);
      if (tolerance !== undefined) patch.lengthTolerance = tolerance;
    }
    if ((family === 'album' || family === 'mixed') && a.album.strictAlbum !== b.album.strictAlbum) patch.strictAlbum = b.album.strictAlbum;
    if (!ranking) {
      if (before.common.rejectUnknownMetadata !== after.common.rejectUnknownMetadata) patch.acceptMissingProps = !after.common.rejectUnknownMetadata;
      if ((family === 'track' || family === 'mixed') && before.track.acceptNoLength !== after.track.acceptNoLength) patch.acceptNoLength = after.track.acceptNoLength;
    }
    return patch;
  }
  const patch: Settings = {};
  const necessary = filePatch(false), preferred = filePatch(true);
  if (Object.keys(necessary).length) patch.necessaryCond = necessary;
  if (Object.keys(preferred).length) patch.preferredCond = preferred;
  if (family === 'album' || family === 'mixed') {
    const folder: Schema<'FolderConditionsPatchDto'> = {};
    for (const key of ['minTrackCount', 'maxTrackCount'] as const) {
      if (String(before.album[key] ?? '').trim() && !String(after.album[key] ?? '').trim()) folder[key === 'minTrackCount' ? 'clearMinTrackCount' : 'clearMaxTrackCount'] = true;
      else {
        const value = changedNumber(before.album[key], after.album[key], key);
        if (value !== undefined) folder[key] = value;
      }
    }
    if (JSON.stringify(before.album.requiredTrackTitles) !== JSON.stringify(after.album.requiredTrackTitles)) folder.requiredTrackTitles = { replace: [...after.album.requiredTrackTitles] };
    if (Object.keys(folder).length) patch.necessaryFolderCond = folder;
    if (before.album.strictAlbumQuality !== after.album.strictAlbumQuality) patch.strictAlbumQuality = after.album.strictAlbumQuality;
  }
  return patch;
}

/** Keep explicit UI edits when a new daemon baseline is resolved for the same intent. */
export function rebaseSearchConditions(
  previous: SearchConditions, edited: SearchConditions, baseline: SearchConditions,
): SearchConditions {
  const rebase = (before: unknown, current: unknown, next: unknown): unknown => {
    if (Array.isArray(next) || next === null || typeof next !== 'object') {
      return JSON.stringify(before) === JSON.stringify(current) ? next : current;
    }
    const result: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(next)) {
      result[key] = rebase(
        (before as Record<string, unknown>)[key],
        (current as Record<string, unknown>)[key], value);
    }
    return result;
  };
  return rebase(previous, edited, baseline) as SearchConditions;
}

export function searchExpectedLength(value: unknown): number | undefined {
  return changedNumber('', value, 'expected length');
}

export function mergeSearchSettings(base: Settings | undefined, edits: Settings): Settings {
  const result = { ...base, ...edits };
  for (const key of ['necessaryCond', 'preferredCond', 'necessaryFolderCond', 'preferredFolderCond'] as const) {
    if (edits[key]) {
      const merged = { ...base?.[key], ...edits[key] };
      const pairs: [string, string][] = key === 'necessaryFolderCond' || key === 'preferredFolderCond'
        ? [['minTrackCount', 'clearMinTrackCount'], ['maxTrackCount', 'clearMaxTrackCount']]
        : Object.entries(clearBound);
      for (const [bound, clear] of pairs) {
        if (Object.hasOwn(edits[key], bound)) delete (merged as Record<string, unknown>)[clear];
        if ((edits[key] as Record<string, unknown>)[clear] === true) delete (merged as Record<string, unknown>)[bound];
      }
      result[key] = merged;
    }
  }
  return result;
}
