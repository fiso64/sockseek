import { randomUUID } from '../../app/uuid';
import type { Schema, SearchViewSummaryDto } from '../../api/types';
import { sameWireInteger } from '../../api/types';

export type GroupOption =
  | { kind: 'track'; ref: string; value: Schema<'SearchViewFileDto'> }
  | { kind: 'album'; ref: string; value: Schema<'SearchViewDirectoryDto'> };

export interface SearchSelectionState {
  readonly chosenOptions: ReadonlyMap<string, GroupOption>;
  readonly checkedChoiceRevisions: ReadonlyMap<string, string>;
  readonly staleChoices: ReadonlySet<string>;
  readonly albumFileSelections: ReadonlyMap<string, { mode: 'Only' | 'AllExcept'; refs: ReadonlySet<string> }>;
  readonly selectedAll: boolean;
  readonly selectionRevision: number | string | null;
  readonly selectionNeedsReview: boolean;
  readonly selectionTopLevelCount: number | null;
  readonly selectionRefs: ReadonlySet<string>;
  readonly selectionCounts: Schema<'CountSearchViewSelectionResponseDto'> | null;
  readonly excludedFiles: ReadonlyMap<string, string>;
  readonly excludedFileIdentities: ReadonlyMap<string, Schema<'FileCandidateRefDto'>>;
  readonly includedFiles: ReadonlyMap<string, string>;
  readonly directoryPathRules: readonly Schema<'SearchViewDirectoryPathRuleDto'>[];
}

type DisplayedSelection = SearchSelectionState & { summary: SearchViewSummaryDto | null };
type CountRequest = Schema<'CountSearchViewSelectionRequestDto'>;
type Counts = Schema<'CountSearchViewSelectionResponseDto'>;
type DownloadOptions = Schema<'SubmissionOptionsDto'>;
export interface PendingSearchCommit {
  readonly viewId: string;
  readonly request: Schema<'CommitSearchViewSelectionRequestDto'>;
}

export interface SelectionRebase {
  readonly summary: SearchViewSummaryDto;
  readonly roots: readonly string[];
  readonly validation: CountRequest | null;
}

export function hasPublishedProjection(summary: SearchViewSummaryDto | null): summary is SearchViewSummaryDto {
  return summary !== null && !sameWireInteger(summary.revision, 0);
}

export function selectionUsesEarlierRevision(state: DisplayedSelection): boolean {
  return state.summary !== null && state.selectionRevision !== null
    && !sameWireInteger(state.selectionRevision, state.summary.revision);
}

export function selectedSearchCount(state: DisplayedSelection): number {
  if (!hasPublishedProjection(state.summary) || state.selectionNeedsReview) return 0;
  if (state.selectionCounts && state.summary
    && sameWireInteger(state.selectionCounts.revision,
      state.selectionRevision ?? state.summary.revision))
    return Number(state.selectionCounts.selectedItemCount);
  return requestedSearchCount(state);
}

/** Whole results, including unfetched pages; file/subfolder exclusions remain partial. */
export function allSearchResultsSelected(state: DisplayedSelection): boolean {
  if (!hasPublishedProjection(state.summary) || state.selectionNeedsReview) return false;
  const wholeRoots = state.excludedFiles.size === 0 && state.albumFileSelections.size === 0
    && !state.directoryPathRules.some(rule => !rule.selected);
  if (state.selectedAll) return state.selectionRefs.size === 0 && wholeRoots;
  // Ordinary views can also be fully selected one file at a time. Reuse the
  // exact counts already requested by the toolbar; group totals include
  // alternative sources, so their completeness is determined by group choices.
  if (state.summary.kind !== 'TrackGroups' && state.summary.kind !== 'AlbumGroups'
    && state.selectionCounts && sameWireInteger(state.selectionCounts.revision, state.summary.revision))
    return BigInt(state.summary.counters.projectedPublicFileCount) > 0n
      && BigInt(state.selectionCounts.publicFileCount) === BigInt(state.summary.counters.projectedPublicFileCount);
  // Album refs can name folders or individual files: their number alone cannot
  // establish that every folder is fully selected before the count arrives.
  return state.summary.kind !== 'AlbumDirectories' && wholeRoots && selectionScopeSize(state) > 0
    && state.selectionRefs.size === selectionScopeSize(state);
}

function requestedSearchCount(state: DisplayedSelection): number {
  if (!state.selectedAll) {
    const selectedRoots = new Set(state.selectionRefs);
    const selectedBranches = new Set(state.directoryPathRules
      .filter(rule => rule.selected && !selectedRoots.has(rule.directoryRef))
      .map(rule => rule.directoryRef));
    return state.selectionRefs.size + state.includedFiles.size + selectedBranches.size;
  }
  const selectedBranches = new Set(state.directoryPathRules
    .filter(rule => rule.selected && state.selectionRefs.has(rule.directoryRef))
    .map(rule => rule.directoryRef));
  return Math.max(0, selectionScopeSize(state) - state.selectionRefs.size) + state.includedFiles.size + selectedBranches.size;
}

function selectionScopeSize(state: DisplayedSelection): number {
  if (state.selectionTopLevelCount !== null)
    return state.selectionTopLevelCount;
  return Number(state.summary?.kind === 'Files'
    ? state.summary.counters.selectableOptionCount : state.summary?.counters.topLevelItemCount ?? 0);
}

function choiceRevisionKey(summary: SearchViewSummaryDto): string {
  return `${summary.viewId}:${String(summary.revision)}`;
}

export function selectionCountKey(viewId: string, request: Schema<'CountSearchViewSelectionRequestDto'>): string {
  // Sorting set-derived refs ignores insertion order; revision and the complete
  // selection expression still invalidate any formerly exact count.
  return JSON.stringify([viewId, request.revision, request.selection.mode,
    [...request.selection.refs].sort(), [...(request.excludedFileRefs ?? [])].sort(),
    Object.entries(request.excludedFileIdentities ?? {}).sort(([a], [b]) => a.localeCompare(b)),
    [...(request.includedFileRefs ?? [])].sort(),
    Object.entries(request.chosenOptions ?? {}).sort(([a], [b]) => a.localeCompare(b)),
    Object.entries(request.albumFileSelections ?? {}).map(([ref, files]) =>
      [ref, files.mode, [...files.refs].sort()]).sort(([a], [b]) => String(a).localeCompare(String(b))),
    request.directoryPathRules ?? []]);
}

/** Browser-owned selection intent. Network operations only supply facts/results;
 * all changes to membership, revision binding, choices and retries happen here. */
export class SearchSelection {
  private state: SearchSelectionState = emptySelection();
  private summary: SearchViewSummaryDto | null = null;
  // Child selections retain their parent even when the displayed page changes.
  private selectedFileParents = new Map<string, string>();
  private fileRelativePaths = new Map<string, string>();
  private pending: PendingSearchCommit | null = null;
  private countedSelection: string | null = null;

  get value(): SearchSelectionState { return this.state; }
  get pendingCommit(): PendingSearchCommit | null { return this.pending; }

  snapshot(): SearchSelectionState {
    return { ...this.state,
      selectionRefs: new Set(this.state.selectionRefs),
      excludedFiles: new Map(this.state.excludedFiles),
      excludedFileIdentities: new Map(this.state.excludedFileIdentities),
      includedFiles: new Map(this.state.includedFiles),
      directoryPathRules: [...this.state.directoryPathRules],
      chosenOptions: new Map(this.state.chosenOptions),
      checkedChoiceRevisions: new Map(this.state.checkedChoiceRevisions),
      staleChoices: new Set(this.state.staleChoices),
      albumFileSelections: new Map([...this.state.albumFileSelections]
        .map(([ref, selection]) => [ref, { ...selection, refs: new Set(selection.refs) }])),
    };
  }

  reset(): void {
    this.state = emptySelection();
    this.countedSelection = null;
    this.summary = null;
    this.pending = null;
    this.selectedFileParents.clear();
    this.fileRelativePaths.clear();
  }

  observe(summary: SearchViewSummaryDto | null): void {
    this.summary = summary;
    if (!this.hasSelection()) {
      if (this.state.selectionRevision !== null)
        this.state = { ...this.state, selectionRevision: null, selectionTopLevelCount: null };
    } else if (!this.state.selectionNeedsReview && this.state.selectionRevision === null
      && hasPublishedProjection(summary)) {
      this.state = { ...this.state, selectionRevision: summary.revision,
        selectionTopLevelCount: Number(summary.kind === 'Files'
          ? summary.counters.selectableOptionCount : summary.counters.topLevelItemCount) };
    }
  }

  rememberFiles(files: readonly Schema<'SearchViewDirectoryFileDto'>[]): void {
    for (const file of files) this.fileRelativePaths.set(file.ref, file.relativePath);
  }

  private changed(): void {
    this.pending = null;
    this.observe(this.summary);
    if (!this.summary || this.countedSelection !== selectionCountKey(this.summary.viewId, this.input()))
      this.clearCounts();
  }

  clearCounts(): void {
    this.countedSelection = null;
    if (this.state.selectionCounts) this.state = { ...this.state, selectionCounts: null };
  }

  acceptCounts(request: CountRequest, counts: Counts): boolean {
    if (!this.summary || !sameWireInteger(counts.revision, request.revision)
      || selectionCountKey(this.summary.viewId, request)
        !== selectionCountKey(this.summary.viewId, this.input())) return false;
    this.state = { ...this.state, selectionCounts: counts };
    this.countedSelection = selectionCountKey(this.summary.viewId, request);
    return true;
  }

  selectedCount(): number { return selectedSearchCount({ ...this.state, summary: this.summary }); }

  needsCounts(visibleGroups: readonly string[]): boolean {
    // Exclusions from a wider projection cannot prove that AllExcept is empty.
    const displayed = { ...this.state, summary: this.summary };
    const maySelectUnexcludedItems = this.state.selectedAll
      && this.state.selectionRefs.size > 0 && selectionScopeSize(displayed) > 0;
    return hasPublishedProjection(this.summary) && !this.state.selectionNeedsReview
      && (maySelectUnexcludedItems || requestedSearchCount(displayed) > 0)
      && !this.hasUnreadySelectedChoices(visibleGroups);
  }

  hasUnreadySelectedChoices(visibleGroups: readonly string[]): boolean {
    if (selectionUsesEarlierRevision({ ...this.state, summary: this.summary })) return false;
    return visibleGroups.some(ref => this.state.chosenOptions.has(ref)
      && this.isSelected(ref) && !this.choiceIsCurrent(ref));
  }
  toggle(ref: string, selected: boolean, parentRef?: string, child?: Schema<'SearchViewDirectoryFileDto'>): boolean {
    if (this.selectionReadOnly()) return false;
    const refs = new Set(this.state.selectionRefs);
    const excludedFiles = new Map(this.state.excludedFiles);
    const excludedFileIdentities = new Map(this.state.excludedFileIdentities);
    const includedFiles = new Map(this.state.includedFiles);
    if (parentRef && this.summary?.kind === 'GenericDirectories') {
      if (child) this.fileRelativePaths.set(ref, child.relativePath);
      const inherited = this.directoryPathInheritance(parentRef, this.fileRelativePaths.get(ref));
      refs.delete(ref);
      if (selected === inherited) {
        includedFiles.delete(ref);
        excludedFiles.delete(ref);
        excludedFileIdentities.delete(ref);
      } else if (selected) {
        includedFiles.set(ref, parentRef);
        excludedFiles.delete(ref);
        excludedFileIdentities.delete(ref);
      } else {
        includedFiles.delete(ref);
        excludedFiles.set(ref, parentRef);
        if (child) excludedFileIdentities.set(ref, {
          username: child.file.peer.username, filename: child.file.remoteFilename,
        });
      }
      this.state = { ...this.state, selectionRefs: refs, excludedFiles, excludedFileIdentities, includedFiles };
      this.changed();
      return true;
    }
    if (parentRef && this.isSelected(parentRef)) {
      if (selected) {
        excludedFiles.delete(ref);
        excludedFileIdentities.delete(ref);
      } else {
        excludedFiles.set(ref, parentRef);
        const file = child?.file;
        if (file) excludedFileIdentities.set(ref, {
          username: file.peer.username, filename: file.remoteFilename,
        });
      }
      refs.delete(ref);
      includedFiles.delete(ref);
    } else if (parentRef && this.state.selectedAll) {
      if (selected) includedFiles.set(ref, parentRef);
      else includedFiles.delete(ref);
      refs.delete(ref);
      excludedFiles.delete(ref);
      excludedFileIdentities.delete(ref);
    } else {
      if (selected === this.state.selectedAll) refs.delete(ref);
      else refs.add(ref);
      excludedFiles.delete(ref);
      excludedFileIdentities.delete(ref);
    }
    let directoryPathRules = this.state.directoryPathRules;
    let albumFileSelections = this.state.albumFileSelections;
    if (!parentRef) {
      directoryPathRules = directoryPathRules.filter(rule => rule.directoryRef !== ref);
      const updatedAlbumFileSelections = new Map(albumFileSelections);
      updatedAlbumFileSelections.delete(ref);
      albumFileSelections = updatedAlbumFileSelections;
      for (const [fileRef, parent] of includedFiles) {
        if (parent === ref) includedFiles.delete(fileRef);
      }
      for (const [fileRef, parent] of this.selectedFileParents) {
        if (parent === ref) {
          refs.delete(fileRef);
          this.selectedFileParents.delete(fileRef);
        }
      }
      for (const [fileRef, parent] of excludedFiles) {
        if (parent === ref) {
          excludedFiles.delete(fileRef);
          excludedFileIdentities.delete(fileRef);
        }
      }
    } else if (refs.has(ref) && !this.state.selectedAll) {
      this.selectedFileParents.set(ref, parentRef);
    } else {
      this.selectedFileParents.delete(ref);
    }
    this.state = {
      ...this.state,
      selectionRefs: refs,
      excludedFiles,
      excludedFileIdentities,
      includedFiles,
      directoryPathRules,
      albumFileSelections,
    };
    this.changed();
    return true;
  }

  selectAll(selected: boolean): boolean {
    if (selected && this.selectionReadOnly()) return false;
    this.selectedFileParents.clear();
    this.state = {
      ...this.state,
      selectedAll: selected,
      selectionRevision: null,
      selectionNeedsReview: false,
      selectionTopLevelCount: null,
      selectionRefs: new Set(),
      excludedFiles: new Map(),
      excludedFileIdentities: new Map(),
      includedFiles: new Map(),
      directoryPathRules: [],
      albumFileSelections: new Map(),
    };
    this.changed();
    return true;
  }

  isSelected(ref: string, parentRef?: string): boolean {
    if (this.selectionReadOnly()) return false;
    if (parentRef) {
      if (this.summary?.kind === 'GenericDirectories') {
        if (this.state.excludedFiles.has(ref)) return false;
        if (this.state.includedFiles.has(ref)) return true;
        return this.directoryPathInheritance(parentRef, this.fileRelativePaths.get(ref));
      }
      const album = this.state.albumFileSelections.get(parentRef);
      if (album) return this.isSelected(parentRef) && (album.mode === 'Only' ? album.refs.has(ref) : !album.refs.has(ref));
      if (this.state.excludedFiles.has(ref)) return false;
      if (this.isSelected(parentRef)) return true;
      if (this.state.selectedAll) return this.state.includedFiles.has(ref);
    }
    return this.selectedRef(ref);
  }

  private selectedRef(ref: string): boolean {
    return this.state.selectedAll ? !this.state.selectionRefs.has(ref) : this.state.selectionRefs.has(ref);
  }

  private directoryPathInheritance(directoryRef: string, relativePath?: string, includeExact = false): boolean {
    let selected = this.isSelected(directoryRef);
    if (!relativePath) return selected;
    const normalized = relativePath.replace(/\\/g, '/');
    let bestLength = -1;
    for (const rule of this.state.directoryPathRules) {
      if (rule.directoryRef !== directoryRef || rule.relativePath.length <= bestLength
        || !(normalized.startsWith(`${rule.relativePath}/`)
          || includeExact && normalized === rule.relativePath)) continue;
      bestLength = rule.relativePath.length;
      selected = rule.selected;
    }
    return selected;
  }

  isDirectoryPathSelected(directoryRef: string, relativePath: string): boolean {
    return this.directoryPathInheritance(directoryRef, relativePath, true);
  }

  toggleDirectoryPath(directoryRef: string, relativePath: string, selected: boolean): boolean {
    if (this.summary?.kind !== 'GenericDirectories') return false;
    if (this.selectionReadOnly()) return false;
    const rules = this.state.directoryPathRules.filter(rule => rule.directoryRef !== directoryRef
      || (rule.relativePath !== relativePath && !rule.relativePath.startsWith(`${relativePath}/`)));
    rules.push({ directoryRef, relativePath, selected });
    const excludedFiles = new Map(this.state.excludedFiles);
    const excludedFileIdentities = new Map(this.state.excludedFileIdentities);
    const includedFiles = new Map(this.state.includedFiles);
    for (const [ref, parent] of [...includedFiles, ...excludedFiles]) {
      if (parent !== directoryRef || !this.fileRelativePaths.get(ref)?.replace(/\\/g, '/').startsWith(`${relativePath}/`)) continue;
      includedFiles.delete(ref);
      excludedFiles.delete(ref);
      excludedFileIdentities.delete(ref);
    }
    this.state = { ...this.state, directoryPathRules: rules, includedFiles, excludedFiles, excludedFileIdentities };
    this.changed();
    return true;
  }

  choiceIsCurrent(groupRef: string): boolean {
    const checked = this.state.checkedChoiceRevisions.get(groupRef);
    return checked !== undefined && this.summary !== null
      && checked === choiceRevisionKey(this.summary)
      && !this.state.staleChoices.has(groupRef);
  }

  input(revision: number | string = this.state.selectionRevision ?? this.summary!.revision): Schema<'CountSearchViewSelectionRequestDto'> {
    const albumFileSelections = Object.fromEntries([...this.state.albumFileSelections]
      .filter(([ref]) => this.selectedRef(ref))
      .map(([ref, selection]) => [ref, { mode: selection.mode, refs: [...selection.refs] }]));
    return {
      revision,
      selection: { mode: this.state.selectedAll ? 'AllExcept' : 'Only', refs: [...this.state.selectionRefs] },
      excludedFileRefs: [...this.state.excludedFiles.keys()],
      excludedFileIdentities: Object.fromEntries(this.state.excludedFileIdentities),
      includedFileRefs: [...this.state.includedFiles.keys()],
      chosenOptions: Object.fromEntries([...this.state.chosenOptions]
        .filter(([ref]) => this.selectedRef(ref))
        .map(([ref, option]) => [ref, option.ref])),
      albumFileSelections,
      directoryPathRules: [...this.state.directoryPathRules],
    };
  }

  private hasSelection(): boolean {
    return this.state.selectedAll || this.state.selectionRefs.size > 0
      || this.state.includedFiles.size > 0 || this.state.directoryPathRules.length > 0;
  }

  private selectionReadOnly(): boolean {
    return this.state.selectionNeedsReview || selectionUsesEarlierRevision({ ...this.state, summary: this.summary });
  }

  choose(groupRef: string, option: GroupOption): boolean {
    if (this.selectionReadOnly()) return false;
    const chosenOptions = new Map(this.state.chosenOptions).set(groupRef, option);
    const checkedChoiceRevisions = new Map(this.state.checkedChoiceRevisions);
    if (this.summary) checkedChoiceRevisions.set(groupRef, choiceRevisionKey(this.summary));
    const staleChoices = new Set(this.state.staleChoices);
    staleChoices.delete(groupRef);
    const refs = new Set(this.state.selectionRefs);
    if (this.state.selectedAll) refs.delete(groupRef); else refs.add(groupRef);
    const albumFileSelections = new Map(this.state.albumFileSelections);
    albumFileSelections.delete(groupRef);
    this.state = { ...this.state, chosenOptions, checkedChoiceRevisions, staleChoices,
      albumFileSelections, selectionRefs: refs };
    this.changed();
    return true;
  }

  toggleAlbumFile(groupRef: string, representative: Schema<'SearchViewDirectoryDto'>,
    fileRef: string, selected: boolean, completeFileRefs?: readonly string[]): boolean {
    if (this.selectionReadOnly()) return false;
    const option = this.choiceIsCurrent(groupRef) ? this.state.chosenOptions.get(groupRef) : null;
    const chosenOptions = option?.kind === 'album' ? this.state.chosenOptions
      : new Map(this.state.chosenOptions).set(groupRef, {
        kind: 'album', ref: representative.ref.ref, value: representative,
      });
    const checkedChoiceRevisions = new Map(this.state.checkedChoiceRevisions);
    checkedChoiceRevisions.set(groupRef, choiceRevisionKey(this.summary!));
    const staleChoices = new Set(this.state.staleChoices);
    staleChoices.delete(groupRef);
    const wasSelected = this.isSelected(groupRef);
    const previous = this.state.albumFileSelections.get(groupRef);
    const mode = previous?.mode ?? (wasSelected ? 'AllExcept' : 'Only');
    const refs = new Set(previous?.refs ?? []);
    if (mode === 'Only') {
      if (selected) refs.add(fileRef);
      else refs.delete(fileRef);
    } else if (selected) refs.delete(fileRef);
    else refs.add(fileRef);
    const allLoadedRefsMatch = completeFileRefs !== undefined && completeFileRefs.length > 0
      && completeFileRefs.every(ref => refs.has(ref));
    const selectionRefs = new Set(this.state.selectionRefs);
    const groupSelected = mode === 'AllExcept' ? !allLoadedRefsMatch : refs.size > 0;
    if (groupSelected === this.state.selectedAll) selectionRefs.delete(groupRef);
    else selectionRefs.add(groupRef);
    const albumFileSelections = new Map(this.state.albumFileSelections);
    if (!groupSelected || (mode === 'AllExcept' && refs.size === 0) || (mode === 'Only' && allLoadedRefsMatch)) albumFileSelections.delete(groupRef);
    else albumFileSelections.set(groupRef, { mode, refs });
    this.state = { ...this.state, selectionRefs, chosenOptions, checkedChoiceRevisions,
      staleChoices, albumFileSelections };
    this.changed();
    return true;
  }

  choicesToValidate(visibleGroups: readonly string[]): Record<string, string> {
    return Object.fromEntries(visibleGroups.flatMap(ref => {
      const choice = this.state.chosenOptions.get(ref);
      return choice && this.summary && this.state.checkedChoiceRevisions.get(ref) !== choiceRevisionKey(this.summary)
        ? [[ref, choice.ref]] : [];
    }));
  }

  acceptChoices(summary: SearchViewSummaryDto, choices: Record<string, string>,
    resolved: Schema<'ResolveSearchViewChosenOptionsResponseDto'>): Schema<'SearchViewDirectoryDto'>[] {
    if (this.summary?.viewId !== summary.viewId
      || !sameWireInteger(this.summary.revision, summary.revision)) return [];
    const byGroup = new Map(resolved.items.map(item => [item.groupRef, item]));
    const chosenOptions = new Map(this.state.chosenOptions);
    const checkedChoiceRevisions = new Map(this.state.checkedChoiceRevisions);
    const staleChoices = new Set(this.state.staleChoices);
    const albums: Schema<'SearchViewDirectoryDto'>[] = [];
    for (const [groupRef, optionRef] of Object.entries(choices)) {
      if (chosenOptions.get(groupRef)?.ref !== optionRef) continue;
      const item = byGroup.get(groupRef);
      checkedChoiceRevisions.set(groupRef, choiceRevisionKey(summary));
      if (item?.optionRef === optionRef && (item.track || item.album)) {
        staleChoices.delete(groupRef);
        if (item.track) chosenOptions.set(groupRef, { kind: 'track', ref: optionRef, value: item.track });
        else if (item.album) {
          chosenOptions.set(groupRef, { kind: 'album', ref: optionRef, value: item.album });
          albums.push(item.album);
        }
      } else staleChoices.add(groupRef);
    }
    this.state = { ...this.state, chosenOptions, checkedChoiceRevisions, staleChoices };
    return albums;
  }

  private roots(): Set<string> {
    return new Set([...this.state.selectionRefs,
      ...this.state.directoryPathRules.map(rule => rule.directoryRef),
      ...this.state.includedFiles.values(), ...this.state.excludedFiles.values()]);
  }

  prepareRebase(summary: SearchViewSummaryDto, visibleRoots: ReadonlySet<string>): SelectionRebase | null {
    if (summary.kind !== 'GenericDirectories' || !hasPublishedProjection(summary)) return null;
    const roots = [...this.roots()];
    return { summary, roots, validation: roots.some(ref => !visibleRoots.has(ref))
      ? { revision: summary.revision, selection: { mode: 'Only', refs: roots } } : null };
  }

  replaceProjection(summary: SearchViewSummaryDto, rebase: SelectionRebase | null, counts?: Counts): void {
    this.pending = null;
    this.countedSelection = null;
    this.state = { ...this.state, selectionRevision: null, selectionTopLevelCount: null,
      selectionCounts: null, selectionNeedsReview: summary.kind === 'GenericDirectories' && this.hasSelection() };
    if (rebase && rebase.summary === summary) {
      const roots = this.roots();
      const valid = rebase.roots.length === roots.size && rebase.roots.every(ref => roots.has(ref))
        && (!rebase.validation || counts && sameWireInteger(counts.revision, summary.revision)
          && Number(counts.selectedItemCount) === roots.size);
      if (valid) {
        this.state = { ...this.state, selectionNeedsReview: false,
          selectionRevision: summary.revision,
          selectionTopLevelCount: Number(summary.counters.topLevelItemCount) };
        // Validation of roots is reusable only when it is the entire count request.
        if (counts && rebase.validation && selectionCountKey(summary.viewId, rebase.validation)
          === selectionCountKey(summary.viewId, this.input(summary.revision))) {
          this.state = { ...this.state, selectionCounts: counts };
          this.countedSelection = selectionCountKey(summary.viewId, rebase.validation);
        }
      }
    }
    this.observe(summary);
  }

  retry(options?: DownloadOptions): PendingSearchCommit | null {
    if (this.pending && JSON.stringify(this.pending.request.options) !== JSON.stringify(options))
      this.pending = null;
    return this.pending;
  }

  captureCommit(options?: DownloadOptions): PendingSearchCommit {
    if (!this.summary) throw new Error('Search results are not available.');
    // Sever nested UI settings/rules once; an uncertain command is never edited.
    return this.pending ??= {
      viewId: this.summary.viewId,
      request: structuredClone({ ...this.input(), options,
        useRepresentativeWhenUnchosen: this.summary.kind === 'TrackGroups' || this.summary.kind === 'AlbumGroups',
        idempotencyKey: randomUUID() }),
    };
  }

  finishCommit(pending: PendingSearchCommit): void {
    if (this.pending === pending) this.pending = null;
  }
}

function emptySelection(): SearchSelectionState {
  return {
    chosenOptions: new Map(),
    checkedChoiceRevisions: new Map(),
    staleChoices: new Set(),
    albumFileSelections: new Map(),
    selectedAll: false,
    selectionRevision: null,
    selectionNeedsReview: false,
    selectionTopLevelCount: null,
    selectionRefs: new Set(),
    selectionCounts: null,
    excludedFiles: new Map(),
    excludedFileIdentities: new Map(),
    includedFiles: new Map(),
    directoryPathRules: [],
  };
}
