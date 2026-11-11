import type { SearchResultMode } from './model';

export type SearchConfigTab = 'conditions' | 'ranking';
export type SearchConfigRelationship = 'paired' | 'partial' | 'conditions-only';

export interface SearchConfigSideDefinition {
  label: string;
  section: 'quality' | 'matching' | 'peers' | 'album-structure';
}

export interface SearchConfigFieldDefinition {
  id: string;
  modes: SearchResultMode[] | 'all';
  relationship: SearchConfigRelationship;
  conditions: SearchConfigSideDefinition;
  ranking?: SearchConfigSideDefinition;
}

/**
 * Human-facing search configuration vocabulary.
 *
 * Keeping the required and ranking copy next to each other makes the daemon's
 * required <-> preferred correspondence obvious. A missing `ranking` entry is
 * intentional: there is no explicit pref-* control for that condition in the
 * documented CLI surface.
 *
 * Compound controls (format sets, exact-vs-range quality controls, track length)
 * are still rendered explicitly in Svelte; this registry is the single source
 * for their labels and relationship semantics rather than trying to force every
 * control into one generic form-field abstraction.
 */
export const SEARCH_CONFIG_FIELDS = {
  formats: {
    id: 'formats', modes: 'all', relationship: 'paired',
    conditions: { label: 'Formats', section: 'quality' },
    ranking: { label: 'Preferred formats', section: 'quality' },
  },
  minBitrate: {
    id: 'minBitrate', modes: 'all', relationship: 'paired',
    conditions: { label: 'Min bitrate', section: 'quality' },
    ranking: { label: 'Min bitrate', section: 'quality' },
  },
  maxBitrate: {
    id: 'maxBitrate', modes: 'all', relationship: 'paired',
    conditions: { label: 'Max bitrate', section: 'quality' },
    ranking: { label: 'Max bitrate', section: 'quality' },
  },
  sampleRate: {
    id: 'sampleRate', modes: 'all', relationship: 'paired',
    conditions: { label: 'Sample rate', section: 'quality' },
    ranking: { label: 'Sample rate', section: 'quality' },
  },
  bitDepth: {
    id: 'bitDepth', modes: 'all', relationship: 'paired',
    conditions: { label: 'Bit depth', section: 'quality' },
    ranking: { label: 'Bit depth', section: 'quality' },
  },
  strictArtist: {
    id: 'strictArtist', modes: ['track', 'album', 'song-aggregate', 'album-aggregate'], relationship: 'paired',
    conditions: { label: 'Require artist in path', section: 'matching' },
    ranking: { label: 'Prefer artist in path', section: 'matching' },
  },
  strictTitle: {
    id: 'strictTitle', modes: ['track'], relationship: 'paired',
    conditions: { label: 'Require track title in filename', section: 'matching' },
    ranking: { label: 'Prefer track title in filename', section: 'matching' },
  },
  strictAlbum: {
    id: 'strictAlbum', modes: ['album'], relationship: 'paired',
    conditions: { label: 'Require album in folder path', section: 'matching' },
    ranking: { label: 'Prefer album in folder path', section: 'matching' },
  },
  lengthTolerance: {
    id: 'lengthTolerance', modes: ['track'], relationship: 'partial',
    conditions: { label: 'Tolerance', section: 'matching' },
    ranking: { label: 'Length tolerance', section: 'matching' },
  },
  allowedUsers: {
    id: 'allowedUsers', modes: 'all', relationship: 'paired',
    conditions: { label: 'Allowed users', section: 'peers' },
    ranking: { label: 'Preferred users', section: 'peers' },
  },
  excludeLocked: {
    id: 'excludeLocked', modes: 'all', relationship: 'paired',
    conditions: { label: 'Hide locked files', section: 'peers' },
    ranking: { label: 'Prefer unlocked files', section: 'peers' },
  },
  bannedUsers: {
    id: 'bannedUsers', modes: 'all', relationship: 'paired',
    conditions: { label: 'Banned users', section: 'peers' },
    ranking: { label: 'Downrank users', section: 'peers' },
  },
  rejectUnknownMetadata: {
    id: 'rejectUnknownMetadata', modes: 'all', relationship: 'conditions-only',
    conditions: { label: 'Reject unknown metadata', section: 'quality' },
  },
  strictAlbumQuality: {
    id: 'strictAlbumQuality', modes: ['album'], relationship: 'conditions-only',
    conditions: { label: 'Every album track must satisfy quality', section: 'quality' },
  },
  minTrackCount: {
    id: 'minTrackCount', modes: ['album'], relationship: 'conditions-only',
    conditions: { label: 'Min tracks', section: 'album-structure' },
  },
  maxTrackCount: {
    id: 'maxTrackCount', modes: ['album'], relationship: 'conditions-only',
    conditions: { label: 'Max tracks', section: 'album-structure' },
  },
  requiredTrackTitle: {
    id: 'requiredTrackTitle', modes: ['album'], relationship: 'conditions-only',
    conditions: { label: 'Required track title', section: 'album-structure' },
  },
} as const satisfies Record<string, SearchConfigFieldDefinition>;

export const SEARCH_AUDIO_FORMATS = ['FLAC', 'MP3', 'OGG', 'OPUS', 'M4A', 'WAV'] as const;
export const SEARCH_GENERIC_FORMATS = ['PDF', 'EPUB', 'ZIP', 'TXT', 'JPG', 'PNG'] as const;
// Compatibility alias for components whose default vocabulary is audio-oriented.
export const SEARCH_FORMATS = SEARCH_AUDIO_FORMATS;
export const SEARCH_SAMPLE_RATES = [
  { value: '44100', label: '44.1 kHz' },
  { value: '48000', label: '48 kHz' },
  { value: '88200', label: '88.2 kHz' },
  { value: '96000', label: '96 kHz' },
  { value: '176400', label: '176.4 kHz' },
  { value: '192000', label: '192 kHz' },
] as const;
export const SEARCH_BIT_DEPTHS = ['16', '24', '32'] as const;

export function searchConfigLabel(
  field: keyof typeof SEARCH_CONFIG_FIELDS,
  tab: SearchConfigTab,
): string {
  const definition = SEARCH_CONFIG_FIELDS[field];
  if (tab === 'ranking' && 'ranking' in definition) return definition.ranking.label;
  if (tab === 'ranking') return definition.conditions.label;
  return definition.conditions.label;
}
