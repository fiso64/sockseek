import type { Schema } from '../../api/types';

export interface ImportOptions { maxTracks: string; offset: string; upgradeToAlbum: boolean }

export function createImportOptions(): ImportOptions {
  return { maxTracks: '', offset: '0', upgradeToAlbum: false };
}

export function importOptionsCustomized(value: ImportOptions, baseline: ImportOptions = createImportOptions()): boolean {
  return value.maxTracks !== baseline.maxTracks
    || (value.offset || '0') !== (baseline.offset || '0')
    || value.upgradeToAlbum !== baseline.upgradeToAlbum;
}

function integer(value: string, name: string, minimum: number): number | undefined {
  if (!value.trim()) return undefined;
  if (!/^\d+$/.test(value.trim())) throw new Error(`${name} must be a whole number.`);
  const parsed = Number(value);
  // ExtractionSettingsPatchDto uses a signed 32-bit integer on the wire.
  if (parsed < minimum || parsed > 2_147_483_647) throw new Error(`${name} must be between ${minimum} and 2147483647.`);
  return parsed;
}

export function importOptionsFromResolved(settings: Schema<'EffectiveDownloadSettingsDto'>): ImportOptions {
  const extraction = settings.values.extraction;
  return { maxTracks: extraction?.maxTracks == null || extraction.maxTracks === 2147483647 ? '' : String(extraction.maxTracks),
    offset: String(extraction?.offset ?? 0), upgradeToAlbum: extraction?.upgradeToAlbum ?? false };
}

export function buildImportPatch(value: ImportOptions, baseline: ImportOptions = createImportOptions()): Schema<'ExtractionSettingsPatchDto'> {
  return {
    ...(value.maxTracks !== baseline.maxTracks ? { maxTracks: integer(value.maxTracks, 'Item limit', 1) ?? 2147483647 } : {}),
    ...((value.offset || '0') !== (baseline.offset || '0') ? { offset: integer(value.offset, 'Offset', 0) ?? 0 } : {}),
    ...(value.upgradeToAlbum !== baseline.upgradeToAlbum ? { upgradeToAlbum: value.upgradeToAlbum } : {}),
  };
}
