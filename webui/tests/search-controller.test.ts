import { describe, expect, it, vi } from 'vitest';
import { DaemonApiClient } from '../src/api/client';
import { DaemonApiError } from '../src/api/http';
import type { Schema, SearchViewFilePageDto, SearchViewSummaryDto, SearchViewUpdateDto } from '../src/api/types';
import { allSearchResultsSelected, SearchSelection } from '../src/domains/search/selection';
import { SearchViewController } from '../src/domains/search/controller';
import { emptySearchDraft } from '../src/domains/search/model';

function summary(viewId: string, revision = 1): SearchViewSummaryDto {
  return {
    viewId, sourceJobId: viewId, kind: 'Files', revision, sourceRevision: revision,
    consumedSequence: revision, isComplete: true, retentionState: 'Complete',
    createdAtUtc: '2026-09-08T00:00:00Z', expiresAtUtc: '2026-09-09T00:00:00Z',
    counters: { publicFileCount: 0, lockedFileCount: 0, publicBytes: 0, lockedBytes: 0,
      observedPeerCount: 0, projectedFileCount: 0, projectedPublicFileCount: 0,
      projectedLockedFileCount: 0, preferredFileCount: 0, otherFileCount: 0,
      topLevelItemCount: 1, selectableOptionCount: 0 },
  };
}

function page(viewId: string, revision = 1, nextCursor: string | null = null): SearchViewFilePageDto {
  return { revision: summary(viewId, revision), items: [], nextCursor };
}

function directorySummary(ref: string, folderPath = 'Music'): Schema<'SearchViewDirectoryDto'> {
  return {
    ref: { ref, username: 'peer', folderPath }, visibility: 'Public', preferenceTier: 'Preferred',
    satisfiedPreferredConditions: [], publicMatchingFileCount: 1, lockedMatchingFileCount: 0,
    publicMatchingBytes: 1, lockedMatchingBytes: 0, retrievedFileCount: null, retrievedBytes: null,
    retrievalState: 'SearchResultsOnly', peer: { username: 'peer' },
    bestChild: { ref: `${ref}-file`, visibility: 'Public', preferenceTier: 'Preferred',
      necessaryConditionsSatisfied: true, satisfiedPreferredConditions: [], unsatisfiedPreferredConditions: [],
      remoteFilename: `${folderPath}/file`, peer: { username: 'peer' },
      file: { name: 'file', size: 1, extension: null, bitRate: null, bitDepth: null, sampleRate: null, length: null } },
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}

function open(controller: SearchViewController, id: string) {
  return controller.open({ job: { jobId: id }, view: summary(id), draft: { ...emptySearchDraft, resultMode: 'track' } });
}

describe('Search View request isolation', () => {
  it('recognizes explicit whole selections while retaining file and branch exclusions', () => {
    const view = { ...summary('view'), kind: 'GenericDirectories' as const,
      counters: { ...summary('view').counters, topLevelItemCount: 2, publicFileCount: 6, projectedPublicFileCount: 4 } };
    const selection = new SearchSelection();
    selection.observe(view);
    const all = () => allSearchResultsSelected({ ...selection.value, summary: view });
    selection.toggle('first', true);
    expect(all()).toBe(false);
    selection.toggle('second', true);
    expect(all()).toBe(true);
    selection.toggle('child', false, 'first');
    expect(all()).toBe(false);
    selection.toggle('child', true, 'first');
    expect(all()).toBe(true);
    selection.selectAll(true);
    selection.toggleDirectoryPath('first', 'nested', false);
    expect(all()).toBe(false);
    selection.selectAll(false);
    selection.toggle('child', true, 'first');
    expect(allSearchResultsSelected({ ...selection.value, summary: view,
      selectionCounts: { revision: 1, selectedItemCount: 2, publicFileCount: 4, lockedFileCount: 0 } })).toBe(true);
    expect(allSearchResultsSelected({ ...selection.value, summary: { ...view, kind: 'AlbumDirectories',
      counters: { ...view.counters, topLevelItemCount: 1 } },
      selectionRefs: new Set(['file']), selectionCounts: { revision: 1, selectedItemCount: 1, publicFileCount: 1, lockedFileCount: 0 } })).toBe(false);
    expect(allSearchResultsSelected({ ...selection.value, summary: { ...view, kind: 'AlbumGroups' },
      selectionCounts: { revision: 1, selectedItemCount: 1, publicFileCount: 4, lockedFileCount: 0 } })).toBe(false);
  });

  it.each(['track', 'generic', 'album', 'song-groups', 'album-groups'] as const)(
    'keeps an all-except %s selection exact while new results arrive', async mode => {
      const commits: Parameters<DaemonApiClient['commitSearchView']>[] = [];
      const counts: Schema<'CountSearchViewSelectionRequestDto'>[] = [];
      const view = (revision: number): SearchViewSummaryDto => ({ ...summary('search', revision),
        kind: mode === 'track' ? 'Files' : mode === 'generic' ? 'GenericDirectories'
          : mode === 'album' ? 'AlbumDirectories' : mode === 'song-groups' ? 'TrackGroups' : 'AlbumGroups',
        counters: { ...summary('search').counters, topLevelItemCount: revision + 1, selectableOptionCount: revision + 1 } });
      class Api extends DaemonApiClient {
        override async getSearchViewFiles(_id: string, revision: number | string) {
          return { revision: view(Number(revision)), items: [], nextCursor: null };
        }
        override getSearchViewDirectories = this.getSearchViewFiles;
        override getSearchViewTrackGroups = this.getSearchViewFiles;
        override getSearchViewAlbumGroups = this.getSearchViewFiles;
        override async getSearchViewUpdate() { return { summary: view(2), hasNewRevision: true }; }
        override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
          counts.push(request);
          const count = Number(request.revision) + 1 - request.selection.refs.length;
          return { revision: request.revision, publicFileCount: count, lockedFileCount: 0, selectedItemCount: count };
        }
        override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<never> {
          commits.push(args);
          throw new Error('Response lost');
        }
      }
      const controller = new SearchViewController(new Api(), 60_000);
      try {
        await controller.open({ job: { jobId: 'search' }, view: view(1), draft: { ...emptySearchDraft, resultMode: mode } });
        expect(allSearchResultsSelected(controller.current())).toBe(false);
        controller.selectAll(true);
        expect(allSearchResultsSelected(controller.current())).toBe(true);
        controller.toggle('excluded', false);
        expect(allSearchResultsSelected(controller.current())).toBe(false);
        await controller.refresh();
        expect(controller.current().summary?.counters.topLevelItemCount).toBe(3);
        expect(controller.selectedCount()).toBe(1);
        expect(controller.isSelected('new-arrival')).toBe(false);
        expect(counts.every(count => Number(count.revision) === 1)).toBe(true);
        await controller.commit();
        expect(commits[0]?.[1]).toMatchObject({ revision: 1, selection: { mode: 'AllExcept', refs: ['excluded'] } });
      } finally { controller.stop(); }
    });

  it('requires an explicit reload after expiry, preserving filters but clearing the old selection', async () => {
    const request: Schema<'CreateSearchViewRequestDto'> = { kind: 'Files', includeFullResults: true, filterText: 'flac' };
    const creates: Schema<'CreateSearchViewRequestDto'>[] = [];
    let polls = 0;
    let failPage = true;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) {
        if (id === 'replacement' && failPage) throw new Error('Temporary page failure');
        return page(id);
      }
      override async getSearchViewUpdate(): Promise<never> {
        polls++;
        throw new DaemonApiError('Snapshot expired', 404);
      }
      override async createSearchView(_id: string, input: Schema<'CreateSearchViewRequestDto'>) {
        creates.push(input);
        return summary('replacement');
      }
      override async countSearchViewSelection(_id: string, input: Schema<'CountSearchViewSelectionRequestDto'>) {
        return { revision: input.revision, publicFileCount: 1, lockedFileCount: 0, selectedItemCount: 1 };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await open(controller, 'source');
      controller.toggle('old-choice', true);
      await controller.refresh();
      await controller.refresh();
      expect(polls).toBe(1);
      expect(creates).toEqual([]);
      expect(await controller.refine(request)).toBe(false);
      expect(await controller.refine(request, true)).toBe(false);
      expect(controller.current().selectionRefs).toEqual(new Set(['old-choice']));
      failPage = false;
      expect(await controller.refine(request, true)).toBe(true);
      expect(creates).toEqual([request, request]);
      expect(controller.current().summary?.viewId).toBe('replacement');
      expect(controller.current().selectionRefs.size).toBe(0);
      expect(controller.selectedCount()).toBe(0);
    } finally { controller.stop(); }
  });

  it('counts an AllExcept selection after a filter hides its excluded file', async () => {
    const wider = summary('wider');
    wider.counters.topLevelItemCount = wider.counters.selectableOptionCount = 2;
    const narrower = summary('narrower');
    narrower.counters.selectableOptionCount = 1;
    const requests: string[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) {
        return { ...page(id), revision: id === 'wider' ? wider : narrower };
      }
      override async createSearchView() { return narrower; }
      override async countSearchViewSelection(id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        requests.push(id);
        const count = id === 'narrower' ? 1 : 2 - request.selection.refs.length;
        return { revision: request.revision, publicFileCount: count, lockedFileCount: 0, selectedItemCount: count };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'source' }, view: wider,
        draft: { ...emptySearchDraft, resultMode: 'track' } });
      controller.selectAll(true);
      controller.toggle('excluded-file', false);
      expect(await controller.refine({ kind: 'Files', includeFullResults: true, filterText: 'remaining' })).toBe(true);
      expect(requests.filter(id => id === 'narrower')).toHaveLength(1);
      expect(controller.current().selectionRefs).toEqual(new Set(['excluded-file']));
      expect(controller.selectedCount()).toBe(1);
    } finally { controller.stop(); }
  });

  it('keeps observed updates incremental, retains the terminal update, and cancels hidden demand', async () => {
    const blockedPage = deferred<SearchViewFilePageDto>();
    const pageStarted = deferred<void>();
    const displayed = deferred<void>();
    const revisions: (number | string)[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, revision: number | string) {
        revisions.push(revision);
        if (revision === 2) { pageStarted.resolve(); return blockedPage.promise; }
        return page(id, Number(revision));
      }
      override getSearchViewUpdate = vi.fn();
    }
    const api = new Api();
    const subscriptions: { next: (value: SearchViewSummaryDto) => void; complete: () => void; release: ReturnType<typeof vi.fn> }[] = [];
    const controller = new SearchViewController(api, 1_000, (_id, next, _error, complete) => {
      const release = vi.fn();
      subscriptions.push({ next, complete, release });
      return release;
    });
    controller.subscribe(state => { if (state.summary?.revision === 3) displayed.resolve(); });
    controller.setLiveConnected(true);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' },
        view: { ...summary('search'), isComplete: false }, draft: { ...emptySearchDraft, resultMode: 'track' } });
      controller.pause();
      expect(subscriptions[0]!.release).toHaveBeenCalledOnce();
      subscriptions[0]!.next(summary('search', 99));
      expect(controller.current().summary?.revision).toBe(1);
      await controller.resume();
      expect(subscriptions).toHaveLength(2);
      subscriptions[1]!.next({ ...summary('search', 2), isComplete: false });
      await pageStarted.promise;
      subscriptions[1]!.next(summary('search', 3));
      subscriptions[1]!.complete();
      blockedPage.resolve(page('search', 2));
      await displayed.promise;
      expect(revisions).toEqual([1, 2, 3]);
      expect(api.getSearchViewUpdate).not.toHaveBeenCalled();
    } finally { controller.stop(); }
  });

  it('cancels hidden read demand and ignores its late response without disturbing the resumed read', async () => {
    const oldUpdate = deferred<SearchViewUpdateDto>();
    const resumedUpdate = deferred<SearchViewUpdateDto>();
    const revisions: (number | string)[] = [];
    const signals: (AbortSignal | undefined)[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, revision: number | string) {
        revisions.push(revision);
        return page(id, Number(revision));
      }
      override getSearchViewUpdate(_id: string, _revision: number | string, signal?: AbortSignal) {
        signals.push(signal);
        return signals.length === 1 ? oldUpdate.promise : resumedUpdate.promise;
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'search');
      const oldRead = controller.refresh();
      controller.pause();
      expect(signals[0]?.aborted).toBe(true);
      const resumed = controller.resume();
      expect(signals).toHaveLength(2);
      oldUpdate.resolve({ summary: summary('search', 2), hasNewRevision: true });
      await oldRead;
      expect(revisions).toEqual([1]);
      const joined = controller.refresh();
      expect(signals).toHaveLength(2);
      resumedUpdate.resolve({ summary: summary('search', 3), hasNewRevision: true });
      await Promise.all([resumed, joined]);
      expect(revisions).toEqual([1, 3]);
      expect(controller.current().summary?.revision).toBe(3);
    } finally { controller.stop(); }
  });

  it('stops an abandoned refinement from polling or loading its hidden View', async () => {
    const created = deferred<SearchViewSummaryDto>();
    let signal: AbortSignal | undefined;
    const requests: string[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { requests.push(id); return page(id); }
      override createSearchView(_id: string, _request: Schema<'CreateSearchViewRequestDto'>, cancellation?: AbortSignal) {
        signal = cancellation;
        return created.promise;
      }
      override async getSearchViewUpdate(id: string) {
        requests.push(`poll:${id}`);
        return { summary: summary(id), hasNewRevision: true };
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'original');
      const refining = controller.refine({ kind: 'Files', filterText: 'abandoned', includeFullResults: false });
      controller.pause();
      expect(signal?.aborted).toBe(true);
      created.resolve(summary('abandoned', 0));
      await refining;
      expect(requests).toEqual(['original']);
      expect(controller.current().summary?.viewId).toBe('original');
      expect(controller.current().refining).toBe(false);
    } finally { controller.stop(); }
  });

  it('does not fetch an empty projection page before results arrive', async () => {
    const initial = summary('search', 0);
    initial.counters.topLevelItemCount = 0;
    initial.isComplete = false;
    initial.retentionState = 'Live';
    const populated = summary('search', 1);
    populated.counters.topLevelItemCount = 1;
    let pageReads = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, revision: number) {
        pageReads += 1;
        return page(id, revision);
      }
      override async getSearchViewUpdate() {
        return { summary: populated, hasNewRevision: true };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search' }, view: initial,
        draft: { ...emptySearchDraft, resultMode: 'track' } });
      expect(pageReads).toBe(0);
      await controller.refresh();
      expect(pageReads).toBe(1);
      expect(controller.current().summary?.revision).toBe(1);
    } finally { controller.stop(); }
  });

  it('keeps the displayed selection while awaiting the first replacement projection and blocks new commits', async () => {
    const created = summary('refined', 0);
    created.kind = 'GenericDirectories';
    created.counters.topLevelItemCount = 0;
    created.isComplete = false;
    const published = { ...created, revision: 1, sourceRevision: 1 };
    const requested = deferred<void>();
    const update = deferred<SearchViewUpdateDto>();
    const counts: Schema<'CountSearchViewSelectionRequestDto'>[] = [];
    let commits = 0;
    class Api extends DaemonApiClient {
      override async createSearchView() { return created; }
      override async commitSearchView(): Promise<never> { commits++; throw new Error('Unpublished commit'); }
      override getSearchViewUpdate() { requested.resolve(); return update.promise; }
      override async getSearchViewDirectories(id: string, revision: number | string) {
        return { revision: { ...summary(id, Number(revision)), kind: 'GenericDirectories' as const },
          items: id === 'source' ? [directorySummary('folder')] : [], nextCursor: null };
      }
      override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        counts.push(request);
        return { revision: request.revision, publicFileCount: _id === 'source' ? 1 : 0,
          lockedFileCount: 0, selectedItemCount: _id === 'source' ? 1 : 0 };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    let refining: Promise<unknown> | undefined;
    try {
      await controller.open({ job: { jobId: 'source', lifecycleState: 'Terminal' },
        view: { ...summary('source'), kind: 'GenericDirectories' },
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      controller.toggle('folder', true);
      refining = controller.refine({ kind: 'GenericDirectories', includeFullResults: false, refinementSettings: {
        necessaryCond: { formats: { replace: ['pdf'] } },
      } });
      await requested.promise;
      expect(counts.some(count => Number(count.revision) === 0)).toBe(false);
      expect(controller.current().summary?.viewId).toBe('source');
      expect(controller.current().rows).toHaveLength(1);
      expect(controller.current().selectionRevision).toBe(1);
      expect(controller.current().refining).toBe(true);
      expect(controller.selectedCount()).toBe(1);
      await controller.commit();
      expect(commits).toBe(0);
      update.resolve({ summary: published, hasNewRevision: true });
      await refining;
      expect(controller.current().selectionRevision).toBeNull();
      expect(controller.current().selectionRefs.has('folder')).toBe(true);
      expect(controller.current().selectionNeedsReview).toBe(true);
      expect(controller.selectedCount()).toBe(0);
    } finally {
      update.resolve({ summary: published, hasNewRevision: true });
      await refining;
      controller.stop();
    }
  });

  it.each([false, true])('validates folder references before rebinding a replacement view (all=%s)', async all => {
    const requests: Array<{ viewId: string; request: Schema<'CountSearchViewSelectionRequestDto'> }> = [];
    let replacing = 'split';
    let commits = 0;
    const projected = (id: string) => ({ ...summary(id), kind: 'GenericDirectories' as const });
    class Api extends DaemonApiClient {
      override async createSearchView() { return projected(replacing); }
      override async getSearchViewDirectories(id: string) {
        return { revision: projected(id), items: [id === 'split'
          ? directorySummary('child', 'A\\B') : directorySummary('parent', 'A')], nextCursor: null };
      }
      override async countSearchViewSelection(viewId: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        requests.push({ viewId, request });
        const selected = viewId === 'split' ? request.selection.refs.includes('child')
          : request.selection.mode === 'Only' ? request.selection.refs.includes('parent')
          : !request.selection.refs.includes('parent');
        return { revision: request.revision, publicFileCount: selected ? 3 : 0,
          lockedFileCount: 0, selectedItemCount: selected ? 1 : 0 };
      }
      override async commitSearchView(): Promise<never> { commits++; throw new Error('Unexpected commit'); }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    const refine = () => controller.refine({ kind: 'GenericDirectories', includeFullResults: false });
    try {
      await controller.open({ job: { jobId: 'source' }, view: projected('source'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      if (all) controller.selectAll(true);
      controller.toggle('parent', !all);
      await refine();
      expect(controller.current().rows.map(row => row.ref)).toEqual(['child']);
      expect(controller.current().selectionNeedsReview).toBe(true);
      expect(controller.current().selectionRevision).toBeNull();
      expect(controller.current().selectionRefs).toEqual(new Set(['parent']));
      expect(controller.selectedCount()).toBe(0);
      expect(controller.isSelected('child')).toBe(false);
      controller.toggle('child', true);
      expect(controller.current().selectionRefs).toEqual(new Set(['parent']));
      await controller.commit();
      expect(commits).toBe(0);
      expect(requests.filter(item => item.viewId === 'split').map(item => item.request.selection))
        .toEqual([{ mode: 'Only', refs: ['parent'] }]);

      replacing = 'restored';
      await refine();
      expect(controller.current().selectionNeedsReview).toBe(false);
      expect(controller.current().selectionRevision).toBe(1);
      expect(controller.isSelected('parent')).toBe(!all);
      expect(controller.selectedCount()).toBe(all ? 0 : 1);

      replacing = 'split';
      await refine();
      controller.selectAll(false);
      controller.toggle('child', true);
      expect(controller.current().selectionNeedsReview).toBe(false);
      expect(controller.current().selectionRevision).toBe(1);
      expect(controller.isSelected('child')).toBe(true);
    } finally { controller.stop(); }
  });

  it('loads a newly published refinement before its first polling interval', async () => {
    const initial = summary('refined', 0);
    initial.counters.topLevelItemCount = 0;
    initial.isComplete = false;
    const published = { ...summary('refined'), isComplete: false };
    const pages: string[] = [];
    let updates = 0;
    class Api extends DaemonApiClient {
      override async createSearchView() { return initial; }
      override async getSearchViewUpdate() {
        updates += 1;
        return { summary: published, hasNewRevision: true };
      }
      override async getSearchViewFiles(id: string, revision: number) {
        pages.push(`${id}:${revision}`);
        return page(id, revision);
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await open(controller, 'source');
      await controller.refine({ kind: 'Files', includeFullResults: true, filterText: 'Track' });
      expect(updates).toBe(1);
      expect(pages).toEqual(['source:1', 'refined:1']);
      expect(controller.current().summary?.viewId).toBe('refined');
      expect(controller.current().summary?.revision).toBe(1);
    } finally { controller.stop(); }
  });

  it('keeps browser selection while hidden and polls only after returning', async () => {
    vi.useFakeTimers();
    const view = summary('search');
    view.isComplete = false;
    let updates = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override async getSearchViewUpdate() {
        updates += 1;
        return { summary: view, hasNewRevision: false };
      }
      override async countSearchViewSelection(_viewId: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        return { revision: request.revision, publicFileCount: 1, lockedFileCount: 0, selectedItemCount: 1 };
      }
    }
    const controller = new SearchViewController(new Api(), 1_000);
    try {
      await controller.open({ job: { jobId: 'search' }, view,
        draft: { ...emptySearchDraft, resultMode: 'track' } });
      controller.toggle('selected-file', true);
      controller.pause();
      await vi.advanceTimersByTimeAsync(3_000);
      expect(updates).toBe(0);
      await controller.resume();
      expect(updates).toBe(1);
      expect(controller.current().selectionRefs.has('selected-file')).toBe(true);
      await vi.advanceTimersByTimeAsync(1_000);
      expect(updates).toBe(2);
    } finally {
      controller.stop();
      vi.useRealTimers();
    }
  });

  it.each(['track', 'generic'] as const)('keeps the displayed %s view coherent until its replacement page is ready', async mode => {
    const requested = deferred<void>();
    const replacement = deferred<SearchViewFilePageDto | Schema<'SearchViewDirectoryPageDto'>>();
    const folder = directorySummary('stable-folder');
    const kind = mode === 'generic' ? 'GenericDirectories' : 'Files';
    const view = (id: string) => ({ ...summary(id), kind } as SearchViewSummaryDto);
    const result = (id: string) => ({ revision: view(id),
      items: mode === 'generic' ? [folder] : [folder.bestChild], nextCursor: null });
    let pageReads = 0;
    class Api extends DaemonApiClient {
      override async createSearchView() { return view('replacement'); }
      override async getSearchViewFiles(id: string) {
        pageReads++;
        if (id === 'replacement') { requested.resolve(); return await replacement.promise as SearchViewFilePageDto; }
        return result(id) as SearchViewFilePageDto;
      }
      override async getSearchViewDirectories(id: string) {
        pageReads++;
        if (id === 'replacement') { requested.resolve(); return await replacement.promise as Schema<'SearchViewDirectoryPageDto'>; }
        return result(id) as Schema<'SearchViewDirectoryPageDto'>;
      }
      override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        return { revision: request.revision, publicFileCount: 1, lockedFileCount: 0, selectedItemCount: 1 };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    const seen: { id: string | undefined; rows: number }[] = [];
    let pending: Promise<unknown> | undefined;
    try {
      await controller.open({ job: { jobId: 'job' }, view: view('source'),
        draft: { ...emptySearchDraft, resultMode: mode } });
      controller.subscribe(state => seen.push({ id: state.summary?.viewId, rows: state.rows.length }));
      pending = controller.refine({ kind, filterText: 'still matches', includeFullResults: true });
      await requested.promise;
      expect(controller.current().summary?.viewId).toBe('source');
      expect(controller.current().rows).toHaveLength(1);
      expect(controller.current().refining).toBe(true);
      controller.toggle(mode === 'generic' ? folder.ref.ref : folder.bestChild.ref, true);
      replacement.resolve(result('replacement') as SearchViewFilePageDto);
      await pending;
      expect(controller.current().summary?.viewId).toBe('replacement');
      expect(controller.current().rows).toHaveLength(1);
      expect(controller.current().refining).toBe(false);
      expect(controller.isSelected(mode === 'generic' ? folder.ref.ref : folder.bestChild.ref)).toBe(true);
      expect(seen.every(state => state.rows === 1)).toBe(true);
      expect(pageReads).toBe(2);
    } finally {
      replacement.resolve(result('replacement') as SearchViewFilePageDto);
      await pending;
      controller.stop();
    }
  });

  it('ignores an obsolete replacement page while a newer refinement is still being created', async () => {
    const firstPage = deferred<SearchViewFilePageDto>();
    const firstRequested = deferred<void>();
    const secondView = deferred<SearchViewSummaryDto>();
    class Api extends DaemonApiClient {
      override createSearchView(_job: string, request: Schema<'CreateSearchViewRequestDto'>) {
        return request.filterText === 'first' ? Promise.resolve(summary('first')) : secondView.promise;
      }
      override getSearchViewFiles(id: string) {
        if (id === 'first') { firstRequested.resolve(); return firstPage.promise; }
        return Promise.resolve(page(id));
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    let first: Promise<unknown> | undefined;
    let second: Promise<unknown> | undefined;
    try {
      await open(controller, 'source');
      first = controller.refine({ kind: 'Files', filterText: 'first', includeFullResults: true });
      await firstRequested.promise;
      second = controller.refine({ kind: 'Files', filterText: 'second', includeFullResults: true });
      firstPage.resolve(page('first'));
      await first;
      expect(controller.current().summary?.viewId).toBe('source');
      expect(controller.current().refining).toBe(true);
      secondView.resolve(summary('second'));
      await second;
      expect(controller.current().summary?.viewId).toBe('second');
      expect(controller.current().refining).toBe(false);
    } finally {
      firstPage.resolve(page('first'));
      secondView.resolve(summary('second'));
      await Promise.all([first, second]);
      controller.stop();
    }
  });

  it('retains the displayed view when a replacement page fails and can retry the refinement', async () => {
    const file = directorySummary('folder').bestChild;
    let fail = true;
    class Api extends DaemonApiClient {
      override async createSearchView() { return summary('replacement'); }
      override async getSearchViewFiles(id: string) {
        if (id === 'replacement' && fail) throw new Error('page unavailable');
        return { ...page(id), items: [file] };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await open(controller, 'source');
      await controller.refine({ kind: 'Files', includeFullResults: true });
      expect(controller.current().summary?.viewId).toBe('source');
      expect(controller.current().rows).toHaveLength(1);
      expect(controller.current().refinementError?.message).toBe('page unavailable');
      fail = false;
      await controller.refine({ kind: 'Files', includeFullResults: true });
      expect(controller.current().summary?.viewId).toBe('replacement');
      expect(controller.current().rows).toHaveLength(1);
      expect(controller.current().refinementError).toBeNull();
    } finally { controller.stop(); }
  });

  it('replaces only the projection and ignores a late older refinement', async () => {
    const first = deferred<SearchViewSummaryDto>();
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override async createSearchView(_jobId: string, request: Schema<'CreateSearchViewRequestDto'>) {
        return request.filterText === 'first' ? first.promise : summary('second-view');
      }
      override async countSearchViewSelection(_viewId: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        return { revision: request.revision, publicFileCount: 0, lockedFileCount: 0, selectedItemCount: 0 };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await open(controller, 'job');
      controller.toggle('stable-ref', true);
      const older = controller.refine({ kind: 'Files', includeFullResults: true, filterText: 'first' });
      await controller.refine({ kind: 'Files', includeFullResults: true, filterText: 'second' });
      first.resolve(summary('first-view'));
      await older;
      expect(controller.current().summary?.viewId).toBe('second-view');
      expect(controller.current().selectionRefs.has('stable-ref')).toBe(true);
      expect(controller.current().refining).toBe(false);
    } finally { controller.stop(); }
  });

  it('observes a retrieval across view replacement without showing an old-view spinner', async () => {
    const directory = directorySummary('old-folder');
    let jobReads = 0;
    let terminal = false;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) {
        return { revision: summary(id), items: id === 'old-view' ? [directory] : [], nextCursor: null };
      }
      override async retrieveSearchViewDirectory() { return { jobId: 'retrieval-job' }; }
      override async getJob() {
        jobReads += 1;
        return { summary: { jobId: 'retrieval-job', lifecycleState: terminal ? 'Terminal' : 'Running',
          terminalOutcome: terminal ? 'Succeeded' : null }, payload: null, childCount: 0, sourceJobAvailable: false } as Schema<'JobDetailDto'>;
      }
      override async getSearchViewUpdate(id: string) { return { summary: summary(id), hasNewRevision: false }; }
      override async createSearchView() { return summary('new-view'); }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('old-view'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      await controller.retrieveDirectory('old-folder');
      expect(jobReads).toBe(1);
      expect(controller.current().retrievals.get('old-folder')?.loading).toBe(true);

      await controller.refine({ kind: 'GenericDirectories', includeFullResults: true, filterText: 'other' });
      await controller.refresh();
      expect(controller.current().summary?.viewId).toBe('new-view');
      expect(controller.current().retrievals.size).toBe(0);
      expect(jobReads).toBe(2);
      terminal = true;
      await controller.refresh();
      expect(controller.current().retrievals.size).toBe(0);
      expect(jobReads).toBe(3);
      await controller.refresh();
      expect(jobReads).toBe(3);
    } finally { controller.stop(); }
  });

  it('keeps an accepted retrieval whose response arrives after the view is replaced', async () => {
    const accepted = deferred<Schema<'JobSummaryDto'>>();
    let jobReads = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) {
        return { revision: summary(id), items: id === 'old-view' ? [directorySummary('old-folder')] : [], nextCursor: null };
      }
      override async retrieveSearchViewDirectory() { return accepted.promise; }
      override async getJob() {
        jobReads += 1;
        return { summary: { jobId: 'retrieval-job', lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' },
          payload: null, childCount: 0, sourceJobAvailable: false } as Schema<'JobDetailDto'>;
      }
      override async getSearchViewUpdate(id: string) { return { summary: summary(id), hasNewRevision: false }; }
      override async createSearchView() { return summary('new-view'); }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('old-view'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      const retrieving = controller.retrieveDirectory('old-folder');
      await controller.refine({ kind: 'GenericDirectories', includeFullResults: true, filterText: 'other' });
      accepted.resolve({ jobId: 'retrieval-job' } as Schema<'JobSummaryDto'>);
      await retrieving;
      await controller.refresh();
      expect(jobReads).toBe(1);
      expect(controller.current().summary?.viewId).toBe('new-view');
      expect(controller.current().retrievals.size).toBe(0);
      await controller.refresh();
      expect(jobReads).toBe(1);
    } finally { controller.stop(); }
  });

  it('does not start a second retrieval when the same folder reappears in a replacement view', async () => {
    const directory = directorySummary('same-folder');
    let starts = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) {
        return { revision: summary(id), items: [directory], nextCursor: null };
      }
      override async retrieveSearchViewDirectory() {
        starts += 1;
        return { jobId: 'retrieval-job' };
      }
      override async getJob() {
        return { summary: { jobId: 'retrieval-job', lifecycleState: 'Running' },
          payload: null, childCount: 0, sourceJobAvailable: false } as Schema<'JobDetailDto'>;
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id), hasNewRevision: false };
      }
      override async createSearchView() { return summary('new-view'); }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('old-view'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      await controller.retrieveDirectory('same-folder');
      await controller.refine({ kind: 'GenericDirectories', includeFullResults: true });
      expect(controller.current().retrievals.get('same-folder')?.loading).toBe(true);
      await controller.retrieveDirectory('same-folder');
      expect(starts).toBe(1);
    } finally { controller.stop(); }
  });

  it('keeps one in-flight retrieval request when refinement returns the same folder', async () => {
    const accepted = deferred<Schema<'JobSummaryDto'>>();
    let starts = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) {
        return { revision: summary(id), items: [directorySummary('same-folder')], nextCursor: null };
      }
      override async retrieveSearchViewDirectory() {
        starts += 1;
        return accepted.promise;
      }
      override async getJob() {
        return { summary: { jobId: 'retrieval-job', lifecycleState: 'Running' },
          payload: null, childCount: 0, sourceJobAvailable: false } as Schema<'JobDetailDto'>;
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id), hasNewRevision: false };
      }
      override async createSearchView() { return summary('new-view'); }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('old-view'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      const retrieving = controller.retrieveDirectory('same-folder');
      await controller.refine({ kind: 'GenericDirectories', includeFullResults: true });
      expect(controller.current().retrievals.get('same-folder')?.loading).toBe(true);
      await controller.retrieveDirectory('same-folder');
      expect(starts).toBe(1);
      accepted.resolve({ jobId: 'retrieval-job' } as Schema<'JobSummaryDto'>);
      await retrieving;
    } finally { controller.stop(); }
  });

  it('can retry a lost retrieval response after refinement with the original request', async () => {
    const accepted = deferred<Schema<'JobSummaryDto'>>();
    const requests: Parameters<DaemonApiClient['retrieveSearchViewDirectory']>[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) {
        return { revision: summary(id), items: [directorySummary('same-folder')], nextCursor: null };
      }
      override async retrieveSearchViewDirectory(...args: Parameters<DaemonApiClient['retrieveSearchViewDirectory']>) {
        requests.push(args);
        return requests.length === 1 ? accepted.promise : { jobId: 'retrieval-job' };
      }
      override async getJob() {
        return { summary: { jobId: 'retrieval-job', lifecycleState: 'Running' },
          payload: null, childCount: 0, sourceJobAvailable: false } as Schema<'JobDetailDto'>;
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id), hasNewRevision: false };
      }
      override async createSearchView() { return summary('new-view'); }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('old-view'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      const retrieving = controller.retrieveDirectory('same-folder');
      await controller.refine({ kind: 'GenericDirectories', includeFullResults: true });
      accepted.reject(new Error('Response lost'));
      await retrieving;
      expect(controller.current().retrievals.get('same-folder')?.error?.message).toBe('Response lost');
      await controller.retrieveDirectory('same-folder');
      expect(requests).toHaveLength(2);
      expect(requests[1]).toEqual(requests[0]);
      expect(controller.current().retrievals.get('same-folder')?.loading).toBe(true);
    } finally { controller.stop(); }
  });

  it('ignores a retrieval response from a search that was replaced entirely', async () => {
    const previous = deferred<Schema<'JobSummaryDto'>>();
    let starts = 0;
    let jobReads = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) {
        return { revision: summary(id), items: [directorySummary('same-folder')], nextCursor: null };
      }
      override async retrieveSearchViewDirectory() {
        starts += 1;
        return starts === 1 ? previous.promise : { jobId: 'new-retrieval' };
      }
      override async getJob() {
        jobReads += 1;
        return { summary: { jobId: 'new-retrieval', lifecycleState: 'Running' },
          payload: null, childCount: 0, sourceJobAvailable: false } as Schema<'JobDetailDto'>;
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id), hasNewRevision: false };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      const draft = { ...emptySearchDraft, resultMode: 'generic' as const };
      await controller.open({ job: { jobId: 'old-search', lifecycleState: 'Terminal' },
        view: summary('old-view'), draft });
      const oldRetrieval = controller.retrieveDirectory('same-folder');
      await controller.open({ job: { jobId: 'new-search', lifecycleState: 'Terminal' },
        view: summary('new-view'), draft });
      previous.resolve({ jobId: 'old-retrieval' } as Schema<'JobSummaryDto'>);
      await oldRetrieval;
      await controller.refresh();
      expect(jobReads).toBe(0);
      expect(controller.current().retrievals.has('same-folder')).toBe(false);
      await controller.retrieveDirectory('same-folder');
      expect(starts).toBe(2);
    } finally { controller.stop(); }
  });

  it('hides a selected ref from the toolbar when it is absent in the current projection', async () => {
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override async countSearchViewSelection(_viewId: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        return { revision: request.revision, publicFileCount: 0, lockedFileCount: 0, selectedItemCount: 0 };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await open(controller, 'search');
      controller.toggle('hidden-ref', true);
      await Promise.resolve();
      expect(controller.current().selectionRefs.has('hidden-ref')).toBe(true);
      expect(controller.selectedCount()).toBe(0);
    } finally { controller.stop(); }
  });

  it('sends download overrides and freezes them with the selection across retries', async () => {
    const requests: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        requests.push(args);
        throw new Error('Response lost');
      }
    }
    const view = summary('search');
    view.counters.selectableOptionCount = 1;
    const controller = new SearchViewController(new Api());
    try {
      await controller.open({ job: { jobId: 'search' }, view,
        draft: { ...emptySearchDraft, resultMode: 'track' } });
      controller.selectAll(true);
      const options: Schema<'SubmissionOptionsDto'> = {
        downloadSettings: { skip: { skipExisting: true } },
      };
      await controller.commit(options);
      options.downloadSettings!.skip!.skipExisting = false;
      await controller.commit({ downloadSettings: { skip: { skipExisting: true } } });
      expect(requests[0]?.[1].options).toEqual({ downloadSettings: { skip: { skipExisting: true } } });
      expect(requests[1]).toEqual(requests[0]);
      await controller.commit(options);
      expect(requests[2]?.[1].options).toEqual({ downloadSettings: { skip: { skipExisting: false } } });
      expect(requests[2]?.[1].idempotencyKey).not.toBe(requests[1]?.[1].idempotencyKey);
    } finally { controller.stop(); }
  });

  it('shows embedded directory children immediately and continues from their cursor', async () => {
    const directory = directorySummary('folder');
    const first = { ref: 'first', relativePath: 'First.txt', file: {
      ...directory.bestChild, ref: 'first', file: { ...directory.bestChild.file, name: 'First.txt' },
    } };
    const second = { ref: 'second', relativePath: 'Second.txt', file: {
      ...directory.bestChild, ref: 'second', file: { ...directory.bestChild.file, name: 'Second.txt' },
    } };
    directory.initialFiles = { items: [first], nextCursor: 'after-first' };
    let fetchedCursor: string | null | undefined;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) {
        return { revision: summary(id), items: [directory], nextCursor: null };
      }
      override async getSearchViewDirectoryFiles(id: string, _ref: string, _revision: number | string,
        cursor?: string | null) {
        fetchedCursor = cursor;
        return { revision: summary(id), directory: directory.ref, directorySummary: directory,
          items: [second], nextCursor: null };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search' }, view: summary('search'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      expect(fetchedCursor).toBeUndefined();
      expect(controller.current().directoryFiles.get('folder')?.items.map(item => item.ref)).toEqual(['first']);
      await controller.loadDirectoryFiles('folder');
      expect(fetchedCursor).toBe('after-first');
      expect(controller.current().directoryFiles.get('folder')?.items.map(item => item.ref)).toEqual(['first', 'second']);
    } finally { controller.stop(); }
  });

  it.each(['generic', 'album'] as const)('keeps continued %s folder files when live results reorder them', async resultMode => {
    const directory = directorySummary('folder');
    const child = (ref: string) => ({ ref, relativePath: `${ref}.txt`, file: {
      ...directory.bestChild, ref, remoteFilename: `Music/${ref}.txt`,
    } });
    let revision = 1;
    const reads: { revision: number | string; cursor: string | null | undefined; limit: number | undefined }[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string, requested: number | string) {
        const current = { ...directory, initialFiles: {
          items: [child(Number(requested) === 1 ? 'first' : 'arrived')], nextCursor: `preview-${requested}`,
        } };
        return { revision: summary(id, Number(requested)), items: [current], nextCursor: null };
      }
      override async getSearchViewDirectoryFiles(id: string, _ref: string, requested: number | string,
        cursor?: string | null, limit?: number) {
        reads.push({ revision: requested, cursor, limit });
        return { revision: summary(id, Number(requested)), directory: directory.ref,
          directorySummary: directory, items: [child(Number(requested) === 1 ? 'second' : 'first')], nextCursor: null };
      }
      override async getSearchViewUpdate(id: string, requested: number | string) {
        return { summary: summary(id, revision), hasNewRevision: Number(requested) !== revision };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('search'),
        draft: { ...emptySearchDraft, resultMode } });
      expect(reads).toHaveLength(0);
      await controller.loadDirectoryFiles('folder');
      expect(controller.current().directoryFiles.get('folder')?.items.map(item => item.ref)).toEqual(['first', 'second']);
      revision = 2;
      await controller.refresh();
      expect(controller.current().directoryFiles.get('folder')?.items.map(item => item.ref)).toEqual(['arrived', 'first']);
      expect(reads).toEqual([
        { revision: 1, cursor: 'preview-1', limit: 500 },
        { revision: 2, cursor: 'preview-2', limit: 500 },
      ]);
      await controller.refresh();
      expect(reads).toHaveLength(2);
    } finally { controller.stop(); }
  });

  it('ignores old child responses and isolates a failed folder refresh without forgetting the requested extent', async () => {
    const folder = directorySummary('folder');
    const other = directorySummary('other', 'Other');
    const child = (ref: string) => ({ ref, relativePath: `${ref}.txt`, file: { ...folder.bestChild, ref } });
    let revision = 1;
    let fail = true;
    const oldPage = deferred<Schema<'SearchViewDirectoryFilePageDto'>>();
    const reads: { revision: number | string; cursor: string | null | undefined; limit: number | undefined }[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string, requested: number | string) {
        return { revision: summary(id, Number(requested)), items: [folder, other].map(directory => ({
          ...directory, initialFiles: { items: [child(`${directory.ref.ref}-${requested}`)], nextCursor: `preview-${requested}` },
        })), nextCursor: null };
      }
      override getSearchViewDirectoryFiles(id: string, _ref: string, requested: number | string,
        cursor?: string | null, limit?: number) {
        reads.push({ revision: requested, cursor, limit });
        if (Number(requested) === 1) return oldPage.promise;
        if (Number(requested) === 3 && fail) { fail = false; return Promise.reject(new Error('Folder unavailable')); }
        return Promise.resolve({ revision: summary(id, Number(requested)), directory: folder.ref,
          directorySummary: folder, items: [child(`continued-${requested}`)], nextCursor: null });
      }
      override async getSearchViewUpdate(id: string, requested: number | string) {
        return { summary: summary(id, revision), hasNewRevision: Number(requested) !== revision };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('search'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      const old = controller.loadDirectoryFiles('folder');
      await controller.refresh(true);
      expect(reads).toHaveLength(1);
      expect(controller.current().directoryFiles.get('folder')?.loading).toBe(true);
      revision = 2;
      await controller.refresh();
      expect(controller.current().directoryFiles.get('folder')?.items.map(item => item.ref)).toEqual(['folder-2', 'continued-2']);
      oldPage.resolve({ revision: summary('search'), directory: folder.ref, directorySummary: folder,
        items: [child('stale')], nextCursor: null });
      await old;
      expect(controller.current().directoryFiles.get('folder')?.items.map(item => item.ref)).toEqual(['folder-2', 'continued-2']);
      revision = 3;
      await controller.refresh();
      expect(controller.current().summary?.revision).toBe(3);
      expect(controller.current().phase).toBe('ready');
      expect(controller.current().error).toBeNull();
      expect(controller.current().directoryFiles.get('folder')?.error?.message).toBe('Folder unavailable');
      expect(controller.current().directoryFiles.get('other')?.items.map(item => item.ref)).toEqual(['other-3']);
      await controller.loadDirectoryFiles('folder');
      expect(controller.current().directoryFiles.get('folder')?.items.map(item => item.ref)).toEqual(['folder-3', 'continued-3']);
      expect(reads).toEqual([
        { revision: 1, cursor: 'preview-1', limit: 500 },
        { revision: 2, cursor: 'preview-2', limit: 500 },
        { revision: 3, cursor: 'preview-3', limit: 500 },
        { revision: 3, cursor: 'preview-3', limit: 500 },
      ]);
      await controller.open({ job: { jobId: 'fresh', lifecycleState: 'Terminal' }, view: summary('fresh'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      expect(reads).toHaveLength(4);
      expect(controller.current().directoryFiles.get('folder')?.items.map(item => item.ref)).toEqual(['folder-1']);
    } finally { controller.stop(); }
  });

  it('keeps selected counts on their original revision while displayed folder contents advance', async () => {
    const folder = directorySummary('folder');
    const child = (ref: string) => ({ ref, relativePath: `${ref}.txt`, file: { ...folder.bestChild, ref } });
    const oldCount = deferred<Schema<'CountSearchViewSelectionResponseDto'>>();
    const nextFiles = deferred<Schema<'SearchViewDirectoryFilePageDto'>>();
    const filesStarted = deferred<void>();
    const countDisplayed = deferred<void>();
    let revision = 1;
    let fileReads = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string, requested: number | string) {
        return { revision: summary(id, Number(requested)), items: [{ ...folder,
          initialFiles: { items: [child(`preview-${requested}`)], nextCursor: `cursor-${requested}` } }], nextCursor: null };
      }
      override getSearchViewDirectoryFiles(id: string, _ref: string, requested: number | string) {
        fileReads += 1;
        if (Number(requested) === 2) { filesStarted.resolve(); return nextFiles.promise; }
        return Promise.resolve({ revision: summary(id), directory: folder.ref, directorySummary: folder,
          items: [child('continued-1')], nextCursor: null });
      }
      override async getSearchViewUpdate(id: string, requested: number | string) {
        return { summary: summary(id, revision), hasNewRevision: Number(requested) !== revision };
      }
      override countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        return Number(request.revision) === 1 ? oldCount.promise : Promise.resolve({ revision: request.revision,
          publicFileCount: 2, lockedFileCount: 0, selectedItemCount: 1 });
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    const observed: { revision: number | string | undefined; selectedRevision: number | string | null }[] = [];
    const unsubscribe = controller.subscribe(state => {
      observed.push({ revision: state.summary?.revision, selectedRevision: state.selectionRevision });
      if (state.summary?.revision === 1 && state.selectionCounts) countDisplayed.resolve();
    });
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('search'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      await controller.loadDirectoryFiles('folder');
      controller.toggle('folder', true);
      revision = 2;
      const refresh = controller.refresh();
      await filesStarted.promise;
      expect(controller.current().directoryFiles.get('folder')?.loading).toBe(true);
      await controller.loadDirectoryFiles('folder');
      expect(fileReads).toBe(2);
      oldCount.resolve({ revision: 1, publicFileCount: 1, lockedFileCount: 0, selectedItemCount: 1 });
      await countDisplayed.promise;
      expect(observed.filter(state => state.revision === 1).every(state => state.selectedRevision !== 2)).toBe(true);
      nextFiles.resolve({ revision: summary('search', 2), directory: folder.ref, directorySummary: folder,
        items: [child('continued-2')], nextCursor: null });
      await refresh;
      expect(controller.current().summary?.revision).toBe(2);
      expect(controller.current().selectionRevision).toBe(1);
      expect(controller.current().selectionCounts?.publicFileCount).toBe(1);
    } finally { unsubscribe(); controller.stop(); }
  });

  it.each(['representative', 'alternative'])('restores continued files for a chosen group album %s without duplicate reads', async chosen => {
    let revision = 1;
    let rejectChoiceCheck = false;
    const directory = (ref: string, requested: number | string): Schema<'SearchViewDirectoryDto'> => {
      const value = directorySummary(ref, ref);
      return { ...value, initialFiles: { items: [{ ref: `${ref}-${requested}`, relativePath: 'first.txt',
        file: { ...value.bestChild, ref: `${ref}-${requested}` } }], nextCursor: `${ref}-preview-${requested}` } };
    };
    const reads: { ref: string; revision: number | string }[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewAlbumGroups(id: string, requested: number | string) {
        return { revision: summary(id, Number(requested)), items: [{ ref: 'group',
          query: { artist: 'Artist', album: 'Album', artistMaybeWrong: false }, shareCount: 2,
          selectableOptionCount: 2, representative: directory('representative', requested) }], nextCursor: null };
      }
      override async getSearchViewAlbumGroupOptions(id: string) {
        return { revision: summary(id), groupRef: 'group',
          items: [directory('representative', 1), directory('alternative', 1)], nextCursor: null };
      }
      override async getSearchViewDirectoryFiles(id: string, ref: string, requested: number | string) {
        reads.push({ ref, revision: requested });
        const value = directory(ref, requested);
        return { revision: summary(id, Number(requested)), directory: value.ref,
          directorySummary: value, items: [{ ref: `${ref}-continued-${requested}`, relativePath: 'second.txt',
            file: { ...value.bestChild, ref: `${ref}-continued-${requested}` } }], nextCursor: null };
      }
      override async getSearchViewUpdate(id: string, requested: number | string) {
        return { summary: summary(id, revision), hasNewRevision: Number(requested) !== revision };
      }
      override async resolveSearchViewChosenOptions(id: string, request: Schema<'ResolveSearchViewChosenOptionsRequestDto'>) {
        if (rejectChoiceCheck) { rejectChoiceCheck = false; throw new Error('Choice check unavailable'); }
        return { revision: summary(id, Number(request.revision)), items: [{ groupRef: 'group', optionRef: chosen,
          track: null, album: directory(chosen, request.revision) }] };
      }
      override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        return { revision: request.revision, publicFileCount: 2, lockedFileCount: 0, selectedItemCount: 1 };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'group', lifecycleState: 'Terminal' }, view: summary('group'),
        draft: { ...emptySearchDraft, resultMode: 'album-groups' } });
      await controller.openGroupOptions('group');
      controller.chooseGroupOption('group', controller.current().groupOptions!.items.find(option => option.ref === chosen)!);
      await controller.loadDirectoryFiles(chosen);
      // Keep the browser choice, but end the exact selection before following arrivals.
      controller.selectAll(false);
      const refreshed = new Promise<void>(resolve => {
        const unsubscribe = controller.subscribe(state => {
          if (state.checkedChoiceRevisions.get('group') === 'group:2'
            && state.directoryFiles.get(chosen)?.items.at(-1)?.ref === `${chosen}-continued-2`) {
            unsubscribe(); resolve();
          }
        });
      });
      revision = 2;
      await controller.refresh();
      await refreshed;
      expect(controller.current().directoryFiles.get(chosen)?.items.map(item => item.ref))
        .toEqual([`${chosen}-2`, `${chosen}-continued-2`]);
      expect(reads).toEqual([{ ref: chosen, revision: 1 }, { ref: chosen, revision: 2 }]);
      const failed = new Promise<void>(resolve => {
        const unsubscribe = controller.subscribe(state => {
          if (state.choiceValidationError) { unsubscribe(); resolve(); }
        });
      });
      revision = 3;
      rejectChoiceCheck = true;
      await controller.refresh();
      await failed;
      const retried = new Promise<void>(resolve => {
        const unsubscribe = controller.subscribe(state => {
          if (state.checkedChoiceRevisions.get('group') === 'group:3'
            && state.directoryFiles.get(chosen)?.items.at(-1)?.ref === `${chosen}-continued-3`) {
            unsubscribe(); resolve();
          }
        });
      });
      await controller.refresh(true);
      await retried;
      expect(reads).toEqual([{ ref: chosen, revision: 1 }, { ref: chosen, revision: 2 }, { ref: chosen, revision: 3 }]);
    } finally { controller.stop(); }
  });

  it('keeps album-group file choices in browser state until commit and freezes retries', async () => {
    const requests: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    const directory = (ref: string): Schema<'SearchViewDirectoryDto'> => ({
      ref: { ref, username: ref, folderPath: `${ref}/album` }, visibility: 'Public', preferenceTier: 'Preferred',
      satisfiedPreferredConditions: [], publicMatchingFileCount: 2, lockedMatchingFileCount: 0,
      publicMatchingBytes: 256, lockedMatchingBytes: 0, retrievedFileCount: null, retrievedBytes: null,
      retrievalState: 'SearchResultsOnly', peer: { username: ref },
      bestChild: { ref: `${ref}-intro`, visibility: 'Public', preferenceTier: 'Preferred',
        necessaryConditionsSatisfied: true, satisfiedPreferredConditions: [], unsatisfiedPreferredConditions: [],
        remoteFilename: `${ref}/album/Intro.flac`, peer: { username: ref },
        file: { name: 'Intro.flac', size: 128, extension: '.flac', bitRate: null, bitDepth: null,
          sampleRate: null, length: null } },
    });
    const first = directory('first');
    const second = directory('second');
    second.initialFiles = { items: [second.bestChild, {
      ...second.bestChild, ref: 'second-finale', remoteFilename: 'second/album/Finale.flac',
      file: { ...second.bestChild.file, name: 'Finale.flac' },
    }].map(file => ({ ref: file.ref, relativePath: file.file.name, file })), nextCursor: null };
    class Api extends DaemonApiClient {
      override async getSearchViewAlbumGroups(id: string) {
        return { revision: summary(id), items: [{ ref: 'group', query: { artist: 'Artist', album: 'Album',
          artistMaybeWrong: false }, shareCount: 2, selectableOptionCount: 2, representative: first }], nextCursor: null };
      }
      override async getSearchViewAlbumGroupOptions(id: string) {
        return { revision: summary(id), groupRef: 'group', items: [first, second], nextCursor: null };
      }
      override async getSearchViewDirectoryFiles(id: string, ref: string) {
        const intro = second.bestChild;
        const finale = { ...intro, ref: `${ref}-finale`, remoteFilename: `${ref}/album/Finale.flac`,
          file: { ...intro.file, name: 'Finale.flac' } };
        return { revision: summary(id), directory: { ref, username: ref, folderPath: `${ref}/album` },
          items: [intro, finale].map(file => ({ ref: file.ref, relativePath: file.file.name, file })),
          nextCursor: null, directorySummary: ref === 'second' ? second : first };
      }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        requests.push(args);
        throw new Error('Response lost');
      }
    }
    const view = summary('search');
    view.kind = 'AlbumGroups';
    view.counters.topLevelItemCount = 1;
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search' }, view,
        draft: { ...emptySearchDraft, resultMode: 'album-groups' } });
      await controller.openGroupOptions('group');
      expect(controller.current().directoryFiles.get('second')?.items).toHaveLength(2);
      controller.chooseGroupOption('group', controller.current().groupOptions!.items[1]!);
      controller.toggleAlbumGroupFile('group', 'second-intro', false);
      expect(controller.current().albumFileSelections.get('group')?.mode).toBe('AllExcept');
      await controller.commit();
      await controller.commit();
      expect(requests[0]?.[1].chosenOptions).toEqual({ group: 'second' });
      expect(requests[0]?.[1].albumFileSelections).toEqual({ group: { mode: 'AllExcept', refs: ['second-intro'] } });
      expect(requests[1]).toEqual(requests[0]);
      controller.toggle('group', true);
      expect(controller.current().albumFileSelections.size).toBe(0);
    } finally { controller.stop(); }
  });

  it('refreshes an open alternatives dialog at a new revision and ignores an old option page', async () => {
    const first = directorySummary('first').bestChild;
    const second = directorySummary('second').bestChild;
    const olderPage = deferred<Awaited<ReturnType<DaemonApiClient['getSearchViewTrackGroupOptions']>>>();
    let nextRevision = 2;
    const view = summary('group', 1);
    view.kind = 'TrackGroups';
    view.counters.topLevelItemCount = 1;
    class Api extends DaemonApiClient {
      override async getSearchViewTrackGroups(id: string, revision: number | string) {
        return { revision: summary(id, Number(revision)), items: Number(revision) === 3 ? [] : [{
          ref: 'group', query: { artist: 'Artist', title: 'Title', artistMaybeWrong: false },
          shareCount: 2, selectableOptionCount: 2, representative: first,
        }], nextCursor: null };
      }
      override getSearchViewTrackGroupOptions(id: string, _group: string, revision: number | string, cursor?: string | null) {
        if (Number(revision) === 1 && cursor) return olderPage.promise;
        return Promise.resolve({ revision: summary(id, Number(revision)), groupRef: 'group',
          items: Number(revision) === 1 ? [first] : [second],
          nextCursor: Number(revision) === 1 ? 'older' : null });
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id, nextRevision), hasNewRevision: true };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'group' }, view,
        draft: { ...emptySearchDraft, resultMode: 'song-groups' } });
      await controller.openGroupOptions('group');
      const oldRequest = controller.loadMoreGroupOptions();
      expect(controller.current().groupOptions?.loading).toBe(true);
      const refreshed = new Promise<void>(resolve => {
        const unsubscribe = controller.subscribe(state => {
          if (state.groupOptions?.revision === 2 && state.groupOptions.items[0]?.ref === second.ref) {
            unsubscribe();
            resolve();
          }
        });
      });
      await controller.refresh();
      await refreshed;
      expect(controller.current().groupOptions?.items.map(item => item.ref)).toEqual([second.ref]);
      olderPage.resolve({ revision: summary('group', 1), groupRef: 'group', items: [first], nextCursor: null });
      await oldRequest;
      expect(controller.current().groupOptions?.items.map(item => item.ref)).toEqual([second.ref]);
      nextRevision = 3;
      await controller.refresh();
      expect(controller.current().groupOptions).toBeNull();
    } finally { controller.stop(); }
  });

  it('submits a locally chosen group source and keeps retries identical', async () => {
    const requests: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    const file = (ref: string, username: string): Schema<'SearchViewFileDto'> => ({
      ref, visibility: 'Public', preferenceTier: 'Preferred', necessaryConditionsSatisfied: true,
      satisfiedPreferredConditions: [], unsatisfiedPreferredConditions: [], remoteFilename: `Music/${ref}.flac`,
      peer: { username }, file: { name: `${ref}.flac`, size: 128, extension: '.flac',
        bitRate: null, bitDepth: null, sampleRate: null, length: null },
    });
    const first = file('first-option', 'first-peer');
    const second = file('second-option', 'second-peer');
    class Api extends DaemonApiClient {
      override async getSearchViewTrackGroups(id: string) {
        return { revision: summary(id), items: [{ ref: 'group', query: { artist: 'Artist', title: 'Title',
          artistMaybeWrong: false }, shareCount: 2, selectableOptionCount: 2, representative: first }], nextCursor: null };
      }
      override async getSearchViewTrackGroupOptions(id: string) {
        return { revision: summary(id), groupRef: 'group', items: [first, second], nextCursor: null };
      }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        requests.push(args);
        throw new Error('Response lost');
      }
    }
    const view = summary('search');
    view.kind = 'TrackGroups';
    view.counters.topLevelItemCount = 1;
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search' }, view,
        draft: { ...emptySearchDraft, resultMode: 'song-groups' } });
      await controller.openGroupOptions('group');
      const panel = controller.current().groupOptions;
      expect(panel?.items.map(option => option.ref)).toEqual(['first-option', 'second-option']);
      controller.chooseGroupOption('group', panel!.items[1]!);
      expect(controller.current().chosenOptions.get('group')?.ref).toBe('second-option');
      expect(controller.selectedCount()).toBe(1);
      await controller.commit();
      await controller.commit();
      expect(requests[0]?.[1].chosenOptions).toEqual({ group: 'second-option' });
      expect(requests[1]).toEqual(requests[0]);
      controller.selectAll(true);
      await controller.commit();
      expect(requests[2]?.[1].selection.mode).toBe('AllExcept');
      expect(requests[2]?.[1].chosenOptions).toEqual({ group: 'second-option' });
      controller.toggle('group', false);
      controller.toggle('group', true);
      await controller.commit();
      expect(requests[3]?.[1].chosenOptions).toEqual({ group: 'second-option' });
    } finally { controller.stop(); }
  });

  it('rechecks an unselected group choice at a new revision before reusing it', async () => {
    const first = directorySummary('first').bestChild;
    const second = { ...directorySummary('second').bestChild, peer: { username: 'second-peer' } };
    const resolution = deferred<Schema<'ResolveSearchViewChosenOptionsResponseDto'>>();
    const commits: string[] = [];
    const checked: Schema<'ResolveSearchViewChosenOptionsRequestDto'>[] = [];
    const view = summary('group', 1);
    view.kind = 'TrackGroups';
    view.counters.topLevelItemCount = 1;
    class Api extends DaemonApiClient {
      override async getSearchViewTrackGroups(id: string, revision: number | string) {
        return { revision: summary(id, Number(revision)), items: [{
          ref: 'group', query: { artist: 'Artist', title: 'Title', artistMaybeWrong: false },
          shareCount: 2, selectableOptionCount: 2, representative: first,
        }], nextCursor: null };
      }
      override async getSearchViewTrackGroupOptions(id: string, _group: string, revision: number | string) {
        return { revision: summary(id, Number(revision)), groupRef: 'group',
          items: Number(revision) === 1 ? [first, second] : [first], nextCursor: null };
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id, 2), hasNewRevision: true };
      }
      override resolveSearchViewChosenOptions(_id: string, request: Schema<'ResolveSearchViewChosenOptionsRequestDto'>) {
        checked.push(request);
        return resolution.promise;
      }
      override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        return { revision: request.revision, publicFileCount: 1, lockedFileCount: 0, selectedItemCount: 1 };
      }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        commits.push(args[1].chosenOptions?.group ?? 'representative');
        throw new Error('Response lost');
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'group' }, view,
        draft: { ...emptySearchDraft, resultMode: 'song-groups' } });
      await controller.openGroupOptions('group');
      controller.chooseGroupOption('group', controller.current().groupOptions!.items[1]!);
      controller.selectAll(false);
      await controller.refresh();
      expect(checked).toEqual([{ revision: 2, chosenOptions: { group: second.ref } }]);
      expect(controller.current().choiceValidationLoading).toBe(true);
      expect(controller.current().checkedChoiceRevisions.get('group')).toBe('group:1');
      await controller.commit();
      expect(commits).toEqual([]);
      const stale = new Promise<void>(resolve => {
        const unsubscribe = controller.subscribe(state => {
          if (state.staleChoices.has('group')) resolve();
        });
        void unsubscribe;
      });
      resolution.resolve({ revision: summary('group', 2), items: [
        { groupRef: 'group', optionRef: second.ref, track: null, album: null },
      ] });
      await stale;
      expect(controller.current().choiceValidationLoading).toBe(false);
      expect(controller.current().chosenOptions.get('group')?.ref).toBe(second.ref);
      await controller.commit();
      expect(commits).toEqual([]);
      await controller.openGroupOptions('group');
      controller.chooseGroupOption('group', controller.current().groupOptions!.items[0]!);
      expect(controller.current().staleChoices.has('group')).toBe(false);
      await controller.commit();
      expect(commits).toEqual([first.ref]);
    } finally { controller.stop(); }
  });

  it('retries the exact accepted command while live presentation validates its choice', async () => {
    const option = directorySummary('chosen').bestChild;
    const requests: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    const unresolved = deferred<Schema<'ResolveSearchViewChosenOptionsResponseDto'>>();
    const view = summary('group', 1);
    view.kind = 'TrackGroups';
    view.counters.topLevelItemCount = 1;
    class Api extends DaemonApiClient {
      override async getSearchViewTrackGroups(id: string, revision: number | string) {
        return { revision: summary(id, Number(revision)), items: [{
          ref: 'group', query: { artist: 'Artist', title: 'Title', artistMaybeWrong: false },
          shareCount: 1, selectableOptionCount: 1, representative: option,
        }], nextCursor: null };
      }
      override async getSearchViewTrackGroupOptions(id: string) {
        return { revision: summary(id, 1), groupRef: 'group', items: [option], nextCursor: null };
      }
      override async getSearchViewUpdate(id: string) { return { summary: summary(id, 2), hasNewRevision: true }; }
      override resolveSearchViewChosenOptions() { return unresolved.promise; }
      override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        return { revision: request.revision, publicFileCount: 1, lockedFileCount: 0, selectedItemCount: 1 };
      }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        requests.push(args);
        throw new Error('Response lost');
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'group', lifecycleState: 'Terminal' }, view,
        draft: { ...emptySearchDraft, resultMode: 'song-groups' } });
      await controller.openGroupOptions('group');
      controller.chooseGroupOption('group', controller.current().groupOptions!.items[0]!);
      await controller.commit();
      expect(controller.current().canRetryPendingCommit).toBe(true);
      await controller.refresh();
      expect(controller.current().choiceValidationLoading).toBe(true);
      expect(controller.current().selectionRevision).toBe(1);
      await controller.commit();
      expect(requests).toHaveLength(2);
      expect(requests[1]).toEqual(requests[0]);
    } finally { controller.stop(); }
  });

  it('keeps an exact commit retry when a newer live revision hides the selected group', async () => {
    const option = directorySummary('chosen').bestChild;
    const requests: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    const view = summary('group', 1);
    view.kind = 'TrackGroups';
    view.counters.topLevelItemCount = 1;
    class Api extends DaemonApiClient {
      override async getSearchViewTrackGroups(id: string, revision: number | string) {
        return { revision: summary(id, Number(revision)), items: Number(revision) === 1 ? [{
          ref: 'group', query: { artist: 'Artist', title: 'Title', artistMaybeWrong: false },
          shareCount: 1, selectableOptionCount: 1, representative: option,
        }] : [], nextCursor: null };
      }
      override async getSearchViewTrackGroupOptions(id: string) {
        return { revision: summary(id, 1), groupRef: 'group', items: [option], nextCursor: null };
      }
      override async getSearchViewUpdate(id: string) {
        const updated = summary(id, 2);
        updated.kind = 'TrackGroups';
        updated.counters.topLevelItemCount = 0;
        return { summary: updated, hasNewRevision: true };
      }
      override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        return { revision: request.revision, publicFileCount: Number(request.revision) === 1 ? 1 : 0,
          lockedFileCount: 0, selectedItemCount: Number(request.revision) === 1 ? 1 : 0 };
      }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        requests.push(args);
        throw new Error('Response lost');
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'group', lifecycleState: 'Terminal' }, view,
        draft: { ...emptySearchDraft, resultMode: 'song-groups' } });
      await controller.openGroupOptions('group');
      controller.chooseGroupOption('group', controller.current().groupOptions!.items[0]!);
      await controller.commit();
      await controller.refresh();
      expect(controller.selectedCount()).toBe(1);
      expect(controller.current().canRetryPendingCommit).toBe(true);
      await controller.commit();
      expect(requests).toHaveLength(2);
      expect(requests[1]).toEqual(requests[0]);
    } finally { controller.stop(); }
  });

  it('isolates retrieval status failures while another folder completes', async () => {
    const directories: Schema<'SearchViewDirectoryDto'>[] = ['first', 'second'].map(ref => ({
      ref: { ref, username: 'peer', folderPath: ref }, visibility: 'Public', preferenceTier: 'Preferred',
      satisfiedPreferredConditions: [], publicMatchingFileCount: 1, lockedMatchingFileCount: 0,
      publicMatchingBytes: 1, lockedMatchingBytes: 0, retrievedFileCount: null, retrievedBytes: null,
      retrievalState: 'SearchResultsOnly', peer: { username: 'peer' },
      bestChild: { ref: `${ref}-file`, visibility: 'Public', preferenceTier: 'Preferred',
        necessaryConditionsSatisfied: true, satisfiedPreferredConditions: [], unsatisfiedPreferredConditions: [],
        remoteFilename: `${ref}/file`, peer: { username: 'peer' },
        file: { name: 'file', size: 1, extension: null, bitRate: null, bitDepth: null, sampleRate: null, length: null } },
    }));
    let failFirst = true;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) { return { revision: summary(id), items: directories, nextCursor: null }; }
      override async retrieveSearchViewDirectory(_id: string, _revision: number | string, directory: Schema<'PeerDirectoryRefDto'>) { return { jobId: directory.ref }; }
      override async getJob(id: string): Promise<Schema<'JobDetailDto'>> {
        if (id === 'first' && failFirst) throw new Error('First status unavailable');
        return { summary: { jobId: id, lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' }, payload: null, childCount: 0, sourceJobAvailable: false };
      }
      override async getSearchViewUpdate(id: string) { return { summary: summary(id), hasNewRevision: false }; }
      override async getSearchViewDirectoryFiles(id: string, ref: string) {
        return { revision: summary(id), directory: { ref, username: 'peer', folderPath: ref },
          items: [], nextCursor: null, directorySummary: directories.find(item => item.ref.ref === ref)! };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search' }, view: summary('search'), draft: { ...emptySearchDraft, resultMode: 'generic' } });
      await controller.retrieveDirectory('first');
      await controller.retrieveDirectory('second');
      expect(controller.current().retrievals.get('first')?.error?.message).toBe('First status unavailable');
      expect(controller.current().retrievals.get('second')).toEqual({ loading: false, error: null });
      expect(controller.current().directoryFiles.has('second')).toBe(true);
      expect(controller.current().error).toBeNull();
      failFirst = false;
      await controller.refresh();
      expect(controller.current().retrievals.get('first')).toEqual({ loading: false, error: null });
    } finally { controller.stop(); }
  });

  it('retries a lost retrieval response with the same accepted request and key', async () => {
    const directory = directorySummary('folder');
    const requests: unknown[][] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) {
        return { revision: summary(id), items: [directory], nextCursor: null };
      }
      override async retrieveSearchViewDirectory(...args: Parameters<DaemonApiClient['retrieveSearchViewDirectory']>) {
        requests.push(args);
        if (requests.length === 1) throw new Error('Response lost');
        return { jobId: 'original-retrieval' } as Schema<'JobSummaryDto'>;
      }
      override async getJob() {
        return { summary: { jobId: 'original-retrieval', lifecycleState: 'Running' }, payload: null, childCount: 0, sourceJobAvailable: false } as Schema<'JobDetailDto'>;
      }
      override async getSearchViewUpdate(id: string) { return { summary: summary(id), hasNewRevision: false }; }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('search'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      await controller.retrieveDirectory('folder');
      expect(controller.current().retrievals.get('folder')?.error?.message).toBe('Response lost');
      await controller.retrieveDirectory('folder');
      expect(requests).toHaveLength(2);
      expect(requests[1]).toEqual(requests[0]);
      expect(requests[0]?.[3]).toMatch(/^[0-9a-f-]{36}$/i);
      expect(controller.current().retrievals.get('folder')?.loading).toBe(true);
    } finally { controller.stop(); }
  });

  it('keeps a lost retrieval request stable while another folder advances the view', async () => {
    const directories = [directorySummary('first'), directorySummary('second')];
    const requests: Parameters<DaemonApiClient['retrieveSearchViewDirectory']>[] = [];
    let revision = 1;
    let firstAttempts = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string, requestedRevision: number | string) {
        return { revision: summary(id, Number(requestedRevision)), items: directories, nextCursor: null };
      }
      override async retrieveSearchViewDirectory(...args: Parameters<DaemonApiClient['retrieveSearchViewDirectory']>) {
        requests.push(args);
        if (args[2].ref === 'first') {
          if (++firstAttempts === 1) throw new Error('First response lost');
          return { jobId: 'first-job' } as Schema<'JobSummaryDto'>;
        }
        revision = 2;
        return { jobId: 'second-job' } as Schema<'JobSummaryDto'>;
      }
      override async getJob(id: string) {
        return { summary: { jobId: id, lifecycleState: id === 'second-job' ? 'Terminal' : 'Running',
          terminalOutcome: id === 'second-job' ? 'Succeeded' : null },
          payload: null, childCount: 0, sourceJobAvailable: false } as Schema<'JobDetailDto'>;
      }
      override async getSearchViewUpdate(id: string, requestedRevision: number | string) {
        return { summary: summary(id, revision), hasNewRevision: Number(requestedRevision) !== revision };
      }
      override async getSearchViewDirectoryFiles(id: string, ref: string) {
        return { revision: summary(id, revision), directory: directories.find(item => item.ref.ref === ref)!.ref,
          items: [], nextCursor: null, directorySummary: directories.find(item => item.ref.ref === ref)! };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search', lifecycleState: 'Terminal' }, view: summary('search'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      await controller.retrieveDirectory('first');
      expect(controller.current().retrievals.get('first')?.error?.message).toBe('First response lost');
      await controller.retrieveDirectory('second');
      expect(controller.current().summary?.revision).toBe(2);
      expect(controller.current().retrievals.get('second')).toEqual({ loading: false, error: null });
      await controller.retrieveDirectory('first');
      const firstRequests = requests.filter((request) => request[2].ref === 'first');
      expect(firstRequests).toHaveLength(2);
      expect(firstRequests[1]).toEqual(firstRequests[0]);
      expect(firstRequests[0]?.[1]).toBe(1);
      expect(controller.current().retrievals.get('first')?.loading).toBe(true);
      expect(controller.current().retrievals.get('second')).toEqual({ loading: false, error: null });
    } finally { controller.stop(); }
  });

  it('keeps an unknown commit outcome retryable but releases a received missing-view rejection', async () => {
    const requests: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    let missingView = false;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        requests.push(args);
        if (missingView) throw new DaemonApiError('View not found', 404);
        throw new Error('Response lost');
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'search');
      controller.toggle('chosen-file', true);
      await controller.commit();
      expect(controller.current().canRetryPendingCommit).toBe(true);
      missingView = true;
      await controller.commit();
      expect(requests).toHaveLength(2);
      expect(requests[1]).toEqual(requests[0]);
      expect(controller.current().canRetryPendingCommit).toBe(false);
      expect(controller.current().error).toBeInstanceOf(DaemonApiError);
    } finally { controller.stop(); }
  });

  it('re-includes a child of an excluded folder and folds it back into the parent', async () => {
    const requests: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        requests.push(args);
        throw new Error('Response lost');
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      const view = summary('search');
      view.counters.selectableOptionCount = 1;
      await controller.open({ job: { jobId: 'search' }, view, draft: { ...emptySearchDraft, resultMode: 'track' } });
      controller.selectAll(true);
      controller.toggle('folder', false);
      controller.toggle('child', true, 'folder');
      expect(controller.isSelected('child', 'folder')).toBe(true);
      expect(controller.isSelected('folder')).toBe(false);
      expect(controller.selectedCount()).toBe(1);
      await controller.commit();
      await controller.commit();
      expect(requests[0]?.[1].includedFileRefs).toEqual(['child']);
      expect(requests[1]).toEqual(requests[0]);
      controller.toggle('folder', true);
      expect(controller.current().includedFiles.size).toBe(0);
      expect(controller.selectedCount()).toBe(1);
      controller.toggle('folder', false);
      expect(controller.isSelected('child', 'folder')).toBe(false);
      expect(controller.selectedCount()).toBe(0);
    } finally { controller.stop(); }
  });

  it('uses top-level refs only to decide whether the selection toolbar is visible', async () => {
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
    }
    const controller = new SearchViewController(new Api());
    try {
      const view = summary('search');
      view.kind = 'GenericDirectories';
      view.counters.topLevelItemCount = 2;
      view.counters.selectableOptionCount = 7;
      await controller.open({ job: { jobId: 'search' }, view, draft: { ...emptySearchDraft, resultMode: 'track' } });
      controller.selectAll(true);
      expect(controller.selectedCount()).toBe(2);
      controller.toggle('folder', false);
      expect(controller.selectedCount()).toBe(1);
      controller.toggle('child', false, 'other-folder');
      expect(controller.selectedCount()).toBe(1);
    } finally { controller.stop(); }
  });

  it('counts selected files at the bound revision and ignores an earlier count response', async () => {
    const first = deferred<Schema<'CountSearchViewSelectionResponseDto'>>();
    const second = deferred<Schema<'CountSearchViewSelectionResponseDto'>>();
    const requests: Schema<'CountSearchViewSelectionRequestDto'>[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, revision: number | string) { return page(id, Number(revision)); }
      override async getSearchViewUpdate(id: string) { return { summary: summary(id, 2), hasNewRevision: true }; }
      override countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        requests.push(request);
        return requests.length === 1 ? first.promise : second.promise;
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await open(controller, 'search');
      controller.toggle('directory', true);
      expect(requests[0]?.selection).toEqual({ mode: 'Only', refs: ['directory'] });
      expect(controller.current().selectionCountsLoading).toBe(true);
      await controller.refresh();
      expect(requests).toHaveLength(1);
      controller.selectAll(false);
      controller.toggle('directory', true);
      expect(requests[1]?.revision).toBe(2);
      first.resolve({ revision: 1, publicFileCount: 99, lockedFileCount: 0, selectedItemCount: 1 });
      await Promise.resolve();
      expect(controller.current().selectionCounts).toBeNull();
      second.resolve({ revision: 2, publicFileCount: 2, lockedFileCount: 1, selectedItemCount: 1 });
      await Promise.resolve();
      expect(controller.current().selectionCounts).toEqual({ revision: 2, publicFileCount: 2, lockedFileCount: 1, selectedItemCount: 1 });
      controller.toggle('directory', true);
      expect(requests).toHaveLength(2);
      expect(controller.current().selectionCounts?.publicFileCount).toBe(2);
      expect(controller.current().selectionCountsLoading).toBe(false);
    } finally { controller.stop(); }
  });

  it.each(['folder', 'excluded-child', 'all-except', 'off-page'] as const)('pins a generic %s selection without validating or recounting it against arrivals', async selection => {
    const requests: Schema<'CountSearchViewSelectionRequestDto'>[] = [];
    const view = (id: string, revision = 1) => {
      const result = summary(id, revision);
      result.kind = 'GenericDirectories';
      result.counters.topLevelItemCount = 2;
      return result;
    };
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string, revision: number | string) {
        return { revision: view(id, Number(revision)),
          items: selection === 'off-page' ? [directorySummary('other-folder')]
            : [directorySummary('folder'), directorySummary('other-folder')], nextCursor: null };
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: view(id, 2), hasNewRevision: true };
      }
      override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        requests.push(request);
        return { revision: request.revision, publicFileCount: request.selection.mode === 'AllExcept' ? 5
          : Number(request.revision) === 1 ? 1 : request.excludedFileRefs?.length ? 10 : 11,
          lockedFileCount: 0, selectedItemCount: 1 };
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search' }, view: view('search'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      if (selection === 'all-except') controller.selectAll(true);
      controller.toggle('folder', selection !== 'all-except');
      if (selection === 'excluded-child') controller.toggle('folder-file', false, 'folder');
      await controller.refresh();
      expect(controller.current().selectionRevision).toBe(1);
      expect(controller.current().selectionCounts?.publicFileCount).toBe(selection === 'all-except' ? 5 : 1);
      expect(requests.filter(request => Number(request.revision) === 2)).toHaveLength(0);
    } finally { controller.stop(); }
  });

  it('keeps a generic directory choice on its reviewed revision when a live parent absorbs it', async () => {
    const first = summary('search', 1);
    first.kind = 'GenericDirectories';
    first.isComplete = false;
    first.counters.topLevelItemCount = 2;
    const merged = summary('search', 2);
    merged.kind = 'GenericDirectories';
    merged.isComplete = false;
    merged.counters.topLevelItemCount = 1;
    const counted: Array<number | string> = [];
    const resolved: Array<number | string> = [];
    const committed: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(_id: string, revision: number | string) {
        return { revision: Number(revision) === 1 ? first : merged,
          items: Number(revision) === 1
            ? [directorySummary('child-b', 'A\\B'), directorySummary('child-c', 'A\\C')]
            : [directorySummary('parent-a', 'A')], nextCursor: null };
      }
      override async getSearchViewUpdate() { return { summary: merged, hasNewRevision: true }; }
      override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        counted.push(request.revision);
        const missingChild = Number(request.revision) === 2
          && request.selection.refs.includes('child-b');
        return { revision: request.revision, publicFileCount: missingChild ? 0 : 1,
          lockedFileCount: 0, selectedItemCount: missingChild ? 0 : 1 };
      }
      override async resolveSearchViewSelectionSettings(_id: string, request: Schema<'ResolveSearchViewSelectionSettingsRequestDto'>) {
        resolved.push(request.revision);
        return {} as Schema<'ResolveSearchViewSelectionSettingsResponseDto'>;
      }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        committed.push(args);
        throw new Error('Response lost');
      }
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'search' }, view: first,
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      controller.toggle('child-b', true);
      expect(controller.current().selectionRevision).toBe(1);
      await controller.refresh();
      expect(controller.current().rows.map(row => row.ref)).toEqual(['parent-a']);
      expect(controller.current().selectionRevision).toBe(1);
      controller.toggle('parent-a', true);
      expect([...controller.current().selectionRefs]).toEqual(['child-b']);
      await controller.resolveSelectionSettings();
      expect(resolved).toEqual([1]);
      await controller.commit();
      expect(committed[0]?.[1].revision).toBe(1);
      expect(committed[0]?.[1].selection.refs).toEqual(['child-b']);
      expect(counted).toEqual([1]);
      controller.selectAll(false);
      controller.toggle('parent-a', true);
      expect(controller.current().selectionRevision).toBe(2);
      expect([...controller.current().selectionRefs]).toEqual(['parent-a']);
    } finally { controller.stop(); }
  });

  it('folds explicit children into their parent selection across page refreshes', async () => {
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id), hasNewRevision: false };
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'search');
      controller.toggle('child-a', true, 'folder');
      controller.toggle('child-b', true, 'folder');
      controller.toggle('other-child', true, 'other-folder');
      expect(controller.selectedCount()).toBe(3);
      await controller.refresh(true);
      controller.toggle('folder', true);
      expect(controller.selectedCount()).toBe(2);
      expect([...controller.current().selectionRefs]).toEqual(['other-child', 'folder']);
      expect(controller.isSelected('child-a', 'folder')).toBe(true);
      controller.toggle('folder', false);
      expect(controller.selectedCount()).toBe(1);
      expect(controller.isSelected('child-a', 'folder')).toBe(false);
      expect(controller.isSelected('other-child', 'other-folder')).toBe(true);
    } finally { controller.stop(); }
  });

  it.each([false, true])('retains child exceptions without changing directory counts (all=%s)', async (all) => {
    const requests: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        requests.push(args);
        throw new Error('Response lost');
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      const view = summary('search');
      view.counters.selectableOptionCount = 1;
      await controller.open({ job: { jobId: 'search' }, view, draft: { ...emptySearchDraft, resultMode: 'track' } });
      if (all) controller.selectAll(true);
      else controller.toggle('directory', true);
      expect(controller.isSelected('file', 'directory')).toBe(true);
      controller.toggle('file', false, 'directory');
      expect(controller.isSelected('file', 'directory')).toBe(false);
      expect(controller.isSelected('directory')).toBe(true);
      expect(controller.selectedCount()).toBe(1);
      await controller.commit();
      await controller.commit();
      expect(requests[0]?.[1].excludedFileRefs).toEqual(['file']);
      expect(requests[1]).toEqual(requests[0]);
      controller.toggle('file', true, 'directory');
      expect(controller.current().excludedFiles.size).toBe(0);
      controller.toggle('directory', false);
      expect(controller.isSelected('file', 'directory')).toBe(false);
    } finally { controller.stop(); }
  });

  it('publishes next-page loading state and coalesces duplicate clicks', async () => {
    const pending = deferred<SearchViewFilePageDto>();
    let nextCalls = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, _revision: number | string, cursor?: string | null) {
        if (cursor) { nextCalls++; return pending.promise; }
        return page(id, 1, 'next');
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'search');
      const loading = controller.loadMore();
      expect(controller.current().loadingMore).toBe(true);
      await controller.loadMore();
      expect(nextCalls).toBe(1);
      pending.resolve(page('search'));
      await loading;
      expect(controller.current().loadingMore).toBe(false);
      expect(controller.current().nextCursor).toBeNull();
    } finally { controller.stop(); }
  });

  it.each([false, true])('keeps a continuation click through an unfinished refresh (waiting for status: %s)', async (waitingForStatus) => {
    const update = deferred<SearchViewUpdateDto>();
    const status = deferred<Schema<'JobDetailDto'>>();
    const statusStarted = deferred<void>();
    let nextCalls = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, revision: number | string, cursor?: string | null) {
        if (cursor) { nextCalls++; return page(id, Number(revision)); }
        return page(id, Number(revision), 'next');
      }
      override getSearchViewUpdate() { return update.promise; }
      override getJob() { statusStarted.resolve(); return status.promise; }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'search');
      const refreshing = controller.refresh();
      if (waitingForStatus) {
        update.resolve({ summary: summary('search', 2), hasNewRevision: true });
        await statusStarted.promise;
        expect(controller.current().summary?.revision).toBe(2);
      }
      const first = controller.loadMore();
      const repeated = controller.loadMore();
      expect(nextCalls).toBe(0);
      if (!waitingForStatus) update.resolve({ summary: summary('search', 2), hasNewRevision: true });
      status.resolve({ summary: { jobId: 'search', lifecycleState: 'Terminal' } } as Schema<'JobDetailDto'>);
      await Promise.all([refreshing, first, repeated]);
      expect(nextCalls).toBe(1);
      expect(controller.current().nextCursor).toBeNull();
      expect(controller.current().loadingMore).toBe(false);
    } finally { controller.stop(); }
  });

  it.each([
    ['track', 'Files'], ['generic', 'GenericDirectories'], ['album', 'AlbumDirectories'],
    ['song-groups', 'TrackGroups'], ['album-groups', 'AlbumGroups'],
  ] as const)('can continue %s results after an in-flight page is superseded by a failed refinement', async (mode, kind) => {
    type EmptyPage = { revision: SearchViewSummaryDto; items: never[]; nextCursor: string | null };
    const oldPage = deferred<EmptyPage>();
    const newPage = deferred<EmptyPage>();
    let continuationReads = 0;
    let oldSignal: AbortSignal | undefined;
    const source = { ...summary('source'), kind };
    const read = (id: string, _revision: number | string, cursor?: string | null, _limit?: number, signal?: AbortSignal): Promise<EmptyPage> => {
      if (id === 'replacement') return Promise.reject(new Error('Replacement unavailable'));
      if (!cursor) return Promise.resolve({ revision: source, items: [], nextCursor: 'next' });
      continuationReads++;
      if (continuationReads === 1) { oldSignal = signal; return oldPage.promise; }
      return newPage.promise;
    };
    class Api extends DaemonApiClient {
      override async createSearchView() { return { ...source, viewId: 'replacement' }; }
      override getSearchViewFiles(...args: Parameters<DaemonApiClient['getSearchViewFiles']>) {
        return read(args[0], args[1], args[2], args[3], args[5]);
      }
      override getSearchViewDirectories(...args: Parameters<DaemonApiClient['getSearchViewDirectories']>) {
        return read(args[0], args[1], args[2], args[3], args[5]);
      }
      override getSearchViewTrackGroups = read;
      override getSearchViewAlbumGroups = read;
    }
    const controller = new SearchViewController(new Api(), 60_000);
    try {
      await controller.open({ job: { jobId: 'source' }, view: source,
        draft: { ...emptySearchDraft, resultMode: mode } });
      const older = controller.loadMore();
      expect(controller.current().loadingMore).toBe(true);
      await controller.refine({ kind, includeFullResults: true });
      expect(controller.current().refinementError?.message).toBe('Replacement unavailable');
      expect(controller.current().summary?.viewId).toBe('source');
      expect(controller.current().loadingMore).toBe(false);
      expect(oldSignal?.aborted).toBe(true);
      const newer = controller.loadMore();
      expect(continuationReads).toBe(2);
      oldPage.resolve({ revision: source, items: [], nextCursor: null });
      await older;
      expect(controller.current().loadingMore).toBe(true);
      newPage.resolve({ revision: source, items: [], nextCursor: null });
      await newer;
      expect(controller.current().loadingMore).toBe(false);
      expect(controller.current().nextCursor).toBeNull();
    } finally { controller.stop(); }
  });

  it('does not clear a next-page failure when only the summary poll succeeds', async () => {
    let failNext = true;
    let failSummary = true;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, _revision: number | string, cursor?: string | null) {
        if (cursor && failNext) throw new Error('Next page unavailable');
        return page(id, 1, cursor ? null : 'next');
      }
      override async getSearchViewUpdate(id: string) {
        if (failSummary) throw new Error('Summary unavailable');
        return { summary: summary(id), hasNewRevision: false };
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'search');
      await controller.loadMore();
      await controller.refresh();
      expect(controller.current().error?.message).toBe('Next page unavailable');
      failSummary = false;
      await controller.refresh();
      expect(controller.current().error?.message).toBe('Next page unavailable');
      expect(controller.current().nextCursor).toBe('next');
      failNext = false;
      await controller.loadMore();
      expect(controller.current().error).toBeNull();
      expect(controller.current().nextCursor).toBeNull();
    } finally { controller.stop(); }
  });

  it('retries a failed initial page even when the summary revision is unchanged', async () => {
    let calls = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) {
        if (++calls === 1) throw new Error('Page unavailable');
        return page(id);
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id), hasNewRevision: false };
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'search');
      expect(controller.current().error?.message).toBe('Page unavailable');
      await controller.refresh();
      expect(calls).toBe(2);
      expect(controller.current().error).toBeNull();
      controller.toggle('file-one', true);
      await controller.refresh(true);
      expect(calls).toBe(3);
      expect(controller.isSelected('file-one')).toBe(true);
    } finally { controller.stop(); }
  });

  it('retries the identical commit after a live revision changes', async () => {
    const requests: Parameters<DaemonApiClient['commitSearchView']>[] = [];
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, revision: number | string) { return page(id, Number(revision)); }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id, 2), hasNewRevision: true };
      }
      override async commitSearchView(...args: Parameters<DaemonApiClient['commitSearchView']>): Promise<Schema<'CommitSearchViewSelectionResponseDto'>> {
        requests.push(args);
        throw new Error('Response lost');
      }
    }
    const controller = new SearchViewController(new Api());
    await open(controller, 'search');
    controller.toggle('file-one', true);
    await controller.commit();
    await controller.refresh();
    await controller.commit();
    expect(requests).toHaveLength(2);
    expect(requests[1]).toEqual(requests[0]);
    expect(requests[1]?.[1].revision).toBe(1);
    controller.selectAll(false);
    controller.toggle('file-one', true);
    controller.toggle('file-two', true);
    await controller.commit();
    expect(requests[2]?.[1].revision).toBe(2);
    expect(requests[2]?.[1].selection.refs).toEqual(['file-one', 'file-two']);
    expect(requests[2]?.[1].idempotencyKey).not.toBe(requests[0]?.[1].idempotencyKey);
    controller.stop();
  });

  it('isolates late directory pages and coalesces repeated requests', async () => {
    const pending = deferred<Schema<'SearchViewDirectoryFilePageDto'>>();
    let calls = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, revision: number | string) { return page(id, Number(revision)); }
      override getSearchViewDirectoryFiles() { calls += 1; return pending.promise; }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id, 2), hasNewRevision: true };
      }
    }
    const controller = new SearchViewController(new Api());
    await open(controller, 'search');
    const children = controller.loadDirectoryFiles('folder');
    await controller.loadDirectoryFiles('folder');
    expect(calls).toBe(1);
    await controller.refresh();
    pending.resolve({ revision: summary('search'), directory: { ref: 'folder', username: 'peer', folderPath: 'Music' },
      items: [], nextCursor: null, directorySummary: directorySummary('folder') });
    await children;
    expect(controller.current().directoryFiles.size).toBe(0);
    controller.stop();
  });

  it('does not fetch an exhausted directory again', async () => {
    let calls = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override async getSearchViewDirectoryFiles() {
        calls += 1;
        return { revision: summary('search'), directory: { ref: 'folder', username: 'peer', folderPath: 'Music' },
          items: [], nextCursor: null, directorySummary: directorySummary('folder') };
      }
    }
    const controller = new SearchViewController(new Api());
    await open(controller, 'search');
    await controller.loadDirectoryFiles('folder');
    await controller.loadDirectoryFiles('folder');
    expect(calls).toBe(1);
    expect(controller.current().directoryFiles.get('folder')?.loading).toBe(false);
    controller.stop();
  });
  it('ignores an old summary refresh after another search is opened', async () => {
    const pending = deferred<SearchViewUpdateDto>();
    let updateSignal: AbortSignal | undefined;
    let updateReads = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string) { return page(id); }
      override getSearchViewUpdate(_id: string, _revision: number | string, signal?: AbortSignal) {
        updateSignal = signal;
        updateReads += 1;
        return pending.promise;
      }
    }
    const controller = new SearchViewController(new Api());
    await open(controller, 'first');
    const refresh = controller.refresh();
    const repeated = controller.refresh();
    expect(updateReads).toBe(1);
    expect(updateSignal?.aborted).toBe(false);
    await open(controller, 'second');
    expect(updateSignal?.aborted).toBe(true);
    pending.resolve({ summary: summary('first', 2), hasNewRevision: true });
    await Promise.all([refresh, repeated]);
    expect(controller.current().summary?.viewId).toBe('second');
    controller.stop();
  });

  it('does not append an old page or clear a newer continuation after a revision replaces visible pages', async () => {
    const pending = deferred<SearchViewFilePageDto>();
    const replacement = deferred<SearchViewFilePageDto>();
    let oldSignal: AbortSignal | undefined;
    let continuationReads = 0;
    class Api extends DaemonApiClient {
      override getSearchViewFiles(id: string, revision: number | string, cursor?: string | null, _limit?: number, _sort?: Schema<'ServerSearchViewSortOrder'>, signal?: AbortSignal) {
        if (!cursor) return Promise.resolve(page(id, Number(revision), 'next'));
        continuationReads++;
        if (Number(revision) === 1) { oldSignal = signal; return pending.promise; }
        return replacement.promise;
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: summary(id, 2), hasNewRevision: true };
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'search');
      const more = controller.loadMore();
      await controller.refresh();
      expect(oldSignal?.aborted).toBe(true);
      expect(controller.current().loadingMore).toBe(false);
      const next = controller.loadMore();
      expect(continuationReads).toBe(2);
      pending.resolve(page('search', 1));
      await more;
      expect(controller.current().summary?.revision).toBe(2);
      expect(controller.current().nextCursor).toBe('next');
      expect(controller.current().loadingMore).toBe(true);
      replacement.resolve(page('search', 2));
      await next;
      expect(controller.current().nextCursor).toBeNull();
      expect(controller.current().loadingMore).toBe(false);
    } finally { controller.stop(); }
  });
});

describe('Search View read ordering', () => {
  it.each(['track', 'generic', 'album'] as const)('reorders %s without replacing the View, selection or counts', async mode => {
    const kinds = { track: 'Files', generic: 'GenericDirectories', album: 'AlbumDirectories' } as const;
    const source = { ...summary('source'), kind: kinds[mode] };
    const left = directorySummary('left'), right = directorySummary('right');
    const orders: (string | undefined)[] = [];
    let creates = 0, counts = 0;
    class Api extends DaemonApiClient {
      override async createSearchView(): Promise<SearchViewSummaryDto> { creates++; throw new Error('A sort must be a read'); }
      override async getSearchViewFiles(id: string, revision: number | string, _cursor?: string | null,
        _limit?: number, order?: Schema<'ServerSearchViewSortOrder'>) {
        orders.push(order);
        const items = [left.bestChild, right.bestChild];
        return { revision: summary(id, Number(revision)), items: order === 'Relevance' ? items : items.reverse(), nextCursor: null };
      }
      override async getSearchViewDirectories(id: string, revision: number | string, _cursor?: string | null,
        _limit?: number, order?: Schema<'ServerSearchViewSortOrder'>) {
        orders.push(order);
        return { revision: summary(id, Number(revision)), items: order === 'Relevance' ? [left, right] : [right, left], nextCursor: null };
      }
      override async countSearchViewSelection(_id: string, request: Schema<'CountSearchViewSelectionRequestDto'>) {
        counts++;
        return { revision: request.revision, publicFileCount: 1, lockedFileCount: 0, selectedItemCount: 1 };
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await controller.open({ job: { jobId: 'source' }, view: source, draft: { ...emptySearchDraft, resultMode: mode } });
      const selected = controller.current().rows[0]!.ref;
      controller.toggle(selected, true);
      await Promise.resolve();
      const before = controller.current();
      const countReads = counts;
      const order = mode === 'generic' ? 'DirectoryNameAscending' : 'UploadSpeed';
      expect(await controller.sort(order)).toBe(true);
      const after = controller.current();
      expect(creates).toBe(0);
      expect(after.summary).toEqual(before.summary);
      expect(after.sortOrder).toBe(order);
      expect(after.selectionRefs).toEqual(before.selectionRefs);
      expect(after.selectionRevision).toBe(before.selectionRevision);
      expect(after.selectionCounts).toEqual(before.selectionCounts);
      expect(counts).toBe(countReads);
      expect(after.rows.map(row => row.ref)).toEqual(before.rows.map(row => row.ref).reverse());
      expect(orders).toEqual(['Relevance', order]);
    } finally { controller.stop(); }
  });

  it('preserves a failed child page during sorting and retries it only when requested', async () => {
    const folder = directorySummary('folder');
    folder.initialFiles = { items: [{ ref: 'first', relativePath: 'First.txt', file: folder.bestChild }], nextCursor: 'next-child' };
    let childReads = 0;
    class Api extends DaemonApiClient {
      override async getSearchViewDirectories(id: string) {
        return { revision: summary(id), items: [folder], nextCursor: null };
      }
      override async getSearchViewDirectoryFiles(id: string) {
        if (++childReads === 1) throw new Error('Child page temporarily unavailable');
        return { revision: summary(id), directory: folder.ref, directorySummary: folder,
          items: [{ ref: 'second', relativePath: 'Second.txt', file: folder.bestChild }], nextCursor: null };
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await controller.open({ job: { jobId: 'source' }, view: summary('source'),
        draft: { ...emptySearchDraft, resultMode: 'generic' } });
      await controller.loadDirectoryFiles(folder.ref.ref);
      const failed = controller.current().directoryFiles.get(folder.ref.ref)!;
      expect(failed.error).not.toBeNull();
      expect(await controller.sort('DirectoryNameAscending')).toBe(true);
      expect(childReads).toBe(1);
      expect(controller.current().directoryFiles.get(folder.ref.ref)).toEqual(failed);
      await controller.loadDirectoryFiles(folder.ref.ref);
      expect(childReads).toBe(2);
      expect(controller.current().directoryFiles.get(folder.ref.ref)?.error).toBeNull();
      expect(controller.current().directoryFiles.get(folder.ref.ref)?.items).toHaveLength(2);
    } finally { controller.stop(); }
  });

  it('ignores an obsolete ordering response and keeps the order through live refresh', async () => {
    const older = deferred<SearchViewFilePageDto>();
    let oldSignal: AbortSignal | undefined;
    const reads: (string | undefined)[] = [];
    class Api extends DaemonApiClient {
      override getSearchViewFiles(id: string, revision: number | string, _cursor?: string | null,
        _limit?: number, order?: Schema<'ServerSearchViewSortOrder'>, signal?: AbortSignal) {
        reads.push(order);
        if (order === 'UploadSpeed') { oldSignal = signal; return older.promise; }
        return Promise.resolve(page(id, Number(revision)));
      }
      override async getSearchViewUpdate(id: string) {
        return { summary: { ...summary(id, 2), isComplete: false }, hasNewRevision: true };
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'source');
      const oldSort = controller.sort('UploadSpeed');
      expect(await controller.sort('QueueDepth')).toBe(true);
      expect(oldSignal?.aborted).toBe(true);
      older.resolve(page('source'));
      expect(await oldSort).toBe(false);
      await controller.refresh();
      expect(controller.current().sortOrder).toBe('QueueDepth');
      expect(controller.current().summary?.revision).toBe(2);
      expect(reads).toEqual(['Relevance', 'UploadSpeed', 'QueueDepth', 'QueueDepth']);
    } finally { controller.stop(); }
  });

  it('keeps the displayed order on failure and retries without another View', async () => {
    let failing = true;
    class Api extends DaemonApiClient {
      override async getSearchViewFiles(id: string, revision: number | string, _cursor?: string | null,
        _limit?: number, order?: Schema<'ServerSearchViewSortOrder'>) {
        if (order === 'UploadSpeed' && failing) throw new Error('Page temporarily unavailable');
        return page(id, Number(revision));
      }
    }
    const controller = new SearchViewController(new Api());
    try {
      await open(controller, 'source');
      const before = controller.current();
      expect(await controller.sort('UploadSpeed')).toBe(false);
      expect(controller.current().rows).toEqual(before.rows);
      expect(controller.current().sortOrder).toBe('Relevance');
      expect(controller.current().refining).toBe(false);
      expect(controller.current().refinementError?.message).toContain('temporarily unavailable');
      failing = false;
      expect(await controller.sort('UploadSpeed')).toBe(true);
      expect(controller.current().sortOrder).toBe('UploadSpeed');
      expect(controller.current().refinementError).toBeNull();
    } finally { controller.stop(); }
  });
});
