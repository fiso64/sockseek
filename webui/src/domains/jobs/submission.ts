import { searchModeFamily, type SearchDraft } from '../search/model';
import type { Schema } from '../../api/types';

export type NewJobChoice = 'song' | 'album' | 'spotify' | 'youtube' | 'bandcamp' | 'soundcloud' | 'musicbrainz' | 'soulseek' | 'csv' | 'list';

export interface NewJobDraft {
  choice: NewJobChoice;
  artist: string;
  title: string;
  album: string;
  source: string;
  expectedLength?: number;
  spotifyInput: 'url' | 'likes' | 'albums';
  artifactId: string | null;
}

export function createNewJobDraft(): NewJobDraft {
  return { choice: 'song', artist: '', title: '', album: '', source: '', spotifyInput: 'url', artifactId: null };
}

export function newJobDraftFromSearch(search: SearchDraft): NewJobDraft {
  const album = searchModeFamily(search.resultMode) === 'album';
  const subject = (search.mode === 'split' ? search.title : search.query).trim();
  return { ...createNewJobDraft(), choice: album ? 'album' : 'song',
    artist: search.mode === 'split' ? search.artist.trim() : '',
    title: album ? '' : subject, album: album ? subject : search.albumContext ?? '',
    expectedLength: search.expectedLength };
}

// Both direct Start and Review consume this exact draft. Planning, extraction,
// settings resolution and child-job construction remain daemon responsibilities.
export function buildJobPreviewRequest(draft: NewJobDraft, options?: Schema<'SubmissionOptionsDto'>): Schema<'CreateJobPreviewRequestDto'> {
  return { job: buildJobDraft(draft, true), options };
}

/** Resolve settings for an editing draft without pretending an upload exists. */
export function buildJobSettingsRequest(draft: NewJobDraft): Schema<'ResolveEffectiveSettingsRequestDto'> {
  return { job: buildJobDraft(draft, false) };
}

function buildJobDraft(draft: NewJobDraft, requireArtifact: boolean): Schema<'JobDraftDto'> {
  let job: Schema<'JobDraftDto'>;
  if (draft.choice === 'song') {
    job = { kind: 'song', songQuery: { artist: draft.artist || null, title: draft.title || null, album: draft.album || null, uri: null, length: draft.expectedLength ?? null, artistMaybeWrong: false } };
  } else if (draft.choice === 'album') {
    job = { kind: 'album', albumQuery: { artist: draft.artist || null, album: draft.album || null, searchHint: null, uri: null, artistMaybeWrong: false } };
  } else if (draft.choice === 'csv' || draft.choice === 'list') {
    if (requireArtifact && !draft.artifactId) throw new Error('Upload a source file before starting or reviewing this job.');
    job = { kind: 'extract', input: '', inputType: draft.choice, artifactId: draft.artifactId ?? undefined };
  } else {
    const input = draft.choice === 'spotify' && draft.spotifyInput !== 'url'
      ? `spotify-${draft.spotifyInput}` : draft.source;
    job = { kind: 'extract', input, inputType: draft.choice };
  }
  return job;
}
