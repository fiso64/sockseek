import { test, expect, deferInitialDaemonSnapshot } from './fixtures';
import type { Schema } from '../src/api/types';

test('serves the WebUI at the daemon root without replacing API routes', async ({ page, request }) => {
  const response = await page.goto('/');
  expect(response?.status()).toBe(200);
  expect(response?.headers()['content-type']).toContain('text/html');
  expect(response?.headers()['cache-control']).toBe('no-cache');
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
  const scriptPath = await page.locator('script[type="module"][src]').getAttribute('src');
  expect(scriptPath).toMatch(/^\/assets\/index-[^/]+\.js$/);
  const asset = await request.get(scriptPath!);
  expect(asset.ok()).toBe(true);
  expect(asset.headers()['cache-control']).toContain('immutable');
  const info = await request.get('/api/server/info');
  expect(info.ok()).toBe(true);
  expect(info.headers()['content-type']).toContain('application/json');
  expect((await request.get('/api/not-a-webui-route')).status()).toBe(404);
  let analyticsReads = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname === '/api/dashboard/analytics') analyticsReads++;
  });
  await page.goto('/jobs');
  await expect(page.getByRole('heading', { name: 'Jobs', exact: true })).toBeVisible();
  await expect(page.locator('.resource-state-notice.empty.blocking')).toContainText('No jobs yet');
  expect(analyticsReads, 'A cold Jobs route must not mount and immediately discard Dashboard.').toBe(0);
});

test('observes Jobs only after opening it without a duplicate HTTP page', async ({ page }) => {
  const releaseSnapshot = await deferInitialDaemonSnapshot(page);
  const jobsRequests: string[] = [];
  const analyticsRequests: string[] = [];
  let observations = 0;
  page.on('websocket', socket => socket.on('framesent', ({ payload }) => {
    for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
      if (JSON.parse(frame).target === 'ObserveJobList') observations++;
    }
  }));
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (path === '/api/jobs' || path === '/api/jobs/snapshot') jobsRequests.push(request.url());
    if (path === '/api/dashboard/analytics') analyticsRequests.push(request.url());
  });
  try {
    const dashboardRead = page.waitForResponse(response =>
      new URL(response.url()).pathname === '/api/dashboard/analytics');
    await page.goto('/dashboard');
    await dashboardRead;
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    expect(jobsRequests).toHaveLength(0);
    expect(observations).toBe(0);
    expect(analyticsRequests).toHaveLength(1);
    await page.getByRole('navigation', { name: 'Primary navigation' })
      .getByRole('button', { name: 'Jobs' }).click();
    await expect(page.getByText('Loading jobs', { exact: true })).toBeVisible();
    releaseSnapshot();
    await expect(page.getByLabel('Connection status')).toContainText('Connected');
    await expect(page.locator('.resource-state-notice.empty.blocking')).toContainText('No jobs yet');
    expect(observations).toBe(1);
    expect(jobsRequests).toHaveLength(0);
  } finally { releaseSnapshot(); }
});

test('retries analytics and never labels previous-range data as the selected range', async ({ page }) => {
  const releaseSnapshot = await deferInitialDaemonSnapshot(page);
  const analytics = '**/api/dashboard/analytics?*';
  const downloaded = page.locator('.dashboard-metric-card').first().locator('strong');
  try {
    await page.route(analytics, route => route.abort());
    await page.goto('/dashboard');
    await expect(page.getByRole('alert')).toContainText('Dashboard analytics unavailable');
    await expect(downloaded).toHaveText('—');
    await page.unroute(analytics);
    await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(downloaded).not.toHaveText('—');
    await expect(page.getByRole('alert')).toHaveCount(0);
  } finally { releaseSnapshot(); }
  await expect(page.locator('.health-list').getByText('API latency').locator('..').locator('strong')).toHaveText(/^\d+ ms$/);
  await expect(page.locator('.activity-panel')).toHaveAttribute('data-bucket-seconds', /\d+/);
  // This fresh daemon cannot have a full day of retained accounting.
  await expect(page.getByText('Analytics partially covered', { exact: true })).toBeVisible();
  await expect(page.locator('.resource-state-notice.degraded')).toBeVisible();
  await expect(page.locator('.chart-x-labels span').first()).toHaveText(/^\d{2}:\d{2}$/);
  await expect(page.locator('.chart-x-labels span').last()).toHaveText('Now');

  let release!: () => void;
  const released = new Promise<void>(resolve => { release = resolve; });
  await page.route(analytics, async route => {
    await released;
    await route.abort();
  });
  await page.getByRole('button', { name: '7d', exact: true }).click();
  await expect(page.getByText('Loading analytics', { exact: true })).toBeVisible();
  await expect(downloaded).toHaveText('—');
  await expect(page.locator('.activity-panel .panel-context')).toHaveText('Last 7 days');
  release();
  await expect(page.getByRole('alert')).toContainText('Dashboard analytics unavailable');
  await expect(downloaded).toHaveText('—');
  await page.unroute(analytics);
  const retried = page.waitForResponse(response => response.url().includes('/dashboard/analytics?range=7d') && response.ok());
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await retried;
  await expect(downloaded).not.toHaveText('—');
  await expect(page.getByRole('alert')).toHaveCount(0);
  await expect(page.locator('.chart-x-labels span').first()).toHaveText(/^(Mon|Tue|Wed|Thu|Fri|Sat|Sun)$/);
  const ranking = page.getByRole('tablist', { name: 'Dashboard ranking' });
  await ranking.getByRole('button', { name: 'Uploads', exact: true }).click();
  await expect(page.locator('.dashboard-ranking-row.heading')).toContainText('Uploaded');
  await ranking.getByRole('button', { name: 'Content', exact: true }).click();
  await expect(page.locator('.dashboard-ranking-row.heading')).toContainText('Folder');
  await ranking.getByRole('button', { name: 'Errors', exact: true }).click();
  await expect(page.locator('.dashboard-ranking-row.heading')).toContainText('Last seen');
  await ranking.getByRole('button', { name: 'Downloads', exact: true }).click();
  await expect(page.locator('.dashboard-ranking-row.heading')).toContainText('Downloaded');
  await page.getByRole('button', { name: '90d', exact: true }).click();
  await expect(page.locator('.chart-x-labels span').first()).toHaveText(/^[A-Z][a-z]{2}$/);
  await expect(page.locator('.chart-x-labels span').last()).toHaveText(/^(Late )?[A-Z][a-z]{2}$/);
});

test('refreshes retained analytics after the terminal transfer commit', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  await page.goto('/dashboard');
  const downloaded = page.locator('.dashboard-metric-card').first().locator('strong');
  await expect(downloaded).toHaveText('0 B');
  const submitted = await page.request.post('/api/jobs/lists', { data: {
    name: 'Dashboard refresh fixture', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(submitted.status()).toBe(202);
  await expect(page.locator('.current-transfer-panel')).toContainText('1 active download');
  const refreshes: Array<number | string> = [];
  await page.route('**/api/dashboard/analytics?*', async route => {
    const response = await route.fetch();
    const analytics = await response.json() as Schema<'DashboardAnalyticsDto'>;
    refreshes.push(analytics.summary.downloadedBytes);
    await route.fulfill({ response });
  });
  await daemon.command('release-download');
  await expect(page.locator('.current-transfer-panel')).toContainText('0 active downloads');
  await expect(downloaded).toHaveText('128 B');
  expect(refreshes).toContain(128);
  await expect(page.locator('.dashboard-metric-card').first().locator('small')).toHaveText('1 file in range');
  await expect(page.locator('.dashboard-metric-card').nth(2).locator('small')).toHaveText('—');
  await expect(page.locator('.dashboard-metric-card').first().locator('.metric-sparkline i:not(.zero)')).toHaveCount(1);
  await expect(page.locator('.dashboard-metric-card').first().locator('.metric-sparkline i.zero').first()).toHaveCSS('height', '0px');
  await expect(page.getByText('Loading analytics', { exact: true })).toHaveCount(0);

  const ranking = page.locator('.ranking-panel');
  await expect(ranking.locator('.peers-table .dashboard-ranking-row').nth(1)).toContainText('fixture-peer');
  await expect(ranking.locator('.peers-table .dashboard-ranking-row').nth(1)).toContainText('128 B');
  await ranking.getByRole('button', { name: 'Content', exact: true }).click();
  // Content ranks folders downloaded from our shares by remote peers, not
  // files we downloaded from them.
  await expect(ranking.locator('.content-table .dashboard-ranking-row')).toHaveCount(1);
  await ranking.getByRole('button', { name: 'Downloads', exact: true }).click();
  await ranking.getByRole('button', { name: 'Actions for fixture-peer', exact: true }).click();
  await ranking.getByRole('menuitem', { name: 'Profile' }).click();
  await expect(page).toHaveURL(/\/users\/fixture-peer$/);
});
