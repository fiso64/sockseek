import { daemonApi } from '../../api/client';
import { DaemonHttpClient } from '../../api/http';
import type { Schema, UserBrowseDto } from '../../api/types';

/** Both browsers use the same pages and filtering; only peer shares are selectable. */
export interface ShareBrowserSource {
  browseId: string;
  /** The owner as shown: a peer's name, or a label for the daemon's own shares. */
  username: string;
  /** The Soulseek name links point to, when it isn't `username`; read when a link is copied. */
  linkUsername?: () => string | null;
  totalFileBytes: number | string;
  directories: (parentId: number | string | null, cursor: string | null, limit: number, signal: AbortSignal) => Promise<Schema<'PageDtoOfBrowseDirectoryEntryDto'>>;
  files: (directoryId: number | string, cursor: string | null, limit: number, signal: AbortSignal) => Promise<Schema<'PageDtoOfBrowseFileEntryDto'>>;
  search: (query: string, cursor: string | null, limit: number, signal: AbortSignal) => Promise<Schema<'BrowseSearchPageDto'>>;
  expiresAt?: string | null;
  checkExpiry?: (signal: AbortSignal) => Promise<string | null>;
  download?: (requestId: string, selections: Schema<'UserShareSelectionDto'>[], excludedFiles: string[], excludedDirectories: string[], options?: Schema<'SubmissionOptionsDto'>) => Promise<Schema<'StartUserShareDownloadsResponseDto'>>;
}

export function peerShareSource(browse: UserBrowseDto): ShareBrowserSource {
  const id = browse.browseId;
  return {
    browseId: id, username: browse.username, totalFileBytes: browse.totalFileBytes,
    expiresAt: browse.expiresAt,
    checkExpiry: async signal => (await daemonApi.getUserBrowse(id, signal)).expiresAt ?? null,
    directories: (...args) => daemonApi.getUserShareDirectories(id, ...args),
    files: (...args) => daemonApi.getUserShareFiles(id, ...args),
    search: (...args) => daemonApi.searchUserShares(id, ...args),
    download: (requestId, selections, files, directories, options) => daemonApi.startUserShareDownloads(id, requestId, selections, undefined, files, directories, options),
  };
}

/** `account` reads the live login, which can change while the catalog stays. */
export function localShareSource(generationId: string, totalBytes: number | string, account: () => string | null): ShareBrowserSource {
  const http = new DaemonHttpClient();
  const base = `/sharing/catalogs/${encodeURIComponent(generationId)}`;
  const query = (values: Record<string, string | number | null>) => {
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(values)) if (value !== null) params.set(key, String(value));
    return `?${params}`;
  };
  return {
    browseId: generationId, username: 'Your shares', totalFileBytes: totalBytes,
    // Others browse these shares under the account the daemon is logged in as.
    linkUsername: account,
    directories: (parentId, cursor, limit, signal) => http.get(`${base}/directories${query({ parentId, cursor, limit })}`, signal),
    files: (directoryId, cursor, limit, signal) => http.get(`${base}/directories/${directoryId}/files${query({ cursor, limit })}`, signal),
    search: (text, cursor, limit, signal) => http.get(`${base}/search${query({ query: text, cursor, limit })}`, signal),
  };
}
