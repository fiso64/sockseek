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
