import { test, expect, deferInitialDaemonSnapshot } from './fixtures';
import { performance } from 'node:perf_hooks';
import type { Schema } from '../src/api/types';

test('keeps mixed Jobs rows legible at narrow width with singular search counts', async ({ page, daemon }) => {
  const releaseSnapshot = await deferInitialDaemonSnapshot(page);
  let historyReads = 0;
  page.on('request', request => {
    if (request.method() === 'GET' && new URL(request.url()).pathname === '/api/jobs') historyReads++;
  });
  try {
    await page.setViewportSize({ width: 390, height: 900 });
    const initialHistory = page.waitForResponse(response => response.request().method() === 'GET'
      && new URL(response.url()).pathname === '/api/jobs' && response.ok());
    await page.goto('/jobs');
    expect(await (await initialHistory).json()).toEqual([]);
    const list = await page.request.post('/api/jobs/lists', { data: {
      name: 'Fixture album download', jobs: [{ kind: 'remote-file', target: {
        username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
      } }],
    } satisfies Schema<'SubmitJobListRequestDto'> });
    expect(list.status()).toBe(202);
    const search = await page.request.post('/api/jobs/search', { data: { queryText: 'Fixture' } });
    expect(search.status()).toBe(202);

    // Both workflows can retire after the empty HTTP read, before the first
    // live snapshot. The initial history must still recover their terminal rows.
    await expect.poll(async () => {
      const response = await page.request.get('/api/jobs?includeAll=false&archived=false&newestFirst=true&limit=100');
      expect(response.ok()).toBeTruthy();
      const jobs = await response.json() as Schema<'JobSummaryDto'>[];
      if (jobs.length !== 2 || jobs.some(job => job.lifecycleState !== 'Terminal')) return false;
      const snapshot = await page.request.get('/api/daemon/snapshot');
      expect(snapshot.ok()).toBeTruthy();
      return ((await snapshot.json()) as Schema<'StateSnapshotDto'>).jobs.length === 0;
    }).toBe(true);
    releaseSnapshot();
    const searchRow = page.locator('.search-history-row').filter({ hasText: 'Fixture' }).filter({ has: page.locator('.job-type-badge.search') });
    await expect(searchRow.locator('.job-type-badge')).toHaveText('File Search');
    await expect(searchRow.locator('.search-history-stats')).toContainText('1 file');
    await expect(searchRow.locator('.search-history-stats')).toContainText('1 peer');
    await expect(page.locator('.search-history-row')).toHaveCount(2);
    expect(historyReads).toBe(2);
    const layout = await page.locator('.search-history-row').evaluateAll((rows) => rows.map((row) => {
      const context = row.querySelector('.search-history-context')!.getBoundingClientRect();
      const stats = row.querySelector('.search-history-stats')!.getBoundingClientRect();
      const bounds = row.getBoundingClientRect();
      return { verticalGap: stats.top - context.bottom, rightOverflow: stats.right - bounds.right };
    }));
    for (const row of layout) {
      expect(row.verticalGap).toBeGreaterThanOrEqual(0);
      expect(row.rightOverflow).toBeLessThanOrEqual(0);
    }
    // A late transfer-history acknowledgement can arrive between the retained
    // Jobs read and startup snapshot. It must not refetch unchanged Jobs rows.
    let captureReloadHistory!: () => void;
    const reloadHistory = new Promise<void>(resolve => { captureReloadHistory = resolve; });
    await page.route('**/api/jobs?**', async route => {
      const result = await route.fetch();
      captureReloadHistory();
      await route.fulfill({ response: result });
    });
    await page.route('**/api/daemon/snapshot*', async route => {
      await reloadHistory;
      await daemon.command('terminal-transfer-committed');
      await route.continue();
    });
    historyReads = 0;
    await page.reload();
    await expect(page.getByLabel('Connection status').locator('strong').nth(1)).toHaveText('Connected');
    await expect(page.locator('.search-history-row')).toHaveCount(2);
    expect(historyReads).toBe(1);
  } finally { releaseSnapshot(); }
});

test('keeps loaded Jobs rows visible when a later history refresh fails', async ({ page }) => {
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Cached root', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Cached.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  await page.goto('/jobs');
  const row = page.locator('.search-history-row').filter({ hasText: 'Cached root' });
  await expect(row).toBeVisible();

  let unavailable = true;
  await page.route('**/api/jobs?**', (route) => unavailable ? route.abort('failed') : route.continue());
  await page.getByRole('button', { name: 'Dashboard', exact: true }).click();
  await page.getByRole('button', { name: 'Jobs', exact: true }).click();
  const alert = page.getByRole('alert').filter({ hasText: 'Jobs unavailable' });
  await expect(alert).toBeVisible();
  await expect(row).toBeVisible();
  await expect(page.locator('.resource-state-notice.blocking')).toHaveCount(0);

  unavailable = false;
  await alert.getByRole('button', { name: 'Retry' }).click();
  await expect(alert).toHaveCount(0);
  await expect(row).toBeVisible();
});

test.describe('Jobs paging', () => {

test('renders full server pages for Jobs and children and fetches only the next cursor', async ({ page, daemon }) => {
  await daemon.command('retained-imports');
  await daemon.command('hold-download');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Paging parent 101', jobs: Array.from({ length: 101 }, (_, child) => ({
      kind: 'remote-file', target: {
        username: 'fixture-peer', filename: `Fixture\\Paging-101-${child}.txt`, size: 128, extension: '.txt',
      },
    })),
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  const latest = await response.json() as Schema<'JobSummaryDto'>;
  const newestPage = await page.request.get('/api/jobs?includeAll=false&archived=false&newestFirst=true&limit=3');
  expect(newestPage.status()).toBe(200);
  const newestRows = await newestPage.json() as Schema<'JobSummaryDto'>[];
  expect(newestRows.map((job) => job.itemName)).toEqual(['Paging parent 101', 'Paging parent 100', 'Paging parent 100']);
  const newestCursor = newestPage.headers()['x-next-cursor'];
  expect(newestCursor).toBeTruthy();
  const wrongDirection = await page.request.get(`/api/jobs?includeAll=false&archived=false&limit=3&cursor=${encodeURIComponent(newestCursor!)}`);
  expect(wrongDirection.status()).toBe(400);
  const olderPage = await page.request.get(`/api/jobs?includeAll=false&archived=false&newestFirst=true&limit=3&cursor=${encodeURIComponent(newestCursor!)}`);
  expect((await olderPage.json() as Schema<'JobSummaryDto'>[]).map((job) => job.itemName))
    .toEqual(['Paging parent 99', 'Paging parent 99', 'Paging parent 98']);
  let childReads = 0, historyReads = 0;
  let listing = false;
  const detailReads: string[] = [];
  const lookups: number[] = [];
  const listingReads: { path: string; milliseconds: number }[] = [];
  page.on('requestfinished', request => {
    if (listing && new URL(request.url()).pathname.startsWith('/api/jobs'))
      listingReads.push({ path: new URL(request.url()).pathname, milliseconds: Math.round(request.timing().responseEnd) });
  });
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (listing && request.method() === 'GET' && /^\/api\/jobs\/[^/]+$/.test(url.pathname)) detailReads.push(url.pathname);
    if (listing && url.pathname === '/api/jobs/lookup') lookups.push(request.postDataJSON().jobIds.length);
    if (url.pathname !== '/api/jobs') return;
    expect(url.searchParams.get('limit')).toBe('100');
    if (url.searchParams.has('parentJobId')) childReads++;
    else historyReads++;
  });
  await daemon.command('wait-download-started');
  await page.goto(`/jobs/${latest!.jobId}`);
  await expect(page.locator('.job-detail-heading-summary .search-status-badge')).toHaveText('Running');
  await expect(page.locator('.job-child-row')).toHaveCount(100);
  const previousChildReads = childReads;
  await page.getByRole('button', { name: 'Load more jobs', exact: true }).click();
  await expect(page.locator('.job-child-row')).toHaveCount(101);
  expect(childReads).toBe(previousChildReads + 1);
  await expect(page.getByRole('button', { name: 'Load more jobs', exact: true })).toHaveCount(0);
  await daemon.command('release-download');
  await expect(page.locator('.job-child-row .search-status-badge')).toHaveText(Array(101).fill('Complete'));
  listing = true;
  const listingStarted = performance.now();
  await page.getByRole('button', { name: 'Jobs', exact: true }).click();
  await expect(page.locator('.search-history-query')).toHaveText(
    Array.from({ length: 100 }, (_, index) => `Paging parent ${101 - index}`));
  await expect(page.locator('.search-history-row .search-status-badge')).toHaveText(Array(100).fill('Complete'));
  await expect(page.locator('.job-type-badge.import')).toHaveText(Array(99).fill('Text import'));
  const initialListingMs = Math.round(performance.now() - listingStarted);
  expect(lookups).toEqual([99]);
  expect(detailReads).toEqual([]);
  const earlier = page.getByRole('button', { name: 'Load earlier jobs', exact: true });
  await expect(earlier).toBeEnabled();
  const previousHistoryReads = historyReads;
  await earlier.click();
  await expect(page.locator('.search-history-row')).toHaveCount(101);
  await expect(page.locator('.search-history-query').last()).toHaveText('Paging parent 1');
  expect(historyReads).toBe(previousHistoryReads + 1);
  await expect(earlier).toHaveCount(0);
  await expect(page.locator('.job-type-badge.import')).toHaveText(Array(100).fill('Text import'));
  await expect(page.locator('.search-history-row .search-status-badge')).toHaveText(Array(101).fill('Complete'));
  expect(lookups).toEqual([99, 1]);
  expect(detailReads).toEqual([]);

  // One failed batch must keep the unrelated parent healthy, and one Retry
  // must recover all affected imports rather than demand 99 separate clicks.
  await page.route('**/api/jobs/lookup', route => route.fulfill({
    status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Linked summaries temporarily unavailable' }),
  }), { times: 1 });
  await page.reload();
  await expect(page.locator('.search-history-row .search-status-badge'))
    .toHaveText(['Complete', ...Array(99).fill('Result unavailable')]);
  await page.getByRole('button', { name: 'Retry', exact: true }).first().click();
  await expect(page.locator('.search-history-row .search-status-badge')).toHaveText(Array(100).fill('Complete'));
  expect(lookups).toEqual([99, 1, 99, 99]);
  expect(detailReads).toEqual([]);
});

});

test('keeps a completed submission in Jobs across both initial snapshot and history orderings', async ({ page, daemon }) => {
  for (const ordering of ['history-first', 'snapshot-first'] as const) {
    const name = `Live history fixture ${ordering}`;
    await daemon.command('hold-download');
    const response = await page.request.post('/api/jobs/lists', { data: {
      name, jobs: [{ kind: 'remote-file', target: {
        username: 'fixture-peer', filename: `Fixture\\${ordering}.txt`, size: 128, extension: '.txt',
      } }],
    } satisfies Schema<'SubmitJobListRequestDto'> });
    expect(response.status()).toBe(202);
    const root = await response.json() as Schema<'JobSummaryDto'>;
    await daemon.command('wait-download-started');
    let observeRetirement!: () => void;
    const retired = new Promise<void>(resolve => { observeRetirement = resolve; });
    const watchRetirement = (socket: import('@playwright/test').WebSocket) => socket.on('framereceived', ({ payload }) => {
      for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
        const message = JSON.parse(frame) as { target?: string; arguments?: Schema<'StateUpdateBatchDto'>[] };
        if (message.target?.toLowerCase() !== 'stateupdatebatch') continue;
        const state = message.arguments?.[0]?.state;
        if (state?.removedJobIds.includes(root.jobId!) || state?.removedWorkflowIds.includes(root.workflowId!)) observeRetirement();
      }
    });
    page.on('websocket', watchRetirement);
    let releaseSnapshot!: () => void;
    let observeSnapshot!: () => void;
    const snapshotGate = new Promise<void>(resolve => { releaseSnapshot = resolve; });
    const snapshotRequested = new Promise<void>(resolve => { observeSnapshot = resolve; });
    await page.route('**/api/daemon/snapshot*', async route => {
      observeSnapshot();
      await snapshotGate;
      await route.continue();
    });
    let releaseHistory!: () => void;
    let captureHistory!: (jobs: Schema<'JobSummaryDto'>[]) => void;
    const historyGate = new Promise<void>(resolve => { releaseHistory = resolve; });
    const historyCaptured = new Promise<Schema<'JobSummaryDto'>[]>(resolve => { captureHistory = resolve; });
    let historyReads = 0;
    await page.route('**/api/jobs?**', async route => {
      const first = ++historyReads === 1;
      const result = await route.fetch();
      if (first) {
        captureHistory(await result.json() as Schema<'JobSummaryDto'>[]);
        if (ordering === 'snapshot-first') await historyGate;
      }
      await route.fulfill({ response: result });
    });
    try {
      await page.goto('/jobs');
      const captured = await historyCaptured;
      expect(captured.find(job => job.jobId === root.jobId)?.lifecycleState).toBe('Running');
      await snapshotRequested;
      const row = page.locator('.search-history-row').filter({ hasText: name });
      if (ordering === 'history-first') await expect(row.locator('.search-status-badge')).toHaveText('Running');
      await daemon.command('release-download');
      await retired;
      releaseSnapshot();
      await expect(page.getByLabel('Connection status').locator('strong').last()).toHaveText('Connected');
      releaseHistory();
      await expect(row.locator('.search-status-badge')).toHaveText('Complete');
      await expect(row.getByRole('button', { name: `Remove ${name}`, exact: true })).toBeVisible();
      // Exactly one reconciliation read: no stale Running row and no repeated
      // polling or duplicate initial-history refreshes.
      expect(historyReads).toBe(2);
    } finally {
      releaseSnapshot();
      releaseHistory();
      page.off('websocket', watchRetirement);
      await page.unroute('**/api/daemon/snapshot*');
      await page.unroute('**/api/jobs?**');
    }
  }
  await page.reload();
  await expect(page.locator('.search-history-row').filter({ hasText: 'Live history fixture' })
    .locator('.search-status-badge')).toHaveText(['Complete', 'Complete']);
});
