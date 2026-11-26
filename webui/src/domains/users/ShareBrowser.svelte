<script lang="ts">
  import ProfilePicker from '../../components/ProfilePicker.svelte';
  import DownloadOptionsPanel from '../../components/DownloadOptionsPanel.svelte';
  import { DaemonApiError } from '../../api/http';
  import Notice from '../../components/Notice.svelte';
  import { daemonApi } from '../../api/client';
  import { downloadOptionsFromResolved, buildDownloadOptionsPatch, rebaseOptions, type DownloadOptions } from '../jobs/download-options';
  import { keyboardList } from '../../lib/keyboard';
  import EmptyState from '../../components/EmptyState.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { onDestroy, untrack } from 'svelte';
  import type { BrowseDirectoryEntryDto, BrowseFileEntryDto, BrowseSearchPageDto, Schema } from '../../api/types';
  import { formatBytes, formatCount } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import type { UserLinkActions } from '../../app/navigation';
  import LockedIndicator from '../../components/LockedIndicator.svelte';
  import ResultFilterControl from '../../components/ResultFilterControl.svelte';
  import SelectionToolbar from '../../components/SelectionToolbar.svelte';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import MutationStatus, { type MutationState } from '../../components/MutationStatus.svelte';
  import { fileTypeIcon } from '../presentation/file-types';
  import { filteredTreeRows } from './filter-tree';
  import { inheritedSelection, projectSelection, type SelectionSize } from '../../app/tree-selection';

  import type { PageState, ShareBrowserPosition, ShareBrowserSession } from './session';

  import { randomUUID } from '../../app/uuid';
  import type { ShareBrowserSource } from './share-source';
  interface TreeRowDirectory { kind: 'directory'; entry: BrowseDirectoryEntryDto; depth: number; }
  interface TreeRowFile { kind: 'file'; entry: BrowseFileEntryDto; depth: number; path: string; }
  interface TreeRowPage { kind: 'page'; parentKey: string; page: PageState; depth: number; }
  type TreeRow = TreeRowDirectory | TreeRowFile | TreeRowPage;

  let { source: input, session = {}, position, connected = true, configurationRevision, userActions }: {
    source: ShareBrowserSource; session?: ShareBrowserSession; position?: ShareBrowserPosition; connected?: boolean; configurationRevision?: number;
    /** Makes a peer's name the usual username link; the daemon's own shares have none. */
    userActions?: UserLinkActions;
  } = $props();
  // The parent keys the browser by immutable generation.
  const source = untrack(() => input);
  const browse = source;
  const sharePageSize = 1_000;
  const saved = untrack(() => session.snapshot?.browseId === source.browseId ? session.snapshot : undefined);
  const previous = untrack(() => position);
  const options = saved?.options ?? previous?.options;
  let container: HTMLDivElement;
  let directories = $state<Map<string, BrowseDirectoryEntryDto>>(saved?.directories ?? new Map());
  let childDirectoryIds = $state<Map<string, string[]>>(saved?.childDirectoryIds ?? new Map());
  let filesByDirectory = $state<Map<string, BrowseFileEntryDto[]>>(saved?.filesByDirectory ?? new Map());
  let pageStates = $state<Map<string, PageState>>(saved?.pageStates ?? new Map());
  let expanded = $state<Set<string>>(saved?.expanded ?? new Set());
  let filterText = $state(saved?.filterText ?? previous?.filterText ?? '');
  // Typing only updates the field; the tree and requests follow the settled query,
  // so a burst of keystrokes costs one re-render instead of one per letter.
  let filterQuery = $state(untrack(() => filterText.trim()));
  $effect(() => {
    const query = filterText.trim();
    if (query === untrack(() => filterQuery)) return;
    // Wait for a pause in typing: phone keyboards space letters wider than a short debounce.
    const timer = setTimeout(() => (filterQuery = query), 300);
    return () => clearTimeout(timer);
  });
  let filterPage = $state.raw<BrowseSearchPageDto | null>(saved?.filterPage ?? null);
  let filterItems = $state.raw<BrowseSearchPageDto['items']>(saved?.filterItems ?? []);
  let filterLoading = $state(false);
  let filterError = $state<Error | null>(null);
  let filterRevision = 0;
  let filterRequest: AbortController | null = null;
  let collectingMatches = $state(false);
  let selectionRevision = 0;
  let selected = $state<Set<string>>(saved?.selected ?? new Set());
  let excludedSelection = $state<Set<string>>(saved?.excludedSelection ?? new Set());
  // Retain only selected entries and their ancestors across filtered page changes.
  let selectedParents = $state<Map<string, string | null>>(saved?.selectedParents ?? new Map());
  let selectedSizes = $state<Map<string, SelectionSize>>(saved?.selectedSizes ?? new Map());
  let optionsOpen = $state(options?.open ?? false);
  let optionsLoading = $state(false);
  let optionsError = $state('');
  let profileNames = $state<string[]>(options?.profileNames ?? []);
  let appliedProfiles = $state<Schema<'AppliedProfileDto'>[]>([]);
  let baseline = $state<DownloadOptions | null>(options?.baseline ?? null);
  let downloadValues = $state<DownloadOptions | null>(options?.values ?? null);
  const capabilities = { albumFolderEnrichment: false, playlistOutput: false, nameFormat: true };
  let downloadOptions = $derived({ ...(baseline && downloadValues ? buildDownloadOptionsPatch(downloadValues, baseline, capabilities) : {}),
    ...(profileNames.length ? { profileNames: [...profileNames] } : {}) });
  let optionsRequest = 0;
  async function loadOptions() {
    const request = ++optionsRequest;
    optionsLoading = true;
    optionsError = '';
    try {
      const resolved = await daemonApi.resolveUserShareDownloadSettings({ profileNames }, lifetime.signal);
      if (request !== optionsRequest || lifetime.signal.aborted) return;
      const next = downloadOptionsFromResolved(resolved.settings);
      downloadValues = rebaseOptions(baseline, downloadValues ?? next, next);
      baseline = next;
      appliedProfiles = resolved.appliedProfiles;
    } catch (cause) { if (request === optionsRequest && !lifetime.signal.aborted) optionsError = String(cause); }
    finally { if (request === optionsRequest) optionsLoading = false; }
  }
  function refreshViewedOptions() {
    if (!retryKey && source.download && (optionsOpen || baseline || profileNames.length)) void loadOptions();
  }
  let refreshedConfiguration: number | undefined;
  $effect(() => {
    const revision = configurationRevision ?? 0;
    if (!connected) { refreshedConfiguration = undefined; return; }
    if (refreshedConfiguration === revision || retryKey) return;
    refreshedConfiguration = revision;
    untrack(refreshViewedOptions);
  });

  function toggleOptions() {
    optionsOpen = !optionsOpen;
    if (optionsOpen && !optionsLoading && (!baseline || !retryKey)) void loadOptions();
  }
  let mutation = $state<MutationState>({ phase: 'idle' });
  let lastRequest = $state.raw(saved?.lastRequest);
  const requestOptions = $derived(JSON.stringify(downloadOptions));
  const retryKey = $derived(lastRequest?.options === requestOptions ? lastRequest.id : null);
  let available = $state(!saved);
  let availabilityError = $state<Error | null>(null);

  let filterKey = saved?.filterKey ?? '';
  let collapsedFiltered = $state<Set<string>>(saved?.collapsedFiltered ?? new Set());
  const lifetime = new AbortController();
  let treeRows = $derived(flattenTree());
  let filteredRows = $derived(filteredTreeRows(filterItems, collapsedFiltered));
  let filteredEntries = $derived(new Map(filterItems.map(item => [`${item.kind === 'directory' ? 'd' : 'f'}:${item.entryId}`, item])));
  let loadedFiles = $derived.by(() => {
    const entries = new Map<string, BrowseFileEntryDto>();
    for (const pageFiles of filesByDirectory.values()) {
      for (const file of pageFiles) entries.set(String(file.fileId), file);
    }
    return entries;
  });
  let selectionProjection = $derived(projectSelection(selected, excludedSelection, selectionParent, selectionSize));
  let selectedCount = $derived(selectionProjection.total);
  const retryAfterExpiry = $derived(Boolean(retryKey) && isExpired(availabilityError));
  const downloadDisabled = $derived(!connected || (!available && !retryKey)
    || collectingMatches || selectedCount === 0 || mutation.phase === 'pending' || optionsLoading || Boolean(optionsError));

  $effect(() => {
    const check = source.checkExpiry;
    if (!check) return;
    if (!connected) {
      available = false;
      return;
    }
    const request = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    function schedule(deadline: string | null | undefined): boolean {
      const delay = deadline ? Date.parse(deadline) - Date.now() : NaN;
      if (delay > 0) timer = setTimeout(confirm, Math.min(delay, 2_147_483_647));
      return delay > 0;
    }
    async function confirm() {
      available = false;
      try {
        const expiresAt = await check!(request.signal);
        if (request.signal.aborted || isExpired(availabilityError)) return;
        availabilityError = null;
        available = true;
        schedule(expiresAt);
      } catch (cause) {
        if (!request.signal.aborted) availabilityError = browseError(cause);
      }
    }
    // Newly acquired shares were just confirmed by the daemon. Cached pages
    // need one local summary read on return, then one at the reported expiry.
    if (!untrack(() => available) || !schedule(source.expiresAt)) void confirm();
    return () => { clearTimeout(timer); request.abort(); };
  });
  $effect(() => {
    const query = filterQuery;
    const browseId = browse.browseId;
    const key = JSON.stringify([browseId, query]);
    if (untrack(() => key === filterKey && filterPage !== null)) return;
    // A refined query keeps the previous matches on screen (inert) until its own
    // arrive, so the tree is not torn down and rebuilt for every query.
    const refining = query !== '' && untrack(() => filterKey !== '' && JSON.parse(filterKey)[0] === browseId && filterItems.length > 0);
    if (key !== filterKey) collapsedFiltered = new Set();
    filterKey = key;
    filterRequest = null;
    filterRevision++;
    if (!refining) {
      filterPage = null;
      filterItems = [];
    }
    if (!query || !browseId) {
      filterLoading = false;
      filterError = null;
      return;
    }
    filterLoading = true;
    filterError = null;
    const request = new AbortController();
    filterRequest = request;
    void loadFilter(browseId, query, null, true, request.signal);
    return () => request.abort();
  });

  function currentOptions() {
    return { baseline, values: downloadValues, profileNames, open: optionsOpen };
  }

  export function capturePosition(): ShareBrowserPosition {
    const active = document.activeElement;
    const row = active?.closest('[data-keyboard-item]') ?? null;
    return {
      filterText,
      expandedPaths: new Set([...expanded].map(id => directories.get(id)!.displayPath)),
      options: currentOptions(),
      scrollTop: container.closest('main')?.scrollTop ?? 0,
      focusedFilter: container.querySelector('.result-filter-control input') === active,
      focusedPath: container.contains(row) ? row?.querySelector('.share-tree-name')?.getAttribute('title') : null,
      focusedControl: active?.matches('button') ? 'button' : active?.matches('input') ? 'input' : null,
    };
  }

  let restorePosition = Boolean(previous);
  $effect(() => {
    if (!restorePosition || !previous) return;
    // Wait for the new rows before restoring the scroll offset and keyboard focus.
    if (!pageStates.get('root')?.loaded || [...pageStates.values()].some(page => page.loading)) return;
    if (filterQuery && !filterPage && !filterError) return;
    restorePosition = false;
    if (document.activeElement !== document.body) return;
    let target: HTMLElement | null = null;
    if (previous.focusedFilter) target = container.querySelector('.result-filter-control input');
    else if (previous.focusedPath) {
      const row = container.querySelector(`.share-tree-name[title="${CSS.escape(previous.focusedPath)}"]`)?.closest<HTMLElement>('[data-keyboard-item]');
      target = (previous.focusedControl && row?.querySelector<HTMLElement>(previous.focusedControl)) || row || null;
    }
    target?.focus({ preventScroll: true });
    container.closest('main')?.scrollTo({ top: previous.scrollTop, behavior: 'instant' });
  });

  onDestroy(() => {
    lifetime.abort();
    session.snapshot = {
      browseId: browse.browseId,
      directories, childDirectoryIds, filesByDirectory,
      pageStates: new Map([...pageStates].map(([key, page]) => [key, { ...page, loading: false }])),
      expanded, filterText, filterKey, filterPage, filterItems, collapsedFiltered,
      selected, excludedSelection, selectedParents, selectedSizes, lastRequest,
      options: currentOptions(),
    };
  });
  $effect(() => {
    untrack(() => {
      if (!pageStates.get('root')?.loaded) void loadChildren(null, true);
      for (const id of expanded) if (!pageStates.get(id)?.loaded) void loadChildren(id);
    });
  });
  function clearSelection(): void {
    selectionRevision++;
    collectingMatches = false;
    selected = new Set();
    excludedSelection = new Set();
    selectedParents = new Map();
    selectedSizes = new Map();
    mutation = { phase: 'idle' };
    lastRequest = undefined;
  }

  async function loadChildren(parentId: number | string | null, reset = false): Promise<void> {
    const signal = lifetime.signal;
    const key = parentId === null ? 'root' : String(parentId);
    const current = pageStates.get(key) ?? { directoryCursor: null, fileCursor: null, loading: false, loaded: false, error: null };
    if (current.loading || (current.loaded && !reset && !current.directoryCursor && !current.fileCursor)) return;
    const parent = directories.get(key);
    setPageState(key, { ...current, loading: true, error: null });
    try {
      const [directoryPage, filePage] = await Promise.all([
        Number(parent?.directDirectoryCount) === 0 || (current.loaded && !reset && !current.directoryCursor)
          ? Promise.resolve({ items: [] as BrowseDirectoryEntryDto[], nextCursor: null })
          : source.directories(parentId, reset ? null : current.directoryCursor, sharePageSize, signal),
        parentId === null || Number(parent?.directFileCount) === 0 || (current.loaded && !reset && !current.fileCursor)
          ? Promise.resolve({ items: [] as BrowseFileEntryDto[], nextCursor: null })
          : source.files(parentId, reset ? null : current.fileCursor, sharePageSize, signal),
      ]);
      if (signal.aborted) return;
      const nextDirectories = new Map(directories);
      for (const entry of directoryPage.items) nextDirectories.set(String(entry.directoryId), entry);
      directories = nextDirectories;
      childDirectoryIds = new Map(childDirectoryIds).set(key, mergeIds(reset ? [] : childDirectoryIds.get(key) ?? [], directoryPage.items.map((entry) => String(entry.directoryId))));
      filesByDirectory = new Map(filesByDirectory).set(key, mergeFiles(reset ? [] : filesByDirectory.get(key) ?? [], filePage.items));
      setPageState(key, { directoryCursor: directoryPage.nextCursor ?? null, fileCursor: filePage.nextCursor ?? null, loading: false, loaded: true, error: null });
      if (previous) {
        const matches = directoryPage.items.filter(entry => previous.expandedPaths.has(entry.displayPath));
        if (matches.length) expanded = new Set([...expanded, ...matches.map(entry => String(entry.directoryId))]);
      } else if (parentId === null && expanded.size === 0) expanded = new Set(directoryPage.items.map((entry) => String(entry.directoryId)));
      for (const entry of directoryPage.items) {
        if (expanded.has(String(entry.directoryId))) void loadChildren(entry.directoryId);
      }
    } catch (cause) {
      if (!signal.aborted) setPageState(key, { ...current, loading: false, error: browseError(cause) });
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

  function toggleFilteredDirectory(id: string): void {
    const next = new Set(collapsedFiltered);
    if (next.has(id)) next.delete(id); else next.add(id);
    collapsedFiltered = next;
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
    const isCurrent = () => !signal.aborted && revision === filterRevision && browse?.browseId === browseId && query === filterQuery;
    filterLoading = true;
    filterError = null;
    try {
      const page = await source.search(query, cursor, sharePageSize, signal);
      if (!isCurrent()) return;
      filterPage = page;
      filterItems = reset ? page.items : [...filterItems, ...page.items];
    } catch (cause) {
      if (isCurrent()) filterError = browseError(cause);
    } finally {
      if (isCurrent()) filterLoading = false;
    }
  }

  async function selectFiltered(keys: string[], checked: boolean, included = selected, excluded = excludedSelection, promote = false) {
    const revision = ++selectionRevision;
    collectingMatches = false;
    const matchingFolders = new Map<string, number>();
    const direct = keys.filter(key => {
      const row = filteredEntries.get(key);
      if (!checked || row?.kind !== 'directory' || promote && isFolderPartial(key)) return true;
      const count = Number(row.publicMatchingFileCount);
      if (count === Number(row.publicSubtreeFileCount)) return true;
      if (count > 0) matchingFolders.set(key, count);
      return false;
    });
    if (!matchingFolders.size) {
      setSelection(direct, checked, included, excluded);
      return;
    }
    const query = filterQuery;
    const signal = filterRequest?.signal ?? lifetime.signal;
    const parents = new Map(selectedParents), sizes = new Map(selectedSizes);
    function collect(items: BrowseSearchPageDto['items']) {
      for (const item of items) {
        const directory = item.kind === 'directory';
        const key = (directory ? 'd:' : 'f:') + item.entryId;
        if (directory) {
          parents.set(key, item.parentDirectoryId == null ? null : String(item.parentDirectoryId));
          continue;
        }
        if (item.visibility !== 'public') continue;
        const parent = String(item.directoryId);
        let matched = false;
        for (let id: string | null | undefined = parent; id != null; id = parents.get('d:' + id)) {
          const ancestor = 'd:' + id;
          const count = matchingFolders.get(ancestor);
          if (count !== undefined) {
            matched = true;
            matchingFolders.set(ancestor, count - 1);
          }
        }
        if (matched) {
          parents.set(key, parent);
          sizes.set(key, { count: 1, bytes: item.fileSize ?? 0 });
          direct.push(key);
        }
      }
    }
    try {
      collect(filterItems);
      let cursor = filterPage?.nextCursor;
      while ([...matchingFolders.values()].some(count => count > 0)) {
        if (!cursor) throw new Error('The filtered folder could not be completely read.');
        collectingMatches = true;
        const page = await source.search(query, cursor, sharePageSize, signal);
        if (signal.aborted || revision !== selectionRevision || query !== filterQuery) return;
        collect(page.items);
        cursor = page.nextCursor;
      }
      selectedParents = parents;
      selectedSizes = sizes;
      // A selected but excluded directory owns the output tree. Explicit file
      // selections inside it restore just the matches, including nested paths.
      const roots = [...matchingFolders.keys()].filter(key => !included.has(key) && !inheritedSelection(key, included, excluded, selectionParent));
      setSelection(direct, true, new Set([...included, ...roots]), new Set([...excluded, ...roots]));
    } catch (cause) {
      if (!signal.aborted && revision === selectionRevision && query === filterQuery) filterError = browseError(cause);
    } finally {
      if (revision === selectionRevision) collectingMatches = false;
    }
  }

  function setSelection(keys: string[], checked: boolean, included = selected, excluded = excludedSelection): void {
    const next = new Set(included), exclusions = new Set(excluded);
    const valid = keys.filter(key => !checked || selectionCount(key) > 0);
    const folders = new Set(valid.filter(key => key.startsWith('d:')));
    const noExclusions = new Set<string>();
    const covered = (key: string) => folders.size > 0 && inheritedSelection(key, folders, noExclusions, selectionParent);
    for (const rules of [next, exclusions]) {
      for (const key of rules) if (covered(key)) rules.delete(key);
    }
    for (const key of valid) {
      if (covered(key)) continue;
      const inherited = inheritedSelection(key, next, exclusions, selectionParent);
      next.delete(key);
      exclusions.delete(key);
      if (checked && !inherited) next.add(key);
      if (!checked && inherited) exclusions.add(key);
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
    selectedSizes = new Map([...next, ...exclusions].map(key => [key, selectionSize(key)]));
    lastRequest = undefined;
    mutation = { phase: 'idle' };
  }

  function selectionSize(key: string): SelectionSize {
    const id = key.slice(2);
    const filtered = filteredEntries.get(key);
    if (key.startsWith('d:')) {
      const directory = directories.get(id);
      if (directory) return { count: Number(directory.recursiveFileCount), bytes: directory.recursiveFileBytes };
      if (filtered) return { count: Number(filtered.publicSubtreeFileCount), bytes: filtered.publicSubtreeBytes ?? 0 };
    } else {
      const file = loadedFiles.get(id);
      const visibility = file?.visibility ?? filtered?.visibility;
      if (visibility != null) return {
        count: visibility === 'public' ? 1 : 0,
        bytes: visibility === 'public' ? file?.file.size ?? filtered?.fileSize ?? 0 : 0,
      };
    }
    return selectedSizes.get(key) ?? { count: 0, bytes: 0 };
  }

  function selectionCount(key: string): number { return selectionSize(key).count; }

  function keyDirectoryId(key: string): string | null {
    if (key.startsWith('d:')) return key.slice(2);
    const fileId = key.slice(2);
    const loaded = loadedFiles.get(fileId);
    if (loaded !== undefined) return String(loaded.directoryId);
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
  function isSelectionChecked(key: string): boolean {
    if (selectionCount(key) === 0) return false;
    if (!key.startsWith('d:')) return !excludedSelection.has(key) && (selected.has(key) || hasSelectedAncestor(key));
    const total = selectionCount(key);
    return total > 0 && selectionProjection.subtreeCount(key) === total;
  }

  function isFolderPartial(key: string): boolean {
    const count = selectionProjection.subtreeCount(key);
    return count > 0 && count < selectionCount(key);
  }

  function beginRange(additive: boolean) {
    const included = additive ? selected : new Set<string>();
    const excluded = additive ? excludedSelection : new Set<string>();
    const parents = selectedParents, sizes = selectedSizes;
    return (rows: HTMLElement[], checked: boolean) => {
      selectedParents = parents;
      selectedSizes = sizes;
      void selectFiltered(rows.map(row => row.dataset.keyboardItem!), checked, included, excluded);
    };
  }

  async function downloadSelection(): Promise<void> {
    if (!source.download || downloadDisabled) return;
    const browseId = browse.browseId;
    const signal = lifetime.signal;
    const selections: Schema<'UserShareSelectionDto'>[] = [...selected].map((key) => key.startsWith('d:')
      ? { kind: 'directory' as const, directoryId: key.slice(2) }
      : { kind: 'file' as const, fileId: key.slice(2) });
    const requestId = retryKey ?? randomUUID();
    lastRequest = { id: requestId, options: requestOptions };
    mutation = { phase: 'pending', label: `Requesting ${selectedCount} shared file${selectedCount === 1 ? '' : 's'}…` };
    try {
      const response = await source.download(requestId, selections, [...excludedSelection].filter(key => key.startsWith('f:')).map(key => key.slice(2)), [...excludedSelection].filter(key => key.startsWith('d:')).map(key => key.slice(2)), downloadOptions);
      // Selection changes invalidate the receipt's presentation, not the accepted
      // download. Do not cancel a submitted command or clear a newer retry key.
      if (signal.aborted || browse?.browseId !== browseId || retryKey !== requestId) return;
      const resolved = Number(response.resolution.totalPublicFiles);
      const skipped = Number(response.resolution.lockedBranchesSkipped);
      mutation = {
        phase: resolved ? skipped ? 'partially-succeeded' : 'succeeded' : 'rejected',
        label: resolved ? `${resolved} download${resolved === 1 ? '' : 's'} requested` : 'Nothing downloadable',
        detail: skipped ? `${skipped} locked branch${skipped === 1 ? '' : 'es'} skipped` : undefined,
      };
      lastRequest = undefined;
    } catch (cause) {
      if (signal.aborted || browse?.browseId !== browseId || retryKey !== requestId) return;
      const error = browseError(cause);
      if (isExpired(error)) {
        lastRequest = undefined;
        mutation = { phase: 'idle' };
      } else {
        mutation = { phase: 'rejected', label: 'Download request failed', detail: error.message };
      }
    }
  }

  function isExpired(cause: unknown): boolean {
    return cause instanceof DaemonApiError && cause.code === 'browse-expired';
  }
  function browseError(cause: unknown): Error {
    const error = cause instanceof Error ? cause : new Error(String(cause));
    if (isExpired(error)) {
      available = false;
      availabilityError = error;
    }
    return error;
  }
  function mergeIds(existing: string[], incoming: string[]): string[] { return [...new Set([...existing, ...incoming])]; }
  function mergeFiles(existing: BrowseFileEntryDto[], incoming: BrowseFileEntryDto[]): BrowseFileEntryDto[] { const values = new Map(existing.map((item) => [String(item.fileId), item])); for (const item of incoming) values.set(String(item.fileId), item); return [...values.values()]; }
</script>

<svelte:window ononline={refreshViewedOptions} onkeydown={(event) => {
  if (event.key === 'Escape' && optionsOpen) { event.preventDefault(); optionsOpen = false; }
}} />

<div bind:this={container} class="share-browser" class:read-only={!source.download} use:keyboardList={{
  scope: `${browse.browseId}:${filterQuery}`,
  enabled: true,
  revision: [treeRows, filteredRows, filterLoading],
  beginRange: source.download && mutation.phase !== 'pending' ? beginRange : undefined,
  loadPage: key => {
    if (filterQuery) {
      if (browse) void loadFilter(browse.browseId, filterQuery, filterPage?.nextCursor ?? null, false);
    } else void loadChildren(key === 'root' ? null : key);
  },
}}>
      {#if availabilityError}
        <Notice variant="unavailable">{isExpired(availabilityError)
          ? (retryAfterExpiry ? 'These shares have expired. Retry the last download to recover its receipt.'
            : 'These shares have expired. Refresh to choose files from the current list.')
          : availabilityError.message}</Notice>
      {/if}
      <MutationStatus state={mutation}/>
      <div class="shares-overview">
        <div class="shares-title-row">
          <div>
            <span class:peer-identity={Boolean(userActions)} data-testid="shares-username">{#if userActions}<UsernameLink username={browse.username} actions={userActions} />{:else}<span class="shares-username">{browse.username}</span>{/if}</span>
            <strong>{formatBytes(browse.totalFileBytes)} shared</strong>
          </div>
          <div class="shares-filter">
            <ResultFilterControl bind:value={filterText} placeholder="Filter shared files…" ariaLabel="Filter user shares">
              <!-- The count lives inside the field so the field never changes size. -->
              {#if filterQuery && filterPage && !filterLoading}
                {@const matchingCount = Number(filterPage.publicMatchingFileCount) + Number(filterPage.lockedMatchingFileCount)}
                <span class="shares-filter-count">{formatCount(matchingCount)} matching {matchingCount === 1 ? 'file' : 'files'}</span>
              {/if}
            </ResultFilterControl>
          </div>
        </div>


        {#if source.download && selectedCount && optionsOpen}
          <button type="button" class="floating-options-backdrop" aria-label="Close download options" onclick={() => optionsOpen = false}></button>
          <section class="popover-panel floating-download-options-popover" data-shortcuts-blocked="true" inert={mutation.phase === 'pending'} aria-label="Download options">
            <header><strong>Download options</strong><div class="download-options-actions"><ProfilePicker value={profileNames} applied={appliedProfiles} onchange={(names) => { profileNames = names; void loadOptions(); }} /><button type="button" aria-label="Close download options" onclick={() => optionsOpen = false}>×</button></div></header>
            {#if optionsLoading}<LoadingIndicator label="Loading download settings" />
            {:else if optionsError}<p role="alert">{optionsError}</p><button type="button" onclick={() => void loadOptions()}>Retry</button>
            {:else if downloadValues}<DownloadOptionsPanel bind:value={downloadValues} {capabilities} compact />{/if}
          </section>
        {/if}

        {#if filterQuery}
          {#if filterError}
            <div class="resource-state-notice unavailable" role="alert">
              <span class="resource-state-dot"></span>
              <span class="resource-state-copy">
                <strong>Share filter failed</strong>
                <small>{filterError.message}</small>
              </span>
              <button class="button" type="button" disabled={filterLoading}
                onclick={() => browse && void loadFilter(browse.browseId, filterQuery, filterPage?.nextCursor ?? null, !filterPage)}>Retry</button>
            </div>
          {/if}
          {#if filterLoading && !filterItems.length}
            <LoadingIndicator label="Filtering shares" />
          {/if}
          {#if collectingMatches}<LoadingIndicator label="Selecting matching files" />{/if}
          {#if filterItems.length}
            <div class="panel inset share-tree" class:refreshing={filterLoading} inert={collectingMatches || filterLoading} aria-busy={collectingMatches || filterLoading} aria-label={`${browse.username} filtered shared files`}>
              {#each filteredRows as { entry: item, depth } (`${item.kind}:${item.entryId}`)}
                {@const key = `${item.kind === 'directory' ? 'd' : 'f'}:${item.entryId}`}
                {#if item.kind === 'directory'}
                  {@const count = Number(item.totalSubtreeFileCount ?? item.publicSubtreeFileCount ?? 0)}
                  <div data-keyboard-item={key} data-range-disabled={selectionCount(key) === 0 || undefined} tabindex="-1" class="share-tree-row folder" style={`--share-depth:${depth}`} title={isInheritedSelection(key) && isSelectionChecked(key) ? 'Included by a selected parent folder' : undefined}>
                    {#if item.visibility === 'locked'}<LockedIndicator />{:else if source.download}<input type="checkbox" indeterminate={isFolderPartial(key)} aria-label={`Select ${item.displayPath}`} checked={isSelectionChecked(key)} disabled={!Number(item.publicMatchingFileCount) && !isFolderPartial(key) && !isSelectionChecked(key)}
                      onchange={(event) => {
                        event.currentTarget.checked = isSelectionChecked(key);
                        event.currentTarget.indeterminate = isFolderPartial(key);
                        void selectFiltered([key], !isSelectionChecked(key), selected, excludedSelection, true);
                      }}/>{/if}
                    <button type="button" class="share-tree-folder-button" data-keyboard-disclosure aria-label={`${collapsedFiltered.has(String(item.entryId)) ? 'Expand' : 'Collapse'} ${item.name}`} aria-expanded={!collapsedFiltered.has(String(item.entryId))} onclick={() => toggleFilteredDirectory(String(item.entryId))}>
                      <span class="share-tree-toggle" class:expanded={!collapsedFiltered.has(String(item.entryId))} aria-hidden="true"><svg viewBox="0 0 16 16"><path d="m6 3.5 4.5 4.5L6 12.5"/></svg></span>
                      <span class="share-tree-icon"><Icon name="folder"/></span>
                      <strong class="share-tree-name" title={item.displayPath}>{item.name}</strong>
                      {#if item.visibility === 'mixed'}<span class="share-visibility mixed">Mixed</span>{/if}
                      <span class="share-tree-folder-meta">{formatCount(count)} {item.totalSubtreeFileCount == null ? 'public ' : ''}{count === 1 ? 'file' : 'files'}</span>
                      <span class="share-tree-size">{formatBytes(item.totalSubtreeBytes ?? item.publicSubtreeBytes)}</span>
                    </button>
                  </div>
                {:else}
                  <label data-keyboard-item={key} data-range-toggle data-range-disabled={selectionCount(key) === 0 || undefined} tabindex="-1" class="share-tree-row file" style={`--share-depth:${depth}`} title={isInheritedSelection(key) && isSelectionChecked(key) ? 'Included by a selected parent folder' : undefined}>
                    {#if item.visibility === 'locked'}<LockedIndicator />{:else if source.download}<input type="checkbox" checked={isSelectionChecked(key)}  onchange={(event) => setSelection([key], (event.currentTarget as HTMLInputElement).checked)}/>{/if}
                    <span class="share-tree-toggle-spacer"></span>
                    <span class="share-tree-icon"><Icon name={fileTypeIcon({ extension: item.extension, filename: item.name })}/></span>
                    <strong class="share-tree-name" title={item.displayPath}>{item.name}</strong>
                    {#if item.visibility === 'mixed'}<span class="share-visibility mixed">Mixed</span>{/if}
                    <span class="share-tree-size">{formatBytes(item.fileSize)}</span>
                  </label>
                {/if}
              {/each}
            </div>
          {/if}
          {#if filterPage && !filterLoading && !filterError && !filterItems.length}
            <EmptyState class="search-results-empty" icon="search" title="No matching files" />
          {/if}
          {#if filterPage?.nextCursor}<div data-keyboard-page="filter" data-loading={filterLoading} data-failed={Boolean(filterError)}><LoadMoreButton loading={filterLoading} onclick={() => browse && void loadFilter(browse.browseId, filterQuery, filterPage?.nextCursor ?? null, false)}/></div>{/if}
        {:else if treeRows.length}
          <div class="panel inset share-tree" aria-label={`${browse.username} shared files`}>
            {#each treeRows as row (row.kind === 'page' ? `p:${row.parentKey}` : row.kind === 'directory' ? `d:${row.entry.directoryId}` : `f:${row.entry.fileId}`)}
              {#if row.kind === 'directory'}
                {@const id = String(row.entry.directoryId)}
                {@const key = `d:${id}`}
                {@const count = Number(row.entry.totalRecursiveFileCount ?? row.entry.recursiveFileCount)}
                <div data-keyboard-item={key} data-range-disabled={selectionCount(key) === 0 || undefined} tabindex="-1" class="share-tree-row folder" style={`--share-depth:${row.depth}`}
                  title={isInheritedSelection(key) && isSelectionChecked(key) ? 'Included by a selected parent folder' : undefined}>
                  {#if row.entry.visibility === 'locked'}<LockedIndicator />{:else if source.download}<input type="checkbox" indeterminate={isFolderPartial(key)}
                    checked={isSelectionChecked(key)} aria-label={`Select ${row.entry.displayPath}`}
                    onchange={(event) => setSelection([key], (event.currentTarget as HTMLInputElement).checked)}/>{/if}
                  <button type="button" class="share-tree-folder-button" data-keyboard-disclosure
                    aria-label={`${expanded.has(id) ? 'Collapse' : 'Expand'} ${row.entry.name}`}
                    aria-expanded={expanded.has(id)} onclick={() => void toggleDirectory(row.entry)}>
                    <span class:expanded={expanded.has(id)} class="share-tree-toggle" aria-hidden="true">
                      <svg viewBox="0 0 16 16"><path d="m6 3.5 4.5 4.5L6 12.5"/></svg>
                    </span>
                    <span class="share-tree-icon"><Icon name="folder"/></span>
                    <strong class="share-tree-name" title={row.entry.displayPath}>{row.entry.name}</strong>
                    {#if row.entry.visibility === 'mixed'}
                      <span class="share-visibility mixed">Mixed</span>
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
                      <button class="button" type="button" disabled={row.page.loading} onclick={() => void loadChildren(row.parentKey === 'root' ? null : row.parentKey)}>Retry</button>
                    </div>
                  {:else if row.page.loading}
                    <LoadingIndicator compact delayMs={250} label="Loading directory" />
                  {:else}
                    <LoadMoreButton onclick={() => void loadChildren(row.parentKey === 'root' ? null : row.parentKey)}/>
                  {/if}
                </div>
              {:else}
                {@const key = `f:${row.entry.fileId}`}
                <label data-keyboard-item={key} data-range-toggle data-range-disabled={selectionCount(key) === 0 || undefined} tabindex="-1" class="share-tree-row file" style={`--share-depth:${row.depth}`}
                  title={isInheritedSelection(key) && isSelectionChecked(key) ? 'Included by a selected parent folder' : undefined}>
                  {#if row.entry.visibility === 'locked'}<LockedIndicator />{:else if source.download}<input type="checkbox" checked={isSelectionChecked(key)}
                    onchange={(event) => setSelection([key], (event.currentTarget as HTMLInputElement).checked)}/>{/if}
                  <span class="share-tree-toggle-spacer"></span>
                  <span class="share-tree-icon">
                    <Icon name={fileTypeIcon({ extension: row.entry.file.extension, filename: row.entry.file.name })}/>
                  </span>
                  <strong class="share-tree-name" title={row.path}>{row.entry.file.name}</strong>
                  <span class="share-tree-size">{formatBytes(row.entry.file.size)}</span>
                </label>
              {/if}
            {/each}
          </div>
        {:else if pageStates.get('root')?.loaded && !pageStates.get('root')?.error}
          <EmptyState class="search-results-empty" icon="folder" title="No shared files" />
        {/if}

      </div>
  {#if source.download}<SelectionToolbar {selectedCount} detail={`${formatBytes(selectionProjection.byteDelta.toString())} selected`}
    floatingLabel={retryAfterExpiry ? 'Retry download' : undefined} actionDisabled={downloadDisabled}
    optionsOpen={optionsOpen} optionsCustomized={Object.keys(downloadOptions).length > 0} onoptions={toggleOptions}
    onclear={clearSelection} onaction={() => void downloadSelection()}/>{/if}
</div>

<style>
  .read-only .share-tree-row.file { cursor: default; }
</style>
