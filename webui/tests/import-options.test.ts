import { expect, it } from 'vitest';
import { buildImportPatch, createImportOptions, importOptionsCustomized } from '../src/domains/jobs/import-options';

it('preserves integer overrides and leaves blank fields inherited', () => {
  expect(buildImportPatch({ maxTracks: '', offset: '', upgradeToAlbum: false })).toEqual({});
  expect(buildImportPatch({ maxTracks: '2147483647', offset: '0', upgradeToAlbum: true })).toEqual({ maxTracks: 2147483647, upgradeToAlbum: true });
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

it('inherits profile import settings and lets the user clear or override them', () => {
  const baseline = { maxTracks: '20', offset: '5', upgradeToAlbum: true };
  expect(buildImportPatch(baseline, baseline)).toEqual({});
  expect(buildImportPatch(createImportOptions(), baseline)).toEqual({ maxTracks: 2147483647, offset: 0, upgradeToAlbum: false });
  expect(buildImportPatch({ ...baseline, offset: '10' }, baseline)).toEqual({ offset: 10 });
});
