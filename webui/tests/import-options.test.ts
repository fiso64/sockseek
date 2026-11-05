import { expect, it } from 'vitest';
import { buildImportPatch, createImportOptions, importOptionsCustomized } from '../src/domains/jobs/import-options';

it('preserves integer overrides and leaves blank fields inherited', () => {
  expect(buildImportPatch({ maxTracks: '', offset: '', upgradeToAlbum: false })).toEqual({ maxTracks: undefined, offset: undefined, upgradeToAlbum: undefined });
  expect(buildImportPatch({ maxTracks: '2147483647', offset: '0', upgradeToAlbum: true })).toEqual({ maxTracks: 2147483647, offset: 0, upgradeToAlbum: true });
});

it('keeps the default import summary for an explicit zero offset', () => {
  expect(createImportOptions()).toEqual({ maxTracks: '', offset: '0', upgradeToAlbum: false });
  expect(importOptionsCustomized(createImportOptions())).toBe(false);
  expect(importOptionsCustomized({ maxTracks: '', offset: '', upgradeToAlbum: false })).toBe(false);
  expect(importOptionsCustomized({ maxTracks: '', offset: '0', upgradeToAlbum: false })).toBe(false);
  expect(importOptionsCustomized({ maxTracks: '', offset: '1', upgradeToAlbum: false })).toBe(true);
  expect(importOptionsCustomized({ maxTracks: '1', offset: '0', upgradeToAlbum: false })).toBe(true);
  expect(importOptionsCustomized({ maxTracks: '', offset: '0', upgradeToAlbum: true })).toBe(true);
});

it.each(['-1', '1.5', 'NaN', '2147483648'])('reports invalid input %s instead of silently changing it', (maxTracks) => {
  expect(() => buildImportPatch({ maxTracks, offset: '', upgradeToAlbum: false })).toThrow();
});
