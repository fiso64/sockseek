import type { AppIconName } from './icons';

export const navigationItems = [
  { id: 'dashboard', label: 'Dashboard', icon: 'dashboard', placement: 'primary' },
  { id: 'searches', label: 'Searches', icon: 'search', placement: 'primary' },
  { id: 'downloads', label: 'Downloads', icon: 'download', placement: 'primary' },
  { id: 'uploads', label: 'Uploads', icon: 'upload', placement: 'primary' },
  { id: 'users', label: 'Users', icon: 'user', placement: 'primary' },
  { id: 'shares', label: 'Shares', icon: 'folder', placement: 'primary' },
  { id: 'chat', label: 'Chat', icon: 'chat', placement: 'primary' },
  { id: 'system', label: 'System', icon: 'settings', placement: 'secondary' },
] as const satisfies readonly {
  id: string;
  label: string;
  icon: AppIconName;
  placement: 'primary' | 'secondary';
}[];

export type PageId = (typeof navigationItems)[number]['id'];

/** What the app renders. Searches and download requests share the job views;
 * the Downloads entry also covers the per-peer file timeline. */
export type ViewId = Exclude<PageId, 'searches'> | 'jobs';

/** The two views of Downloads: what was asked for, and the files moving. */
export type DownloadsView = 'requests' | 'files';
export const downloadsViews = [
  { id: 'requests', label: 'Requests' },
  { id: 'files', label: 'Files' },
] as const satisfies readonly { id: DownloadsView; label: string }[];

export interface UserLinkActions {
  profile: (username: string) => void;
  shares: (username: string) => void;
  message: (username: string) => void;
}
