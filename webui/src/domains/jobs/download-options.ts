import type { Schema } from '../../api/types';
import { mergeSearchSettings } from '../search/config-mapping';

export interface DownloadOptions {
  skipExisting: boolean;
  loadFullAlbumFolder: boolean;
  outputParentDir: string;
  nameFormat: string;
  writePlaylist: boolean;
}

export interface DownloadOptionCapabilities {
  albumFolderEnrichment: boolean;
  playlistOutput: boolean;
  nameFormat?: boolean;
}

export function downloadOptionsFromResolved(settings: Schema<'EffectiveDownloadSettingsDto'>): DownloadOptions {
  const values = settings.values;
  const skipExisting = values.skip?.skipExisting;
  const noBrowseFolder = values.search?.noBrowseFolder;
  const writePlaylist = values.output?.writePlaylist;
  const parentDir = values.output?.parentDir;
  const nameFormat = values.output?.nameFormat;
  if (typeof skipExisting !== 'boolean' || typeof noBrowseFolder !== 'boolean'
    || typeof writePlaylist !== 'boolean' || typeof parentDir !== 'string' || typeof nameFormat !== 'string') {
    throw new Error('The daemon returned incomplete effective download settings.');
  }
  return { skipExisting, loadFullAlbumFolder: !noBrowseFolder, writePlaylist, outputParentDir: parentDir, nameFormat };
}

export function buildDownloadOptionsPatch(value: DownloadOptions, baseline: DownloadOptions, capabilities: DownloadOptionCapabilities, forceFields: ReadonlySet<keyof DownloadOptions> = new Set()): Schema<'SubmissionOptionsDto'> {
  const settings: Schema<'DownloadSettingsPatchDto'> = {};
  if (value.skipExisting !== baseline.skipExisting || forceFields.has('skipExisting')) settings.skip = { skipExisting: value.skipExisting };
  if (capabilities.albumFolderEnrichment && (value.loadFullAlbumFolder !== baseline.loadFullAlbumFolder || forceFields.has('loadFullAlbumFolder'))) {
    settings.search = { noBrowseFolder: !value.loadFullAlbumFolder };
  }
  if ((capabilities.nameFormat ?? true) && (value.nameFormat !== baseline.nameFormat || forceFields.has('nameFormat'))) {
    settings.output = { nameFormat: value.nameFormat };
  }
  if (capabilities.playlistOutput && (value.writePlaylist !== baseline.writePlaylist || forceFields.has('writePlaylist'))) {
    settings.output = { ...settings.output, writePlaylist: value.writePlaylist };
  }
  return {
    ...(value.outputParentDir !== baseline.outputParentDir || forceFields.has('outputParentDir') ? { outputParentDir: value.outputParentDir || null } : {}),
    ...(Object.keys(settings).length ? { downloadSettings: settings } : {}),
  };
}

/** Move untouched fields to a new resolved baseline, retaining explicit edits. */
/** Later edits win over an earlier patch, field by field within each section. */
export function mergeSettingsPatch(base: Schema<'DownloadSettingsPatchDto'> | undefined,
  edits: Schema<'DownloadSettingsPatchDto'>): Schema<'DownloadSettingsPatchDto'> {
  const result: Record<string, unknown> = { ...base };
  for (const [section, value] of Object.entries(edits)) {
    if (value == null) continue;
    const earlier = (base as Record<string, unknown> | undefined)?.[section];
    if (section === 'search') result.search = mergeSearchSettings(base?.search ?? undefined, value as Schema<'SearchSettingsPatchDto'>);
    else if (typeof value !== 'object' || typeof earlier !== 'object' || earlier === null) result[section] = value;
    else {
      const merged: Record<string, unknown> = { ...earlier, ...value };
      // A field cannot be both set and cleared; the later instruction stands.
      for (const [key, edit] of Object.entries(value)) {
        if (key.startsWith('clear')) { if (edit === true) delete merged[key.charAt(5).toLowerCase() + key.slice(6)]; }
        else delete merged[`clear${key.charAt(0).toUpperCase()}${key.slice(1)}`];
      }
      result[section] = merged;
    }
  }
  return result;
}

export function rebaseOptions<T extends object>(previous: T | null, value: T, baseline: T, explicitFields?: ReadonlySet<keyof T>): T {
  if (!previous) return { ...baseline };
  const next = { ...baseline };
  for (const key of Object.keys(value) as (keyof T)[])
    if (value[key] !== previous[key] || explicitFields?.has(key)) next[key] = value[key];
  return next;
}

export function changedOptions<T extends object>(value: T, baseline: T): Partial<T> {
  return Object.fromEntries(Object.entries(value).filter(([key, item]) => item !== baseline[key as keyof T])) as Partial<T>;
}

/** Section indicators include profile settings; request patches still compare against resolved values. */
export function profileSettingsSections(provenance: Record<string, string>) {
  const fields = Object.keys(provenance).filter(field => provenance[field] === 'profile');
  const has = (...paths: string[]) => fields.some(field => paths.some(path => field === path || field.startsWith(`${path}.`)));
  return {
    filtering: has('Search.NecessaryCond', 'Search.NecessaryFolderCond', 'Search.StrictAlbumQuality'),
    ranking: has('Search.PreferredCond', 'Search.PreferredFolderCond'),
    download: has('Skip', 'Output', 'Transfer', 'Search.NoBrowseFolder'),
    import: has('Extraction.MaxTracks', 'Extraction.Offset', 'Extraction.UpgradeToAlbum'),
    csv: has('Csv'),
  };
}
