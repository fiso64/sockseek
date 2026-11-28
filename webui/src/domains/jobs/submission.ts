import { searchModeFamily, type SearchDraft } from '../search/model';
import type { Schema } from '../../api/types';

export type NewJobChoice = 'song' | 'album' | 'spotify' | 'youtube' | 'bandcamp' | 'soundcloud' | 'musicbrainz' | 'soulseek-link' | 'csv' | 'list' | 'string';

export interface NewJobDraft {
  choice: NewJobChoice;
  artist: string;
  title: string;
  album: string;
  source: string;
  expectedLength?: number;
  spotifyInput: 'url' | 'likes' | 'albums';
  fileInput: 'upload' | 'path';
  artifactId: string | null;
}

export function createNewJobDraft(): NewJobDraft {
  return { choice: 'song', artist: '', title: '', album: '', source: '', spotifyInput: 'url', fileInput: 'upload', artifactId: null };
}

/** What the New job dialog opens with: an input, and settings that are
 * treated as its starting point rather than as edits. */
export interface NewJobStart {
  draft: NewJobDraft;
  profileNames: string[];
  settings?: Schema<'DownloadSettingsPatchDto'>;
}

export function newJobStartFromSearch(search: SearchDraft): NewJobStart {
  const album = searchModeFamily(search.resultMode) === 'album';
  const subject = (search.mode === 'split' ? search.title : search.query).trim();
  return {
    draft: { ...createNewJobDraft(), choice: album ? 'album' : 'song',
      artist: search.mode === 'split' ? search.artist.trim() : '',
      title: album ? '' : subject, album: album ? subject : search.albumContext ?? '',
      expectedLength: search.expectedLength },
    profileNames: [...(search.profileNames ?? [])],
    settings: search.searchSettings ? { search: search.searchSettings } : undefined,
  };
}

const sourceChoices: readonly NewJobChoice[] = ['spotify', 'youtube', 'bandcamp', 'soundcloud', 'musicbrainz', 'soulseek-link', 'csv', 'list', 'string'];

/** The request of an earlier job, as the daemon reads it back. */
export function newJobStartFromRerun(rerun: Schema<'JobRerunDraftDto'>): NewJobStart {
  const job = rerun.job;
  const draft = createNewJobDraft();
  if (job.kind === 'song') {
    Object.assign(draft, { choice: 'song', artist: job.songQuery.artist ?? '', title: job.songQuery.title ?? '',
      album: job.songQuery.album ?? '', expectedLength: job.songQuery.length == null ? undefined : Number(job.songQuery.length) });
  } else if (job.kind === 'album') {
    Object.assign(draft, { choice: 'album', artist: job.albumQuery.artist ?? '', album: job.albumQuery.album ?? '' });
  } else if (job.kind === 'extract') {
    const input = job.input ?? '';
    const type = job.inputType?.toLowerCase() as NewJobChoice | undefined;
    draft.choice = type && sourceChoices.includes(type) ? type : 'string';
    draft.fileInput = 'path';
    const saved = draft.choice === 'spotify' ? /^spotify-(likes|albums)$/.exec(input)?.[1] as 'likes' | 'albums' | undefined : undefined;
    if (saved) draft.spotifyInput = saved;
    else draft.source = input;
  } else {
    throw new Error('This job cannot be edited in the New job dialog.');
  }
  return { draft, profileNames: [...rerun.profileNames], settings: rerun.downloadSettings ?? undefined };
}

export function profileInputDefaults(names: string[], catalog: Schema<'ProfileSummaryDto'>[]) {
  const profiles = [...names].reverse().map(name => catalog.find(item => item.name === name));
  const input = profiles.find(profile => profile?.input != null);
  const type = profiles.find(profile => profile?.inputType != null);
  const resolvedType = type?.inputType && type.inputType !== 'None' ? type.inputType : input?.detectedInputType;
  return { input, type, resolvedType };
}

export function applyProfileInput(draft: NewJobDraft, input: string | undefined, type: string | null | undefined): NewJobDraft | null {
  const requested = type?.toLowerCase();
  let choice: NewJobChoice;
  switch (requested) {
    case 'list': case 'csv': case 'string': case 'spotify': case 'bandcamp': case 'musicbrainz':
    case 'soulseek-link': case 'soundcloud': case 'youtube':
      choice = requested;
      break;
    default: return input ? null : draft;
  }
  const source = input ?? (draft.choice === 'song' ? draft.title : draft.choice === 'album' ? draft.album : draft.source);
  return { ...draft, choice, source,
    fileInput: input !== undefined || source ? 'path' : draft.fileInput,
    artifactId: input === undefined && choice === draft.choice ? draft.artifactId : null,
    spotifyInput: source === 'spotify-likes' ? 'likes' : source === 'spotify-albums' ? 'albums' : 'url' };
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

function buildJobDraft(draft: NewJobDraft, requireInput: boolean): Schema<'JobDraftDto'> {
  let job: Schema<'JobDraftDto'>;
  if (draft.choice === 'song') {
    job = { kind: 'song', songQuery: { artist: draft.artist || null, title: draft.title || null, album: draft.album || null, uri: null, length: draft.expectedLength ?? null, artistMaybeWrong: false } };
  } else if (draft.choice === 'album') {
    job = { kind: 'album', albumQuery: { artist: draft.artist || null, album: draft.album || null, searchHint: null, uri: null, artistMaybeWrong: false } };
  } else if (draft.choice === 'csv' || draft.choice === 'list') {
    if (draft.fileInput === 'path') {
      if (requireInput && !draft.source.trim()) throw new Error('Enter a file path on the daemon’s machine before starting or reviewing this job.');
      job = { kind: 'extract', input: draft.source, inputType: draft.choice };
    } else {
      if (requireInput && !draft.artifactId) throw new Error('Upload a source file before starting or reviewing this job.');
      job = { kind: 'extract', input: '', inputType: draft.choice, artifactId: draft.artifactId ?? undefined };
    }
  } else {
    const input = draft.choice === 'spotify' && draft.spotifyInput !== 'url'
      ? `spotify-${draft.spotifyInput}` : draft.source;
    job = { kind: 'extract', input, inputType: draft.choice };
  }
  return job;
}
