<script lang="ts">
  import ManualLink from '../../components/ManualLink.svelte';
  import HelpPopover from '../../components/HelpPopover.svelte';
  import { tick, untrack } from 'svelte';
  import { keyboardList } from '../../lib/keyboard';
  import EmptyState from '../../components/EmptyState.svelte';
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import type { JobSummaryDto, Schema, SearchViewRequest } from '../../api/types';
  import { searchViewUnavailable } from './controller';
  import { allSearchResultsSelected, selectionUsesEarlierRevision as usesEarlierRevision, type GroupOption } from './selection';
  import { DaemonApiError } from '../../api/http';
  import { formatCount, formatCountWithNoun, humanizeStateValue } from '../../app/format';
  import FileItemCard from '../../components/items/FileItemCard.svelte';
  import FolderItemCard from '../../components/items/FolderItemCard.svelte';
  import SelectionToolbar from '../../components/SelectionToolbar.svelte';
  import Icon from '../../components/Icon.svelte';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import PeerItemGroup from '../../components/items/PeerItemGroup.svelte';
  import PeerStats from '../../components/items/PeerStats.svelte';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import ProfilePicker from '../../components/ProfilePicker.svelte';
  import { rebaseOptions } from '../jobs/download-options';
  import DownloadOptionsPanel from '../../components/DownloadOptionsPanel.svelte';
  import ResultFilterControl from '../../components/ResultFilterControl.svelte';
  import { filterPattern, matchedText } from '../../components/MatchedText.svelte';
  import SearchConditionPills from '../../components/SearchConditionPills.svelte';
  import SearchConfigPanel from '../../components/SearchConfigPanel.svelte';
  import { anchoredPopover } from '../../lib/anchored-menu';
  import { readSearchConditions, rebaseSearchConditions, editedSearchSettings, searchExpectedLength } from './config-mapping';
  import { createEmptySearchConditions, type SearchConditions } from './config-state';
  import type { DownloadOptions } from '../jobs/download-options';
  import { buildSearchSelectionDownloadPatch, searchDownloadOptionDefaults, type SearchDownloadOptionDefaults } from './download-options';
  import type { UserLinkActions } from '../../app/navigation';
  import { audioAttributes as audio, type FolderItemFile } from '../presentation/items';
  import { directoryTotals, isGroupSearchMode, searchModeFamily, searchModeLabel } from './model';
  import { progressiveCount } from '../../lib/progressive.svelte';
  import { notify } from '../../lib/notifications.svelte';
  import type { SearchRow, SearchViewState, SearchRangeItem } from './controller';

  type OrdinaryRow = Extract<SearchRow, { kind: 'file' | 'directory' }>;

  interface Props {
    userActions: UserLinkActions;
    connected: boolean;
    configurationRevision?: number;
    daemonOffline?: boolean;
    viewState: SearchViewState;
    liveJob?: JobSummaryDto | null;
    liveJobError?: Error | null;
    removeAction?: 'cancel' | 'delete' | null;
    removePending?: boolean;
    actionError?: Error | null;
    onremove: () => void;
    searchAgainAvailable?: boolean;
    searchAgainPending?: boolean;
    isselected: (ref: string, parentRef?: string) => boolean;
    selectedCount: number;
    onbeginrange: (additive: boolean) => (items: SearchRangeItem[], selected: boolean) => void;
    ontoggle: (ref: string | readonly string[], selected: boolean, parentRef?: string) => void;
    onselectfolder: (directoryRef: string, relativePath: string, selected: boolean, includeFiltered?: boolean) => void;
    isfolderselected: (directoryRef: string, relativePath: string, filtered?: boolean) => boolean;
    onselectall: (selected: boolean) => void;
    ondownload: (options?: Schema<'SubmissionOptionsDto'>) => Promise<{ receipt: Schema<'CommitSearchViewSelectionResponseDto'> } | { error: Error } | null>;
    onlibrarymatches: (id: string, request: Schema<'SearchViewLibraryMatchesRequestDto'>, signal: AbortSignal) => Promise<string[]>;
    onresolvesettings: (options?: Schema<'SubmissionOptionsDto'>) => Promise<Schema<'ResolveSearchViewSelectionSettingsResponseDto'>>;
    oncheckrevision: (viewId: string, revision: number | string, signal: AbortSignal) => Promise<Schema<'SearchViewRevisionLifetimeDto'>>;
    onloaddefinition: (viewId: string) => Promise<Schema<'SearchViewDefinitionDto'>>;
    onrefine: (request: SearchViewRequest, resetSelection?: boolean) => Promise<boolean>;
    onsort: (order: Schema<'SearchViewSortOrder'>) => Promise<boolean>;
    onloadmore: () => void;
    onretry: () => void;
    onloaddirectory: (ref: string) => void;
    onretrievedirectory: (ref: string) => void;
    onopenoptions: (ref: string) => void;
    onstepoption: (ref: string, direction: -1 | 1) => void;
    oncloseoptions: () => void;
    onfilteroptions: (text: string) => void;
    onloadoptions: () => void;
    onchooseoption: (groupRef: string, option: GroupOption) => void;
    ontogglealbumfile: (groupRef: string, fileRefs: string[], selected: boolean) => void;
    onviewdownloads: (submissionId: string) => void;
    onopendownload: (jobId: string) => void;
    onback: () => void;
    onsearchagain: () => void;
  }

  let {
    userActions,
    connected,
    configurationRevision,
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
    onbeginrange,
    ontoggle,
    onselectfolder,
    isfolderselected,
    onselectall,
    ondownload,
    onlibrarymatches,
    onresolvesettings,
    oncheckrevision,
    onloaddefinition,
    onrefine,
    onsort,
    onloadmore,
    onretry,
    onloaddirectory,
    onretrievedirectory,
    onopenoptions,
    onstepoption,
    oncloseoptions,
    onfilteroptions,
    onloadoptions,
    onchooseoption,
    ontogglealbumfile,
    onviewdownloads,
    onopendownload,
    onback,
    onsearchagain,
  }: Props = $props();

  let status = $derived(liveJob?.lifecycle ?? viewState.job?.lifecycle);
  let searching = $derived(status?.lifecycleState !== 'Terminal'
    && status?.activityPhase === 'Searching');
  let filterText = $state('');
  let highlight = $derived(filterPattern(viewState.summary?.filterText));
  let optionFilter = $state('');
  let optionHighlight = $derived(filterPattern(viewState.groupOptions?.filterText ?? ''));
  $effect(() => {
    const panel = viewState.groupOptions;
    if (!panel) { optionFilter = ''; return; }
    const text = optionFilter.trim();
    if (text === panel.filterText) return;
    const timer = setTimeout(() => onfilteroptions(text), 200);
    return () => clearTimeout(timer);
  });
  let sort = $state<'relevance' | 'speed' | 'queue' | 'size' | 'count' | 'name'>('relevance');
  let sizeDirection = $state<'asc' | 'desc'>('desc');
  let conditions = $state<SearchConditions>(createEmptySearchConditions());
  let viewProfileNames = $state<string[]>([]);
  let baseline = $state<Schema<'SearchViewDefinitionDto'> | null>(null);
  let baselineConditions = $state<SearchConditions | null>(null);
  let inheritedConditions = $derived(baseline ? viewConditions(baseline, baseline.baseSearchSettings ?? baseline.effectiveSearchSettings) : null);
  let configurationJobId = $state<string | null>(null);
  let configurationLoading = $state(false);
  let configurationError = $state<Error | null>(null);
  let conditionsOpen = $state(false);
  let conditionsInitialTab = $state<'conditions' | 'ranking'>('conditions');
  let conditionsAnchor = $state<HTMLButtonElement>();
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
    Pruned: { title: 'Search results removed',
      detail: 'These results were removed from history. Search again for fresh results.' },
    Incomplete: { title: 'Search results are incomplete',
      detail: 'Some results couldn’t be saved, so only part of them is shown. Search again for fresh results.' },
    Interrupted: { title: 'Search was interrupted',
      detail: 'Only results found before the interruption are shown. Search again for fresh results.' },
  };
  let retentionNotice = $derived(viewState.summary ? retainedResultNotices[viewState.summary.retentionState] ?? null : null);
  let revisionError = $state<Error | null>(null);
  let checkingRevision = $state(false);
  let revisionRetry = $state(0);
  let checkedView = $derived(connected && selectedCount > 0 && !searchViewUnavailable(viewState)
    ? viewState.summary?.viewId : undefined);
  let checkedRevision = $derived(viewState.selectionRevision ?? viewState.summary?.revision);
  $effect(() => {
    const viewId = checkedView;
    const revision = checkedRevision;
    revisionRetry;
    revisionError = null;
    checkingRevision = false;
    if (!viewId || revision == null) return;
    const request = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    const check = async () => {
      checkingRevision = true;
      try {
        const lifetime = await oncheckrevision(viewId, revision, request.signal);
        if (!request.signal.aborted)
          timer = setTimeout(check, Math.min(Number(lifetime.remainingMilliseconds), 2_147_483_647));
      } catch (error) {
        if (!request.signal.aborted) revisionError = error instanceof Error ? error : new Error(String(error));
      } finally {
        if (!request.signal.aborted) checkingRevision = false;
      }
    };
    void check();
    return () => { request.abort(); clearTimeout(timer); };
  });

  let viewUnavailable = $derived(!daemonOffline && (searchViewUnavailable(viewState)
    || (revisionError instanceof DaemonApiError && revisionError.status === 404)
    || (configurationError instanceof DaemonApiError && configurationError.status === 404)));
  // The daemon drops a results view some time after nobody watches it (a
  // sleeping laptop, an idle tab, a restart). Rebuild it from the stored
  // results when nothing would be lost; ask first when a selection would be.
  let autoReloaded = false;
  $effect(() => {
    if (!viewUnavailable) { autoReloaded = false; return; }
    if (autoReloaded || !connected || selectedCount > 0 || viewState.canRetryPendingCommit || viewState.refining) return;
    autoReloaded = true;
    untrack(() => void requestRefinement(true));
  });
  let hasBareFileGroups = $derived(genericMode && viewState.rows.some(row =>
    row.kind === 'directory' && row.value.ref.folderPath.length === 0));
  let selectionUsesEarlierRevision = $derived(usesEarlierRevision(viewState));
  // Rows keep their shape while a refinement runs (the page dims and ignores clicks
  // instead): reshaping every row on each filter change rebuilt the whole list twice.
  let rowsReadOnly = $derived(selectionUsesEarlierRevision || viewState.selectionNeedsReview || viewUnavailable);
  let selectionReadOnly = $derived(rowsReadOnly || viewState.refining);
  // The outcome is a notification: the list may be scrolled far from any inline status.
  async function download(): Promise<void> {
    // Generic directory jobs group transfers internally. For a fully accepted
    // revision-bound selection, report the public file count the user reviewed.
    const reviewed = genericMode ? viewState.selectionCounts : null;
    const outcome = await ondownload(downloadCustomized ? downloadPatch : undefined);
    if (!outcome) return;
    if ('error' in outcome) {
      notify({ key: 'search-download', tone: 'error', title: 'Download request failed', details: [outcome.error.message] });
      return;
    }
    const { receipt } = outcome;
    const skipped = Number(receipt.skippedCount), rejected = Number(receipt.rejectedCount);
    const count = !skipped && !rejected && reviewed && String(reviewed.revision) === String(receipt.viewRevision)
      ? reviewed.publicFileCount : receipt.submittedCount;
    const { jobId, submissionId } = receipt;
    notify({
      key: 'search-download',
      tone: skipped || rejected ? 'warning' : 'success',
      title: `${formatCount(count)} download${Number(count) === 1 ? '' : 's'} requested`,
      details: [
        ...(skipped || rejected ? [`${formatCount(receipt.skippedCount)} skipped · ${formatCount(receipt.rejectedCount)} rejected`] : []),
        ...receipt.rejectionReasons.flatMap(reason => reason.message ? [reason.message] : []),
      ],
      action: jobId ? { label: 'View download job', testId: 'open-download-job', run: () => onopendownload(jobId) }
        : submissionId ? { label: 'View download jobs', testId: 'view-download-jobs', run: () => onviewdownloads(submissionId) }
        : undefined,
    });
  }
  let groupMode = $derived(viewState.draft != null && isGroupSearchMode(viewState.draft.resultMode));
  let refinementPending = $derived(viewState.refining || Boolean(localRefinementError || viewState.refinementError)
    || (baseline !== null && lastRequestedFingerprint !== currentFingerprint()));

  function emptyResultsHeading(): string {
    if (retentionNotice) return retentionNotice.title;
    if (!receivedFileCount()) return 'No results';
    return groupMode ? 'No matching groups' : genericMode ? 'No matching directories' : 'No matching results';
  }

  function receivedFileCount(): number {
    const counters = viewState.summary?.counters;
    return Number(counters?.publicFileCount ?? 0) + Number(counters?.lockedFileCount ?? 0);
  }

  function emptyResultsDetail(): string {
    if (retentionNotice) return retentionNotice.detail;
    const count = receivedFileCount();
    if (!count) return 'No files were returned by peers.';
    return `${formatCountWithNoun(count, 'file')} received, but none match the current ${filterText.trim() ? 'text filter or ' : ''}${groupMode ? 'conditions and grouping requirements' : 'result conditions'}.`;
  }

  function currentFingerprint(): string {
    return JSON.stringify([projectionFingerprint(), sort, sizeDirection]);
  }

  function currentSortOrder(): Schema<'SearchViewSortOrder'> {
    if (sort === 'speed') return 'UploadSpeed';
    if (sort === 'queue') return 'QueueDepth';
    if (sort === 'size') return genericMode
      ? sizeDirection === 'asc' ? 'DirectorySizeAscending' : 'DirectorySizeDescending'
      : sizeDirection === 'asc' ? 'ItemSizeAscending' : 'ItemSizeDescending';
    if (sort === 'count') return sizeDirection === 'asc' ? 'FileCountAscending' : 'FileCountDescending';
    if (sort === 'name') return sizeDirection === 'asc' ? 'DirectoryNameAscending' : 'DirectoryNameDescending';
    return 'Relevance';
  }

  function projectionFingerprint(text = filterText, values = conditions, profiles = viewProfileNames): string {
    return JSON.stringify([text, values, profiles]);
  }

  function viewConditions(definition: Schema<'SearchViewDefinitionDto'>, settings = definition.effectiveSearchSettings): SearchConditions {
    const length = definition.projection.songQuery?.length;
    return readSearchConditions(settings, length == null ? undefined : Number(length));
  }

  async function requestRefinement(resetSelection = false): Promise<void> {
    if (!baseline || !baselineConditions) {
      if (resetSelection) window.location.reload();
      return;
    }
    const version = ++refinementRequest;
    try {
      const fingerprint = currentFingerprint();
      const projection = projectionFingerprint();
      const profilesChanged = JSON.stringify(viewProfileNames) !== JSON.stringify(baseline.projection.profileNames ?? []);
      if (!resetSelection && !profilesChanged && !viewState.refining && projection === appliedProjectionFingerprint) {
        localRefinementError = null;
        lastRequestedFingerprint = fingerprint;
        await onsort(currentSortOrder());
        return;
      }
      const submitted = profilesChanged ? $state.snapshot(conditions) : null;
      const request: SearchViewRequest = {
        kind: baseline.projection.kind,
        songQuery: baseline.projection.songQuery,
        albumQuery: baseline.projection.albumQuery,
        profileNames: viewProfileNames.length ? [...viewProfileNames] : undefined,
        filterText,
        sortOrder: currentSortOrder(),
        refinementSettings: editedSearchSettings(viewState.draft!.resultMode, baseline.projection.refinementSettings,
          baselineConditions, conditions, inheritedConditions),
      };
      if (searchModeFamily(viewState.draft!.resultMode) === 'track' && request.songQuery) {
        request.songQuery = { ...request.songQuery, length: searchExpectedLength(conditions.track.expectedLength) ?? null };
      }
      localRefinementError = null;
      lastRequestedFingerprint = fingerprint;
      if (!await onrefine(request, resetSelection) || version !== refinementRequest) return;
      appliedProjectionFingerprint = projection;
      if (submitted) {
        const definition = await onloaddefinition(viewState.summary!.viewId);
        if (version !== refinementRequest) return;
        const settled = currentFingerprint() === fingerprint;
        const effective = viewConditions(definition);
        conditions = rebaseSearchConditions(submitted, conditions, effective);
        baseline = definition;
        baselineConditions = effective;
        appliedProjectionFingerprint = projectionFingerprint(request.filterText ?? '', effective, request.profileNames ?? []);
        if (settled) lastRequestedFingerprint = currentFingerprint();
      }
    } catch (error) {
      if (version === refinementRequest) localRefinementError = error instanceof Error ? error : new Error(String(error));
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
      const read = viewConditions(definition);
      viewProfileNames = definition.projection.profileNames ?? [];
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
    if (row.kind === 'album-group') return albumFull(row.ref);
    if (row.kind !== 'directory') return isselected(row.ref);
    const children = viewState.directoryFiles.get(row.ref);
    const files = children ? children.items.map(child => child.file) : [row.value.bestChild];
    return ordinaryDirectorySelection(row.ref, files.filter(file => file.visibility !== 'Locked').length,
      files.filter(file => file.visibility !== 'Locked' && isselected(file.ref, row.ref)).length).selected;
  }

  function searchRangeItem(row: HTMLElement): SearchRangeItem {
    return { ref: row.dataset.keyboardItem!, folder: row.hasAttribute('data-selection-folder'),
      parentRef: row.parentElement?.closest<HTMLElement>('[data-keyboard-item]')?.dataset.keyboardItem };
  }

  function beginRange(additive: boolean) {
    const apply = onbeginrange(additive);
    return (rows: HTMLElement[], selected: boolean) => apply(rows.map(searchRangeItem), selected);
  }

  function retrieveSelectedFolders(): void {
    if (selectionReadOnly || viewState.committing || refinementPending) return;
    for (const row of viewState.rows) {
      if (!rowFullySelected(row)) continue;
      let directory: Schema<'SearchViewDirectoryDto'>;
      if (row.kind === 'directory') directory = row.value;
      else if (row.kind === 'album-group' && (!viewState.chosenOptions.has(row.ref) || choiceIsCurrent(row.ref))) {
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
    if (viewState.groupOptions) oncloseoptions();
    else if (downloadOptionsOpen) downloadOptionsOpen = false;
    else if (conditionsOpen) conditionsOpen = false;
  }

  function windowPointerDown(event: PointerEvent): void {
    if (!(event.target instanceof Node)) return;
    if (!conditionsOpen || conditionsAnchor?.contains(event.target) || conditionsPopover?.contains(event.target)) return;
    conditionsOpen = false;
  }
  let profileNames = $state<string[]>([]);
  let appliedProfiles = $state<Schema<'AppliedProfileDto'>[]>([]);
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
    viewState.directoryPathRules.map(rule => [rule.directoryRef, rule.relativePath, rule.selected, rule.includeFilteredFiles]).sort(),
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

  const emptyPaths: ReadonlySet<string> = new Set();
  let filteredSelectionPaths = $derived.by(() => {
    const byDirectory = new Map<string, string[]>();
    for (const rule of viewState.directoryPathRules) {
      if (!rule.selected || rule.includeFilteredFiles || !rule.relativePath) continue;
      const paths = byDirectory.get(rule.directoryRef);
      if (paths) paths.push(rule.relativePath);
      else byDirectory.set(rule.directoryRef, [rule.relativePath]);
    }
    return byDirectory;
  });

  function filteredSubfolders(folders: (string | null | undefined)[]): Set<string> {
    const paths = new Set<string>();
    for (let path of folders) {
      while (path && !paths.has(path)) {
        paths.add(path);
        const end = path.lastIndexOf('/');
        path = end < 0 ? null : path.slice(0, end);
      }
    }
    return paths;
  }

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
    appliedProfiles = [];
    downloadDefaults = null;
    downloadValues = null;
    downloadOptionsError = null;
    touchedMixedFields = new Set();
  });
  let visibleMixedFields = $derived(new Set(
    [...(downloadDefaults?.mixedFields ?? [])].filter(field => !touchedMixedFields.has(field)),
  ));
  let downloadPatch = $derived({
    ...(downloadDefaults && downloadValues ? buildSearchSelectionDownloadPatch(downloadValues, downloadDefaults, touchedMixedFields) : {}),
    ...(profileNames.length ? { profileNames: [...profileNames] } : {}),
  });
  let downloadCustomized = $derived(Boolean(downloadPatch && Object.keys(downloadPatch).length));

  let libraryMatches = $state<ReadonlySet<string>>(new Set());
  let libraryStatus = $state('');
  let libraryRetry = $state(0);
  let resultRows = $derived(viewState.rows);
  let libraryRows = $derived(resultRows[0]?.kind === 'track-group' || resultRows[0]?.kind === 'album-group' ? resultRows : []);
  let libraryViewId = $derived(viewState.summary?.viewId);
  let libraryRevision = $derived(viewState.summary?.revision);
  $effect(() => {
    configurationRevision;
    libraryRetry;
    const id = libraryViewId;
    const revision = libraryRevision;
    const rows = libraryRows;
    libraryMatches = new Set();
    libraryStatus = '';
    if (!connected || !id || revision == null || !rows.length || downloadOptionsOpen) return;

    const controller = new AbortController();
    onlibrarymatches(id, { revision, groupRefs: rows.map(row => row.ref), options: downloadPatch }, controller.signal)
      .then(matches => {
        if (controller.signal.aborted) return;
        libraryMatches = new Set(matches);
        libraryStatus = '';
      }).catch(() => {
        if (!controller.signal.aborted) libraryStatus = 'Library check unavailable';
      });
    return () => controller.abort();
  });

  async function loadDownloadOptions(): Promise<void> {
    const request = ++optionsRequest;
    downloadOptionsLoading = true;
    downloadOptionsError = null;
    try {
      const resolved = await onresolvesettings({ profileNames });
      const defaults = searchDownloadOptionDefaults(resolved);
      if (request !== optionsRequest) return;
      const exactAlbumFiles = [...viewState.albumFileSelections.keys()]
        .some(ref => isselected(ref));
      appliedProfiles = resolved.variants.flatMap(variant => variant.appliedProfiles)
        .filter((profile, index, all) => all.findIndex(other => other.name === profile.name && other.source === profile.source) === index);
      downloadValues = rebaseOptions(downloadDefaults?.values ?? null, downloadValues ?? defaults.values, defaults.values, touchedMixedFields);
      downloadDefaults = exactAlbumFiles ? {
        ...defaults,
        capabilities: { ...defaults.capabilities, albumFolderEnrichment: false },
      } : defaults;

    } catch (error) {
      if (request === optionsRequest)
        downloadOptionsError = error instanceof Error ? error : new Error(String(error));
    } finally {
      if (request === optionsRequest) downloadOptionsLoading = false;
    }
  }

  // Keep the original options until a lost download receipt can be recovered.
  let awaitingDownloadReceipt = $derived(viewState.canRetryPendingCommit || viewState.committing);
  let refreshedConfiguration: number | undefined;
  $effect(() => {
    const revision = configurationRevision ?? 0;
    if (!connected) { refreshedConfiguration = undefined; return; }
    if (refreshedConfiguration === revision || awaitingDownloadReceipt) return;
    refreshedConfiguration = revision;
    untrack(() => {
      if (downloadDefaults || downloadOptionsOpen) void loadDownloadOptions();
    });
  });

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
    : Boolean(viewState.job && viewState.job.lifecycle.lifecycleState !== 'Terminal' && !viewState.summary?.isComplete)));

  const folderFileCache = new WeakMap<Schema<'SearchViewFileDto'>, FolderItemFile>();
  function folderFile(file: Schema<'SearchViewFileDto'>, relativePath?: string): FolderItemFile {
    const path = relativePath ?? file.file.name;
    const cached = folderFileCache.get(file);
    if (cached?.relativePath === path) return cached;
    const result: FolderItemFile = {
      id: file.ref,
      relativePath: path,
      sourcePath: file.remoteFilename,
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
    return viewState.rows.some(row => (row.kind === 'track-group' || row.kind === 'album-group')
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

  function preference(row: OrdinaryRow): Schema<'SearchPreferenceTier'> {
    return row.value.preferenceTier;
  }

  function queryTitle(): string {
    const title = viewState.job?.display.itemName ?? viewState.job?.display.queryText;
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
      if (job?.lifecycle.lifecycleState === 'Terminal') return job.lifecycle.terminalOutcome
        ? searchOutcome(job.lifecycle.terminalOutcome) : 'Complete';
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
      .map(({ tier, rows }) => ({ tier, groups: samePeerGroups(rows) }));
  }
  let groupedRows: SearchRow[] | null = null;
  let groupedByPreference = false;
  let cachedGroups: ReturnType<typeof groupRows> = [];
  // A results page builds its list a slice per frame when it opens. Later filters
  // and sorts update the keyed list in place, which reuses the rows they keep.
  const rendered = progressiveCount(() => viewState.rows.length);
  // Until every row is built, reserve their estimated height so the scrollbar
  // shows the whole list from the start instead of growing under the reader.
  let reserve = $state<HTMLElement>();
  let rowHeight = $state(0);
  let reservedHeight = $derived(rowHeight && Math.max(0, Math.round(rowHeight * (viewState.rows.length - rendered.count))));
  $effect(() => {
    // Measure the first slice once: reading layout on every frame would force
    // the whole growing list to lay out again each time.
    if (rendered.count !== rendered.slice) return;
    const first = reserve?.parentElement?.querySelector('.result-tier-heading, .result-tier, .group-results-list');
    if (!reserve || !first) return;
    rowHeight = (reserve.getBoundingClientRect().top - first.getBoundingClientRect().top) / rendered.count;
  });
  let visibleTiers = $derived.by(() => {
    let budget = rendered.count;
    const visible: (typeof groupedTiers[number] & { pending: typeof groupedTiers[number]['groups'] })[] = [];
    for (const { tier, groups } of groupedTiers) {
      if (budget <= 0) break;
      const kept: typeof groups = [];
      // Peers not built yet still size this tier's columns, so none shift as they arrive.
      const pending: typeof groups = [];
      for (const group of groups) {
        if (budget <= 0) { pending.push(group); continue; }
        // Never empty: a group is only visited while budget remains.
        const rows = (group.rows.length > budget ? group.rows.slice(0, budget) : group.rows) as typeof group.rows;
        budget -= rows.length;
        kept.push(rows === group.rows ? group : { ...group, rows });
      }
      visible.push({ tier, groups: kept, pending });
    }
    return visible;
  });
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

{#snippet groupPeerSummary(peer: Schema<'PeerInfoDto'>, pattern: RegExp | undefined)}
  <div class="group-peer-summary">
    <span class="group-peer-username"><UsernameLink highlight={pattern} username={peer.username} actions={userActions} /></span>
    <PeerStats {peer} />
  </div>
{/snippet}

{#snippet groupOptionControls(ref: string, count: number | string)}
  {@const step = viewState.optionSteps.get(ref)}
  {@const revision = `${viewState.summary?.viewId}:${String(viewState.summary?.revision)}`}
  {@const choice = choiceIsCurrent(ref) ? viewState.chosenOptions.get(ref) : undefined}
  {@const position = choice ? (step?.revision === revision && step.optionRef === choice.ref ? step.position : undefined) : 1}
  <div class="group-result-stats">
    <button type="button" class="button quiet group-option-step" aria-label="Previous option" title="Previous option" disabled={selectionReadOnly || position === 1 || step?.loading} onclick={() => onstepoption(ref, -1)}><svg viewBox="0 0 20 20" aria-hidden="true"><path d="m12 5-5 5 5 5" /></svg></button>
    <button type="button" class="button group-options-button" disabled={step?.loading} onclick={() => onopenoptions(ref)}>{position ? formatCount(position) : '—'} / {formatCount(count)} {Number(count) === 1 ? 'option' : 'options'}</button>
    <button type="button" class="button quiet group-option-step" aria-label="Next option" title="Next option" disabled={selectionReadOnly || position === Number(count) || step?.loading} onclick={() => onstepoption(ref, 1)}><svg viewBox="0 0 20 20" aria-hidden="true"><path d="m8 5 5 5-5 5" /></svg></button>
  </div>
{/snippet}

{#snippet groupIdentity(row: Extract<SearchRow, { kind: 'track-group' | 'album-group' }>)}
  <span class="group-result-title">
    <strong>{@render matchedText(row.kind === 'track-group' ? row.value.query.title ?? 'Untitled track' : row.value.query.album ?? 'Untitled album', highlight)}</strong>
    {#if libraryMatches.has(row.ref)}
      <span id={`library-${row.ref}`} class="status-label complete" title={row.kind === 'track-group'
        ? 'A matching song was found locally. Download requests this peer’s file.'
        : 'A matching album was found locally. Download requests the selected files from this peer.'}>In library</span>
    {/if}
  </span>
  {#if row.value.query.artist}<small>{@render matchedText(row.value.query.artist, highlight)}</small>{/if}
{/snippet}

{#snippet groupGroupCard(row: Extract<SearchRow, { kind: 'track-group' | 'album-group' }>)}
  {#if row.kind === 'track-group'}
    {@const representative = trackRepresentative(row.ref, row.value.representative)}
    <section class:selected={isselected(row.ref)} class="panel group-result-group" data-keyboard-item={row.ref} data-range-disabled={representative.visibility === 'Locked' || undefined} tabindex="-1">
      <header class="group-result-header">
        <button type="button" class="group-header-select-button group-result-identity" data-range-toggle disabled={selectionReadOnly || representative.visibility === 'Locked'} aria-describedby={libraryMatches.has(row.ref) ? `library-${row.ref}` : undefined} aria-label={`${isselected(row.ref) ? 'Deselect' : 'Select'} ${row.value.query.title ?? 'track'}`} aria-pressed={isselected(row.ref)} onclick={() => ontoggle(row.ref, !isselected(row.ref))}>
          {@render groupIdentity(row)}
        </button>
        <div class="group-result-source">{@render groupPeerSummary(representative.peer, highlight)}</div>
        {@render groupOptionControls(row.ref, row.value.optionCount)}
      </header>
      {#if viewState.optionSteps.get(row.ref)?.error}<div class="resource-state-notice unavailable" role="alert">Could not load option: {viewState.optionSteps.get(row.ref)?.error?.message}</div>{/if}
      {#if viewState.chosenOptions.has(row.ref) && !choiceIsCurrent(row.ref)}
        <div class="resource-state-notice degraded group-choice-notice" role="status">
          <span class="resource-state-dot"></span>
          <span>{viewState.staleChoices.has(row.ref)
            ? 'Chosen source is no longer in these results. Select another option before downloading.'
            : viewState.choiceValidationError
              ? 'Could not check the chosen source. Retry the result refresh before downloading.'
              : 'Checking the chosen source in the updated results…'}</span>
          {#if viewState.staleChoices.has(row.ref)}<button class="button" type="button" onclick={() => onopenoptions(row.ref)}>Choose another</button>{/if}
        </div>
      {/if}
      <FileItemCard {highlight} path={representative.remoteFilename} sizeBytes={Number(representative.file.size)} audio={audio(representative.file)} locked={representative.visibility === 'Locked'} selected={isselected(row.ref)} selectable={!rowsReadOnly} onselect={(checked) => ontoggle(row.ref, checked)} />
    </section>
  {:else}
    {@const chosenDirectory = choiceIsCurrent(row.ref) && viewState.chosenOptions.get(row.ref)?.kind === 'album' ? viewState.chosenOptions.get(row.ref)?.value as Schema<'SearchViewDirectoryDto'> : row.value.representative}
    {@const children = viewState.directoryFiles.get(chosenDirectory.ref.ref)}
    {@const directory = children?.directorySummary ?? chosenDirectory}
    {@const files = children ? children.items.map((child) => folderFile(child.file, child.relativePath)) : [folderFile(directory.bestChild)]}
    {@const totals = directoryTotals(directory)}
    {@const retrieval = viewState.retrievals.get(directory.ref.ref)}
    <section class:selected={albumFull(row.ref)} class:partial={albumPartial(row.ref)} class="panel group-result-group" data-keyboard-item={row.ref} data-range-disabled={directory.visibility === 'Locked' || undefined} tabindex="-1">
      <header class="group-result-header">
        <button type="button" class="group-header-select-button group-result-identity" data-range-toggle disabled={selectionReadOnly || directory.visibility === 'Locked'} aria-describedby={libraryMatches.has(row.ref) ? `library-${row.ref}` : undefined} aria-label={`${albumFull(row.ref) ? 'Deselect' : 'Select'} ${row.value.query.album ?? 'album'}`} aria-pressed={albumFull(row.ref)} onclick={() => ontoggle(row.ref, !albumFull(row.ref))}>
          {@render groupIdentity(row)}
        </button>
        <div class="group-result-source">{@render groupPeerSummary(directory.peer, highlight)}</div>
        {@render groupOptionControls(row.ref, row.value.optionCount)}
      </header>
      {#if viewState.optionSteps.get(row.ref)?.error}<div class="resource-state-notice unavailable" role="alert">Could not load option: {viewState.optionSteps.get(row.ref)?.error?.message}</div>{/if}
      {#if viewState.chosenOptions.has(row.ref) && !choiceIsCurrent(row.ref)}
        <div class="resource-state-notice degraded group-choice-notice" role="status">
          <span class="resource-state-dot"></span>
          <span>{viewState.staleChoices.has(row.ref)
            ? 'Chosen folder is no longer in these results. Select another option before downloading.'
            : viewState.choiceValidationError
              ? 'Could not check the chosen folder. Retry the result refresh before downloading.'
              : 'Checking the chosen folder in the updated results…'}</span>
          {#if viewState.staleChoices.has(row.ref)}<button class="button" type="button" onclick={() => onopenoptions(row.ref)}>Choose another</button>{/if}
        </div>
      {/if}
      <FolderItemCard {highlight} keyboardChildren
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
        selectable={!rowsReadOnly}
        selectedFileIds={new Set(files.filter((file) => !file.locked && isselected(file.id, row.ref)).map((file) => file.id))}
        onselectall={(checked) => ontoggle(row.ref, checked)}
        onselectfiles={(files, checked) => ontogglealbumfile(row.ref, files.map(file => file.id), checked)}
      />
      {#if !children || (children.loading && !children.refreshing) || children.error || children.nextCursor}
        <div class="load-more-row"><button class="button" type="button" disabled={children?.loading} onclick={() => onloaddirectory(directory.ref.ref)}>{children?.loading ? 'Loading…' : children?.error ? 'Retry matching files' : children ? 'Load more' : 'Load matching files'}</button></div>
      {/if}
    </section>
  {/if}
{/snippet}

<section class="page page-search redesigned-search-page" class:refining={viewState.refining} use:keyboardList={{
  scope: `${viewState.job?.jobId}:${viewState.summary?.viewId}:${viewState.sortOrder}`,
  revision: [viewState.rows, viewState.loadingMore, rendered.count],
  enabled: viewState.phase === 'ready' && !refinementPending,
  beginRange: selectionReadOnly || viewState.committing ? undefined : beginRange,
  // Each group is a different song or album, so choosing one never drops another.
  toggleOnClick: groupMode,
  retrieve: retrieveSelectedFolders,
  loadPage: onloadmore,
}}>
  <header class="detail-heading search-results-heading">
    <button type="button" class="button quiet icon-button back-button" aria-label="Back to jobs" onclick={onback}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M12.5 4.5L7 10l5.5 5.5M7.5 10H16" /></svg>
    </button>
    <div class="detail-heading-title search-results-title">
      <h1>{queryTitle()}</h1>
    </div>
    <p class="detail-heading-meta search-results-meta">{viewState.draft ? searchModeLabel(viewState.draft.resultMode) : 'Search'}</p>
    <div class="detail-heading-facts search-results-summary">
      {#if viewState.phase === 'ready'}
      {#if statusLabel !== 'Complete'}
        <span class={`status-label search-status-badge ${isActive ? 'searching' : retentionNotice ? '' : 'complete'}`}>{statusLabel}</span>
      {/if}
      {#if (!retentionNotice && !viewUnavailable) || viewState.rows.length > 0}
      {#if viewState.draft?.resultMode === 'generic'}<span>{formatCountWithNoun(viewState.summary?.counters.topLevelItemCount, hasBareFileGroups ? 'group' : 'directory', hasBareFileGroups ? 'groups' : 'directories')}</span>{/if}
      {#if groupMode}<span>{formatCountWithNoun(viewState.summary?.counters.topLevelItemCount, 'group')}</span>{/if}
      <span>{formatCountWithNoun(viewState.summary?.counters.publicFileCount, 'file')}</span>
      <span>{formatCount(viewState.summary?.counters.lockedFileCount)} locked</span>
      <span>{formatCountWithNoun(viewState.summary?.counters.observedPeerCount, 'peer')}</span>
      {/if}
      {/if}
    </div>
    <div class="detail-heading-actions search-results-actions">
      <button
        type="button"
        class="button action-button collapse-narrow"
        disabled={!searchAgainAvailable || searchAgainPending}
        title={searchAgainAvailable ? 'Run this search again with the same settings' : 'Single-search replay is unavailable for this job'}
        onclick={onsearchagain}
      ><Icon name="search" /><span>{searchAgainPending ? 'Starting…' : 'Search again'}</span></button>
      {#if removeAction}
        <button type="button" class="button danger action-button collapse-narrow" disabled={removePending} aria-label={`${removeAction === 'cancel' ? 'Cancel' : 'Delete'} ${queryTitle()}`} title={removeAction === 'cancel' ? 'Cancel job' : 'Delete search'} onclick={onremove}>
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

  {#if viewUnavailable && !viewState.refining}
    <div class="resource-state-notice unavailable" role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>Results expired</strong><small>These results expired while the page was idle.
        {#if viewState.canRetryPendingCommit}Retry your download request to recover its outcome.
        {:else}Reload them to continue; your selection will be cleared.{/if}
      </small></span>
      {#if !viewState.canRetryPendingCommit}<button class="button" type="button" disabled={viewState.refining} onclick={() => requestRefinement(true)}>Reload results</button>{/if}
    </div>
  {/if}

  {#if (viewState.error || revisionError || liveJobError) && !viewUnavailable}
    <div class={`resource-state-notice ${daemonOffline ? 'offline' : 'unavailable'}`} class:blocking={viewState.phase === 'failed'} role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>{daemonOffline ? 'Daemon unavailable' : revisionError ? 'Selected results could not be checked' : viewState.error ? 'Search results could not be refreshed' : 'Job updates could not be refreshed'}</strong><small>{daemonOffline ? 'This can’t be loaded while the daemon is offline.' : (revisionError ?? viewState.error ?? liveJobError)?.message}</small></span>
      {#if !daemonOffline}<button class="button" type="button" onclick={() => { if (revisionError) revisionRetry++; else onretry(); }}>Retry</button>{/if}
    </div>
  {/if}

  {#if viewState.choiceValidationError && !viewUnavailable}
    <div class="resource-state-notice unavailable" role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>Chosen sources could not be checked</strong><small>{viewState.choiceValidationError.message}</small></span>
      <button class="button" type="button" onclick={onretry}>Retry</button>
    </div>
  {/if}

  {#if !viewUnavailable && (configurationError || localRefinementError || viewState.refinementError)}
    <div class="resource-state-notice unavailable" role="alert">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>{configurationError ? 'Result configuration unavailable' : 'Search results could not be updated'}</strong><small>{viewState.refinementError ? 'Previous results are still shown. ' : ''}{(configurationError ?? localRefinementError ?? viewState.refinementError)?.message}</small></span>
      {#if configurationError}<button class="button" type="button" onclick={() => { configurationJobId = null; }}>Retry</button>
      {:else}<button class="button" type="button" onclick={() => requestRefinement()}>Retry</button>{/if}
    </div>
  {/if}

  {#if viewState.phase !== 'failed'}
  <div class="result-refine-wrap">
    <div class="result-refine-row">
      <ResultFilterControl bind:value={() => filterText, changeFilter} placeholder={genericMode ? 'Filter files or directories…' : 'Filter results…'} ariaLabel="Filter search results">
        {#if !groupMode}
          <!-- Narrow layouts sort from inside the filter bar; the native picker opens over the icon. -->
          <label class="result-sort-inline" class:active={sort !== 'relevance'} title="Sort">
            <Icon name="sort" />
            <select aria-label="Sort results" value={sort} onchange={changeSort}>{@render sortOptions()}</select>
          </label>
          {@render sortDirection('inline')}
        {/if}
        <button type="button" class:active={conditionsOpen} class="edit-conditions-button" aria-label="Filtering" title="Filtering" aria-expanded={conditionsOpen} disabled={configurationLoading || !baseline} onclick={(event) => { conditionsAnchor = event.currentTarget; conditionsInitialTab = 'conditions'; conditionsOpen = !conditionsOpen; }}>
          <Icon name="sliders" />
        </button>
      </ResultFilterControl>
      {#if !groupMode}
        <div class="result-sort-control">
          <label for="result-sort">Sort</label>
          <select class="field" id="result-sort" value={sort} onchange={changeSort}>{@render sortOptions()}</select>
          {@render sortDirection('')}
        </div>
      {/if}
      {#snippet sortOptions()}
        <option value="relevance">Relevance</option>
        <option value="speed">Upload speed</option>
        {#if genericMode}
          <option value="size">Directory size</option><option value="count">File count</option><option value="name">Directory name</option>
        {:else}
          <option value="queue">Queue depth</option><option value="size">Item size</option>
        {/if}
      {/snippet}
      {#snippet sortDirection(placement: string)}
        {#if sort === 'size' || (genericMode && (sort === 'count' || sort === 'name'))}
          <button type="button" class={`button size-direction-button ${placement}`} aria-label={`Reverse ${genericMode ? 'directory' : 'item'} sort`} onclick={reverseSort}>
            <svg class:ascending={sizeDirection === 'asc'} viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M6 8l4-4 4 4" /></svg>
          </button>
        {/if}
      {/snippet}
    </div>
    {#if viewState.draft}
      <SearchConditionPills mode={viewState.draft.resultMode} bind:conditions />
    {/if}
    {#if conditionsOpen && viewState.draft}
      <section data-shortcuts-blocked="true" bind:this={conditionsPopover} class="popover-panel form search-config-popover results-config-popover" aria-label="Result search configuration" use:anchoredPopover={{ anchor: conditionsAnchor, align: 'start', gap: 8, viewportMargin: 16, minHeight: 240 }}>
        <SearchConfigPanel mode={viewState.draft.resultMode} bind:conditions title="Search configuration"
          profiles={{ value: viewProfileNames, onchange: names => viewProfileNames = names }}
          loadDefaults={() => inheritedConditions!} initialTab={conditionsInitialTab} onclose={() => (conditionsOpen = false)} />
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



  {#if selectedCount && downloadOptionsOpen}
    <button type="button" class="floating-options-backdrop" aria-label="Close download options" onclick={() => (downloadOptionsOpen = false)}></button>
    <section inert={viewState.committing} data-shortcuts-blocked="true" class="popover-panel floating-download-options-popover" aria-label="Download options">
      <header><strong>Download options</strong><div class="download-options-actions"><ProfilePicker disabled={viewState.committing} value={profileNames} applied={appliedProfiles} onchange={(names) => { profileNames = names; void loadDownloadOptions(); }} /><button type="button" aria-label="Close download options" onclick={() => (downloadOptionsOpen = false)}>×</button></div></header>
      {#if downloadOptionsLoading}
        <LoadingIndicator label="Loading effective defaults" />
      {:else if downloadOptionsError}
        <p class="download-option-note" role="alert">{downloadOptionsError.message}</p>
        <button type="button" onclick={() => void loadDownloadOptions()}>Retry</button>
      {:else if downloadDefaults && downloadValues}
        <DownloadOptionsPanel bind:value={downloadValues} capabilities={downloadDefaults.capabilities} mixedFields={visibleMixedFields} onfieldchange={markDownloadField} compact />
      {/if}
    </section>
  {/if}

  {#if retentionNotice && viewState.rows.length > 0}
    <div class="resource-state-notice unavailable" role="status">
      <span class="resource-state-dot"></span>
      <span class="resource-state-copy"><strong>{retentionNotice.title}</strong><small>{retentionNotice.detail}</small></span>
    </div>
  {/if}

  <!-- Results wait for the view's definition too (read in parallel), so its filter
       pills never arrive after the list and push it down. -->
  {#if (viewState.phase === 'loading' || configurationLoading) && viewState.rows.length === 0}
    <LoadingIndicator label="Loading search results" blocking />
  {:else if viewState.rows.length === 0 && !viewState.error && !viewUnavailable}
    {#if !retentionNotice && !viewState.summary?.isComplete && !daemonOffline}
      {#if searching && receivedFileCount()}
        <div class="resource-state-notice loading blocking" role="status"><span class="resource-state-dot" aria-hidden="true"></span><span class="resource-state-copy"><strong>{emptyResultsHeading()}</strong><small>{emptyResultsDetail()}</small></span></div>
      {:else}
        <LoadingIndicator label={searching ? 'Searching' : 'Loading search results'} blocking />
      {/if}
    {:else}
      <!-- The same compact card as the searching notice it replaces, so it does not change shape. -->
      <EmptyState class="resource-state-notice empty blocking search-results-empty" icon="search" title={emptyResultsHeading()}
        detail={retentionNotice || searching || (viewState.summary?.isComplete && !viewState.refining) ? emptyResultsDetail() : undefined} />
    {/if}
  {:else if viewState.draft?.resultMode === 'song-groups' || viewState.draft?.resultMode === 'album-groups'}
    {#if libraryStatus}
      <div class="resource-state-notice" role="status">{libraryStatus}
        {#if libraryStatus === 'Library check unavailable'}<button class="button" onclick={() => libraryRetry++}>Retry library check</button>{/if}
      </div>
    {/if}
    <div class="group-results-list">
      {#each viewState.rows.slice(0, rendered.count) as row (row.ref)}
        {#if row.kind === 'track-group' || row.kind === 'album-group'}
          {@render groupGroupCard(row)}
        {/if}
      {/each}
    </div>
  {:else}
    {#each visibleTiers as { tier, groups, pending } (tier)}
        {#if tier !== 'All' && (tier !== 'Other' || groupedTiers.some(group => group.tier === 'Preferred'))}
          <div class:preferred={tier === 'Preferred'} class="result-tier-heading">
            <span>{tier === 'Preferred' ? 'Preferred matches' : 'Other matches'}</span>
            {#if tier === 'Preferred'}
              <HelpPopover title="Preferred matches" testId="preferred-matches-help">
                {#snippet children(close, anchor)}
                  <span>These results satisfy all your ranking conditions when using "Relevance" sorting. Other matches remain available below.</span>
                  <span>Edit your preferences in your config, e.g. set <code>pref-format = flac</code> to prefer FLAC files. <ManualLink topic="fileConditions">How ranking works</ManualLink></span>
                  <span><button type="button" class="text-link" data-testid="edit-search-preferences" disabled={configurationLoading || !baseline}
                    onclick={async () => {
                      conditionsAnchor = anchor;
                      conditionsInitialTab = 'ranking';
                      conditionsOpen = true;
                      close();
                      await tick();
                      conditionsPopover?.querySelector<HTMLButtonElement>('[aria-current="page"]')?.focus();
                    }}>Edit preferences</button> for this search only.</span>
                {/snippet}
              </HelpPopover>
            {/if}
          </div>
        {/if}
        <div class:preferred-tier={tier === 'Preferred'} class="result-tier peer-group-list">
          {#each groups as group (group.key)}
            {@const first = group.rows[0]}
            {@const peer = first.value.peer}
            {@const noun = genericMode ? (group.rows.some(row => row.kind === 'directory' && !row.value.ref.folderPath) ? 'group' : 'directory') : 'result'}
            <PeerItemGroup {highlight} {peer}
              itemNoun={noun}
              itemNounPlural={noun === 'directory' ? 'directories' : `${noun}s`}
              {userActions}
            >
                {#each group.rows as row (row.ref)}
                  {#if row.kind === 'file'}
                    <FileItemCard {highlight}
                      keyboardKey={row.ref}
                      path={row.value.remoteFilename}
                      sizeBytes={Number(row.value.file.size)}
                      audio={audio(row.value.file)}
                      locked={row.value.visibility === 'Locked'}
                      selected={isselected(row.ref)}
                      preferred={tier === 'Preferred'}
                      selectable={!rowsReadOnly}
                      onselect={(checked) => ontoggle(row.ref, checked)}
                    />
                  {:else}
                    {@const children = viewState.directoryFiles.get(row.ref)}
                    {@const files = children ? children.items.map((child) => folderFile(child.file, child.relativePath)) : [folderFile(row.value.bestChild)]}
                    {@const selectedFileIds = new Set(files.filter((file) => !file.locked && isselected(file.id, row.ref)).map((file) => file.id))}
                    {@const folderSelection = ordinaryDirectorySelection(row.ref, files.filter(file => !file.locked).length, selectedFileIds.size)}
                    {@const hidden = genericMode && (children?.directorySummary ?? row.value).hasFilteredFiles}
                    {@const hiddenPaths = hidden && children ? filteredSubfolders(children.items.map(file => file.deepestFilteredFolder)) : emptyPaths}
                    {@const selectedPaths = hiddenPaths.size ? filteredSelectionPaths.get(row.ref) : undefined}
                    {@const partialPaths = selectedPaths ? filteredSubfolders(selectedPaths.filter(path => hiddenPaths.has(path))) : emptyPaths}
                    {@const filteredOnly = hidden && folderSelection.selected && (!isfolderselected(row.ref, '', true) || partialPaths.size > 0)}
                    {@const totals = directoryTotals(row.value)}
                    {@const retrieval = viewState.retrievals.get(row.ref)}
                    <FolderItemCard {highlight}
                      keyboardKey={row.ref}
                      contentsState={!row.value.ref.folderPath ? undefined : retrieval?.loading ? 'retrieving' : retrieval?.error ? 'failed' : row.value.retrievalState === 'Complete' ? 'complete' : 'partial'}
                      onloadfullcontents={() => onretrievedirectory(row.ref)}
                      path={row.value.ref.folderPath || 'Files without a folder'}
                      sizeBytes={totals.bytes}
                      {files}
                      totalFileCount={totals.fileCount}
                      filesComplete={Boolean(children && !children.nextCursor && (!children.loading || children.refreshing) && !children.error)}
                      fileLayout={genericMode ? 'tree' : 'sections'}
                      showFileIcons={!genericMode}
                      dataStateLabel={retrieval?.error?.message ?? children?.error?.message ?? (children?.loading && !children.refreshing ? 'Loading matching files…' : undefined)}
                      locked={row.value.visibility === 'Locked'}
                      selected={folderSelection.selected && !filteredOnly}
                      partial={folderSelection.partial || filteredOnly}
                      preferred={tier === 'Preferred'}
                      selectable={!rowsReadOnly}
                      {selectedFileIds}
                      onselectall={(checked) => hidden && checked && (folderSelection.partial || filteredOnly)
                        ? onselectfolder(row.ref, '', true, true) : ontoggle(row.ref, checked)}
                      onselectfiles={(files, checked) => ontoggle(files.map(file => file.id), checked, row.ref)}
                      onselectfolder={genericMode && !rowsReadOnly ? (path, checked, partial) => onselectfolder(row.ref, path, checked, checked && partial && hiddenPaths.has(path)) : undefined}
                      isfolderselected={genericMode ? (path) => isfolderselected(row.ref, path) && (!hiddenPaths.has(path) || isfolderselected(row.ref, path, true) && !partialPaths.has(path)) : undefined}
                    />
                    {#if !children || (children.loading && !children.refreshing) || children.error || children.nextCursor}
                      <div class="load-more-row"><button class="button" type="button" disabled={children?.loading} onclick={() => onloaddirectory(row.ref)}>{children?.loading ? 'Loading…' : children?.error ? 'Retry matching files' : children ? 'Load more' : 'Load matching files'}</button></div>
                    {/if}
                  {/if}
                {/each}
            </PeerItemGroup>
          {/each}
          {#if pending.length}
            <div class="peer-sizers" aria-hidden="true" inert>
              {#each pending as group (group.key)}<PeerItemGroup sizer peer={group.rows[0].value.peer} {userActions} />{/each}
            </div>
          {/if}
        </div>
    {/each}
  {/if}

  {#if rendered.count < viewState.rows.length}<div class="progressive-reserve" bind:this={reserve} style:height={`${reservedHeight}px`} aria-hidden="true"></div>{/if}
  {#if viewState.nextCursor}<div data-keyboard-page="results" data-loading={viewState.loadingMore} data-failed={Boolean(viewState.error)}><LoadMoreButton loading={viewState.loadingMore} disabled={viewState.refining} onclick={onloadmore}/></div>{/if}
  {/if}
  <SelectionToolbar
    {selectedCount}
    visible={selectedCount > 0 || viewState.selectionNeedsReview || viewState.canRetryPendingCommit}
    countPending={viewState.selectionCountsLoading && !viewState.canRetryPendingCommit}
    floatingLabel={viewState.committing ? 'Requesting…' : viewState.canRetryPendingCommit ? 'Retry download request'
      : viewState.selectionCountsLoading ? 'Counting files…' : viewState.selectionCounts
      && BigInt(viewState.selectionCounts.publicFileCount) > 0n
      ? `Download ${formatCount(viewState.selectionCounts.publicFileCount)}` : 'Download selected'}
    detail={viewState.selectionCountsError?.message}
    actionDisabled={!connected || downloadOptionsLoading || Boolean(downloadOptionsError) || viewState.committing || (!viewState.canRetryPendingCommit && (viewUnavailable || checkingRevision || Boolean(revisionError) || refinementPending
      || hasUnreadySelectedChoice()
      || Boolean(viewState.selectionCountsError) || selectedCount === 0))}
    optionsOpen={downloadOptionsOpen}
    optionsCustomized={downloadCustomized}
    onclear={() => onselectall(false)}
    onselectall={selectedCount && !selectionReadOnly && !viewState.committing && !allSearchResultsSelected(viewState) ? () => onselectall(true) : undefined}
    onoptions={selectedCount ? toggleDownloadOptions : undefined}
    onaction={() => void download()}
  />
</section>

{#if viewState.groupOptions}
  {@const panel = viewState.groupOptions}
  <div class="group-options-modal">
    <button type="button" class="group-options-backdrop" aria-label="Close options" onclick={oncloseoptions}></button>
    <div class="popover-panel group-options-dialog" role="dialog" aria-modal="true" aria-label={`${panel.title} options`}>
      <header class="group-options-header">
        <div class="group-options-heading">
          <strong>{panel.title}</strong>
          <small>{panel.artist ? `${panel.artist} · ` : ''}{formatCountWithNoun(panel.shareCount, 'user')} · {formatCountWithNoun(panel.optionCount, panel.kind === 'album' ? 'folder' : 'file')}</small>
        </div>
        <ResultFilterControl collapsible bind:value={optionFilter} placeholder="Filter by peer or path…" ariaLabel="Filter group options" />
        <button type="button" class="button group-options-close" aria-label="Close options" onclick={oncloseoptions}>×</button>
      </header>
      <div class="group-options-list">
        {#if panel.filterText && !panel.items.length && !panel.loading && !panel.error}
          <EmptyState class="search-results-empty" icon="search" title="No matching options." />
        {/if}
        {#each panel.items as option (option.ref)}
          {@const peer = option.value.peer}
          <div class:current={(choiceIsCurrent(panel.groupRef) ? viewState.chosenOptions.get(panel.groupRef)?.ref : panel.representativeRef) === option.ref} class="group-option">
            <div class="group-option-toolbar">
              {@render groupPeerSummary(peer, optionHighlight)}
              <button type="button" class="button group-use-option" disabled={selectionReadOnly || option.value.visibility === 'Locked'} onclick={() => onchooseoption(panel.groupRef, option)}>Use this option</button>
            </div>
            <div class="group-option-card-wrap">
              {#if option.kind === 'track'}
                <button type="button" class="group-option-card-picker" disabled={selectionReadOnly || option.value.visibility === 'Locked'} aria-label={`Use ${option.value.remoteFilename}`} onclick={() => onchooseoption(panel.groupRef, option)}></button>
                <FileItemCard highlight={optionHighlight} path={option.value.remoteFilename} sizeBytes={Number(option.value.file.size)} audio={audio(option.value.file)} locked={option.value.visibility === 'Locked'} />
              {:else}
                {@const children = viewState.directoryFiles.get(option.ref)}
                {@const directory = children?.directorySummary ?? option.value}
                {@const totals = directoryTotals(directory)}
                {@const retrieval = viewState.retrievals.get(directory.ref.ref)}
                <button type="button" class="group-option-card-picker" disabled={selectionReadOnly || option.value.visibility === 'Locked'} aria-label={`Use ${directory.ref.folderPath || 'Files without a folder'}`} onclick={() => onchooseoption(panel.groupRef, option)}></button>
                <FolderItemCard highlight={optionHighlight}
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
                    <button class="button" type="button" disabled={children?.loading} onclick={() => onloaddirectory(directory.ref.ref)}>{children?.loading ? 'Loading…' : children?.error ? 'Retry matching files' : children ? 'Load more' : 'Load matching files'}</button>
                  </div>
                {/if}
              {/if}
            </div>
          </div>
        {/each}
        {#if panel.error}
          <div class="resource-state-notice unavailable" role="alert"><span class="resource-state-copy"><strong>Options unavailable</strong><small>{panel.error.message}</small></span><button class="button" type="button" onclick={onloadoptions}>Retry</button></div>
        {/if}
        {#if panel.loading && panel.items.length === 0}<LoadingIndicator label="Loading options" />{/if}
        {#if panel.nextCursor}<LoadMoreButton loading={panel.loading} onclick={onloadoptions}/>{/if}
      </div>
    </div>
  </div>
{/if}
