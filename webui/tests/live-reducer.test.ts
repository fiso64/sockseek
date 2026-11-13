import { describe, expect, it } from 'vitest';
import { ChatStateReducer, DaemonStateReducer, UserBrowseStateReducer } from '../src/api/live';
import type { ChatMessageDto, JobStateDto, StateSnapshotDto, StateUpdateBatchDto, UserBrowseDto } from '../src/api/types';

const workflowId = '10000000-0000-0000-0000-000000000001';
const jobId = '20000000-0000-0000-0000-000000000001';
const epoch = '30000000-0000-0000-0000-000000000001';

function job(revision: number, phase: JobStateDto['lifecycle']['activityPhase'] = 'Searching'): JobStateDto {
  return {
    jobId,
    revision,
    display: {
      displayId: 1,
      workflowId,
      kind: 'search',
      itemName: null,
      queryText: 'boards of canada',
      appliedProfiles: [],
      printOption: 0,
      submissionId: null,
      role: 'user-root',
      createdAtUtc: null,
    },
    lifecycle: {
      lifecycleState: 'Running',
      activityPhase: phase,
      activityUntilUtc: null,
      terminalOutcome: 'None',
      skipReason: 'None',
      failureReason: null,
      failureMessage: null,
      failureDetail: null,
      cancellationSource: 'None',
      availableActions: [],
    },
    discovery: {
      rawResultCount: 2,
      lockedFileCount: 0,
      publicFileCount: 2,
      observedPeerCount: 1,
    },
    relationships: { parentJobId: null, resultJobId: null, sourceJobId: null },
  };
}

function snapshot(sequence = 10): StateSnapshotDto {
  return {
    scope: { kind: 'daemon' },
    position: { epoch, sequence },
    capturedAtUtc: '2026-09-05T12:00:00Z',
    daemon: null,
    workflows: [],
    jobs: [job(1)],
    searches: [],
    transfers: [],
    chatTarget: null,
    userBrowse: null,
  };
}

function batch(overrides: Partial<StateUpdateBatchDto> = {}): StateUpdateBatchDto {
  return {
    scope: { kind: 'daemon' },
    epoch,
    previousSequence: 10,
    sequence: 11,
    occurredAtUtc: '2026-09-05T12:00:01Z',
    state: {
      workflows: [],
      jobs: [],
      searches: [],
      transfers: [],
      removedWorkflowIds: [],
      removedJobIds: [],
      removedSearchJobIds: [],
      removedTransferIds: [],
    },
    activity: [],
    ...overrides,
  };
}

it('delivers every accepted chat observation while keeping the replicated tail bounded', () => {
  const reducer = new ChatStateReducer();
  const scope = { kind: 'chatConversation' as const, chatTargetId: jobId };
  reducer.applySnapshot({ ...snapshot(), scope, chatTarget: {
    kind: 'Direct', targetId: jobId, conversation: null, room: null,
    messages: [], hasEarlierMessages: false,
  } });
  const messages = Array.from({ length: 103 }, (_, index) => ({
    messageId: `message-${index}`, sequence: index + 1, targetKind: 'Direct' as const,
    targetId: jobId, sender: 'Peer', direction: 'Incoming' as const,
    text: `Message ${index}`, state: 'Received' as const,
    occurredAtUtc: '2026-09-21T00:00:00Z', recordedAtUtc: '2026-09-21T00:00:00Z',
    failureReason: null,
  }));
  const update = batch({ scope, state: { ...batch().state,
    chatTargets: [{ kind: 'Direct', targetId: jobId, messages, replaceMessages: false }],
  } });
  const observed: string[] = [];
  const observe = (items: ChatMessageDto[]) => { observed.push(...items.map(item => item.messageId)); };
  expect(reducer.applyBatch(update, observe)).toBe('applied');
  expect(observed).toEqual(messages.map(message => message.messageId));
  expect(reducer.snapshot()?.messages).toEqual(messages.slice(3));
  expect(reducer.applyBatch(update, observe)).toBe('stale');
  expect(reducer.applyBatch({ ...update, previousSequence: 20, sequence: 21 }, observe)).toBe('recovery-required');
  expect(observed).toHaveLength(103);
});

describe('UserBrowseStateReducer', () => {
  const scope = { kind: 'userBrowse' as const, userBrowseId: jobId };
  const resource: UserBrowseDto = {
    browseId: jobId, username: 'CaseExactPeer', state: 'running', phase: 'receiving',
    compressedBytesReceived: 10, compressedBytesExpected: null, directoryCount: 0,
    fileCount: 0, totalFileBytes: 0, revision: 1,
    createdAt: '2026-10-22T00:00:00Z', updatedAt: '2026-10-22T00:00:00Z',
    expiresAt: null, failure: null,
  };
  const update = (sequence: number, previousSequence: number, value: UserBrowseDto) => batch({
    scope, sequence, previousSequence, state: { ...batch().state, userBrowse: value },
  });

  it('keeps live progress and completion in one authoritative resource', () => {
    const reducer = new UserBrowseStateReducer();
    reducer.applySnapshot({ ...snapshot(), scope, userBrowse: resource });
    const indexing = { ...resource, revision: 2, phase: 'indexing' as const, fileCount: 9, totalFileBytes: 900 };
    expect(reducer.applyBatch(update(11, 10, indexing))).toBe('applied');
    expect(reducer.snapshot()).toEqual(indexing);
    const complete = { ...indexing, revision: 3, state: 'complete' as const, phase: 'ready' as const,
      fileCount: 10, totalFileBytes: 1_000 };
    expect(reducer.applyBatch(update(12, 11, complete))).toBe('applied');
    expect(reducer.snapshot()).toEqual(complete);
  });

  it('ignores old revisions and duplicate batches, but requires recovery across gaps and epochs', () => {
    const reducer = new UserBrowseStateReducer();
    expect(reducer.applyBatch(update(11, 10, resource))).toBe('recovery-required');
    reducer.applySnapshot({ ...snapshot(), scope, userBrowse: { ...resource, revision: 3, fileCount: 20 } });
    expect(reducer.applyBatch(update(11, 9, resource))).toBe('applied');
    expect(reducer.snapshot()?.fileCount).toBe(20);
    expect(reducer.applyBatch(update(11, 10, resource))).toBe('stale');
    expect(reducer.applyBatch(update(13, 12, { ...resource, revision: 4 }))).toBe('recovery-required');
    expect(reducer.applyBatch({ ...update(12, 11, resource), epoch: workflowId })).toBe('recovery-required');
    expect(reducer.snapshot()?.fileCount).toBe(20);
  });

  it('does not let a different resource or scope replace the displayed browse', () => {
    const reducer = new UserBrowseStateReducer();
    expect(() => reducer.applySnapshot(snapshot())).toThrow('browse-scoped');
    expect(() => reducer.applySnapshot({ ...snapshot(), scope, userBrowse: { ...resource, browseId: workflowId } }))
      .toThrow('matching resource');
    reducer.applySnapshot({ ...snapshot(), scope, userBrowse: resource });
    expect(() => reducer.applyBatch(update(11, 10, { ...resource, browseId: workflowId }))).toThrow('different resource');
    expect(() => reducer.applyBatch({ ...update(11, 10, resource), scope: { ...scope, userBrowseId: workflowId } }))
      .toThrow('different resource');
    expect(reducer.snapshot()).toEqual(resource);
  });
});

describe('DaemonStateReducer', () => {
  function transfer(id: string, direction = 'Download', owner = jobId, workflow = workflowId): NonNullable<StateUpdateBatchDto['state']['transfers'][number]['added']> {
    return { transferId: id, revision: 1, createdAtUtc: new Date(0).toISOString(),
      identity: { direction, source: 'Download', jobId: owner, workflowId: workflow, groupKind: 'Directory' },
      status: { state: 'Queued', isTerminal: false, attemptCount: 1, terminalOutcome: 'None', failureReason: 'None', cancellationSource: 'None' },
      progress: { bytesTransferred: 0, totalBytes: 100, bytesPerSecond: null },
    };
  }

  it('keeps server totals independent of cached rows through progress, stale updates, retirement and reconnect', () => {
    const reducer = new DaemonStateReducer();
    const download = transfer('download');
    const upload = transfer('upload', 'Upload');
    reducer.applySnapshot({ ...snapshot(), transfers: [download, upload],
      transferSummary: { active: '12', downloads: '10', uploads: '2',
        queued: 2, failed: 0, downloadRate: 0, uploadRate: 0 } });
    const state = reducer.state;
    expect(reducer.takeChanges().initialSnapshot).toBe(true);
    expect(state.transferSummary).toEqual({ active: 12, downloads: 10, uploads: 2,
      queued: 2, failed: 0, downloadRate: 0, uploadRate: 0 });
    const originalJob = state.jobs.get(jobId);
    const progress = { ...download.progress, bytesTransferred: 25, bytesPerSecond: 12 };
    reducer.applyBatch(batch({ state: { ...batch().state,
      transferSummary: { active: 12, downloads: 10, uploads: 2,
        queued: 1, failed: 0, downloadRate: 12, uploadRate: 0 }, transfers: [{
      transferId: download.transferId, revision: 2, progress,
      status: { ...download.status, state: 'InProgress' },
    }] } }));
    expect(state.transfers.get('download')?.progress).toEqual(progress);
    expect(state.jobs.get(jobId)).toBe(originalJob);
    expect(state.transfers.get('upload')).toBe(upload);
    expect(state.transferSummary).toEqual({ active: 12, downloads: 10, uploads: 2,
      queued: 1, failed: 0, downloadRate: 12, uploadRate: 0 });
    const changes = reducer.takeChanges();
    expect(changes.jobs.size).toBe(0);
    expect([...changes.transfers.keys()]).toEqual(['download']);
    expect(changes.transfers.get('download')?.previous).toBe(download);
    reducer.applyBatch(batch({ sequence: 12, previousSequence: 11, state: { ...batch().state, transfers: [{
      transferId: 'download', revision: 1, added: download,
    }] } }));
    expect(reducer.takeChanges().transfers.size).toBe(0);
    expect(state.transferSummary.downloadRate).toBe(12);
    expect(reducer.applyBatch(batch({ sequence: 11, previousSequence: 10, state: { ...batch().state,
      transferSummary: { active: 0, downloads: 0, uploads: 0, queued: 0, failed: 0, downloadRate: 0, uploadRate: 0 },
    } }))).toBe('stale');
    expect(state.transferSummary.active).toBe(12);
    reducer.applyBatch(batch({ sequence: 13, previousSequence: 12, state: { ...batch().state,
      transferSummary: { active: 11, downloads: 9, uploads: 2,
        queued: 1, failed: 1, downloadRate: 0, uploadRate: 0 }, transfers: [{
      transferId: 'download', revision: 3,
      status: { ...download.status, state: 'Failed', isTerminal: true, terminalOutcome: 'Failed' },
    }] } }));
    expect(state.transferSummary).toEqual({ active: 11, downloads: 9, uploads: 2,
      queued: 1, failed: 1, downloadRate: 0, uploadRate: 0 });
    reducer.applyBatch(batch({ sequence: 14, previousSequence: 13,
      state: { ...batch().state, removedTransferIds: ['download'],
        transferSummary: { active: 11, downloads: 9, uploads: 2,
          queued: 1, failed: 0, downloadRate: 0, uploadRate: 0 } } }));
    expect(state.transferSummary.failed).toBe(0);
    expect(state.transfers.has('download')).toBe(false);
    reducer.takeChanges();
    reducer.applySnapshot({ ...snapshot(), position: { epoch: 'restart', sequence: 1 }, transfers: [download],
      transferSummary: { active: 1, downloads: 1, uploads: 0,
        queued: 1, failed: 0, downloadRate: 0, uploadRate: 0 } });
    expect(reducer.state).toBe(state);
    expect(state.transfers.has('upload')).toBe(false);
    expect(state.transferSummary).toEqual({ active: 1, downloads: 1, uploads: 0,
      queued: 1, failed: 0, downloadRate: 0, uploadRate: 0 });
    expect(reducer.takeChanges()).toMatchObject({ snapshot: true, initialSnapshot: false });
  });

  it('tracks child membership and removes only the retiring job or workflow dependents', () => {
    const reducer = new DaemonStateReducer();
    const child: JobStateDto = { ...job(1), jobId: 'child', display: { ...job(1).display, kind: 'retrieve-folder' },
      relationships: { ...job(1).relationships, parentJobId: jobId } };
    const unrelated = { ...job(1), jobId: 'unrelated', display: { ...job(1).display, workflowId: 'other' } };
    reducer.applySnapshot({ ...snapshot(), jobs: [job(1), child, unrelated],
      transferSummary: { active: 2, downloads: 1, uploads: 1,
        queued: 2, failed: 0, downloadRate: 0, uploadRate: 0 },
      transfers: [transfer('child-transfer', 'Download', 'child'), transfer('other-transfer', 'Upload', 'unrelated', 'other')] });
    reducer.takeChanges();
    expect(reducer.state.childCounts.get(jobId)).toEqual({ total: 1, retrievals: 1 });
    expect([...reducer.state.rootJobs.keys()]).toEqual([jobId, 'unrelated']);
    reducer.applyBatch(batch({ state: { ...batch().state, jobs: [{ jobId: 'child', revision: 2,
      relationships: { ...child.relationships, parentJobId: 'unrelated' },
    }] } }));
    expect(reducer.state.childCounts.has(jobId)).toBe(false);
    expect(reducer.state.childCounts.get('unrelated')).toEqual({ total: 1, retrievals: 1 });
    reducer.applyBatch(batch({ sequence: 12, previousSequence: 11,
      state: { ...batch().state, removedWorkflowIds: [workflowId],
        transferSummary: { active: 1, downloads: 0, uploads: 1,
          queued: 1, failed: 0, downloadRate: 0, uploadRate: 0 } } }));
    expect([...reducer.state.jobs.keys()]).toEqual(['unrelated']);
    expect(reducer.state.childCounts.size).toBe(0);
    expect([...reducer.state.transfers.keys()]).toEqual(['other-transfer']);
    expect(reducer.state.transferSummary.uploads).toBe(1);
    const changes = reducer.takeChanges();
    expect(changes.jobs.get('child')).toEqual({ previous: child, current: undefined });
    expect(changes.transfers.get('child-transfer')?.current).toBeUndefined();
    expect(changes.transfers.has('other-transfer')).toBe(false);
    reducer.applyBatch(batch({ sequence: 13, previousSequence: 12,
      state: { ...batch().state, removedJobIds: ['unrelated'],
        transferSummary: { active: 0, downloads: 0, uploads: 0,
          queued: 0, failed: 0, downloadRate: 0, uploadRate: 0 } } }));
    expect(reducer.state.transfers.size).toBe(0);
    expect(reducer.state.transferSummary.active).toBe(0);
  });

  it('hydrates a snapshot and applies overlapping component deltas once', () => {
    const reducer = new DaemonStateReducer();
    reducer.applySnapshot({ ...snapshot(), jobHistoryPosition: { epoch, sequence: 7 } });
    expect(reducer.takeChanges().jobHistoryPosition).toEqual({ epoch, sequence: 7 });

    const nextLifecycle = { ...job(2, 'ProcessingSearchResults').lifecycle };
    expect(reducer.applyBatch(batch({
      previousSequence: 9,
      state: {
        workflows: [],
        jobs: [{ jobId, revision: 2, lifecycle: nextLifecycle }],
        searches: [],
        transfers: [],
        removedWorkflowIds: [],
        removedJobIds: [],
        removedSearchJobIds: [],
        removedTransferIds: [],
      },
    }))).toBe('applied');

    expect(reducer.state.jobs.get(jobId)?.lifecycle.activityPhase).toBe('ProcessingSearchResults');
    expect(reducer.takeChanges().jobHistoryPosition).toEqual({ epoch, sequence: 7 });
    expect(reducer.applyBatch(batch())).toBe('stale');
    reducer.applySnapshot({ ...snapshot(20), jobHistoryPosition: { epoch, sequence: 18 } });
    expect(reducer.takeChanges().jobHistoryPosition).toEqual({ epoch, sequence: 18 });
  });

  it('applies a changed job summary without losing the other live components', () => {
    const reducer = new DaemonStateReducer();
    reducer.applySnapshot({ ...snapshot(), jobs: [{ ...job(1), stats: {
      memberCount: 2, completedMemberCount: 0, failedMemberCount: 0,
    } }] });
    expect(reducer.applyBatch(batch({ state: { ...batch().state, jobs: [{
      jobId, revision: 2, stats: { memberCount: 2, completedMemberCount: 1, failedMemberCount: 0 },
    }] } }))).toBe('applied');
    expect(reducer.state.jobs.get(jobId)?.stats?.completedMemberCount).toBe(1);
    expect(reducer.state.jobs.get(jobId)?.lifecycle.activityPhase).toBe('Searching');
  });

  it('clears an unknown stat through the cohesive live stats component', () => {
    const reducer = new DaemonStateReducer();
    reducer.applySnapshot({ ...snapshot(), jobs: [{ ...job(1), stats: { candidateCount: 2 } }] });
    expect(reducer.applyBatch(batch({ state: { ...batch().state, jobs: [{
      jobId, revision: 2, stats: { candidateCount: null },
    }] } }))).toBe('applied');
    expect(reducer.state.jobs.get(jobId)?.stats?.candidateCount).toBeNull();
  });

  it('requires a new snapshot for a sequence gap or epoch change', () => {
    const reducer = new DaemonStateReducer();
    reducer.applySnapshot(snapshot());

    expect(reducer.applyBatch(batch({ previousSequence: 12, sequence: 13 }))).toBe('recovery-required');
    expect(reducer.state.sequence).toBe(10);
    expect(reducer.applyBatch(batch({ epoch: '30000000-0000-0000-0000-000000000002' }))).toBe('recovery-required');
  });

  it('removes dependent live rows when a job is removed', () => {
    const reducer = new DaemonStateReducer();
    reducer.applySnapshot(snapshot());

    expect(reducer.applyBatch(batch({
      state: {
        workflows: [],
        jobs: [],
        searches: [],
        transfers: [],
        removedWorkflowIds: [],
        removedJobIds: [jobId],
        removedSearchJobIds: [],
        removedTransferIds: [],
      },
    }))).toBe('applied');
    expect(reducer.state.jobs.size).toBe(0);
  });
});
