<script lang="ts">
  import { keyboardList } from '../../lib/keyboard';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import type { JobStateDto, Schema } from '../../api/types';
  import { searchViewUnavailable } from './controller';
  import { allSearchResultsSelected, selectionUsesEarlierRevision as usesEarlierRevision, type AggregateOption } from './selection';
  import { DaemonApiError } from '../../api/http';
  import { formatCount, formatCountWithNoun, humanizeStateValue } from '../../app/format';
  import FileItemCard from '../../components/items/FileItemCard.svelte';
  import FolderItemCard from '../../components/items/FolderItemCard.svelte';
  import SelectionToolbar from '../../components/SelectionToolbar.svelte';
  import Icon from '../../components/Icon.svelte';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import PeerItemGroup from '../../components/items/PeerItemGroup.svelte';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import DownloadOptionsPanel from '../../components/DownloadOptionsPanel.svelte';
  import ResultFilterControl from '../../components/ResultFilterControl.svelte';
  import SearchConditionPills from '../../components/SearchConditionPills.svelte';
  import SearchConfigPanel from '../../components/SearchConfigPanel.svelte';
  import { anchoredPopover } from '../../lib/anchored-menu';
  import { hasAppliedConditions } from './applied-conditions';
  import { readSearchConditions, searchConditionsPatch, searchExpectedLength, mergeSearchSettings } from './config-mapping';
  import { createEmptySearchConditions, type SearchConditions } from './config-state';
  import type { DownloadOptions } from '../jobs/download-options';
  import { buildSearchSelectionDownloadPatch, searchDownloadOptionDefaults, type SearchDownloadOptionDefaults } from './download-options';
  import type { UserLinkActions } from '../../app/navigation';
  import { audioAttributes as audio, type FolderItemFile } from '../presentation/items';
  import { directoryTotals, searchModeFamily, searchModeLabel } from './model';
  import type { SearchRow, SearchViewState } from './controller';

  type OrdinaryRow = Extract<SearchRow, { kind: 'file' | 'directory' }>;

  interface Props {
    userActions: UserLinkActions;
    daemonOffline?: boolean;
    viewState: SearchViewState;
    liveJob?: JobStateDto | null;
    liveJobError?: Error | null;
    removeAction?: 'cancel' | 'archive' | null;
    removePending?: boolean;
    actionError?: Error | null;
    onremove: () => void;
    searchAgainAvailable?: boolean;
    searchAgainPending?: boolean;
    isselected: (ref: string, parentRef?: string) => boolean;
    selectedCount: number;
    ontoggle: (ref: string, selected: boolean, parentRef?: string) => void;
    onselectfolder: (directoryRef: string, relativePath: string, selected: boolean) => void;
    isfolderselected: (directoryRef: string, relativePath: string) => boolean;
    onselectall: (selected: boolean) => void;
    ondownload: (options?: Schema<'SubmissionOptionsDto'>) => void;
    onresolvesettings: () => Promise<Schema<'ResolveSearchViewSelectionSettingsResponseDto'>>;
    onloaddefinition: (viewId: string) => Promise<Schema<'SearchViewDefinitionDto'>>;
    onrefine: (request: Schema<'CreateSearchViewRequestDto'>, resetSelection?: boolean) => Promise<boolean>;
    onsort: (order: Schema<'ServerSearchViewSortOrder'>) => Promise<boolean>;
    onloadmore: () => void;
    onretry: () => void;
    onloaddirectory: (ref: string) => void;
    onretrievedirectory: (ref: string) => void;
    onopenoptions: (ref: string) => void;
    oncloseoptions: () => void;
    onloadoptions: () => void;
    onchooseoption: (groupRef: string, option: AggregateOption) => void;
    ontogglealbumfile: (groupRef: string, fileRef: string, selected: boolean) => void;
    onviewdownloads: (submissionId: string) => void;
    onback: () => void;
    onsearchagain: () => void;
  }

  let {
    userActions,
    daemonOffline = false,
    viewState,
    liveJob = null,
    liveJobError = null,
    removeAction = null,
    removePending = false,
    actionError = null,
    onremove,
    searchAgainAvailable = true,
    searchAgainPending = false,
    isselected,
    selectedCount,
    ontoggle,
    onselectfolder,
    isfolderselected,
    onselectall,
    ondownload,
    onresolvesettings,
    onloaddefinition,
    onrefine,
    onsort,
    onloadmore,
    onretry,
    onloaddirectory,
    onretrievedirectory,
    onopenoptions,
    oncloseoptions,
    onloadoptions,
    onchooseoption,
    ontogglealbumfile,
    onviewdownloads,
    onback,
    onsearchagain,
  }: Props = $props();

  let status = $derived(liveJob?.lifecycle ?? null);
  let searching = $derived((status ?? viewState.job)?.lifecycleState !== 'Terminal'
    && (status ?? viewState.job)?.activityPhase === 'Searching');
  let filterText = $state('');
  let sort = $state<'relevance' | 'speed' | 'queue' | 'size' | 'count' | 'name'>('relevance');
  let sizeDirection = $state<'asc' | 'desc'>('desc');
  let conditions = $state<SearchConditions>(createEmptySearchConditions());
  let baseline = $state<Schema<'SearchViewDefinitionDto'> | null>(null);
  let baselineConditions = $state<SearchConditions | null>(null);
  let configurationJobId = $state<string | null>(null);
  let configurationLoading = $state(false);
  let configurationError = $state<Error | null>(null);
  let conditionsOpen = $state(false);
  let conditionsButton = $state<HTMLButtonElement>();
  let conditionsPopover = $state<HTMLElement>();
  let lastRequestedFingerprint = $state<string | null>(null);
  let localRefinementError = $state<Error | null>(null);
  let configurationRequest = 0;
  let refinementRequest = 0;
  let appliedProjectionFingerprint = '';
  let filterEdited = false;
  let sortEdited = false;
  let genericMode = $derived(viewState.draft?.resultMode === 'generic');
  const retainedResultNotices: Partial<Record<Schema<'SearchViewRetentionState'>, { title: string; detail: string }>> = {
    Pruned: { title: 'Search results were pruned',
      detail: 'Saved results were removed by retention. Search again to get fresh results.' },
    Incomplete: { title: 'Search results are incomplete',
      detail: 'Some results could not be retained. Only available results are shown. Search again to get fresh results.' },
    Interrupted: { title: 'Search was interrupted',
      detail: 'Only results retained before the interruption are available. Search again to get fresh results.' },
  };
  let retentionNotice = $derived(viewState.summary ? retainedResultNotices[viewState.summary.retentionState] ?? null : null);
  let viewUnavailable = $derived(!daemonOffline && (searchViewUnavailable(viewState)
    || (configurationError instanceof DaemonApiError && configurationError.status === 404)));
  let hasBareFileGroups = $derived(genericMode && viewState.rows.some(row =>
    row.kind === 'directory' && row.value.ref.folderPath.length === 0));
  let selectionUsesEarlierRevision = $derived(usesEarlierRevision(viewState));
  let selectionReadOnly = $derived(selectionUsesEarlierRevision || viewState.selectionNeedsReview);
  // Generic directory jobs group transfers internally. For a fully accepted
  // revision-bound selection, report the public file count the user reviewed.
  let mutationDownloadCount = $derived(viewState.receipt && genericMode
    && Number(viewState.receipt.skippedCount) === 0
    && Number(viewState.receipt.rejectedCount) === 0
    && viewState.selectionCounts
    && String(viewState.selectionCounts.revision) === String(viewState.receipt.viewRevision)
    ? viewState.selectionCounts.publicFileCount
    : viewState.receipt?.submittedCount ?? 0);
  let aggregateMode = $derived(viewState.draft?.resultMode?.includes('aggregate') ?? false);
  let refinementPending = $derived(viewState.refining || Boolean(localRefinementError || viewState.refinementError)
    || (baseline !== null && lastRequestedFingerprint !== currentFingerprint()));

  function emptyResultsHeading(): string {
    if (retentionNotice) return retentionNotice.title;
    if (!receivedFileCount()) return 'No results';
    return aggregateMode ? 'No matching groups' : genericMode ? 'No matching directories' : 'No matching results';
  }

  function receivedFileCount(): number {
    const counters = viewState.summary?.counters;
    return Number(counters?.publicFileCount ?? 0) + Number(counters?.lockedFileCount ?? 0);
  }

  function emptyResultsDetail(): string {
    if (retentionNotice) return retentionNotice.detail;
    const count = receivedFileCount();
    if (!count) return 'No files were returned by peers.';
    return `${formatCountWithNoun(count, 'file')} received, but none match the current ${filterText.trim() ? 'text filter or ' : ''}${aggregateMode ? 'conditions and grouping requirements' : 'result conditions'}.`;
  }

  function currentFingerprint(): string {
    return JSON.stringify([filterText, conditions, sort, sizeDirection]);
  }

  function currentSortOrder(): Schema<'ServerSearchViewSortOrder'> {
    if (sort === 'speed') return 'UploadSpeed';
    if (sort === 'queue') return 'QueueDepth';
    if (sort === 'size') return genericMode
      ? sizeDirection === 'asc' ? 'DirectorySizeAscending' : 'DirectorySizeDescending'
      : sizeDirection === 'asc' ? 'ItemSizeAscending' : 'ItemSizeDescending';
    if (sort === 'count') return sizeDirection === 'asc' ? 'FileCountAscending' : 'FileCountDescending';
    if (sort === 'name') return sizeDirection === 'asc' ? 'DirectoryNameAscending' : 'DirectoryNameDescending';
    return 'Relevance';
  }

  function projectionFingerprint(): string {
    return JSON.stringify([filterText, conditions]);
  }

  async function requestRefinement(resetSelection = false): Promise<void> {
    if (!baseline || !baselineConditions) {
      if (resetSelection) window.location.reload();
      return;
    }
    try {
      const fingerprint = currentFingerprint();
      const projection = projectionFingerprint();
      const version = ++refinementRequest;
      if (!resetSelection && projection === appliedProjectionFingerprint) {
        localRefinementError = null;
        lastRequestedFingerprint = fingerprint;
        await onsort(currentSortOrder());
        return;
      }
      const request: Schema<'CreateSearchViewRequestDto'> = {
        ...baseline.projection,
        filterText,
        sortOrder: currentSortOrder(),
        refinementSettings: mergeSearchSettings(baseline.projection.refinementSettings ?? undefined,
          searchConditionsPatch(viewState.draft!.resultMode, baselineConditions, conditions)),
      };
      if (searchModeFamily(viewState.draft!.resultMode) === 'track' && request.songQuery)
        request.songQuery = { ...request.songQuery, length: searchExpectedLength(conditions.track.expectedLength) ?? null };
      localRefinementError = null;
      lastRequestedFingerprint = fingerprint;
      if (await onrefine(request, resetSelection) && version === refinementRequest) appliedProjectionFingerprint = projection;
    } catch (error) {
      localRefinementError = error instanceof Error ? error : new Error(String(error));
    }
  }

  $effect(() => {
    const jobId = viewState.job?.jobId ?? null;
    const viewId = viewState.summary?.viewId;
    if (!jobId || !viewId || configurationJobId === jobId) return;
    configurationJobId = jobId;
    filterEdited = false;
    sortEdited = false;
    const request = ++configurationRequest;
    ++refinementRequest;
    baseline = null;
    baselineConditions = null;
    filterText = '';
    sort = 'relevance';
    sizeDirection = 'desc';
    conditions = createEmptySearchConditions();
    lastRequestedFingerprint = null;
    configurationLoading = true;
    configurationError = null;
    void onloaddefinition(viewId).then(definition => {
      if (request !== configurationRequest) return;
      const editedFilter = filterEdited ? filterText : null;
      const editedSort = sortEdited ? { sort, direction: sizeDirection } : null;
      const read = readSearchConditions(definition.effectiveSearchSettings,
        definition.projection.songQuery?.length == null ? undefined : Number(definition.projection.songQuery.length));
      baseline = definition;
      baselineConditions = structuredClone(read);
      conditions = read;
      filterText = definition.projection.filterText ?? '';
      const order = viewState.sortOrder;
      sort = order === 'UploadSpeed' ? 'speed' : order === 'QueueDepth' ? 'queue'
        : order.startsWith('ItemSize') || order.startsWith('DirectorySize') ? 'size'
        : order.startsWith('FileCount') ? 'count' : order.startsWith('DirectoryName') ? 'name' : 'relevance';
      sizeDirection = order.endsWith('Ascending') ? 'asc' : 'desc';
      // Bind the fingerprint to the server projection, then apply edits made
      // while its definition was loading so they produce one normal refinement.
      lastRequestedFingerprint = currentFingerprint();
      appliedProjectionFingerprint = projectionFingerprint();
      if (editedFilter !== null) filterText = editedFilter;
      if (editedSort) { sort = editedSort.sort; sizeDirection = editedSort.direction; }
      configurationLoading = false;
    }).catch(error => {
      if (request !== configurationRequest) return;
      configurationLoading = false;
      configurationError = error instanceof Error ? error : new Error(String(error));
    });
  });

  $effect(() => {
    const fingerprint = currentFingerprint();
    if (viewUnavailable || !baseline || !baselineConditions || lastRequestedFingerprint === fingerprint) return;
    const timer = setTimeout(requestRefinement, projectionFingerprint() === appliedProjectionFingerprint ? 0 : 300);
    return () => clearTimeout(timer);
  });

  function changeFilter(value: string): void {
    filterEdited = true;
    filterText = value;
  }

  function reverseSort(): void {
    sortEdited = true;
    sizeDirection = sizeDirection === 'desc' ? 'asc' : 'desc';
  }

  function changeSort(event: Event): void {
    sortEdited = true;
    const next = (event.currentTarget as HTMLSelectElement).value as typeof sort;
    sort = next;
    if (genericMode) sizeDirection = next === 'name' ? 'asc' : 'desc';
  }

  function rowFullySelected(row: SearchRow): boolean {
    if (row.kind === 'aggregate-album') return albumFull(row.ref);
    if (row.kind !== 'directory') return isselected(row.ref);
    const children = viewState.directoryFiles.get(row.ref);
    const files = children ? children.items.map(child => child.file) : [row.value.bestChild];
    return ordinaryDirectorySelection(row.ref, files.length,
      files.filter(file => isselected(file.ref, row.ref)).length).selected;
  }

  function toggleKeyboardResult(ref: string): void {
    if (selectionReadOnly || viewState.committing || refinementPending) return;
    const row = viewState.rows.find(row => row.ref === ref);
    if (row) ontoggle(ref, !rowFullySelected(row));
  }

  function retrieveSelectedFolders(): void {
    if (selectionReadOnly || viewState.committing || refinementPending) return;
    for (const row of viewState.rows) {
      if (!rowFullySelected(row)) continue;
      let directory: Schema<'SearchViewDirectoryDto'>;
      if (row.kind === 'directory') directory = row.value;
      else if (row.kind === 'aggregate-album' && (!viewState.chosenOptions.has(row.ref) || choiceIsCurrent(row.ref))) {
        const chosen = viewState.chosenOptions.get(row.ref);
        directory = chosen?.kind === 'album' ? chosen.value : row.value.representative;
      } else continue;
      directory = viewState.directoryFiles.get(directory.ref.ref)?.directorySummary ?? directory;
      if (directory.ref.folderPath && directory.retrievalState !== 'Complete'
        && !viewState.retrievals.get(directory.ref.ref)?.loading) onretrievedirectory(directory.ref.ref);
    }
  }

  function windowKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape') return;
    if (viewState.aggregateOptions) oncloseoptions();
    else if (downloadOptionsOpen) downloadOptionsOpen = false;
    else if (conditionsOpen) conditionsOpen = false;
  }

  function windowPointerDown(event: PointerEvent): void {
    if (!conditionsOpen || !(event.target instanceof Node)) return;
    if (conditionsButton?.contains(event.target) || conditionsPopover?.contains(event.target)) return;
    conditionsOpen = false;
  }
  let downloadOptionsOpen = $state(false);
  let downloadOptionsLoading = $state(false);
  let downloadOptionsError = $state<Error | null>(null);
  let downloadDefaults = $state<SearchDownloadOptionDefaults | null>(null);
  let downloadValues = $state<DownloadOptions | null>(null);
  let touchedMixedFields = $state<Set<keyof DownloadOptions>>(new Set());
  let optionsRequest = 0;
  let selectionFingerprint = $derived(JSON.stringify([
    viewState.summary?.viewId,
    viewState.selectedAll,
    [...viewState.selectionRefs].sort(),
    [...viewState.includedFiles.keys()].sort(),
    [...viewState.excludedFiles.keys()].sort(),
    viewState.directoryPathRules.map(rule => [rule.directoryRef, rule.relativePath, rule.selected]).sort(),
    [...viewState.chosenOptions].map(([groupRef, option]) => [groupRef, option.ref]).sort(),
    [...viewState.albumFileSelections].map(([ref, selection]) =>
      [ref, selection.mode, [...selection.refs].sort()]).sort(),
  ]));

  let directoriesWithExcludedChoices = $derived(new Set([
    ...viewState.excludedFiles.values(),
    ...viewState.directoryPathRules.filter(rule => !rule.selected).map(rule => rule.directoryRef),
  ]));
  let directoriesWithIncludedChoices = $derived(new Set([
    ...viewState.includedFiles.values(),
    ...viewState.directoryPathRules.filter(rule => rule.selected).map(rule => rule.directoryRef),
  ]));

  function ordinaryDirectorySelection(ref: string, fileCount: number, selectedFileCount: number): { selected: boolean; partial: boolean } {
    const rootSelected = isselected(ref);
    const partial = rootSelected
      ? directoriesWithExcludedChoices.has(ref) || selectedFileCount < fileCount
      : directoriesWithIncludedChoices.has(ref) || selectedFileCount > 0;
    return { selected: rootSelected && !partial, partial };
  }
  $effect(() => {
    selectionFingerprint;
    optionsRequest += 1;
    downloadOptionsOpen = false;
    downloadOptionsLoading = false;
    downloadDefaults = null;
    downloadValues = null;
    downloadOptionsError = null;
    touchedMixedFields = new Set();
  });
  let visibleMixedFields = $derived(new Set(
    [...(downloadDefaults?.mixedFields ?? [])].filter(field => !touchedMixedFields.has(field)),
  ));
  let downloadPatch = $derived(downloadDefaults && downloadValues
    ? buildSearchSelectionDownloadPatch(downloadValues, downloadDefaults, touchedMixedFields)
    : undefined);
  let downloadCustomized = $derived(Boolean(downloadPatch && Object.keys(downloadPatch).length));

  async function loadDownloadOptions(): Promise<void> {
    const request = ++optionsRequest;
    downloadOptionsLoading = true;
    downloadOptionsError = null;
    try {
      const defaults = searchDownloadOptionDefaults(
        await onresolvesettings(),
        viewState.summary?.kind,
      );
      if (request !== optionsRequest) return;
      const exactAlbumFiles = [...viewState.albumFileSelections.keys()]
        .some(ref => isselected(ref));
      downloadDefaults = exactAlbumFiles ? {
        ...defaults,
        capabilities: { ...defaults.capabilities, albumFolderEnrichment: false },
      } : defaults;
      downloadValues = { ...defaults.values };
    } catch (error) {
      if (request === optionsRequest)
        downloadOptionsError = error instanceof Error ? error : new Error(String(error));
    } finally {
      if (request === optionsRequest) downloadOptionsLoading = false;
    }
  }

  function toggleDownloadOptions(): void {
    downloadOptionsOpen = !downloadOptionsOpen;
    if (downloadOptionsOpen && !downloadDefaults && !downloadOptionsLoading)
      void loadDownloadOptions();
  }

  function markDownloadField(field: keyof DownloadOptions): void {
    if (downloadDefaults?.mixedFields.has(field))
      touchedMixedFields = new Set([...touchedMixedFields, field]);
  }
  let isActive = $derived(viewState.refining || viewState.summary?.isComplete === false || (status
    ? status.lifecycleState !== 'Terminal'
    : Boolean(viewState.job && viewState.job.lifecycleState !== 'Terminal' && !viewState.summary?.isComplete)));

  const folderFileCache = new WeakMap<Schema<'SearchViewFileDto'>, FolderItemFile>();
  function folderFile(file: Schema<'SearchViewFileDto'>, relativePath?: string): FolderItemFile {
    const path = relativePath ?? file.file.name;
    const cached = folderFileCache.get(file);
    if (cached?.relativePath === path) return cached;
    const result: FolderItemFile = {
      id: file.ref,
      relativePath: path,
      locked: file.visibility === 'Locked',
      sizeBytes: Number(file.file.size),
      audio: audio(file.file),
    };
    folderFileCache.set(file, result);
    return result;
  }

  function trackRepresentative(groupRef: string, fallback: Schema<'SearchViewFileDto'>): Schema<'SearchViewFileDto'> {
    const chosen = viewState.chosenOptions.get(groupRef);
    return choiceIsCurrent(groupRef) && chosen?.kind === 'track' ? chosen.value : fallback;
  }

  function choiceIsCurrent(groupRef: string): boolean {
    const summary = viewState.summary;
    return Boolean(summary && !viewState.staleChoices.has(groupRef)
      && viewState.checkedChoiceRevisions.get(groupRef) === `${summary.viewId}:${String(summary.revision)}`);
  }

  function hasUnreadySelectedChoice(): boolean {
    return viewState.rows.some(row => (row.kind === 'aggregate-track' || row.kind === 'aggregate-album')
      && isselected(row.ref) && viewState.chosenOptions.has(row.ref) && !choiceIsCurrent(row.ref));
  }

  function albumFull(groupRef: string): boolean {
    const selection = viewState.albumFileSelections.get(groupRef);
    return isselected(groupRef) && (!selection || (selection.mode === 'AllExcept' && selection.refs.size === 0));
  }

  function albumPartial(groupRef: string): boolean {
    return isselected(groupRef) && !albumFull(groupRef);
  }

  function peerName(row: OrdinaryRow): string {
    return row.value.peer.username;
  }

  function preference(row: OrdinaryRow): Schema<'ServerSearchPreferenceTier'> {
    return row.value.preferenceTier;
  }

  function queryTitle(): string {
    const title = viewState.job?.itemName ?? viewState.job?.queryText;
    if (title != null) return title;
    const draft = viewState.draft;
    if (!draft) return '';
    return draft.mode === 'split'
      ? [draft.artist.trim(), draft.title.trim()].filter(Boolean).join(' — ')
      : draft.query;
  }

  let statusLabel = $derived(jobStatus());

  function jobStatus(): string {
    if (!status) {
      const job = viewState.job;
      if (job?.lifecycleState === 'Terminal') return job.terminalOutcome
        ? searchOutcome(job.terminalOutcome) : 'Complete';
      return viewState.summary?.isComplete || Number(viewState.summary?.consumedSequence ?? 0) > 0
        ? searchOutcome(null) : 'Starting';
    }
    if (status.lifecycleState === 'Terminal') return searchOutcome(status.terminalOutcome);
    if (status.lifecycleState === 'AwaitingSelection') return 'Awaiting selection';
    return humanizeStateValue(status.activityPhase === 'None' ? status.lifecycleState : status.activityPhase);
  }

  function searchOutcome(outcome: string | null | undefined): string {
    if (outcome && outcome !== 'Succeeded') return humanizeStateValue(outcome);
    const summary = viewState.summary;
    if (summary && (!summary.isComplete || viewState.refining)) return 'Updating results';
    return summary && summary.retentionState !== 'Complete'
      ? humanizeStateValue(summary.retentionState) : 'Complete';
  }

  function samePeerGroups(rows: OrdinaryRow[]): { key: string; username: string; rows: [OrdinaryRow, ...OrdinaryRow[]] }[] {
    const groups: { key: string; username: string; rows: [OrdinaryRow, ...OrdinaryRow[]] }[] = [];
    const peerRuns = new Map<string, number>();
    for (const row of rows) {
      const username = peerName(row);
      const previous = groups.at(-1);
      if (previous?.username === username) previous.rows.push(row);
      else {
        // Another peer inserting earlier must not recreate this group or reset
        // its collapse state. A peer may have several nonadjacent runs.
        const run = peerRuns.get(username) ?? 0;
        peerRuns.set(username, run + 1);
        groups.push({ key: JSON.stringify([username, run]), username, rows: [row] });
      }
    }
    return groups;
  }

  function groupRows(rows: SearchRow[], byPreference: boolean) {
    const tiers: ('Preferred' | 'Other' | 'All')[] = byPreference ? ['Preferred', 'Other'] : ['All'];
    const rowsByTier = new Map(tiers.map(tier => [tier, [] as OrdinaryRow[]]));
    for (const row of rows) {
      if (row.kind !== 'file' && row.kind !== 'directory') continue;
      rowsByTier.get(tiers[0] === 'All' ? 'All' : preference(row))?.push(row);
    }
    return tiers.map(tier => ({ tier, rows: rowsByTier.get(tier)! }))
      .filter(({ rows }) => rows.length > 0)
      .map(({ tier, rows }) => ({ tier, count: rows.length, groups: samePeerGroups(rows) }));
  }
  let groupedRows: SearchRow[] | null = null;
  let groupedByPreference = false;
  let cachedGroups: ReturnType<typeof groupRows> = [];
  let groupedTiers = $derived.by(() => {
    const rows = viewState.rows;
    const byPreference = viewState.sortOrder === 'Relevance' && !genericMode;
    if (rows !== groupedRows || byPreference !== groupedByPreference) {
      groupedRows = rows;
      groupedByPreference = byPreference;
      cachedGroups = groupRows(rows, byPreference);
    }
    return cachedGroups;
  });
</script>

<svelte:window onkeydown={windowKeydown} onpointerdown={windowPointerDown} />

{#snippet aggregatePeerSummary(peer: Schema<'SearchViewFileDto'>['peer'])}
  <div class="aggregate-peer-summary">
    <span class="aggregate-peer-username"><UsernameLink username={peer.username} actions={userActions} /></span>
    {#if peer.uploadSpeed != null}<span class="aggregate-peer-speed"><strong>{(Number(peer.uploadSpeed) / 1_000_000).toFixed(1)} MB/s</strong></span>{/if}
    <span class="aggregate-peer-slot"><strong class:available={peer.hasFreeUploadSlot === true} class:unavailable={peer.hasFreeUploadSlot === false}>{peer.hasFreeUploadSlot == null ? '—' : peer.hasFreeUploadSlot ? 'Yes' : 'No'}</strong><small>free slot</small></span>
  </div>
{/snippet}

{#snippet aggregateGroupCard(row: Extract<SearchRow, { kind: 'aggregate-track' | 'aggregate-album' }>)}
  {#if row.kind === 'aggregate-track'}
    {@const representative = trackRepresentative(row.ref, row.value.representative)}
    <section class:selected={isselected(row.ref)} class="aggregate-result-group" data-keyboard-item={row.ref} tabindex="-1">
      <header class="aggregate-result-header">
        <button type="button" class="aggregate-header-select-button" disabled={selectionReadOnly} aria-label={`${isselected(row.ref) ? 'Deselect' : 'Select'} ${row.value.query.title ?? 'track'}`} aria-pressed={isselected(row.ref)} onclick={() => ontoggle(row.ref, !isselected(row.ref))}></button>
        <div class="aggregate-result-identity"><strong>{row.value.query.title ?? 'Untitled track'}</strong>{#if row.value.query.artist}<small>{row.value.query.artist}</small>{/if}</div>
        <div class="aggregate-result-source">{@render aggregatePeerSummary(representative.peer)}</div>
        <div class="aggregate-result-stats"><button type="button" class="aggregate-options-button" onclick={() => onopenoptions(row.ref)}>{formatCount(row.value.selectableOptionCount)} {Number(row.value.selectableOptionCount) === 1 ? 'option' : 'options'}</button></div>
      </header>
      {#if viewState.chosenOptions.has(row.ref) && !choiceIsCurrent(row.ref)}
        <div class="resource-state-notice degraded aggregate-choice-notice" role="status">
          <span class="resource-state-dot"></span>
          <span>{viewState.staleChoices.has(row.ref)
            ? 'Chosen source is no longer in these results. Select another option before downloading.'
            : viewState.choiceValidationError
              ? 'Could not check the chosen source. Retry the result refresh before downloading.'
              : 'Checking the chosen source in the updated results…'}</span>
          {#if viewState.staleChoices.has(row.ref)}<button type="button" onclick={() => onopenoptions(row.ref)}>Choose another</button>{/if}
        </div>
      {/if}
      <FileItemCard path={representative.remoteFilename} sizeBytes={Number(representative.file.size)} audio={audio(representative.file)} locked={representative.visibility === 'Locked'} selected={isselected(row.ref)} selectable={!selectionReadOnly} onselect={(checked) => ontoggle(row.ref, checked)} />
    </section>
  {:else}
    {@const chosenDirectory = choiceIsCurrent(row.ref) && viewState.chosenOptions.get(row.ref)?.kind === 'album' ? viewState.chosenOptions.get(row.ref)?.value as Schema<'SearchViewDirectoryDto'> : row.value.representative}
    {@const children = viewState.directoryFiles.get(chosenDirectory.ref.ref)}
    {@const directory = children?.directorySummary ?? chosenDirectory}
    {@const files = children ? children.items.map((child) => folderFile(child.file, child.relativePath)) : [folderFile(directory.bestChild)]}
    {@const totals = directoryTotals(directory)}
    {@const retrieval = viewState.retrievals.get(directory.ref.ref)}
    <section class:selected={albumFull(row.ref)} class:partial={albumPartial(row.ref)} class="aggregate-result-group" data-keyboard-item={row.ref} tabindex="-1">
      <header class="aggregate-result-header">
        <button type="button" class="aggregate-header-select-button" disabled={selectionReadOnly} aria-label={`${albumFull(row.ref) ? 'Deselect' : 'Select'} ${row.value.query.album ?? 'album'}`} aria-pressed={albumFull(row.ref)} onclick={() => ontoggle(row.ref, !albumFull(row.ref))}></button>
        <div class="aggregate-result-identity"><strong>{row.value.query.album ?? 'Untitled album'}</strong>{#if row.value.query.artist}<small>{row.value.query.artist}</small>{/if}</div>
        <div class="aggregate-result-source">{@render aggregatePeerSummary(directory.peer)}</div>
        <div class="aggregate-result-stats"><button type="button" class="aggregate-options-button" onclick={() => onopenoptions(row.ref)}>{formatCount(row.value.selectableOptionCount)} {Number(row.value.selectableOptionCount) === 1 ? 'option' : 'options'}</button></div>
      </header>
      {#if viewState.chosenOptions.has(row.ref) && !choiceIsCurrent(row.ref)}
        <div class="resource-state-notice degraded aggregate-choice-notice" role="status">
          <span class="resource-state-dot"></span>
          <span>{viewState.staleChoices.has(row.ref)
            ? 'Chosen folder is no longer in these results. Select another option before downloading.'
            : viewState.choiceValidationError
              ? 'Could not check the chosen folder. Retry the result refresh before downloading.'
              : 'Checking the chosen folder in the updated results…'}</span>
          {#if viewState.staleChoices.has(row.ref)}<button type="button" onclick={() => onopenoptions(row.ref)}>Choose another</button>{/if}
        </div>
      {/if}
      <FolderItemCard
        contentsState={!directory.ref.folderPath ? undefined : retrieval?.loading ? 'retrieving' : retrieval?.error ? 'failed' : directory.retrievalState === 'Complete' ? 'complete' : 'partial'}
        onloadfullcontents={() => onretrievedirectory(directory.ref.ref)}
        path={directory.ref.folderPath || 'Files without a folder'}
        sizeBytes={totals.bytes}
        {files}
        totalFileCount={totals.fileCount}
        filesComplete={Boolean(children && !children.nextCursor && (!children.loading || children.refreshing) && !children.error)}
        dataStateLabel={retrieval?.error?.message ?? children?.error?.message ?? (children?.loading && !children.refreshing ? 'Loading matching files…' : undefined)}
        locked={directory.visibility === 'Locked'}
        selected={albumFull(row.ref)}
        partial={albumPartial(row.ref)}
        selectable={!selectionReadOnly}
        selectedFileIds={new Set(files.filter((file) => isselected(file.id, row.ref)).map((file) => file.id))}
        onselectall={(checked) => ontoggle(row.ref, checked)}
        onselectfile={(file, checked) => ontogglealbumfile(row.ref, file.id, checked)}
      />
      {#if !children || (children.loading && !children.refreshing) || children.error || children.nextCursor}
        <div class="load-more-row"><button type="button" disabled={children?.loading} onclick={() => onloaddirectory(directory.ref.ref)}>{children?.loading ? 'Loading…' : children?.error ? 'Retry matching files' : children ? 'Load more' : 'Load matching files'}</button></div>
      {/if}
    </section>
  {/if}
{/snippet}

<section class="page page-search redesigned-search-page" use:keyboardList={{
  scope: `${viewState.job?.jobId}:${viewState.summary?.viewId}:${viewState.sortOrder}`,
  revision: [viewState.rows, viewState.loadingMore],
  enabled: viewState.phase === 'ready' && !refinementPending,
  toggle: toggleKeyboardResult, retrieve: retrieveSelectedFolders,
  loadPage: onloadmore,
}}>
  <header class="search-results-heading">
    <button type="button" class="icon-button back-button" aria-label="Back to jobs" onclick={onback}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M12.5 4.5L7 10l5.5 5.5M7.5 10H16" /></svg>
    </button>
    <div class="search-results-title">
      <p class="eyebrow">{viewState.draft ? searchModeLabel(viewState.draft.resultMode) : 'Search'}</p>
      <h1>{queryTitle()}</h1>
    </div>
    <div class="search-results-summary">
      {#if statusLabel !== 'Complete'}
        <span class={`search-status-badge ${isActive ? 'searching' : retentionNotice ? '' : 'complete'}`}><i></i>{statusLabel}</span>
      {/if}
      {#if (!retentionNotice && !viewUnavailable) || viewState.rows.length > 0}
      {#if viewState.draft?.resultMode === 'generic'}<span>{formatCountWithNoun(viewState.summary?.counters.topLevelItemCount, hasBareFileGroups ? 'group' : 'directory', hasBareFileGroups ? 'groups' : 'directories')}</span>{/if}
      {#if viewState.draft?.resultMode?.includes('aggregate')}<span>{formatCountWithNoun(viewState.summary?.counters.topLevelItemCount, 'group')}</span>{/if}
      <span>{formatCountWithNoun(viewState.summary?.counters.publicFileCount, 'file')}</span>
      <span>{formatCount(viewState.summary?.counters.lockedFileCount)} locked</span>
      <span>{formatCountWithNoun(viewState.summary?.counters.observedPeerCount, 'peer')}</span>
      {/if}
    </div>
    <div class="search-results-actions">
      <button
        type="button"
        class="search-again-button"
        disabled={!searchAgainAvailable || searchAgainPending}
        title={searchAgainAvailable ? 'Run this search again with its retained settings' : 'Single-search replay is unavailable for this job'}
        onclick={onsearchagain}
      ><Icon name="search" /><span>{searchAgainPending ? 'Starting…' : 'Search again'}</span></button>
      {#if removeAction}
        <button type="button" class:quiet-cancel={removeAction === 'cancel'} class="delete-search-button" disabled={removePending} aria-label={`${removeAction === 'cancel' ? 'Cancel' : 'Delete'} ${queryTitle()}`} title={removeAction === 'cancel' ? 'Cancel job' : 'Delete search'} onclick={onremove}>
          <Icon name={removeAction === 'cancel' ? 'x' : 'trash'} />
          <span>{removePending ? 'Working…' : removeAction === 'cancel' ? 'Cancel' : 'Delete'}</span>
        </button>
      {/if}
    </div>
  </header>

  {#if actionError}
    <div class="resource-state-notice unavailable" role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>Search action failed</strong><small>{actionError.message}</small></span>
    </div>
  {/if}

  {#if viewUnavailable}
    <div class="resource-state-notice unavailable" role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>Results view unavailable</strong><small>This results snapshot expired or the daemon restarted.
        {#if viewState.canRetryPendingCommit}Retry your download request to recover its outcome.
        {:else}Reload results; your current selection will be cleared.{/if}
      </small></span>
      {#if !viewState.canRetryPendingCommit}<button type="button" disabled={viewState.refining} onclick={() => requestRefinement(true)}>Reload results</button>{/if}
    </div>
  {/if}

  {#if (viewState.error || liveJobError) && !viewUnavailable}
    <div class={`resource-state-notice ${daemonOffline ? 'offline' : 'unavailable'}`} class:blocking={viewState.phase === 'failed'} role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>{daemonOffline ? 'Daemon unavailable' : viewState.error ? 'Search results could not be refreshed' : 'Job updates could not be refreshed'}</strong><small>{daemonOffline ? 'This resource cannot be loaded while the daemon is offline.' : (viewState.error ?? liveJobError)?.message}</small></span>
      {#if !daemonOffline}<button type="button" onclick={onretry}>Retry</button>{/if}
    </div>
  {/if}

  {#if viewState.choiceValidationError && !viewUnavailable}
    <div class="resource-state-notice unavailable" role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>Chosen sources could not be checked</strong><small>{viewState.choiceValidationError.message}</small></span>
      <button type="button" onclick={onretry}>Retry</button>
    </div>
  {/if}

  {#if !viewUnavailable && (configurationError || localRefinementError || viewState.refinementError)}
    <div class="resource-state-notice unavailable" role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>{configurationError ? 'Result configuration unavailable' : 'Search results could not be updated'}</strong><small>{viewState.refinementError ? 'Previous results are still shown. ' : ''}{(configurationError ?? localRefinementError ?? viewState.refinementError)?.message}</small></span>
      {#if configurationError}<button type="button" onclick={() => { configurationJobId = null; }}>Retry</button>
      {:else}<button type="button" onclick={() => requestRefinement()}>Retry</button>{/if}
    </div>
  {/if}

  {#if viewState.phase !== 'failed'}
  <div class="result-refine-wrap">
    <div class="result-refine-row">
      <ResultFilterControl bind:value={() => filterText, changeFilter} placeholder={genericMode ? 'Filter files or directories…' : 'Filter results…'} ariaLabel="Filter search results" />
      <button bind:this={conditionsButton} type="button" class:active={conditionsOpen} class="edit-conditions-button" aria-expanded={conditionsOpen} disabled={configurationLoading || !baseline} onclick={() => (conditionsOpen = !conditionsOpen)}>
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 5h12M4 10h12M4 15h12"/><circle cx="8" cy="5" r="1.6"/><circle cx="13" cy="10" r="1.6"/><circle cx="7" cy="15" r="1.6"/></svg>
        Filtering
      </button>
      {#if !aggregateMode}
        <div class="result-sort-control">
          <label for="result-sort">Sort</label>
          <select id="result-sort" value={sort} onchange={changeSort}>
            <option value="relevance">Relevance</option>
            <option value="speed">Upload speed</option>
            {#if genericMode}
              <option value="size">Directory size</option><option value="count">File count</option><option value="name">Directory name</option>
            {:else}
              <option value="queue">Queue depth</option><option value="size">Item size</option>
            {/if}
          </select>
          {#if sort === 'size' || (genericMode && (sort === 'count' || sort === 'name'))}
            <button type="button" class="size-direction-button" aria-label={`Reverse ${genericMode ? 'directory' : 'item'} sort`} onclick={reverseSort}>
              <svg class:ascending={sizeDirection === 'asc'} viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M6 8l4-4 4 4" /></svg>
            </button>
          {/if}
        </div>
      {/if}
    </div>
    {#if viewState.draft && hasAppliedConditions(viewState.draft.resultMode, conditions)}
      <div class="result-condition-pills"><SearchConditionPills mode={viewState.draft.resultMode} bind:conditions /></div>
    {/if}
    {#if conditionsOpen && viewState.draft}
      <section data-shortcuts-blocked="true" bind:this={conditionsPopover} class="search-config-popover results-config-popover" aria-label="Result search configuration" use:anchoredPopover={{ anchor: conditionsButton, align: 'start', gap: 8, viewportMargin: 16, minHeight: 240 }}>
        <SearchConfigPanel mode={viewState.draft.resultMode} bind:conditions title="Search configuration" initialTab="conditions" onclose={() => (conditionsOpen = false)} />
      </section>
    {/if}
  </div>

  {#if viewState.selectionNeedsReview && viewState.phase === 'ready'}
    <div class="resource-state-notice degraded" role="status">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>Selection needs review</strong><small>Result grouping changed or could not be checked. Restore your filters or clear the selection to choose from these results.</small></span>
    </div>
  {:else if selectionUsesEarlierRevision}
    <div class="resource-state-notice degraded" role="status">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>Selection from earlier results</strong><small>Download the files you selected, or clear the selection to choose from the latest results.</small></span>
    </div>
  {/if}

  <SelectionToolbar
    {selectedCount}
    visible={selectedCount > 0 || viewState.selectionNeedsReview || viewState.canRetryPendingCommit}
    floatingLabel={viewState.committing ? 'Requesting…' : viewState.canRetryPendingCommit ? 'Retry download request' : viewState.selectionCounts
      && BigInt(viewState.selectionCounts.publicFileCount) > 0n
      ? `Download ${formatCount(viewState.selectionCounts.publicFileCount)}` : 'Download selected'}
    detail={viewState.selectionCounts && Number(viewState.selectionCounts.lockedFileCount) > 0
      ? `${formatCount(String(BigInt(viewState.selectionCounts.publicFileCount) + BigInt(viewState.selectionCounts.lockedFileCount)))} selected · ${formatCount(viewState.selectionCounts.lockedFileCount)} locked`
      : viewState.selectionCountsLoading ? 'Counting selected files…'
      : viewState.selectionCountsError ? viewState.selectionCountsError.message : undefined}
    actionDisabled={viewState.committing || (!viewState.canRetryPendingCommit && (refinementPending
      || viewState.selectionCountsLoading || hasUnreadySelectedChoice()
      || Boolean(viewState.selectionCountsError) || selectedCount === 0))}
    optionsOpen={downloadOptionsOpen}
    optionsCustomized={downloadCustomized}
    onclear={() => onselectall(false)}
    onselectall={selectedCount && !selectionReadOnly && !viewState.committing && !allSearchResultsSelected(viewState) ? () => onselectall(true) : undefined}
    onoptions={selectedCount ? toggleDownloadOptions : undefined}
    onaction={() => ondownload(downloadCustomized ? downloadPatch : undefined)}
  />

  {#if selectedCount && downloadOptionsOpen}
    <button type="button" class="floating-options-backdrop" aria-label="Close download options" onclick={() => (downloadOptionsOpen = false)}></button>
    <section data-shortcuts-blocked="true" class="floating-download-options-popover" aria-label="Download options">
      <header><strong>Download options</strong><button type="button" aria-label="Close download options" onclick={() => (downloadOptionsOpen = false)}>×</button></header>
      {#if downloadOptionsLoading}
        <LoadingIndicator label="Loading effective defaults" />
      {:else if downloadOptionsError}
        <p class="download-option-note" role="alert">{downloadOptionsError.message}</p>
        <button type="button" onclick={() => void loadDownloadOptions()}>Retry</button>
      {:else if downloadDefaults && downloadValues}
        <DownloadOptionsPanel bind:value={downloadValues} capabilities={downloadDefaults.capabilities} context="selection" mixedFields={visibleMixedFields} onfieldchange={markDownloadField} compact />
      {/if}
    </section>
  {/if}

  {#if viewState.receipt}
    <p class:partially-succeeded={Number(viewState.receipt.skippedCount) > 0 || Number(viewState.receipt.rejectedCount) > 0} class="mutation-status succeeded">
      {#if viewState.receipt.submissionId}<button type="button" class="text-button" onclick={() => onviewdownloads(viewState.receipt!.submissionId!)}>View download jobs</button>{/if}
      {formatCount(mutationDownloadCount)} download{Number(mutationDownloadCount) === 1 ? '' : 's'} requested
      {#if Number(viewState.receipt.skippedCount) || Number(viewState.receipt.rejectedCount)}<small>{formatCount(viewState.receipt.skippedCount)} skipped · {formatCount(viewState.receipt.rejectedCount)} rejected</small>{/if}
    </p>
    {#if viewState.receipt.rejectionReasons.some(reason => reason.message)}
      <div class="resource-state-notice unavailable" role="alert">
        <span class="resource-state-dot"></span>
        <span class="resource-state-copy"><strong>Downloads rejected</strong>
          {#each viewState.receipt.rejectionReasons as reason}
            {#if reason.message}<small>{reason.message}</small>{/if}
          {/each}
        </span>
      </div>
    {/if}
  {/if}

  {#if retentionNotice && viewState.rows.length > 0}
    <div class="resource-state-notice unavailable" role="status">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>{retentionNotice.title}</strong><small>{retentionNotice.detail}</small></span>
    </div>
  {/if}

  {#if viewState.phase === 'loading' && viewState.rows.length === 0}
    <LoadingIndicator label="Loading search results" blocking />
  {:else if viewState.rows.length === 0 && !viewState.error && !viewUnavailable}
    {#if !retentionNotice && !viewState.summary?.isComplete && !daemonOffline}
      {#if searching && receivedFileCount()}
        <div class="resource-state-notice loading blocking" role="status"><span class="resource-state-dot" aria-hidden="true"></span><span class="resource-state-copy"><strong>{emptyResultsHeading()}</strong><small>{emptyResultsDetail()}</small></span></div>
      {:else}
        <LoadingIndicator label={searching ? 'Searching' : 'Loading search results'} blocking />
      {/if}
    {:else}
      <div class="search-results-empty"><strong>{emptyResultsHeading()}</strong>{#if retentionNotice || searching || (viewState.summary?.isComplete && !viewState.refining)}<span>{emptyResultsDetail()}</span>{/if}</div>
    {/if}
  {:else if viewState.draft?.resultMode === 'song-aggregate' || viewState.draft?.resultMode === 'album-aggregate'}
    <div class="aggregate-results-list">
      {#each viewState.rows as row (row.ref)}
        {#if row.kind === 'aggregate-track' || row.kind === 'aggregate-album'}
          {@render aggregateGroupCard(row)}
        {/if}
      {/each}
    </div>
  {:else}
    {#each groupedTiers as { tier, count, groups } (tier)}
        {#if tier !== 'All'}<div class:preferred={tier === 'Preferred'} class="result-tier-heading"><span>{tier === 'Preferred' ? 'Preferred matches' : 'Other matches'}</span><small>{count}</small></div>{/if}
        <div class:preferred-tier={tier === 'Preferred'} class="result-tier">
          {#each groups as group (group.key)}
            {@const first = group.rows[0]}
            {@const peer = first.value.peer}
            {@const noun = genericMode ? (group.rows.some(row => row.kind === 'directory' && !row.value.ref.folderPath) ? 'group' : 'directory') : 'result'}
            <PeerItemGroup
              peer={{
                username: peer.username,
                freeUploadSlot: peer.hasFreeUploadSlot ?? undefined,
                uploadSpeedMbps: peer.uploadSpeed == null ? undefined : Number(peer.uploadSpeed) / 1_000_000,
                queueLength: peer.queueLength == null ? undefined : Number(peer.queueLength),
              }}
              itemNoun={noun}
              itemNounPlural={noun === 'directory' ? 'directories' : `${noun}s`}
              {userActions}
            >
                {#each group.rows as row (row.ref)}
                  {#if row.kind === 'file'}
                    <FileItemCard
                      keyboardKey={row.ref}
                      path={row.value.remoteFilename}
                      sizeBytes={Number(row.value.file.size)}
                      audio={audio(row.value.file)}
                      locked={row.value.visibility === 'Locked'}
                      selected={isselected(row.ref)}
                      preferred={tier === 'Preferred'}
                      selectable={!selectionReadOnly}
                      onselect={(checked) => ontoggle(row.ref, checked)}
                    />
                  {:else}
                    {@const children = viewState.directoryFiles.get(row.ref)}
                    {@const files = children ? children.items.map((child) => folderFile(child.file, child.relativePath)) : [folderFile(row.value.bestChild)]}
                    {@const selectedFileIds = new Set(files.filter((file) => isselected(file.id, row.ref)).map((file) => file.id))}
                    {@const folderSelection = ordinaryDirectorySelection(row.ref, files.length, selectedFileIds.size)}
                    {@const totals = directoryTotals(row.value)}
                    {@const retrieval = viewState.retrievals.get(row.ref)}
                    <FolderItemCard
                      keyboardKey={row.ref}
                      contentsState={!row.value.ref.folderPath ? undefined : retrieval?.loading ? 'retrieving' : retrieval?.error ? 'failed' : row.value.retrievalState === 'Complete' ? 'complete' : 'partial'}
                      onloadfullcontents={() => onretrievedirectory(row.ref)}
                      path={row.value.ref.folderPath || 'Files without a folder'}
                      sizeBytes={totals.bytes}
                      {files}
                      totalFileCount={totals.fileCount}
                      filesComplete={Boolean(children && !children.nextCursor && (!children.loading || children.refreshing) && !children.error)}
                      fileLayout={genericMode ? 'tree' : 'flat'}
                      dataStateLabel={retrieval?.error?.message ?? children?.error?.message ?? (children?.loading && !children.refreshing ? 'Loading matching files…' : undefined)}
                      locked={row.value.visibility === 'Locked'}
                      selected={folderSelection.selected}
                      partial={folderSelection.partial}
                      preferred={tier === 'Preferred'}
                      selectable={!selectionReadOnly}
                      {selectedFileIds}
                      onselectall={(checked) => ontoggle(row.ref, checked)}
                      onselectfile={(file, checked) => ontoggle(file.id, checked, row.ref)}
                      onselectfolder={genericMode && !selectionReadOnly ? (path, checked) => onselectfolder(row.ref, path, checked) : undefined}
                      isfolderselected={genericMode ? (path) => isfolderselected(row.ref, path) : undefined}
                    />
                    {#if !children || (children.loading && !children.refreshing) || children.error || children.nextCursor}
                      <div class="load-more-row"><button type="button" disabled={children?.loading} onclick={() => onloaddirectory(row.ref)}>{children?.loading ? 'Loading…' : children?.error ? 'Retry matching files' : children ? 'Load more' : 'Load matching files'}</button></div>
                    {/if}
                  {/if}
                {/each}
            </PeerItemGroup>
          {/each}
        </div>
    {/each}
  {/if}

  {#if viewState.nextCursor}<div data-keyboard-page="results" data-loading={viewState.loadingMore} data-failed={Boolean(viewState.error)}><LoadMoreButton loading={viewState.loadingMore} disabled={viewState.refining} onclick={onloadmore}/></div>{/if}
  {/if}
</section>

{#if viewState.aggregateOptions}
  {@const panel = viewState.aggregateOptions}
  <div class="aggregate-options-modal">
    <button type="button" class="aggregate-options-backdrop" aria-label="Close options" onclick={oncloseoptions}></button>
    <div class="aggregate-options-dialog" role="dialog" aria-modal="true" aria-label={`${panel.title} options`}>
      <header class="aggregate-options-header">
        <div>
          <strong>{panel.title}</strong>
          <small>{panel.artist ? `${panel.artist} · ` : ''}{formatCountWithNoun(panel.shareCount, 'share')} · {panel.items.length === panel.optionCount && !panel.nextCursor
            ? formatCountWithNoun(panel.optionCount, 'option')
            : `${formatCount(panel.items.length)} of ${formatCountWithNoun(panel.optionCount, 'option')}`}</small>
        </div>
        <button type="button" class="aggregate-options-close" aria-label="Close options" onclick={oncloseoptions}>×</button>
      </header>
      <div class="aggregate-options-list">
        {#each panel.items as option (option.ref)}
          {@const peer = option.value.peer}
          <div class:current={(choiceIsCurrent(panel.groupRef) ? viewState.chosenOptions.get(panel.groupRef)?.ref : panel.representativeRef) === option.ref} class="aggregate-option">
            <div class="aggregate-option-toolbar">
              {@render aggregatePeerSummary(peer)}
              <button type="button" class="aggregate-use-option" disabled={selectionReadOnly} onclick={() => onchooseoption(panel.groupRef, option)}>Use this option</button>
            </div>
            <div class="aggregate-option-card-wrap">
              {#if option.kind === 'track'}
                <button type="button" class="aggregate-option-card-picker" disabled={selectionReadOnly} aria-label={`Use ${option.value.remoteFilename}`} onclick={() => onchooseoption(panel.groupRef, option)}></button>
                <FileItemCard path={option.value.remoteFilename} sizeBytes={Number(option.value.file.size)} audio={audio(option.value.file)} locked={option.value.visibility === 'Locked'} />
              {:else}
                {@const children = viewState.directoryFiles.get(option.ref)}
                {@const directory = children?.directorySummary ?? option.value}
                {@const totals = directoryTotals(directory)}
                {@const retrieval = viewState.retrievals.get(directory.ref.ref)}
                <button type="button" class="aggregate-option-card-picker" disabled={selectionReadOnly} aria-label={`Use ${directory.ref.folderPath || 'Files without a folder'}`} onclick={() => onchooseoption(panel.groupRef, option)}></button>
                <FolderItemCard
                  contentsState={!directory.ref.folderPath ? undefined : retrieval?.loading ? 'retrieving' : retrieval?.error ? 'failed' : directory.retrievalState === 'Complete' ? 'complete' : 'partial'}
                  onloadfullcontents={() => onretrievedirectory(directory.ref.ref)}
                  path={directory.ref.folderPath || 'Files without a folder'}
                  sizeBytes={totals.bytes}
                  files={children ? children.items.map((child) => folderFile(child.file, child.relativePath)) : [folderFile(directory.bestChild)]}
                  totalFileCount={totals.fileCount}
                  filesComplete={Boolean(children && !children.nextCursor && (!children.loading || children.refreshing) && !children.error)}
                  dataStateLabel={retrieval?.error?.message ?? children?.error?.message ?? (children?.loading && !children.refreshing ? 'Loading matching files…' : undefined)}
                  locked={directory.visibility === 'Locked'}
                />
                {#if !children || (children.loading && !children.refreshing) || children.error || children.nextCursor}
                  <div class="load-more-row">
                    <button type="button" disabled={children?.loading} onclick={() => onloaddirectory(directory.ref.ref)}>{children?.loading ? 'Loading…' : children?.error ? 'Retry matching files' : children ? 'Load more' : 'Load matching files'}</button>
                  </div>
                {/if}
              {/if}
            </div>
          </div>
        {/each}
        {#if panel.error}
          <div class="resource-state-notice unavailable" role="alert"><span class="resource-state-copy"><strong>Options unavailable</strong><small>{panel.error.message}</small></span><button type="button" onclick={onloadoptions}>Retry</button></div>
        {/if}
        {#if panel.loading && panel.items.length === 0}<LoadingIndicator label="Loading options" />{/if}
        {#if panel.nextCursor}<LoadMoreButton loading={panel.loading} onclick={onloadoptions}/>{/if}
      </div>
    </div>
  </div>
{/if}
