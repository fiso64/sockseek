import type { AppIconName } from './icons';

export const navigationItems = [
  { id: 'dashboard', label: 'Dashboard', icon: 'dashboard', placement: 'primary' },
  { id: 'jobs', label: 'Jobs', icon: 'jobs', placement: 'primary' },
  { id: 'downloads', label: 'Downloads', icon: 'download', placement: 'primary' },
  { id: 'uploads', label: 'Uploads', icon: 'upload', placement: 'primary' },
  { id: 'users', label: 'Users', icon: 'user', placement: 'primary' },
  { id: 'shares', label: 'Shares', icon: 'folder', placement: 'primary' },
  { id: 'chat', label: 'Chat', icon: 'chat', placement: 'primary' },
  { id: 'settings', label: 'Settings', icon: 'settings', placement: 'secondary' },
] as const satisfies readonly {
  id: string;
  label: string;
  icon: AppIconName;
  placement: 'primary' | 'secondary';
}[];

export type PageId = (typeof navigationItems)[number]['id'];

export interface UserLinkActions {
  profile: (username: string) => void;
  shares: (username: string) => void;
  message: (username: string) => void;
}
