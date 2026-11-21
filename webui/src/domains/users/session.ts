import type { BrowseDirectoryEntryDto, BrowseFileEntryDto, BrowseSearchPageDto, UserBrowseDto, UserProfileDto } from '../../api/types';

import type { SelectionSize } from '../../app/tree-selection';
import type { DownloadOptions } from '../jobs/download-options';

export type UserBrowseView = 'user' | 'shares';
export interface PageState { directoryCursor: string | null; fileCursor: string | null; loading: boolean; loaded: boolean; error: Error | null; }

/** Each tab remembers its last explicit user and saves its view on unmount.
 * Requests and DOM remain owned by the mounted page, not by this cache. */
export interface UserBrowserSession {
  username: string;
  shares: ShareBrowserSession;
  snapshot?: {
    profile: UserProfileDto | null;
    browse: UserBrowseDto | null;
  };
}

export function userBrowserSession(username = ''): UserBrowserSession {
  return { username, shares: {} };
}

export function loadUserBrowserSession(view: UserBrowseView): UserBrowserSession {
  try { return userBrowserSession(sessionStorage.getItem(`sockseek.userBrowser.${view}`) ?? ''); }
  catch { return userBrowserSession(); }
}

export function saveUserBrowserUsername(view: UserBrowseView, username: string): void {
  try { sessionStorage.setItem(`sockseek.userBrowser.${view}`, username); }
  catch { /* Navigation still works when browser storage is disabled. */ }
}

export interface ShareBrowserSession {
  snapshot?: {
    browseId: string;
    directories: Map<string, BrowseDirectoryEntryDto>;
    childDirectoryIds: Map<string, string[]>;
    filesByDirectory: Map<string, BrowseFileEntryDto[]>;
    pageStates: Map<string, PageState>;
    expanded: Set<string>;
    filterText: string;
    filterKey: string;
    filterPage: BrowseSearchPageDto | null;
    filterItems: BrowseSearchPageDto['items'];
    collapsedFiltered: Set<string>;
    lastRequest?: { id: string; options: string };
    selected: Set<string>;
    excludedSelection: Set<string>;
    selectedParents: Map<string, string | null>;
    selectedSizes: Map<string, SelectionSize>;
    options: {
      baseline: DownloadOptions | null;
      values: DownloadOptions | null;
      profileNames: string[];
      open: boolean;
    };
  };
}

/** Presentation that can be restored against fresh share entries. */
export interface ShareBrowserPosition {
  filterText: string;
  expandedPaths: Set<string>;
  options: NonNullable<ShareBrowserSession['snapshot']>['options'];
  scrollTop: number;
  focusedFilter: boolean;
  focusedPath: string | null | undefined;
  focusedControl: 'input' | 'button' | null;
}
