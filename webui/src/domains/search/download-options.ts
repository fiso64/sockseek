import type { Schema } from '../../api/types';
import {
  buildDownloadOptionsPatch,
  downloadOptionsFromResolved,
  type DownloadOptionCapabilities,
  type DownloadOptions,
} from '../jobs/download-options';

export interface SearchDownloadOptionDefaults {
  baseline: DownloadOptions;
  values: DownloadOptions;
  mixedFields: ReadonlySet<keyof DownloadOptions>;
  capabilities: DownloadOptionCapabilities;
}

const fields: readonly (keyof DownloadOptions)[] = [
  'skipExisting', 'loadFullAlbumFolder', 'outputParentDir', 'nameFormat', 'writePlaylist',
];

export function searchDownloadOptionDefaults(
  response: Schema<'ResolveSearchViewSelectionSettingsResponseDto'>,
  projectionKind?: string,
): SearchDownloadOptionDefaults {
  if (!response.variants.length) throw new Error('The selection has no downloadable public results.');
  const variants = response.variants.map(variant => ({
    kind: variant.jobKind,
    options: downloadOptionsFromResolved(variant.settings),
  }));
  const defaults = variants.map(variant => variant.options);
  const albumDirectory = projectionKind === 'AlbumDirectories'
    ? variants.find(variant => variant.kind === 'remote-directory')
    : undefined;
  const baseline = { ...defaults[0]! };
  if (albumDirectory) baseline.loadFullAlbumFolder = albumDirectory.options.loadFullAlbumFolder;
  const mixedFields = new Set<keyof DownloadOptions>();
  for (const field of fields) {
    if (field !== 'loadFullAlbumFolder' || !albumDirectory) {
      if (defaults.some(value => value[field] !== baseline[field])) mixedFields.add(field);
    }
  }
  const values = { ...baseline };
  if (mixedFields.has('outputParentDir')) values.outputParentDir = '';
  if (mixedFields.has('nameFormat')) values.nameFormat = '';
  return {
    baseline,
    values,
    mixedFields,
    capabilities: {
      albumFolderEnrichment: response.variants.every(variant => variant.jobKind === 'album')
        || (projectionKind === 'AlbumDirectories'
          && Boolean(albumDirectory)
          && response.variants.every(variant => variant.jobKind === 'remote-directory'
            || variant.jobKind === 'remote-file')),
      playlistOutput: false,
      nameFormat: true,
    },
  };
}

export function buildSearchSelectionDownloadPatch(
  value: DownloadOptions,
  defaults: SearchDownloadOptionDefaults,
  touchedMixedFields: ReadonlySet<keyof DownloadOptions>,
): Schema<'SubmissionOptionsDto'> {
  const comparison = { ...defaults.baseline };
  // Mixed text fields are displayed blank. That presentation is not itself an
  // override; only an edit to that field should become a commit patch.
  if (defaults.mixedFields.has('outputParentDir') && !touchedMixedFields.has('outputParentDir'))
    comparison.outputParentDir = value.outputParentDir;
  if (defaults.mixedFields.has('nameFormat') && !touchedMixedFields.has('nameFormat'))
    comparison.nameFormat = value.nameFormat;
  return buildDownloadOptionsPatch(value, comparison, defaults.capabilities, touchedMixedFields);
}
