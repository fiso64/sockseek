import type { Schema } from '../../api/types';

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
