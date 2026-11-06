<script lang="ts">
  import { keyboardList } from '../../lib/keyboard';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { randomUUID } from '../../app/uuid';
  import { onDestroy, untrack } from 'svelte';
  import type { BrowseDirectoryEntryDto, BrowseFileEntryDto, BrowseSearchPageDto, Schema, UserBrowseDto, UserProfileDto } from '../../api/types';
  import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
  import { daemonApi } from '../../api/client';
  import { formatBytes, formatCount, formatSpeed, userInitials } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import LinkifiedText from '../../components/LinkifiedText.svelte';
  import ResultFilterControl from '../../components/ResultFilterControl.svelte';
  import SelectionToolbar from '../../components/SelectionToolbar.svelte';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import MutationStatus, { type MutationState } from '../../components/MutationStatus.svelte';
  import { fileTypeIcon } from '../presentation/file-types';
  import { filteredTreeRows } from './filter-tree';
  import { inheritedSelection, projectSelection } from './selection';

  export type UserBrowseView = 'user' | 'shares';

  const sharePageSize = 1_000;

  interface Props {
    username: string;
    view: UserBrowseView;
    liveStatus: LiveConnectionStatus;
    liveClient: DaemonLiveClient;
    onviewchange: (view: UserBrowseView) => void;
    onmessageuser: (username: string) => void;
  }

  interface TreeRowDirectory { kind: 'directory'; entry: BrowseDirectoryEntryDto; depth: number; }
  interface TreeRowFile { kind: 'file'; entry: BrowseFileEntryDto; depth: number; path: string; }
  interface TreeRowPage { kind: 'page'; parentKey: string; page: PageState; depth: number; }
  type TreeRow = TreeRowDirectory | TreeRowFile | TreeRowPage;

  interface PageState { directoryCursor: string | null; fileCursor: string | null; loading: boolean; loaded: boolean; error: Error | null; }

  let { username, view, liveStatus, liveClient, onviewchange, onmessageuser }: Props = $props();
  let profile = $state<UserProfileDto | null>(null);
  let profileLoading = $state(false);
  let profileError = $state<Error | null>(null);
  let profilePictureOpen = $state(false);
  let browse = $state<UserBrowseDto | null>(null);
  let browseError = $state<Error | null>(null);
  let directories = $state<Map<string, BrowseDirectoryEntryDto>>(new Map());
  let childDirectoryIds = $state<Map<string, string[]>>(new Map());
  let filesByDirectory = $state<Map<string, BrowseFileEntryDto[]>>(new Map());
  let pageStates = $state<Map<string, PageState>>(new Map());
  let expanded = $state<Set<string>>(new Set());
  let filterText = $state('');
  let filterPage = $state<BrowseSearchPageDto | null>(null);
  let filterItems = $state<BrowseSearchPageDto['items']>([]);
  let filterLoading = $state(false);
  let filterError = $state<Error | null>(null);
  let filterRevision = 0;
  let filterRequest: AbortController | null = null;
  let selected = $state<Set<string>>(new Set());
  let excludedSelection = $state<Set<string>>(new Set());
  // Retain only selected entries and their ancestors across filtered page changes.
  let selectedParents = $state<Map<string, string | null>>(new Map());
  let selectedDirectoryCounts = $state<Map<string, number>>(new Map());
  let selectedDirectoryTotals = $state<Map<string, number>>(new Map());
  let selectedFileIsPublic = $state<Map<string, boolean>>(new Map());
  let mutation = $state<MutationState>({ phase: 'idle' });
  let commitKey: string | null = null;
  let loadedUsername = '';
  let lifetime = new AbortController();
  let browseTimer: ReturnType<typeof setTimeout> | null = null;
  let browsePollAttempts = 0;
  let wasOffline = false;
  let browseStreamFailed = false;
  const activeBrowseId = $derived(view === 'shares' && browse
    && (browse.state === 'queued' || browse.state === 'running') ? browse.browseId : null);

  let treeRows = $derived(flattenTree());
  let filteredRows = $derived(filteredTreeRows(filterItems));
  let filteredEntries = $derived(new Map(filterItems.map(item => [`${item.kind === 'directory' ? 'd' : 'f'}:${item.entryId}`, item])));
  let loadedFiles = $derived.by(() => {
    const entries = new Map<string, { directoryId: string; isPublic: boolean }>();
    for (const pageFiles of filesByDirectory.values()) {
      for (const file of pageFiles) entries.set(String(file.fileId), {
        directoryId: String(file.directoryId), isPublic: file.visibility === 'public',
      });
    }
    return entries;
  });
  let selectionProjection = $derived(projectSelection(selected, excludedSelection, selectionParent, selectionSize));
  let publicSelectionProjection = $derived(projectSelection(selected, excludedSelection, selectionParent, publicSelectionSize));
  let selectedCount = $derived(selectionProjection.total);
  let downloadableCount = $derived(publicSelectionProjection.total);
  let lockedCount = $derived(Math.max(0, selectedCount - downloadableCount));
  let pictureUrl = $derived(profile?.picture?.url ? absoluteApiPath(profile.picture.url) : null);

  $effect(() => {
    const currentUsername = username;
    const currentView = view;
    untrack(() => {
      if (currentUsername !== loadedUsername) {
        loadedUsername = currentUsername;
        resetForUser();
      }
      if (currentView === 'user' && !profile && !profileLoading) void loadProfile(currentUsername);
    });
  });

  $effect(() => {
    const currentView = view;
    const currentUsername = username;
    if (currentView === 'shares' && (!browse || browse.username !== currentUsername)) void acquireBrowse(currentUsername);
  });

  $effect(() => {
    if (liveStatus === 'offline') {
      wasOffline = true;
      return;
    }
    if (liveStatus !== 'connected' || !wasOffline) return;
    wasOffline = false;
    if (view === 'user' && profileError && !profile && !profileLoading) void loadProfile(username);
    if (view === 'shares' && browseError) {
      if (!browse) void acquireBrowse(username);
      else if (browse.state === 'queued' || browse.state === 'running') {
        browseError = null;
        void advanceBrowse();
      }
    }
  });

  $effect(() => {
    const browseId = activeBrowseId;
    const connected = liveStatus === 'connected';
    if (!browseId || !connected) {
      untrack(() => { void advanceBrowse(); });
      return;
    }
    let active = true;
    let unsubscribe: (() => Promise<void>) | null = null;
    browseStreamFailed = false;
    untrack(() => { void advanceBrowse(); });
    const failed = () => {
      if (!active) return;
      active = false;
      browseStreamFailed = true;
      void unsubscribe?.();
      void advanceBrowse();
    };
    void liveClient.subscribeUserBrowse(browseId, value => {
      if (!active || lifetime.signal.aborted) return;
      browse = value;
      browseError = null;
      void advanceBrowse();
    }, failed).then(release => {
      if (!active) void release();
      else unsubscribe = release;
    }).catch(failed);
    return () => { active = false; void unsubscribe?.(); };
  });

  $effect(() => {
    const query = filterText.trim();
    const browseId = browse?.state === 'complete' ? browse.browseId : null;
    filterRequest = null;
    filterRevision++;
    filterPage = null;
    filterItems = [];
    if (!query || !browseId) {
      filterPage = null;
      filterItems = [];
      filterLoading = false;
      filterError = null;
      return;
    }
    filterLoading = true;
    filterError = null;
    const request = new AbortController();
    filterRequest = request;
    const timer = setTimeout(() => void loadFilter(browseId, query, null, true, request.signal), 100);
    return () => { clearTimeout(timer); request.abort(); };
  });

  onDestroy(() => {
    lifetime.abort();
    if (browseTimer) clearTimeout(browseTimer);
  });

  function clearSelection(): void {
    selected = new Set();
    excludedSelection = new Set();
    selectedParents = new Map();
    selectedDirectoryCounts = new Map();
    selectedDirectoryTotals = new Map();
    selectedFileIsPublic = new Map();
    mutation = { phase: 'idle' };
    commitKey = null;
  }

  function resetForUser(): void {
    lifetime.abort();
    lifetime = new AbortController();
    if (browseTimer) clearTimeout(browseTimer);
    browseTimer = null;
    browsePollAttempts = 0;
    profile = null;
    profileLoading = false;
    profileError = null;
    browse = null;
    browseError = null;
    directories = new Map();
    childDirectoryIds = new Map();
    filesByDirectory = new Map();
    pageStates = new Map();
    expanded = new Set();
    filterText = '';
    filterPage = null;
    filterItems = [];
    clearSelection();
  }

  async function loadProfile(value: string, refresh = false): Promise<void> {
    const signal = lifetime.signal;
    profileLoading = true;
    profileError = null;
    try {
      const result = await daemonApi.getUserProfile(value, refresh, signal);
      if (!signal.aborted) profile = result;
    }
    catch (cause) { if (!signal.aborted) profileError = asError(cause); }
    finally { if (!signal.aborted) profileLoading = false; }
  }

  async function acquireBrowse(value: string, refresh = false): Promise<void> {
    if (browse?.username === value && (browse.state === 'running' || browse.state === 'queued' || browse.state === 'complete') && !refresh) return;
    browseError = null;
    const signal = lifetime.signal;
    try {
      const result = await daemonApi.startUserBrowse(value, refresh, signal);
      if (signal.aborted) return;
      browse = result;
      browsePollAttempts = 0;
      await advanceBrowse();
    } catch (cause) {
      if (!signal.aborted) browseError = asError(cause);
    }
  }

  async function advanceBrowse(): Promise<void> {
    if (browseTimer) clearTimeout(browseTimer);
    browseTimer = null;
    if (!browse || view !== 'shares' || lifetime.signal.aborted) return;
    if (browse.state === 'complete') {
      if (!pageStates.get('root')?.loaded) await loadChildren(null, true);
      return;
    }
    if (browse.state === 'failed' || browse.state === 'cancelled') {
      browseError = new Error(browse.failure?.error ?? `Browse ${browse.state}.`);
      return;
    }
    if (liveStatus === 'connected' && !browseStreamFailed) return;
    const delay = browsePollAttempts++ === 0 ? 500 : 1_000;
    browseTimer = setTimeout(async () => {
      browseTimer = null;
      if (!browse || lifetime.signal.aborted) return;
      const signal = lifetime.signal;
      try {
        const result = await daemonApi.getUserBrowse(browse.browseId, signal);
        if (signal.aborted) return;
        browse = result;
        await advanceBrowse();
      } catch (cause) {
        if (!signal.aborted) browseError = asError(cause);
      }
    }, delay);
  }

  async function loadChildren(parentId: number | string | null, reset = false): Promise<void> {
    if (!browse || browse.state !== 'complete') return;
    const signal = lifetime.signal;
    const key = parentId === null ? 'root' : String(parentId);
    const current = pageStates.get(key) ?? { directoryCursor: null, fileCursor: null, loading: false, loaded: false, error: null };
    if (current.loading || (current.loaded && !reset && !current.directoryCursor && !current.fileCursor)) return;
    setPageState(key, { ...current, loading: true, error: null });
    try {
      const [directoryPage, filePage] = await Promise.all([
        current.loaded && !reset && !current.directoryCursor
          ? Promise.resolve({ items: [] as BrowseDirectoryEntryDto[], nextCursor: null })
          : daemonApi.getUserShareDirectories(browse.browseId, parentId, reset ? null : current.directoryCursor, sharePageSize, signal),
        parentId === null || (current.loaded && !reset && !current.fileCursor)
          ? Promise.resolve({ items: [] as BrowseFileEntryDto[], nextCursor: null })
          : daemonApi.getUserShareFiles(browse.browseId, parentId, reset ? null : current.fileCursor, sharePageSize, signal),
      ]);
      if (signal.aborted) return;
      const nextDirectories = new Map(directories);
      for (const entry of directoryPage.items) nextDirectories.set(String(entry.directoryId), entry);
      directories = nextDirectories;
      childDirectoryIds = new Map(childDirectoryIds).set(key, mergeIds(reset ? [] : childDirectoryIds.get(key) ?? [], directoryPage.items.map((entry) => String(entry.directoryId))));
      filesByDirectory = new Map(filesByDirectory).set(key, mergeFiles(reset ? [] : filesByDirectory.get(key) ?? [], filePage.items));
      setPageState(key, { directoryCursor: directoryPage.nextCursor ?? null, fileCursor: filePage.nextCursor ?? null, loading: false, loaded: true, error: null });
      if (parentId === null && expanded.size === 0) expanded = new Set(directoryPage.items.map((entry) => String(entry.directoryId)));
      for (const entry of directoryPage.items) {
        if (expanded.has(String(entry.directoryId))) void loadChildren(entry.directoryId);
      }
    } catch (cause) {
      if (!signal.aborted) setPageState(key, { ...current, loading: false, error: asError(cause) });
    }
  }

  function setPageState(key: string, state: PageState): void { pageStates = new Map(pageStates).set(key, state); }

  async function toggleDirectory(entry: BrowseDirectoryEntryDto): Promise<void> {
    const id = String(entry.directoryId);
    const next = new Set(expanded);
    if (next.has(id)) next.delete(id); else next.add(id);
    expanded = next;
    if (next.has(id)) await loadChildren(entry.directoryId);
  }

  function flattenTree(): TreeRow[] {
    const result: TreeRow[] = [];
    const append = (parentKey: string, depth: number): void => {
      if (parentKey !== 'root') {
        const parent = directories.get(parentKey);
        for (const entry of filesByDirectory.get(parentKey) ?? []) {
          result.push({ kind: 'file', entry, depth, path: parent ? `${parent.displayPath}/${entry.file.name}` : entry.file.name });
        }
      }
      for (const id of childDirectoryIds.get(parentKey) ?? []) {
        const entry = directories.get(id);
        if (!entry) continue;
        result.push({ kind: 'directory', entry, depth });
        if (expanded.has(id)) append(id, depth + 1);
      }
      const page = pageStates.get(parentKey);
      if (page && (page.loading || page.error || page.directoryCursor || page.fileCursor)) {
        result.push({ kind: 'page', parentKey, page, depth });
      }
    };
    append('root', 0);
    return result;
  }

  async function loadFilter(browseId: string, query: string, cursor: string | null, reset: boolean, signal: AbortSignal = filterRequest?.signal ?? lifetime.signal): Promise<void> {
    const revision = ++filterRevision;
    const isCurrent = () => !signal.aborted && revision === filterRevision && browse?.browseId === browseId && query === filterText.trim();
    filterLoading = true;
    filterError = null;
    try {
      const page = await daemonApi.searchUserShares(browseId, query, cursor, sharePageSize, signal);
      if (!isCurrent()) return;
      filterPage = page;
      filterItems = reset ? page.items : [...filterItems, ...page.items];
    } catch (cause) {
      if (isCurrent()) filterError = asError(cause);
    } finally {
      if (isCurrent()) filterLoading = false;
    }
  }

  function toggleSelection(key: string, checked: boolean): void {
    const inherited = hasSelectedAncestor(key);
    const next = new Set(selected);
    const exclusions = new Set(excludedSelection);
    next.delete(key);
    exclusions.delete(key);
    if (checked && !inherited) next.add(key);
    if (!checked && inherited) exclusions.add(key);
    if (key.startsWith('d:')) {
      for (const rules of [next, exclusions]) {
        for (const candidate of rules) {
          if (candidate !== key && isDescendantOf(candidate, key.slice(2))) rules.delete(candidate);
        }
      }
    }
    selected = next;
    excludedSelection = exclusions;
    const retainedParents = new Map<string, string | null>();
    for (const selectedKey of [...next, ...excludedSelection]) {
      let current: string | null = selectedKey;
      while (current !== null && !retainedParents.has(current)) {
        const parent: string | null = current.startsWith('d:')
          ? parentDirectoryId(current.slice(2)) : keyDirectoryId(current);
        retainedParents.set(current, parent);
        current = parent === null ? null : `d:${parent}`;
      }
    }
    selectedParents = retainedParents;
    selectedDirectoryCounts = new Map([...next, ...excludedSelection].filter(key => key.startsWith('d:')).map(key => [key,
      Number(directories.get(key.slice(2))?.recursiveFileCount
        ?? filterDirectory(key)?.publicSubtreeFileCount ?? selectedDirectoryCounts.get(key) ?? 0),
    ]));
    selectedDirectoryTotals = new Map([...next, ...excludedSelection].filter(key => key.startsWith('d:')).map(key => [key,
      Number(directories.get(key.slice(2))?.totalRecursiveFileCount
        ?? filterDirectory(key)?.totalSubtreeFileCount
        ?? selectedDirectoryTotals.get(key)
        ?? selectedDirectoryCounts.get(key) ?? 0),
    ]));
    selectedFileIsPublic = new Map([...next, ...excludedSelection].filter(key => key.startsWith('f:')).map(key => [key,
      loadedFiles.get(key.slice(2))?.isPublic
        ?? (filteredEntries.get(key) ? filteredEntries.get(key)!.visibility === 'public' : undefined)
        ?? selectedFileIsPublic.get(key) ?? false,
    ]));
    commitKey = null;
    mutation = { phase: 'idle' };
  }

  function selectionSize(key: string): number {
    return key.startsWith('d:')
      ? Number(directories.get(key.slice(2))?.totalRecursiveFileCount
        ?? filterDirectory(key)?.totalSubtreeFileCount
        ?? selectedDirectoryTotals.get(key)
        ?? directories.get(key.slice(2))?.recursiveFileCount
        ?? filterDirectory(key)?.publicSubtreeFileCount
        ?? selectedDirectoryCounts.get(key) ?? 0)
      : 1;
  }

  function publicSelectionSize(key: string): number {
    if (key.startsWith('d:')) return Number(directories.get(key.slice(2))?.recursiveFileCount
      ?? filterDirectory(key)?.publicSubtreeFileCount ?? selectedDirectoryCounts.get(key) ?? 0);
    const filtered = filteredEntries.get(key);
    const isPublic = loadedFiles.get(key.slice(2))?.isPublic
      ?? (filtered ? filtered.visibility === 'public' : undefined)
      ?? selectedFileIsPublic.get(key) ?? false;
    return isPublic ? 1 : 0;
  }

  function filterDirectory(key: string): BrowseSearchPageDto['items'][number] | undefined {
    return filteredEntries.get(key);
  }

  function keyDirectoryId(key: string): string | null {
    if (key.startsWith('d:')) return key.slice(2);
    const fileId = key.slice(2);
    const loaded = loadedFiles.get(fileId);
    if (loaded !== undefined) return loaded.directoryId;
    const filtered = filteredEntries.get(key);
    return filtered ? String(filtered.directoryId) : selectedParents.get(key) ?? null;
  }

  function parentDirectoryId(directoryId: string): string | null {
    const local = directories.get(directoryId);
    if (local) return local.parentId == null ? null : String(local.parentId);
    const filtered = filteredEntries.get(`d:${directoryId}`);
    if (filtered) return filtered.parentDirectoryId == null ? null : String(filtered.parentDirectoryId);
    return selectedParents.get(`d:${directoryId}`) ?? null;
  }

  function hasSelectedAncestor(key: string): boolean {
    return inheritedSelection(key, selected, excludedSelection, selectionParent);
  }

  function selectionParent(key: string): string | null {
    const id = key.startsWith('d:') ? parentDirectoryId(key.slice(2)) : keyDirectoryId(key);
    return id === null ? null : `d:${id}`;
  }

  function isInheritedSelection(key: string): boolean { return !selected.has(key) && hasSelectedAncestor(key); }
  function selectedSize(key: string): number {
    const included = !excludedSelection.has(key) && (selected.has(key) || hasSelectedAncestor(key));
    return (included ? selectionSize(key) : 0) + (selectionProjection.descendantDeltas.get(key) ?? 0);
  }

  function isSelectionChecked(key: string): boolean {
    if (!key.startsWith('d:')) return !excludedSelection.has(key) && (selected.has(key) || hasSelectedAncestor(key));
    const total = selectionSize(key);
    return total > 0 && selectedSize(key) === total;
  }

  function isFolderPartial(key: string): boolean {
    const count = selectedSize(key);
    return count > 0 && count < selectionSize(key);
  }

  function indeterminate(node: HTMLInputElement, value: boolean): { update: (value: boolean) => void } {
    node.indeterminate = value;
    return { update: (next) => { node.indeterminate = next; } };
  }

  function isDescendantOf(key: string, ancestorId: string): boolean {
    const visited = new Set<string>();
    let directoryId = keyDirectoryId(key);
    if (key.startsWith('d:') && key.slice(2) === ancestorId) return false;
    while (directoryId && !visited.has(directoryId)) {
      visited.add(directoryId);
      if (directoryId === ancestorId) return true;
      directoryId = parentDirectoryId(directoryId);
    }
    return false;
  }

  async function downloadSelection(): Promise<void> {
    if (!browse || !selected.size || mutation.phase === 'pending') return;
    const browseId = browse.browseId;
    const signal = lifetime.signal;
    const selections: Schema<'UserShareSelectionDto'>[] = [...selected].map((key) => key.startsWith('d:')
      ? { kind: 'directory' as const, directoryId: key.slice(2) }
      : { kind: 'file' as const, fileId: key.slice(2) });
    const requestId = commitKey ??= randomUUID();
    mutation = { phase: 'pending', label: `Requesting ${downloadableCount} shared file${downloadableCount === 1 ? '' : 's'}…` };
    try {
      const response = await daemonApi.startUserShareDownloads(browseId, requestId, selections, undefined, [...excludedSelection].filter(key => key.startsWith('f:')).map(key => key.slice(2)), [...excludedSelection].filter(key => key.startsWith('d:')).map(key => key.slice(2)));
      // Selection changes invalidate the receipt's presentation, not the accepted
      // download. Do not cancel a submitted command or clear a newer retry key.
      if (signal.aborted || browse?.browseId !== browseId || commitKey !== requestId) return;
      const resolved = Number(response.resolution.totalPublicFiles);
      const skipped = Number(response.resolution.lockedBranchesSkipped);
      mutation = {
        phase: resolved ? skipped ? 'partially-succeeded' : 'succeeded' : 'rejected',
        label: resolved ? `${resolved} download${resolved === 1 ? '' : 's'} requested` : 'Nothing downloadable',
        detail: skipped ? `${skipped} locked branch${skipped === 1 ? '' : 'es'} skipped` : undefined,
      };
      commitKey = null;
    } catch (cause) {
      if (signal.aborted || browse?.browseId !== browseId || commitKey !== requestId) return;
      mutation = { phase: 'rejected', label: 'Download request failed', detail: asError(cause).message };
    }
  }

  function absoluteApiPath(value: string): string { return value.startsWith('/') ? value : `/${value}`; }
  function asError(cause: unknown): Error { return cause instanceof Error ? cause : new Error(String(cause)); }
  function mergeIds(existing: string[], incoming: string[]): string[] { return [...new Set([...existing, ...incoming])]; }
  function mergeFiles(existing: BrowseFileEntryDto[], incoming: BrowseFileEntryDto[]): BrowseFileEntryDto[] { const values = new Map(existing.map((item) => [String(item.fileId), item])); for (const item of incoming) values.set(String(item.fileId), item); return [...values.values()]; }
</script>

<svelte:window onkeydown={(event) => {
  if (event.key === 'Escape') profilePictureOpen = false;
}} />

<section class="page user-browser-page" use:keyboardList={{
  scope: `${username}:${view}:${browse?.browseId}:${filterText.trim()}`,
  enabled: view === 'shares' && browse?.state === 'complete',
  revision: [treeRows, filteredRows, filterLoading],
  toggle: key => toggleSelection(key, !isSelectionChecked(key)),
  horizontal: (direction, key) => {
    if (direction !== 1 || !key?.startsWith('d:') || filterText.trim()) return;
    const directory = directories.get(key.slice(2));
    if (directory) void toggleDirectory(directory);
  },
  loadPage: key => {
    if (filterText.trim()) {
      if (browse) void loadFilter(browse.browseId, filterText.trim(), filterPage?.nextCursor ?? null, false);
    } else void loadChildren(key === 'root' ? null : key);
  },
}}>
  <header class="user-browser-heading">
    <div><p class="eyebrow">Network</p><h1>User browser</h1></div>
    <nav class="user-view-tabs" aria-label="User browser views">
      <button type="button" class:active={view === 'user'}
        aria-current={view === 'user' ? 'page' : undefined}
        onclick={() => onviewchange('user')}>User</button>
      <button type="button" class:active={view === 'shares'}
        aria-current={view === 'shares' ? 'page' : undefined}
        onclick={() => onviewchange('shares')}>Shares</button>
    </nav>
  </header>

  {#if view === 'user'}
    {#if profileError}
      <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'}`}
        class:blocking={!profile} role="alert">
        <span class="resource-state-dot"></span>
        <span class="resource-state-copy">
          <strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Profile unavailable'}</strong>
          <small>{liveStatus === 'offline' ? 'Profile data cannot be loaded while the daemon is offline.' : profileError.message}</small>
        </span>
        {#if liveStatus !== 'offline'}
          <button type="button" disabled={profileLoading}
            onclick={() => void loadProfile(username)}>Retry</button>
        {/if}
      </div>
    {/if}
    {#if profileLoading && !profile && !profileError}
      <LoadingIndicator label="Loading profile" blocking />
    {/if}
    {#if profile}
      <article class="user-profile-card">
        <div class="user-profile-primary">
          {#if pictureUrl}
            <button type="button" class="user-profile-picture-button"
              aria-label={`View ${profile.username}'s profile picture`}
              onclick={() => (profilePictureOpen = true)}>
              <img class="user-profile-picture" src={pictureUrl} alt="" />
            </button>
          {:else}
            <div class="user-profile-picture user-profile-placeholder" aria-label="No profile picture">
              <Icon name="user"/><span>{userInitials(profile.username)}</span>
            </div>
          {/if}
          <div class="user-profile-copy">
            <div class="user-profile-name-line">
              <h2>{profile.username}</h2>
              <span class={`user-presence ${profile.presence}`}><i></i>{profile.presence[0]!.toUpperCase() + profile.presence.slice(1)}</span>
              <span class="user-capacity-inline" aria-label="Upload capacity">
                <span class:available={profile.hasFreeUploadSlot === true}
                  class:unknown={profile.hasFreeUploadSlot == null} class="slot-state"><i></i>{profile.hasFreeUploadSlot == null ? 'Slot unknown' : profile.hasFreeUploadSlot ? 'Free slot' : 'No free slot'}</span>
                <span><strong>{profile.uploadSlots ?? '—'}</strong> slots</span>
                <span><strong>{profile.queueLength ?? '—'}</strong> queued</span>
              </span>
            </div>
            {#if profile.description}
              <p class="user-profile-description"><LinkifiedText text={profile.description}/></p>
            {/if}
            <small class="user-profile-observed">Observed {new Date(profile.observedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</small>
            <div class="user-profile-actions">
              <button type="button" class="user-message-button" disabled={profile.privateMessagesBlocked}
                onclick={() => onmessageuser(profile!.username)}>
                <Icon name="chat"/>
                <span>{profile.privateMessagesBlocked ? 'Messages blocked' : 'Message'}</span>
              </button>
            </div>
          </div>
        </div>
        {#if profile.info.state !== 'available' || profile.statistics.state !== 'available' || profile.pictureSection.state !== 'available'}
          <div class="profile-section-states" aria-label="Profile section availability">
            {#if profile.info.state !== 'available'}
              <span>Info {profile.info.state}{profile.info.reason ? ` · ${profile.info.reason}` : ''}</span>
            {/if}
            {#if profile.statistics.state !== 'available'}
              <span>Statistics {profile.statistics.state}{profile.statistics.reason ? ` · ${profile.statistics.reason}` : ''}</span>
            {/if}
            {#if profile.pictureSection.state !== 'available'}
              <span>Picture {profile.pictureSection.state}{profile.pictureSection.reason ? ` · ${profile.pictureSection.reason}` : ''}</span>
            {/if}
          </div>
        {/if}
        <div class="user-profile-stats">
          <div><span>Shared files</span><strong>{profile.sharedFileCount == null ? '—' : formatCount(profile.sharedFileCount)}</strong></div>
          <div><span>Shared folders</span><strong>{profile.sharedDirectoryCount == null ? '—' : formatCount(profile.sharedDirectoryCount)}</strong></div>
          <div><span>Average upload speed</span><strong>{formatSpeed(profile.averageUploadSpeed) ?? '—'}</strong></div>
          <div><span>Uploads</span><strong>{profile.uploadCount == null ? '—' : formatCount(profile.uploadCount)}</strong></div>
        </div>
      </article>
    {/if}
  {:else}
    {#if browseError}
      <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'}`}
        class:blocking={!browse || browse.state !== 'complete'} role="alert">
        <span class="resource-state-dot"></span>
        <span class="resource-state-copy">
          <strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Shares unavailable'}</strong>
          <small>{liveStatus === 'offline' ? 'Shared files cannot be loaded while the daemon is offline.' : browseError.message}</small>
        </span>
      </div>
    {/if}
    {#if !browseError && (!browse || browse.state === 'queued' || browse.state === 'running')}
      {#if browse && Number(browse.compressedBytesReceived) > 0}
        <div class="resource-state-notice loading blocking">
          <span class="resource-state-dot"></span>
          <span class="resource-state-copy">
            <strong>{browse?.phase === 'receiving' ? 'Receiving shares' : browse?.phase === 'indexing' ? 'Indexing shares' : 'Browsing user'}</strong>
            <small>{browse ? `${formatBytes(browse.compressedBytesReceived)} received${browse.compressedBytesExpected ? ` of ${formatBytes(browse.compressedBytesExpected)}` : ''}` : `Requesting ${username}'s shares.`}</small>
          </span>
        </div>
      {:else}
        <LoadingIndicator label="Loading shares" blocking />
      {/if}
    {/if}
    {#if browse?.state === 'complete'}
      <MutationStatus state={mutation}/>
      <div class="shares-overview">
        <div class="shares-title-row">
          <div>
            <span class="shares-username">{browse.username}</span>
            <strong>{formatBytes(browse.totalFileBytes)} shared</strong>
          </div>
        </div>
        <div class="shares-refine-row">
          <ResultFilterControl bind:value={filterText} placeholder="Filter shared files…"
            ariaLabel="Filter user shares"/>
          {#if filterText.trim() && filterPage}
            {@const matchingCount = Number(filterPage.publicMatchingFileCount) + Number(filterPage.lockedMatchingFileCount)}
            <span class="shares-filter-count">{formatCount(matchingCount)} matching {matchingCount === 1 ? 'file' : 'files'}</span>
          {/if}
        </div>
        <SelectionToolbar {selectedCount} floatingLabel={`Download ${downloadableCount}`}
          detail={lockedCount ? `${selectedCount} selected · ${lockedCount} locked` : undefined}
          actionDisabled={downloadableCount === 0 || mutation.phase === 'pending'}
          onclear={clearSelection} onaction={() => void downloadSelection()}/>

        {#if filterText.trim()}
          {#if filterError}
            <div class="resource-state-notice unavailable" role="alert">
              <span class="resource-state-dot"></span>
              <span class="resource-state-copy">
                <strong>Share filter failed</strong>
                <small>{filterError.message}</small>
              </span>
              <button type="button" disabled={filterLoading}
                onclick={() => browse && void loadFilter(browse.browseId, filterText.trim(), filterPage?.nextCursor ?? null, !filterPage)}>Retry</button>
            </div>
          {/if}
          {#if filterLoading && !filterItems.length}
            <LoadingIndicator label="Filtering shares" />
          {/if}
          {#if filterItems.length}
            <div class="share-tree" aria-label={`${browse.username} filtered shared files`}>
              {#each filteredRows as { entry: item, depth } (`${item.kind}:${item.entryId}`)}
                {@const key = `${item.kind === 'directory' ? 'd' : 'f'}:${item.entryId}`}
                {#if item.kind === 'directory'}
                  {@const count = Number(item.totalSubtreeFileCount ?? item.publicSubtreeFileCount ?? 0)}
                  <div data-keyboard-item={key} tabindex="-1" class="share-tree-row folder" style={`--share-depth:${depth}`} title={isInheritedSelection(key) && isSelectionChecked(key) ? 'Included by a selected parent folder' : undefined}>
                    <input type="checkbox" use:indeterminate={isFolderPartial(key)} aria-label={`Select ${item.displayPath}`} checked={isSelectionChecked(key)}  onchange={(event) => toggleSelection(key, (event.currentTarget as HTMLInputElement).checked)}/>
                    <button type="button" class="share-tree-folder-button" aria-label={`Collapse ${item.name}`} aria-expanded={true} disabled>
                      <span class="share-tree-toggle expanded" aria-hidden="true"><svg viewBox="0 0 16 16"><path d="m6 3.5 4.5 4.5L6 12.5"/></svg></span>
                      <span class="share-tree-icon"><Icon name="folder"/></span>
                      <strong class="share-tree-name" title={item.displayPath}>{item.name}</strong>
                      {#if item.visibility !== 'public'}<span class={`share-visibility ${item.visibility}`}>{item.visibility === 'locked' ? 'Locked' : 'Mixed'}</span>{/if}
                      <span class="share-tree-folder-meta">{formatCount(count)} {item.totalSubtreeFileCount == null ? 'public ' : ''}{count === 1 ? 'file' : 'files'}</span>
                      <span class="share-tree-size">{formatBytes(item.totalSubtreeBytes ?? item.publicSubtreeBytes)}</span>
                    </button>
                  </div>
                {:else}
                  <label data-keyboard-item={key} tabindex="-1" class="share-tree-row file" style={`--share-depth:${depth}`} title={isInheritedSelection(key) && isSelectionChecked(key) ? 'Included by a selected parent folder' : undefined}>
                    <input type="checkbox" checked={isSelectionChecked(key)}  onchange={(event) => toggleSelection(key, (event.currentTarget as HTMLInputElement).checked)}/>
                    <span class="share-tree-toggle-spacer"></span>
                    <span class="share-tree-icon"><Icon name={fileTypeIcon({ extension: item.extension, filename: item.name })}/></span>
                    <strong class="share-tree-name" title={item.displayPath}>{item.name}</strong>
                    {#if item.visibility !== 'public'}<span class={`share-visibility ${item.visibility}`}>{item.visibility === 'locked' ? 'Locked' : 'Mixed'}</span>{/if}
                    <span class="share-tree-size">{formatBytes(item.fileSize)}</span>
                  </label>
                {/if}
              {/each}
            </div>
          {/if}
          {#if filterPage && !filterLoading && !filterError && !filterItems.length}
            <div class="search-results-empty"><strong>No matching shared files</strong><span>Try a different filter.</span></div>
          {/if}
          {#if filterPage?.nextCursor}<div data-keyboard-page="filter" data-loading={filterLoading} data-failed={Boolean(filterError)}><LoadMoreButton loading={filterLoading} onclick={() => browse && void loadFilter(browse.browseId, filterText.trim(), filterPage?.nextCursor ?? null, false)}/></div>{/if}
        {:else if treeRows.length}
          <div class="share-tree" aria-label={`${browse.username} shared files`}>
            {#each treeRows as row (row.kind === 'page' ? `p:${row.parentKey}` : row.kind === 'directory' ? `d:${row.entry.directoryId}` : `f:${row.entry.fileId}`)}
              {#if row.kind === 'directory'}
                {@const id = String(row.entry.directoryId)}
                {@const key = `d:${id}`}
                {@const count = Number(row.entry.totalRecursiveFileCount ?? row.entry.recursiveFileCount)}
                <div data-keyboard-item={key} tabindex="-1" class="share-tree-row folder" style={`--share-depth:${row.depth}`}
                  title={isInheritedSelection(key) && isSelectionChecked(key) ? 'Included by a selected parent folder' : undefined}>
                  <input type="checkbox" use:indeterminate={isFolderPartial(key)}
                    checked={isSelectionChecked(key)} aria-label={`Select ${row.entry.displayPath}`}
                    onchange={(event) => toggleSelection(key, (event.currentTarget as HTMLInputElement).checked)}/>
                  <button type="button" class="share-tree-folder-button"
                    aria-label={`${expanded.has(id) ? 'Collapse' : 'Expand'} ${row.entry.name}`}
                    aria-expanded={expanded.has(id)} onclick={() => void toggleDirectory(row.entry)}>
                    <span class:expanded={expanded.has(id)} class="share-tree-toggle" aria-hidden="true">
                      <svg viewBox="0 0 16 16"><path d="m6 3.5 4.5 4.5L6 12.5"/></svg>
                    </span>
                    <span class="share-tree-icon"><Icon name="folder"/></span>
                    <strong class="share-tree-name" title={row.entry.displayPath}>{row.entry.name}</strong>
                    {#if row.entry.visibility !== 'public'}
                      <span class={`share-visibility ${row.entry.visibility}`}>{row.entry.visibility === 'locked' ? 'Locked' : 'Mixed'}</span>
                    {/if}
                    <span class="share-tree-folder-meta">{formatCount(count)} {row.entry.totalRecursiveFileCount == null ? 'public ' : ''}{count === 1 ? 'file' : 'files'}</span>
                    <span class="share-tree-size">{formatBytes(row.entry.totalRecursiveFileBytes ?? row.entry.recursiveFileBytes)}</span>
                  </button>
                </div>
              {:else if row.kind === 'page'}
                <div data-keyboard-page={row.parentKey} data-loading={row.page.loading} data-failed={Boolean(row.page.error)} class="share-tree-page" style={`--share-depth:${row.depth}`}>
                  {#if row.page.error}
                    <div class="resource-state-notice unavailable" role="alert">
                      <span class="resource-state-copy"><strong>Folder page unavailable</strong><small>{row.page.error.message}</small></span>
                      <button type="button" disabled={row.page.loading} onclick={() => void loadChildren(row.parentKey === 'root' ? null : row.parentKey)}>Retry</button>
                    </div>
                  {:else}
                    <LoadMoreButton loading={row.page.loading}
                      onclick={() => void loadChildren(row.parentKey === 'root' ? null : row.parentKey)}/>
                  {/if}
                </div>
              {:else}
                {@const key = `f:${row.entry.fileId}`}
                <label data-keyboard-item={key} tabindex="-1" class="share-tree-row file" style={`--share-depth:${row.depth}`}
                  title={isInheritedSelection(key) && isSelectionChecked(key) ? 'Included by a selected parent folder' : undefined}>
                  <input type="checkbox" checked={isSelectionChecked(key)}
                    onchange={(event) => toggleSelection(key, (event.currentTarget as HTMLInputElement).checked)}/>
                  <span class="share-tree-toggle-spacer"></span>
                  <span class="share-tree-icon">
                    <Icon name={fileTypeIcon({ extension: row.entry.file.extension, filename: row.entry.file.name })}/>
                  </span>
                  <strong class="share-tree-name" title={row.path}>{row.entry.file.name}</strong>
                  {#if row.entry.visibility !== 'public'}
                    <span class={`share-visibility ${row.entry.visibility}`}>Locked</span>
                  {/if}
                  <span class="share-tree-size">{formatBytes(row.entry.file.size)}</span>
                </label>
              {/if}
            {/each}
          </div>
        {:else if pageStates.get('root')?.loaded && !pageStates.get('root')?.error}
          <div class="search-results-empty">
            <strong>No shared files</strong>
            <span>This browse contains no visible entries.</span>
          </div>
        {/if}

      </div>
    {/if}
  {/if}
</section>

{#if profilePictureOpen && pictureUrl}
  <div class="profile-picture-lightbox">
    <button type="button" class="profile-picture-lightbox-backdrop"
      aria-label="Close profile picture" onclick={() => (profilePictureOpen = false)}></button>
    <div class="profile-picture-lightbox-dialog" role="dialog" aria-modal="true"
      aria-label={`${profile?.username} profile picture`}>
      <img src={pictureUrl} alt={`${profile?.username} profile picture`}/>
      <button type="button" class="profile-picture-lightbox-close"
        aria-label="Close profile picture" onclick={() => (profilePictureOpen = false)}>×</button>
    </div>
  </div>
{/if}
