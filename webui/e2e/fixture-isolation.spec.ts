import { test, expect } from './fixtures';
import type { Schema } from '../src/api/types';

// These two sessions deliberately run consecutively in one worker. Reusing
// the CLR must not reuse any daemon, fixture, persistence or sharing state.
test.describe.configure({ mode: 'serial' });
let previousJobId: string;

test.describe('upload-enabled session', () => {
  test.use({ uploadShares: true });
  test('creates work and changes the peer fixture before disposal', async ({ daemon, context }) => {
    await daemon.command('mixed-visibility-search');
    const submitted = await context.request.post('/api/jobs/search', {
      data: { queryText: 'Fixture' } satisfies Schema<'SubmitSearchJobRequestDto'>,
    });
    expect(submitted.ok(), await submitted.text()).toBe(true);
    previousJobId = (await submitted.json() as Schema<'JobSummaryDto'>).jobId!;
    await daemon.command('request-upload');
    const snapshot = await context.request.get('/api/daemon/snapshot');
    expect(snapshot.ok(), await snapshot.text()).toBe(true);
    const state = await snapshot.json() as Schema<'StateSnapshotDto'>;
    expect(state.daemon?.sharing.state).toBe('Ready');
    expect(state.daemon?.sharing.catalog.fileCount).toBe(2);
    // Teardown also has to drain the held upload, rather than just abandoning
    // the application and letting process exit hide a disposal problem.
  });
});

test('starts with fresh history, sharing configuration and peer behavior', async ({ daemon, context }) => {
  expect(previousJobId).toBeTruthy();
  const previous = await context.request.get(`/api/jobs/${previousJobId}`);
  expect(previous.status()).toBe(404);
  const jobs = await context.request.get('/api/jobs?archived=false&includeAll=true&limit=100');
  expect(jobs.ok(), await jobs.text()).toBe(true);
  expect(await jobs.json()).toEqual([]);
  const uploads = await context.request.get('/api/transfers?direction=upload&archived=false&limit=100');
  expect(uploads.ok(), await uploads.text()).toBe(true);
  expect((await uploads.json() as Schema<'TransferTimelinePageDto'>).items).toEqual([]);
  const snapshot = await context.request.get('/api/daemon/snapshot');
  expect(snapshot.ok(), await snapshot.text()).toBe(true);
  expect((await snapshot.json() as Schema<'StateSnapshotDto'>).daemon?.sharing.state).toBe('Disabled');

  const submitted = await context.request.post('/api/jobs/search', {
    data: { queryText: 'Fixture' } satisfies Schema<'SubmitSearchJobRequestDto'>,
  });
  expect(submitted.ok(), await submitted.text()).toBe(true);
  const job = await submitted.json() as Schema<'JobSummaryDto'>;
  const created = await context.request.post(`/api/jobs/${job.jobId}/search-views`, {
    data: { kind: 'GenericDirectories', includeFullResults: true },
  });
  expect(created.ok(), await created.text()).toBe(true);
  let view = await created.json() as Schema<'SearchViewSummaryDto'>;
  while (!view.isComplete) {
    const update = await context.request.get(`/api/search-views/${view.viewId}/updates?afterRevision=${view.revision}&waitForChange=true`);
    expect(update.ok(), await update.text()).toBe(true);
    view = (await update.json() as Schema<'SearchViewUpdateDto'>).summary;
  }
  expect(Number(view.counters.publicFileCount)).toBe(1);
  expect(Number(view.counters.lockedFileCount)).toBe(0);
});
