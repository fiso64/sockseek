export type AppIconName = 'search-split' | 'search-merge' | 'refresh' | 'help' | 'info' | 'dashboard' | 'jobs' | 'search' | 'user' | 'folder' | 'file' | 'lock' | 'download' | 'upload' | 'chat' | 'settings' | 'log-out' | 'sliders' | 'trash' | 'check' | 'clock' | 'x' | 'more' | 'generic' | 'track' | 'album' | 'song-groups' | 'album-groups' | 'group-job' | 'album-groups-job' | 'job-list' | 'retrieve-folder' | 'link' | 'upload-file' | 'spotify' | 'youtube' | 'soundcloud' | 'bandcamp' | 'musicbrainz' | 'soulseek';

// Single paths let folder cards clone each decorative icon as one static SVG template.
export const folderItemIconPaths = {
  folder: 'M3.5 7h6l1.8 2h9.2v8.5A2.5 2.5 0 0 1 18 20H6a2.5 2.5 0 0 1-2.5-2.5z M3.5 7V5.5A1.5 1.5 0 0 1 5 4h4l1.5 1.5',
  file: 'M7 3.5h6l4 4V20H7z M13 3.5V8h4',
  track: 'M9 17.5V6.5l9-2v10.8 M9 17.5a2.5 2.5 0 1 1-5 0 2.5 2.5 0 1 1 5 0z M18 15.5a2.5 2.5 0 1 1-5 0 2.5 2.5 0 1 1 5 0z',
} as const;
