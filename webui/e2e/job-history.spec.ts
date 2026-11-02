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
    await page.goto('/jobs');
    await expect(page.getByText('Loading jobs', { exact: true })).toBeVisible();
    const list = await page.request.post('/api/jobs/lists', { data: {
      name: 'Fixture album download', jobs: [{ kind: 'remote-file', target: {
        username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
      } }],
    } satisfies Schema<'SubmitJobListRequestDto'> });
    expect(list.status()).toBe(202);
    const search = await page.request.post('/api/jobs/search', { data: { queryText: 'Fixture' } });
    expect(search.status()).toBe(202);

    // Both workflows retire before connection hydration. The list scope must
    // recover their retained rows without relying on a live workflow snapshot.
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
    expect(historyReads).toBe(0);
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
    // A transfer acknowledgement during connection hydration must not trigger
    // a second Jobs HTTP path alongside the canonical list observation.
    await page.route('**/api/daemon/snapshot*', async route => {
      await daemon.command('terminal-transfer-committed');
      await route.continue();
    });
    historyReads = 0;
    await page.reload();
    await expect(page.getByLabel('Connection status').locator('strong').nth(1)).toHaveText('Connected');
    await expect(page.locator('.search-history-row')).toHaveCount(2);
    expect(historyReads).toBe(0);
  } finally { releaseSnapshot(); }
});

test('keeps loaded Jobs rows visible when a later observation fails', async ({ page }) => {
  let unavailable = false;
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    route.onMessage(payload => {
      if (typeof payload !== 'string') { server.send(payload); return; }
      for (const frame of payload.split('\u001e').filter(Boolean)) {
        const message = JSON.parse(frame);
        if (unavailable && message.type === 4 && message.target === 'ObserveJobList')
          route.send(JSON.stringify({ type: 3, invocationId: message.invocationId, error: 'Jobs temporarily unavailable' }) + '\u001e');
        else server.send(frame + '\u001e');
      }
    });
  });
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Cached root', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Cached.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  await page.goto('/jobs');
  const row = page.locator('.search-history-row').filter({ hasText: 'Cached root' });
  await expect(row).toBeVisible();

  unavailable = true;
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

test('renders full Jobs scopes and child pages without redundant detail reads', async ({ page, daemon }) => {
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
  const observedLimits: number[] = [];
  page.on('websocket', socket => socket.on('framesent', ({ payload }) => {
    for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
      const message = JSON.parse(frame);
      if (message.target === 'ObserveJobList') observedLimits.push(message.arguments[0].limit);
    }
  }));
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
  expect(observedLimits).toEqual([100]);
  expect(historyReads).toBe(0);
  await earlier.click();
  await expect(page.locator('.search-history-row')).toHaveCount(101);
  await expect(page.locator('.search-history-query').last()).toHaveText('Paging parent 1');
  expect(observedLimits).toEqual([100, 200]);
  expect(historyReads).toBe(0);
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

test('keeps completed Jobs through a delayed list snapshot and reconnect', async ({ page, daemon }) => {
  let holding = true;
  let releaseList = () => {};
  let captureList!: () => void;
  const captured = new Promise<void>(resolve => { captureList = resolve; });
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    const lists = new Set<string>();
    const delayed: string[] = [];
    releaseList = () => { holding = false; for (const frame of delayed.splice(0)) route.send(frame); };
    route.onMessage(payload => {
      if (typeof payload === 'string') for (const frame of payload.split('\u001e').filter(Boolean)) {
        const message = JSON.parse(frame);
        if (message.type === 4 && message.target === 'ObserveJobList') lists.add(message.invocationId);
      }
      server.send(payload);
    });
    server.onMessage(payload => {
      if (typeof payload !== 'string') { route.send(payload); return; }
      for (const frame of payload.split('\u001e').filter(Boolean)) {
        const message = JSON.parse(frame);
        if (holding && lists.has(message.invocationId)) {
          delayed.push(frame + '\u001e');
          if (message.type === 2 && message.item.windowJobIds) captureList();
        } else route.send(frame + '\u001e');
      }
    });
  });
  await daemon.command('hold-download');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Delayed list fixture', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Delayed.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  const root = await response.json() as Schema<'JobSummaryDto'>;
  await daemon.command('wait-download-started');
  let observeRetirement!: () => void;
  const retired = new Promise<void>(resolve => { observeRetirement = resolve; });
  page.on('websocket', socket => socket.on('framereceived', ({ payload }) => {
    for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
      const message = JSON.parse(frame) as { target?: string; arguments?: Schema<'StateUpdateBatchDto'>[] };
      if (message.target?.toLowerCase() === 'stateupdatebatch'
        && message.arguments?.[0]?.state?.removedWorkflowIds.includes(root.workflowId!)) observeRetirement();
    }
  }));
  let httpPages = 0;
  page.on('request', request => {
    if (['/api/jobs', '/api/jobs/snapshot'].includes(new URL(request.url()).pathname)) httpPages++;
  });
  try {
    await page.goto('/jobs');
    await captured;
    await expect(page.getByText('Loading jobs', { exact: true })).toBeVisible();
    await daemon.command('release-download');
    await retired;
    releaseList();
    const row = page.locator('.search-history-row').filter({ hasText: 'Delayed list fixture' });
    await expect(row.locator('.search-status-badge')).toHaveText('Complete');
    await expect(row.getByRole('button', { name: 'Remove Delayed list fixture', exact: true })).toBeVisible();
    expect(httpPages).toBe(0);
    await page.reload();
    await expect(row.locator('.search-status-badge')).toHaveText('Complete');
    expect(httpPages).toBe(0);
  } finally { releaseList(); }
});
