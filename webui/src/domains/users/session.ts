import type { BrowseDirectoryEntryDto, BrowseFileEntryDto, BrowseSearchPageDto, UserBrowseDto, UserProfileDto } from '../../api/types';

export type UserBrowseView = 'user' | 'shares';
export interface PageState { directoryCursor: string | null; fileCursor: string | null; loading: boolean; loaded: boolean; error: Error | null; }

/** Each tab remembers its last explicit user and saves its view on unmount.
 * Requests and DOM remain owned by the mounted page, not by this cache. */
export interface UserBrowserSession {
  username: string;
  snapshot?: {
    profile: UserProfileDto | null;
    browse: UserBrowseDto | null;
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
    selected: Set<string>;
    excludedSelection: Set<string>;
    selectedParents: Map<string, string | null>;
    selectedDirectoryCounts: Map<string, number>;
    selectedDirectoryTotals: Map<string, number>;
    selectedFileIsPublic: Map<string, boolean>;
  };
}

export function userBrowserSession(username = ''): UserBrowserSession {
  return { username };
}
