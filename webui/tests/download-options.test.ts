import { expect, it } from 'vitest';
import { buildDownloadOptionsPatch, downloadOptionsFromResolved, type DownloadOptions } from '../src/domains/jobs/download-options';
import type { Schema } from '../src/api/types';
import { buildSearchSelectionDownloadPatch, searchDownloadOptionDefaults } from '../src/domains/search/download-options';

const baseline: DownloadOptions = { skipExisting: false, loadFullAlbumFolder: false, outputParentDir: '', nameFormat: '{title}', writePlaylist: true };
const capabilities = { albumFolderEnrichment: true, playlistOutput: true };

it('uses daemon values rather than prototype defaults and reports an incomplete response', () => {
  const resolved: Schema<'EffectiveDownloadSettingsDto'> = {
    values: { skip: { skipExisting: false }, search: { noBrowseFolder: true }, output: { parentDir: '/downloads', nameFormat: '', writePlaylist: true } },
    onCompleteCommandCount: 0, regexRuleCount: 0, spotifyClientIdConfigured: false, spotifyClientSecretConfigured: false,
    spotifyTokenConfigured: false, spotifyRefreshConfigured: false, youTubeApiKeyConfigured: false,
    ytDlpArgumentConfigured: false, bandcampHtmlFileConfigured: false, soundCloudTokenConfigured: false,
  };
  expect(downloadOptionsFromResolved(resolved)).toEqual({ skipExisting: false, loadFullAlbumFolder: false, outputParentDir: '/downloads', nameFormat: '', writePlaylist: true });
  expect(() => downloadOptionsFromResolved({ ...resolved, values: {} })).toThrow('incomplete effective download settings');
});

it('inherits resolved defaults when the controls are unchanged', () => {
  expect(buildDownloadOptionsPatch({ ...baseline }, baseline, capabilities)).toEqual({});
});

it('preserves explicit false and empty overrides without resubmitting unrelated defaults', () => {
  expect(buildDownloadOptionsPatch({ ...baseline, nameFormat: '', writePlaylist: false }, baseline, capabilities)).toEqual({
    downloadSettings: { output: { nameFormat: '', writePlaylist: false } },
  });
});

it('does not submit hidden controls for a different job family', () => {
  expect(buildDownloadOptionsPatch({ ...baseline, loadFullAlbumFolder: true, writePlaylist: false }, baseline, { albumFolderEnrichment: false, playlistOutput: false })).toEqual({});
});

it('keeps mixed selection defaults unset until explicitly changed', () => {
  const settings = (skipExisting: boolean): Schema<'EffectiveDownloadSettingsDto'> => ({
    values: { skip: { skipExisting }, search: { noBrowseFolder: true }, output: {
      parentDir: '/downloads', nameFormat: '{title}', writePlaylist: false,
    } },
    onCompleteCommandCount: 0, regexRuleCount: 0, spotifyClientIdConfigured: false,
    spotifyClientSecretConfigured: false, spotifyTokenConfigured: false,
    spotifyRefreshConfigured: false, youTubeApiKeyConfigured: false,
    ytDlpArgumentConfigured: false, bandcampHtmlFileConfigured: false, soundCloudTokenConfigured: false,
  });
  const defaults = searchDownloadOptionDefaults({ revision: 3, variants: [
    { jobKind: 'remote-directory', settings: settings(true) },
    { jobKind: 'remote-file', settings: settings(false) },
  ] });
  expect(defaults.mixedFields.has('skipExisting')).toBe(true);
  expect(defaults.capabilities.albumFolderEnrichment).toBe(false);
  expect(buildDownloadOptionsPatch(defaults.values, defaults.baseline, defaults.capabilities)).toEqual({});
  expect(buildDownloadOptionsPatch({ ...defaults.values, skipExisting: true }, defaults.baseline,
    defaults.capabilities, new Set(['skipExisting']))).toEqual({
      downloadSettings: { skip: { skipExisting: true } },
    });
});

it('applies the album folder option to directories even beside exact files', () => {
  const settings: Schema<'EffectiveDownloadSettingsDto'> = {
    values: { skip: { skipExisting: true }, search: { noBrowseFolder: false },
      output: { parentDir: '/downloads', nameFormat: '', writePlaylist: false } },
    onCompleteCommandCount: 0, regexRuleCount: 0, spotifyClientIdConfigured: false,
    spotifyClientSecretConfigured: false, spotifyTokenConfigured: false,
    spotifyRefreshConfigured: false, youTubeApiKeyConfigured: false,
    ytDlpArgumentConfigured: false, bandcampHtmlFileConfigured: false, soundCloudTokenConfigured: false,
  };
  const response: Schema<'ResolveSearchViewSelectionSettingsResponseDto'> = {
    revision: 3, variants: [{ jobKind: 'remote-directory', settings }],
  };
  expect(searchDownloadOptionDefaults(response, 'AlbumDirectories').capabilities.albumFolderEnrichment).toBe(true);
  expect(searchDownloadOptionDefaults(response, 'GenericDirectories').capabilities.albumFolderEnrichment).toBe(false);
  const fileSettings: Schema<'EffectiveDownloadSettingsDto'> = {
    ...settings,
    values: { ...settings.values, search: { noBrowseFolder: true } },
  };
  const mixed = searchDownloadOptionDefaults({ ...response, variants: [
    { jobKind: 'remote-file', settings: fileSettings },
    ...response.variants,
  ] }, 'AlbumDirectories');
  expect(mixed.capabilities.albumFolderEnrichment).toBe(true);
  expect(mixed.baseline.loadFullAlbumFolder).toBe(true);
  expect(mixed.mixedFields.has('loadFullAlbumFolder')).toBe(false);
  expect(buildSearchSelectionDownloadPatch({ ...mixed.values, loadFullAlbumFolder: false },
    mixed, new Set())).toEqual({ downloadSettings: { search: { noBrowseFolder: true } } });
});

it('does not submit blank display values for untouched mixed text defaults', () => {
  const settings = (parentDir: string): Schema<'EffectiveDownloadSettingsDto'> => ({
    values: { skip: { skipExisting: false }, search: { noBrowseFolder: true },
      output: { parentDir, nameFormat: '{title}', writePlaylist: false } },
    onCompleteCommandCount: 0, regexRuleCount: 0, spotifyClientIdConfigured: false,
    spotifyClientSecretConfigured: false, spotifyTokenConfigured: false,
    spotifyRefreshConfigured: false, youTubeApiKeyConfigured: false,
    ytDlpArgumentConfigured: false, bandcampHtmlFileConfigured: false, soundCloudTokenConfigured: false,
  });
  const defaults = searchDownloadOptionDefaults({ revision: 3, variants: [
    { jobKind: 'remote-directory', settings: settings('/one') },
    { jobKind: 'remote-file', settings: settings('/two') },
  ] });
  expect(defaults.values.outputParentDir).toBe('');
  expect(buildSearchSelectionDownloadPatch(defaults.values, defaults, new Set())).toEqual({});
  expect(buildSearchSelectionDownloadPatch({ ...defaults.values, outputParentDir: '/new' },
    defaults, new Set(['outputParentDir']))).toEqual({ outputParentDir: '/new' });
});
