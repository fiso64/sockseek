import type { Schema } from '../../api/types';

export interface ImportOptions { maxTracks: string; offset: string; upgradeToAlbum: boolean }

export function createImportOptions(): ImportOptions {
  return { maxTracks: '', offset: '0', upgradeToAlbum: false };
}

export function importOptionsCustomized(value: ImportOptions): boolean {
  return Boolean(value.maxTracks.trim())
    || (value.offset.trim() !== '' && value.offset.trim() !== '0')
    || value.upgradeToAlbum;
}

function integer(value: string, name: string, minimum: number): number | undefined {
  if (!value.trim()) return undefined;
  if (!/^\d+$/.test(value.trim())) throw new Error(`${name} must be a whole number.`);
  const parsed = Number(value);
  // ExtractionSettingsPatchDto uses a signed 32-bit integer on the wire.
  if (parsed < minimum || parsed > 2_147_483_647) throw new Error(`${name} must be between ${minimum} and 2147483647.`);
  return parsed;
}

export function buildImportPatch(value: ImportOptions): Schema<'ExtractionSettingsPatchDto'> {
  return {
    maxTracks: integer(value.maxTracks, 'Item limit', 1),
    offset: integer(value.offset, 'Offset', 0),
    upgradeToAlbum: value.upgradeToAlbum ? true : undefined,
  };
}
