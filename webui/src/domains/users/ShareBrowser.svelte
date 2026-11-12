<script lang="ts">
  import { keyboardList } from '../../lib/keyboard';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { onDestroy, untrack } from 'svelte';
  import type { BrowseDirectoryEntryDto, BrowseFileEntryDto, BrowseSearchPageDto, Schema } from '../../api/types';
  import { formatBytes, formatCount } from '../../app/format';
  import Icon from '../../components/Icon.svelte';
  import ResultFilterControl from '../../components/ResultFilterControl.svelte';
  import SelectionToolbar from '../../components/SelectionToolbar.svelte';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import MutationStatus, { type MutationState } from '../../components/MutationStatus.svelte';
  import { fileTypeIcon } from '../presentation/file-types';
  import { filteredTreeRows } from './filter-tree';
  import { inheritedSelection, projectSelection } from './selection';

  import type { PageState, ShareBrowserSession } from './session';

  import { randomUUID } from '../../app/uuid';
  import type { ShareBrowserSource } from './share-source';
  interface TreeRowDirectory { kind: 'directory'; entry: BrowseDirectoryEntryDto; depth: number; }
  interface TreeRowFile { kind: 'file'; entry: BrowseFileEntryDto; depth: number; path: string; }
  interface TreeRowPage { kind: 'page'; parentKey: string; page: PageState; depth: number; }
  type TreeRow = TreeRowDirectory | TreeRowFile | TreeRowPage;

  let { source: input, session = {} }: { source: ShareBrowserSource; session?: ShareBrowserSession } = $props();
  // The parent keys the browser by immutable generation.
  const source = untrack(() => input);
  const browse = source;
  const sharePageSize = 1_000;
  const saved = untrack(() => session.snapshot?.browseId === source.browseId ? session.snapshot : undefined);
  let directories = $state<Map<string, BrowseDirectoryEntryDto>>(saved?.directories ?? new Map());
  let childDirectoryIds = $state<Map<string, string[]>>(saved?.childDirectoryIds ?? new Map());
  let filesByDirectory = $state<Map<string, BrowseFileEntryDto[]>>(saved?.filesByDirectory ?? new Map());
  let pageStates = $state<Map<string, PageState>>(saved?.pageStates ?? new Map());
  let expanded = $state<Set<string>>(saved?.expanded ?? new Set());
  let filterText = $state(saved?.filterText ?? '');
  let filterPage = $state<BrowseSearchPageDto | null>(saved?.filterPage ?? null);
  let filterItems = $state<BrowseSearchPageDto['items']>(saved?.filterItems ?? []);
  let filterLoading = $state(false);
  let filterError = $state<Error | null>(null);
  let filterRevision = 0;
  let filterRequest: AbortController | null = null;
  let selected = $state<Set<string>>(saved?.selected ?? new Set());
  let excludedSelection = $state<Set<string>>(saved?.excludedSelection ?? new Set());
  // Retain only selected entries and their ancestors across filtered page changes.
  let selectedParents = $state<Map<string, string | null>>(saved?.selectedParents ?? new Map());
  let selectedDirectoryCounts = $state<Map<string, number>>(saved?.selectedDirectoryCounts ?? new Map());
  let selectedDirectoryTotals = $state<Map<string, number>>(saved?.selectedDirectoryTotals ?? new Map());
  let selectedFileIsPublic = $state<Map<string, boolean>>(saved?.selectedFileIsPublic ?? new Map());
  let mutation = $state<MutationState>({ phase: 'idle' });
  let commitKey: string | null = null;
  let filterKey = saved?.filterKey ?? '';
  let collapsedFiltered = $state<Set<string>>(saved?.collapsedFiltered ?? new Set());
  const lifetime = new AbortController();
  let treeRows = $derived(flattenTree());
  let filteredRows = $derived(filteredTreeRows(filterItems, collapsedFiltered));
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
  $effect(() => {
    const query = filterText.trim();
    const browseId = browse.browseId;
    const key = JSON.stringify([browseId, query]);
    if (untrack(() => key === filterKey && filterPage !== null)) return;
    if (key !== filterKey) collapsedFiltered = new Set();
    filterKey = key;
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
    session.snapshot = {
      browseId: browse.browseId,
      directories, childDirectoryIds, filesByDirectory,
      pageStates: new Map([...pageStates].map(([key, page]) => [key, { ...page, loading: false }])),
      expanded, filterText, filterKey, filterPage, filterItems, collapsedFiltered,
      selected, excludedSelection, selectedParents, selectedDirectoryCounts, selectedDirectoryTotals, selectedFileIsPublic,
    };
  });
  $effect(() => {
    untrack(() => {
      if (!pageStates.get('root')?.loaded) void loadChildren(null, true);
      for (const id of expanded) if (!pageStates.get(id)?.loaded) void loadChildren(id);
    });
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

  function toggleFilteredDirectory(id: string, open = collapsedFiltered.has(id)): void {
    const next = new Set(collapsedFiltered);
    if (open) next.delete(id); else next.add(id);
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
    const isCurrent = () => !signal.aborted && revision === filterRevision && browse?.browseId === browseId && query === filterText.trim();
    filterLoading = true;
    filterError = null;
    try {
      const page = await source.search(query, cursor, sharePageSize, signal);
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
    if (!source.download || !selected.size || mutation.phase === 'pending') return;
    const browseId = browse.browseId;
    const signal = lifetime.signal;
    const selections: Schema<'UserShareSelectionDto'>[] = [...selected].map((key) => key.startsWith('d:')
      ? { kind: 'directory' as const, directoryId: key.slice(2) }
      : { kind: 'file' as const, fileId: key.slice(2) });
    const requestId = commitKey ??= randomUUID();
    mutation = { phase: 'pending', label: `Requesting ${downloadableCount} shared file${downloadableCount === 1 ? '' : 's'}…` };
    try {
      const response = await source.download(requestId, selections, [...excludedSelection].filter(key => key.startsWith('f:')).map(key => key.slice(2)), [...excludedSelection].filter(key => key.startsWith('d:')).map(key => key.slice(2)));
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

  function asError(cause: unknown): Error { return cause instanceof Error ? cause : new Error(String(cause)); }
  function mergeIds(existing: string[], incoming: string[]): string[] { return [...new Set([...existing, ...incoming])]; }
  function mergeFiles(existing: BrowseFileEntryDto[], incoming: BrowseFileEntryDto[]): BrowseFileEntryDto[] { const values = new Map(existing.map((item) => [String(item.fileId), item])); for (const item of incoming) values.set(String(item.fileId), item); return [...values.values()]; }
</script>

<div class="share-browser" class:read-only={!source.download} use:keyboardList={{
  scope: `${browse.browseId}:${filterText.trim()}`,
  enabled: true,
  revision: [treeRows, filteredRows, filterLoading],
  toggle: source.download ? key => toggleSelection(key, !isSelectionChecked(key)) : undefined,
  horizontal: (direction, key) => {
    if (!key?.startsWith('d:')) return;
    const id = key.slice(2);
    if (filterText.trim()) toggleFilteredDirectory(id, direction === 1);
    else {
      const directory = directories.get(id);
      if (directory && expanded.has(id) !== (direction === 1)) void toggleDirectory(directory);
    }
  },
  loadPage: key => {
    if (filterText.trim()) {
      if (browse) void loadFilter(browse.browseId, filterText.trim(), filterPage?.nextCursor ?? null, false);
    } else void loadChildren(key === 'root' ? null : key);
  },
}}>
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
        {#if source.download}<SelectionToolbar {selectedCount} floatingLabel={`Download ${downloadableCount}`}
          detail={lockedCount ? `${selectedCount} selected · ${lockedCount} locked` : undefined}
          actionDisabled={downloadableCount === 0 || mutation.phase === 'pending'}
          onclear={clearSelection} onaction={() => void downloadSelection()}/>{/if}

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
                    {#if source.download}<input type="checkbox" use:indeterminate={isFolderPartial(key)} aria-label={`Select ${item.displayPath}`} checked={isSelectionChecked(key)}  onchange={(event) => toggleSelection(key, (event.currentTarget as HTMLInputElement).checked)}/>{/if}
                    <button type="button" class="share-tree-folder-button" aria-label={`${collapsedFiltered.has(String(item.entryId)) ? 'Expand' : 'Collapse'} ${item.name}`} aria-expanded={!collapsedFiltered.has(String(item.entryId))} onclick={() => toggleFilteredDirectory(String(item.entryId))}>
                      <span class="share-tree-toggle" class:expanded={!collapsedFiltered.has(String(item.entryId))} aria-hidden="true"><svg viewBox="0 0 16 16"><path d="m6 3.5 4.5 4.5L6 12.5"/></svg></span>
                      <span class="share-tree-icon"><Icon name="folder"/></span>
                      <strong class="share-tree-name" title={item.displayPath}>{item.name}</strong>
                      {#if item.visibility !== 'public'}<span class={`share-visibility ${item.visibility}`}>{item.visibility === 'locked' ? 'Locked' : 'Mixed'}</span>{/if}
                      <span class="share-tree-folder-meta">{formatCount(count)} {item.totalSubtreeFileCount == null ? 'public ' : ''}{count === 1 ? 'file' : 'files'}</span>
                      <span class="share-tree-size">{formatBytes(item.totalSubtreeBytes ?? item.publicSubtreeBytes)}</span>
                    </button>
                  </div>
                {:else}
                  <label data-keyboard-item={key} tabindex="-1" class="share-tree-row file" style={`--share-depth:${depth}`} title={isInheritedSelection(key) && isSelectionChecked(key) ? 'Included by a selected parent folder' : undefined}>
                    {#if source.download}<input type="checkbox" checked={isSelectionChecked(key)}  onchange={(event) => toggleSelection(key, (event.currentTarget as HTMLInputElement).checked)}/>{/if}
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
            <div class="search-results-empty"><strong>No matching files</strong></div>
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
                  {#if source.download}<input type="checkbox" use:indeterminate={isFolderPartial(key)}
                    checked={isSelectionChecked(key)} aria-label={`Select ${row.entry.displayPath}`}
                    onchange={(event) => toggleSelection(key, (event.currentTarget as HTMLInputElement).checked)}/>{/if}
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
                  {#if source.download}<input type="checkbox" checked={isSelectionChecked(key)}
                    onchange={(event) => toggleSelection(key, (event.currentTarget as HTMLInputElement).checked)}/>{/if}
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
</div>

<style>
  .read-only .share-tree-row.file { cursor: default; }
</style>
