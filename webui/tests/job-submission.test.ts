import { describe, expect, it } from 'vitest';
import { buildJobPreviewRequest, buildJobSettingsRequest, createNewJobDraft } from '../src/domains/jobs/submission';

describe('New Job submission', () => {
  it('preserves song metadata and passes settings to the daemon unchanged', () => {
    const draft = { ...createNewJobDraft(), artist: 'Artist', title: 'Track', album: 'Album' };
    const request = buildJobPreviewRequest(draft);
    expect(request.job).toEqual({ kind: 'song', songQuery: { artist: 'Artist', title: 'Track', album: 'Album', uri: null, length: null, artistMaybeWrong: false } });
    expect(request.options).toBeUndefined();
  });

  it('uses daemon Spotify collection inputs', () => {
    const draft = { ...createNewJobDraft(), choice: 'spotify' as const, spotifyInput: 'albums' as const };
    expect(buildJobPreviewRequest(draft).job).toEqual({ kind: 'extract', input: 'spotify-albums', inputType: 'spotify' });
    expect(buildJobPreviewRequest({ ...createNewJobDraft(), choice: 'soundcloud', source: 'https://soundcloud.com/artist/sets/list' }).job)
      .toEqual({ kind: 'extract', input: 'https://soundcloud.com/artist/sets/list', inputType: 'soundcloud' });
  });

  it('requires a real uploaded artifact instead of treating the browser filename as a server path', () => {
    const draft = { ...createNewJobDraft(), choice: 'csv' as const, source: 'private.csv' };
    expect(() => buildJobPreviewRequest(draft)).toThrow();
    expect(buildJobPreviewRequest({ ...draft, artifactId: 'uploaded-artifact' }).job).toEqual({ kind: 'extract', input: '', inputType: 'csv', artifactId: 'uploaded-artifact' });
    expect(buildJobSettingsRequest(draft).job).toEqual({ kind: 'extract', input: '', inputType: 'csv', artifactId: undefined });
  });
  it.each(['csv', 'list'] as const)('uses an explicit daemon path for %s without uploading', (choice) => {
    const draft = { ...createNewJobDraft(), choice, fileInput: 'path' as const,
      source: '/srv/music inputs/wishlist.txt', artifactId: 'unused-upload' };
    const job = { kind: 'extract', input: draft.source, inputType: choice };
    expect(buildJobPreviewRequest(draft).job).toEqual(job);
    expect(buildJobSettingsRequest(draft).job).toEqual(job);
    expect(() => buildJobPreviewRequest({ ...draft, source: '  ' })).toThrow();
  });

});
