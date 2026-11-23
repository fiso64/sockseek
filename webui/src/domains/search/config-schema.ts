import type { SearchResultMode } from './model';

export type SearchConfigTab = 'conditions' | 'ranking';
export type SearchConfigRelationship = 'paired' | 'partial' | 'conditions-only';

export interface SearchConfigSideDefinition {
  label: string;
  section: 'quality' | 'matching' | 'peers' | 'album-structure' | 'grouping';
  help?: readonly string[];
}

export interface SearchConfigFieldDefinition {
  id: string;
  modes: SearchResultMode[] | 'all';
  relationship: SearchConfigRelationship;
  conditions: SearchConfigSideDefinition;
  ranking?: SearchConfigSideDefinition;
}

const artistMatchHelp = [
  'Peers can return a path with the search words in different places. This match looks for the artist’s name as a contiguous string anywhere in the path.',
  'Casing, underscores, spaces and special strings are ignored when comparing.',
] as const;

const titleMatchHelp = [
  'Like the artist match option above, but for the track title. Matches only against the filename instead of full path.',
] as const;

const albumMatchHelp = [
  'Like the artist match option above, but for the album name.',
] as const;

// TODO: Consider moving some matching and other options to an "advanced" (maybe collapsed) part of the search config panel.
/** Filtering and ranking labels, help and mode coverage for the search configuration panel. */
export const SEARCH_CONFIG_FIELDS = {
  formats: {
    id: 'formats', modes: 'all', relationship: 'paired',
    conditions: { label: 'File extensions', section: 'quality' },
    ranking: { label: 'Preferred file extensions', section: 'quality' },
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
  minSampleRate: {
    id: 'minSampleRate', modes: 'all', relationship: 'paired',
    conditions: { label: 'Min sample rate', section: 'quality' },
    ranking: { label: 'Min sample rate', section: 'quality' },
  },
  maxSampleRate: {
    id: 'maxSampleRate', modes: 'all', relationship: 'paired',
    conditions: { label: 'Max sample rate', section: 'quality' },
    ranking: { label: 'Max sample rate', section: 'quality' },
  },
  minBitDepth: {
    id: 'minBitDepth', modes: 'all', relationship: 'paired',
    conditions: { label: 'Min bit depth', section: 'quality' },
    ranking: { label: 'Min bit depth', section: 'quality' },
  },
  maxBitDepth: {
    id: 'maxBitDepth', modes: 'all', relationship: 'paired',
    conditions: { label: 'Max bit depth', section: 'quality' },
    ranking: { label: 'Max bit depth', section: 'quality' },
  },
  strictArtist: {
    id: 'strictArtist', modes: ['track', 'album', 'song-groups', 'album-groups'], relationship: 'paired',
    conditions: { label: 'Require artist name in path', section: 'matching', help: artistMatchHelp },
    ranking: { label: 'Prefer artist name in path', section: 'matching', help: artistMatchHelp },
  },
  strictTitle: {
    id: 'strictTitle', modes: ['track', 'song-groups'], relationship: 'paired',
    conditions: { label: 'Require track title in filename', section: 'matching', help: titleMatchHelp },
    ranking: { label: 'Prefer track title in filename', section: 'matching', help: titleMatchHelp },
  },
  strictAlbum: {
    id: 'strictAlbum', modes: ['album', 'album-groups'], relationship: 'paired',
    conditions: { label: 'Require album name in folder path', section: 'matching', help: albumMatchHelp },
    ranking: { label: 'Prefer album name in folder path', section: 'matching', help: albumMatchHelp },
  },
  expectedLength: {
    id: 'expectedLength', modes: ['track', 'song-groups'], relationship: 'conditions-only',
    conditions: { label: 'Expected length', section: 'matching' },
  },
  acceptNoLength: {
    id: 'acceptNoLength', modes: ['track', 'song-groups'], relationship: 'conditions-only',
    conditions: {
      label: 'Accept unknown length', section: 'matching',
      help: ['This applies only when the track has an expected length and a tolerance is set. Enable this to let a file pass the length check when its peer omits the duration.'],
    },
  },
  lengthTolerance: {
    id: 'lengthTolerance', modes: ['track', 'song-groups'], relationship: 'partial',
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
    ranking: { label: 'Downranked users', section: 'peers' },
  },
  rejectUnknownMetadata: {
    id: 'rejectUnknownMetadata', modes: ['track', 'song-groups', 'album', 'album-groups'], relationship: 'conditions-only',
    conditions: {
      label: 'Reject unknown metadata', section: 'quality',
      help: [
        "Some peers' clients do not report properties such as bitrate. By default, a file with an unknown bitrate is still accepted even when you set a minimum bitrate.",
        'Enable this to reject files when a property you require is unknown. Use with care: This can exclude otherwise valid files because of the client sharing them.',
      ],
    },
  },
  strictAlbumQuality: {
    id: 'strictAlbumQuality', modes: ['album', 'album-groups'], relationship: 'conditions-only',
    conditions: {
      label: 'Every track must meet quality conditions', section: 'quality',
      help: [
        'By default, mixed-quality folders are ranked by coverage. For example, if you prefer FLAC, a folder with 9 FLAC files and 1 MP3 is still accepted (ranking above a mostly-MP3 folder).',
        'Enable this to require every audio file in the folder to meet your audio quality conditions.',
      ],
    },
  },
  trackCount: {
    id: 'trackCount', modes: ['album', 'album-groups'], relationship: 'conditions-only',
    conditions: { label: 'Track count', section: 'album-structure' },
  },
  minTrackCount: {
    id: 'minTrackCount', modes: ['album', 'album-groups'], relationship: 'conditions-only',
    conditions: { label: 'Min tracks', section: 'album-structure' },
  },
  maxTrackCount: {
    id: 'maxTrackCount', modes: ['album', 'album-groups'], relationship: 'conditions-only',
    conditions: { label: 'Max tracks', section: 'album-structure' },
  },
  requiredTrackTitle: {
    id: 'requiredTrackTitle', modes: ['album', 'album-groups'], relationship: 'conditions-only',
    conditions: { label: 'Required track title', section: 'album-structure' },
  },
  minShares: {
    id: 'minShares', modes: ['song-groups', 'album-groups'], relationship: 'conditions-only',
    conditions: { label: 'Minimum sharers', section: 'grouping' },
  },
  groupLengthTolerance: {
    id: 'groupLengthTolerance', modes: ['song-groups', 'album-groups'], relationship: 'conditions-only',
    conditions: { label: 'Grouping tolerance', section: 'grouping' },
  },
} as const satisfies Record<string, SearchConfigFieldDefinition>;

export const SEARCH_AUDIO_FORMATS = ['FLAC', 'MP3', 'OGG', 'OPUS', 'M4A', 'WAV'] as const;
export const SEARCH_GENERIC_FORMATS = ['PDF', 'EPUB', 'ZIP', 'TXT', 'JPG', 'PNG'] as const;
export const SEARCH_SAMPLE_RATES = [
  { value: 44100, label: '44.1 kHz' },
  { value: 48000, label: '48 kHz' },
  { value: 88200, label: '88.2 kHz' },
  { value: 96000, label: '96 kHz' },
  { value: 176400, label: '176.4 kHz' },
  { value: 192000, label: '192 kHz' },
] as const;
export const SEARCH_BIT_DEPTHS = [16, 24, 32] as const;
