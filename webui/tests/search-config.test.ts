import { describe, expect, it } from 'vitest';
import { hasAppliedConditions, visibleFormatPills } from '../src/domains/search/applied-conditions';
import { readSearchConditions, rebaseSearchConditions, searchConditionsPatch, searchConditionsPatchForFamily, mergeSearchSettings, searchExpectedLength } from '../src/domains/search/config-mapping';

describe('resolved search configuration edits', () => {
  it('leaves untouched settings inherited and explicitly clears lists and booleans', () => {
    const before = readSearchConditions({ preferredCond: { formats: { replace: ['mp3'] }, strictTitle: true, minBitrate: 200, excludeLocked: true } });
    expect(before.ranking.common.formats).toEqual(['MP3']);
    expect(searchConditionsPatch('track', before, structuredClone(before))).toEqual({});
    const after = structuredClone(before);
    after.ranking.common.formats = [];
    after.ranking.track.strictTitle = false;
    after.ranking.common.excludeLocked = false;
    after.common.excludeLocked = true;
    expect(hasAppliedConditions('track', after)).toBe(true);
    expect(searchConditionsPatch('track', before, after)).toEqual({ preferredCond: { formats: { replace: [] }, strictTitle: false, excludeLocked: false }, necessaryCond: { excludeLocked: true } });
  });

  it('normalizes format vocabulary without changing exact peer spelling', () => {
    const before = readSearchConditions({});
    const after = structuredClone(before);
    after.common.formats = ['TXT'];
    after.common.allowedUsers = 'PeerA, peera';
    expect(searchConditionsPatch('generic', before, after)).toEqual({ necessaryCond: { formats: { replace: ['txt'] }, allowedUsers: { replace: ['PeerA', 'peera'] } } });
  });

  it('clears inherited numeric bounds explicitly instead of turning them into inheritance', () => {
    const before = readSearchConditions({ preferredCond: { maxSampleRate: 48000 } });
    const after = structuredClone(before);
    after.ranking.common.maxSampleRate = '';
    expect(searchConditionsPatch('track', before, after)).toEqual({ preferredCond: { clearMaxSampleRate: true } });
    // Number inputs bind null when emptied in the browser.
    (after.ranking.common as { maxSampleRate: string | null }).maxSampleRate = null;
    expect(searchConditionsPatch('track', before, after)).toEqual({ preferredCond: { clearMaxSampleRate: true } });
    after.ranking.common.maxSampleRate = '44100';
    expect(searchConditionsPatch('track', before, after)).toEqual({ preferredCond: { maxSampleRate: 44100 } });
  });

  it('clears album track bounds and supersedes an older bound patch in the same draft', () => {
    const before = readSearchConditions({ necessaryFolderCond: { minTrackCount: 3 } });
    const after = structuredClone(before);
    after.album.minTrackCount = '';
    const clear = searchConditionsPatch('album', before, after);
    expect(clear).toEqual({ necessaryFolderCond: { clearMinTrackCount: true } });
    expect(mergeSearchSettings({ necessaryFolderCond: { minTrackCount: 4 } }, clear))
      .toEqual({ necessaryFolderCond: { clearMinTrackCount: true } });
    expect(mergeSearchSettings(clear, { necessaryFolderCond: { minTrackCount: 5 } }))
      .toEqual({ necessaryFolderCond: { minTrackCount: 5 } });
  });

  it('uses the supported length-disable sentinel and keeps expected length out of condition patches', () => {
    const before = readSearchConditions({ necessaryCond: { lengthTolerance: 3 } }, 180);
    const after = structuredClone(before);
    after.track.lengthTolerance = '';
    after.track.expectedLength = '';
    expect(searchConditionsPatch('track', before, after)).toEqual({ necessaryCond: { lengthTolerance: -1 } });
    expect(searchExpectedLength(after.track.expectedLength)).toBeUndefined();
    expect(searchExpectedLength(0)).toBe(0);
    expect(() => searchExpectedLength('1.5')).toThrow('integer');
  });

  it('clears an aggregate sharer filter while inheriting an empty grouping tolerance', () => {
    const before = readSearchConditions({ minSharesAggregate: 2, aggregateLengthTol: 3 });
    const after = structuredClone(before);
    after.aggregate.minShares = '';
    after.aggregate.lengthTolerance = '';
    expect(searchConditionsPatch('song-aggregate', before, after))
      .toEqual({ minSharesAggregate: 1 });
    (after.aggregate as { lengthTolerance: string | null }).lengthTolerance = null;
    expect(searchConditionsPatch('album-aggregate', before, after))
      .toEqual({ minSharesAggregate: 1 });
    after.aggregate.lengthTolerance = '5';
    expect(searchConditionsPatch('song-aggregate', before, after))
      .toEqual({ minSharesAggregate: 1, aggregateLengthTol: 5 });
  });

  it('retains existing overrides when another condition changes', () => {
    expect(mergeSearchSettings({ searchTimeout: 5000, necessaryCond: { strictArtist: true } }, { necessaryCond: { formats: { replace: [] } } })).toEqual({ searchTimeout: 5000, necessaryCond: { strictArtist: true, formats: { replace: [] } } });
  });

  it('maps mixed-source song and album controls through the shared sparse patch', () => {
    const before = readSearchConditions({});
    const after = structuredClone(before);
    after.track.strictTitle = true;
    after.album.strictAlbum = true;
    after.album.minTrackCount = '3';
    after.ranking.common.formats = ['FLAC'];
    expect(searchConditionsPatchForFamily('mixed', before, after)).toEqual({
      necessaryCond: { strictTitle: true, strictAlbum: true },
      preferredCond: { formats: { replace: ['flac'] } },
      necessaryFolderCond: { minTrackCount: 3 },
    });
  });

  it('rebases only explicit edits when auto-profile defaults change', () => {
    const before = readSearchConditions({ necessaryCond: { formats: { replace: ['mp3'] }, strictTitle: false } });
    const edited = structuredClone(before);
    edited.common.formats = ['FLAC'];
    const next = readSearchConditions({ necessaryCond: { formats: { replace: ['ogg'] }, strictTitle: true } });
    const rebased = rebaseSearchConditions(before, edited, next);
    expect(rebased.common.formats).toEqual(['FLAC']);
    expect(rebased.track.strictTitle).toBe(true);
  });
});

it('hides only the complete built-in music format set, retaining other filters and generic formats', () => {
  const formats = ['ALAC', 'AAC', 'WAV', 'OPUS', 'M4A', 'OGG', 'FLAC', 'mp3', 'MP3'];
  const conditions = readSearchConditions({ necessaryCond: { formats: { replace: formats } } });
  for (const mode of ['track', 'album', 'song-aggregate', 'album-aggregate'] as const) {
    expect(visibleFormatPills(mode, formats)).toEqual([]);
    expect(hasAppliedConditions(mode, conditions)).toBe(false);
  }
  expect(visibleFormatPills('generic', formats)).toEqual(formats);
  expect(hasAppliedConditions('generic', conditions)).toBe(true);
  expect(visibleFormatPills('track', ['FLAC'])).toEqual(['FLAC']);
  expect(visibleFormatPills('album', formats.slice(1))).toEqual(formats.slice(1));
  expect(visibleFormatPills('album', [...formats, 'APE'])).toEqual([...formats, 'APE']);
  conditions.aggregate.minShares = '2';
  expect(hasAppliedConditions('album-aggregate', conditions)).toBe(true);
  expect(hasAppliedConditions('track', conditions)).toBe(false);
  conditions.common.minBitrate = '256';
  expect(hasAppliedConditions('track', conditions)).toBe(true);
});
