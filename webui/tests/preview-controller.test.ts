import { afterEach, expect, it, vi } from 'vitest';
import { DaemonApiClient } from '../src/api/client';
import type { Schema } from '../src/api/types';
import { PreviewController, type PreviewState } from '../src/domains/jobs/preview-controller';

function summary(id: string): Schema<'JobPreviewSummaryDto'> {
  return { previewId: id, revision: 1, state: 'Ready', createdAtUtc: '', expiresAtUtc: '', nodeCount: 0, readyNodeCount: 0, failedNodeCount: 0, selectableNodeCount: 0 };
}
const request: Schema<'CreateJobPreviewRequestDto'> = { job: { kind: 'extract', input: 'source' } };

afterEach(() => vi.useRealTimers());

it('shows partial readiness as soon as the planning request completes and retains failed entries', async () => {
  vi.useFakeTimers();
  let polls = 0;
  let pages = 0;
  const failed: Schema<'JobPreviewNodeDto'> = {
    ref: 'failed', parentRef: null, role: 'user-root', isReady: false, isSelectable: false,
    kind: 'extract', itemName: 'Source', queryText: null, directChildCount: 0,
    selectableSubtreeCount: 0, appliedProfiles: [], failureCode: 'extraction-failed', failureMessage: 'Source unavailable',
  };
  class Api extends DaemonApiClient {
    override async createJobPreview() { return { preview: { ...summary('preview'), state: 'Planning' as const } }; }
    override async getJobPreview(_id: string, _signal?: AbortSignal, waitForCompletion = false) {
      expect(waitForCompletion).toBe(true);
      polls += 1;
      return { ...summary('preview'), revision: 2, state: 'PartiallyReady' as const, failedNodeCount: 1 };
    }
    override async getJobPreviewNodes() { pages += 1; return { items: [failed], nextCursor: null }; }
  }
  const controller = new PreviewController(new Api());
  let latest: PreviewState | undefined;
  controller.subscribe((state) => { latest = state; });
  await controller.create(request);
  await vi.advanceTimersByTimeAsync(0);
  expect(latest?.summary?.state).toBe('PartiallyReady');
  expect(latest?.branches.get(null)?.items).toEqual([failed]);
  await vi.advanceTimersByTimeAsync(5_000);
  expect(polls).toBe(1);
  expect(pages).toBe(1);
  controller.stop();
});

it('retries a lost commit response identically and does not repeat a confirmed submission', async () => {
  const requests: Schema<'CommitJobPreviewRequestDto'>[] = [];
  class Api extends DaemonApiClient {
    override async createJobPreview() { return { preview: summary('preview') }; }
    override async getJobPreviewNodes() { return { items: [], nextCursor: null }; }
    override async commitJobPreview(_id: string, body: Schema<'CommitJobPreviewRequestDto'>): Promise<Schema<'CommitJobPreviewResponseDto'>> {
      requests.push(body);
      if (requests.length === 1) throw new Error('Response lost');
      return { previewId: 'preview', submissionId: 'submission', workflowId: 'workflow', requestedCount: 2, resolvedCount: 2, submittedCount: 2, skippedCount: 0, rejectedCount: 0, rejectionReasons: [] };
    }
  }
  const controller = new PreviewController(new Api());
  await controller.create(request);
  await controller.commit({ mode: 'Only', refs: ['b', 'a'] });
  await controller.commit({ mode: 'Only', refs: ['a', 'b', 'a'] });
  expect(requests).toHaveLength(2);
  expect(requests[1]).toEqual(requests[0]);
  await controller.commit({ mode: 'AllExcept', refs: [] });
  expect(requests).toHaveLength(2);
  controller.stop();
});

it('includes subtree choices in idempotency comparison', async () => {
  const requests: Schema<'CommitJobPreviewRequestDto'>[] = [];
  class Api extends DaemonApiClient {
    override async createJobPreview() { return { preview: summary('preview') }; }
    override async getJobPreviewNodes() { return { items: [], nextCursor: null }; }
    override async commitJobPreview(_id: string, body: Schema<'CommitJobPreviewRequestDto'>): Promise<Schema<'CommitJobPreviewResponseDto'>> {
      requests.push(body);
      throw new Error('Response lost');
    }
  }
  const controller = new PreviewController(new Api());
  await controller.create(request);
  await controller.commit({ mode: 'AllExcept', refs: [] }, [{ ref: '0', selected: false }]);
  await controller.commit({ mode: 'AllExcept', refs: [] }, [{ ref: '0', selected: false }]);
  expect(requests[1]).toEqual(requests[0]);
  await controller.commit({ mode: 'AllExcept', refs: [] }, [{ ref: '0', selected: true }]);
  expect(requests[2]?.idempotencyKey).not.toBe(requests[1]?.idempotencyKey);
  controller.stop();
});

it('does not let an older preview replace a newer review', async () => {
  let resolve!: (value: Schema<'CreateJobPreviewResponseDto'>) => void;
  const pending = new Promise<Schema<'CreateJobPreviewResponseDto'>>((done) => { resolve = done; });
  let calls = 0;
  class Api extends DaemonApiClient {
    override createJobPreview() { return ++calls === 1 ? pending : Promise.resolve({ preview: summary('new') }); }
    override async getJobPreviewNodes() { return { items: [], nextCursor: null }; }
  }
  const controller = new PreviewController(new Api());
  let latest: PreviewState | undefined;
  controller.subscribe((state) => { latest = state; });
  const first = controller.create(request);
  await controller.create(request);
  resolve({ preview: summary('old') });
  await first;
  expect(latest?.summary?.previewId).toBe('new');
  controller.stop();
});

it('loads only the requested branch and does not refetch exhausted pages', async () => {
  const parents: (string | null | undefined)[] = [];
  class Api extends DaemonApiClient {
    override async createJobPreview() { return { preview: summary('preview') }; }
    override async getJobPreviewNodes(_id: string, parent?: string | null) {
      parents.push(parent);
      return { items: [], nextCursor: null };
    }
  }
  const controller = new PreviewController(new Api());
  await controller.create(request);
  await controller.loadBranch(null);
  expect(parents).toEqual([null]);
  await controller.loadBranch('expanded-parent');
  expect(parents).toEqual([null, 'expanded-parent']);
  controller.stop();
});

it('opens a small review and only the first structural page of a large review', async () => {
  const reads: Array<string | null | undefined> = [];
  const node = (ref: string, parentRef: string | null, directChildCount: number): Schema<'JobPreviewNodeDto'> => ({
    ref, parentRef, directChildCount, role: 'user-root', isReady: true, isSelectable: true,
    kind: 'song', itemName: ref, queryText: null, selectableSubtreeCount: 1, appliedProfiles: [],
  });
  class Api extends DaemonApiClient {
    override async createJobPreview(body: Schema<'CreateJobPreviewRequestDto'>) {
      return { preview: { ...summary(body.job.kind ?? 'unknown'), nodeCount: body.job.kind === 'song' ? 4 : 25 } };
    }
    override async getJobPreviewNodes(id: string, parent?: string | null) {
      reads.push(parent);
      return { items: parent == null ? [node('root', null, 1)]
        : parent === 'root' ? [node('list', 'root', 1)]
          : parent === 'list' && id === 'extract'
            ? [node('a', 'list', 1), node('b', 'list', 1)]
            : [node('song', parent ?? 'list', 0)], nextCursor: null };
    }
  }
  const controller = new PreviewController(new Api());
  let latest: PreviewState | undefined;
  controller.subscribe((state) => { latest = state; });
  await controller.create({ job: { kind: 'song', songQuery: { artist: null, title: 'Song', album: null, uri: null, length: null, artistMaybeWrong: false } } });
  expect(reads).toEqual([null, 'root', 'list']);
  expect(latest?.branches.get('list')?.items[0]?.ref).toBe('song');
  await controller.create(request);
  expect(reads).toEqual([null, 'root', 'list', null, 'root', 'list']);
  expect(latest?.branches.has('a')).toBe(false);
  await controller.loadBranch('a');
  expect(reads.at(-1)).toBe('a');
  controller.stop();
});

it('aborts a pending readiness request when review closes without polling again', async () => {
  vi.useFakeTimers();
  let signal: AbortSignal | undefined;
  let reads = 0;
  class Api extends DaemonApiClient {
    override async createJobPreview() { return { preview: { ...summary('preview'), state: 'Planning' as const } }; }
    override getJobPreview(_id: string, requestSignal?: AbortSignal): Promise<Schema<'JobPreviewSummaryDto'>> {
      reads++;
      signal = requestSignal;
      return new Promise((_resolve, reject) => requestSignal!.addEventListener('abort', () => reject(new Error('closed')), { once: true }));
    }
  }
  const controller = new PreviewController(new Api());
  let latest: PreviewState | undefined;
  controller.subscribe(state => { latest = state; });
  await controller.create(request);
  expect(reads).toBe(1);
  controller.stop();
  expect(signal?.aborted).toBe(true);
  await vi.advanceTimersByTimeAsync(30_000);
  expect(reads).toBe(1);
  expect(latest?.error).toBeNull();
});
