import { test, expect, deferInitialDaemonSnapshot } from './fixtures';
import type { Schema } from '../src/api/types';

test.use({ timezoneId: 'UTC' });

test('serves the WebUI at the daemon root without replacing API routes', async ({ page, request }) => {
  let jobReads = 0, observations = 0;
  page.on('request', request => {
    if (['/api/jobs', '/api/jobs/snapshot'].includes(new URL(request.url()).pathname)) jobReads++;
  });
  page.on('websocket', socket => socket.on('framesent', ({ payload }) => {
    for (const frame of payload.toString().split('\u001e').filter(Boolean))
      if (JSON.parse(frame).target === 'ObserveJobList') observations++;
  }));
  const response = await page.goto('/');
  expect(response?.status()).toBe(200);
  expect(response?.headers()['content-type']).toContain('text/html');
  expect(response?.headers()['cache-control']).toBe('no-cache');
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
  await expect(page.getByLabel('Connection status')).toHaveText('Connected, Logged In');
  await expect(page.getByText('History range', { exact: true })).toHaveCount(0);
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
  expect(jobReads).toBe(0);
  expect(observations).toBe(0);
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Jobs', exact: true }).click();
  await expect(page.getByText('No searches yet', { exact: true })).toBeVisible();
  expect(observations).toBe(1);
  expect(jobReads).toBe(0);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Jobs', exact: true })).toBeVisible();
  await expect(page.locator('.resource-state-notice.empty.blocking')).toContainText('No searches yet');
  expect(analyticsReads, 'A cold Jobs route must not mount and immediately discard Dashboard.').toBe(0);
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
  // This fresh daemon cannot have a full month of retained accounting.
  await expect(page.locator('.resource-state-notice.degraded')).toHaveCount(0);
  await expect(page.locator('.activity-panel .panel-context')).toHaveText('Last 30 days');
  await expect(page.getByRole('button', { name: '30d', exact: true })).toHaveClass(/active/);
  await page.getByRole('button', { name: '24h', exact: true }).click();
  await expect(page.locator('.chart-x-labels span').first()).toHaveText(/^\d{2}:\d{2}$/);
  await expect(page.locator('.chart-x-labels span').last()).toHaveText('Now');

  let release!: () => void;
  const released = new Promise<void>(resolve => { release = resolve; });
  await page.route(analytics, async route => {
    await released;
    await route.abort();
  });
  const metricsTop = (await page.locator('.dashboard-metrics').boundingBox())!.y;
  await page.getByRole('button', { name: '7d', exact: true }).click();
  await expect(page.getByText('Loading analytics', { exact: true })).toBeVisible();
  expect((await page.locator('.dashboard-metrics').boundingBox())!.y).toBe(metricsTop);
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
  await expect(page.locator('.peers-table').first().locator('.heading')).toContainText('Downloaded');
  await expect(page.locator('.peers-table').last().locator('.heading')).toContainText('Uploaded');
  await ranking.getByRole('button', { name: 'Content', exact: true }).click();
  await expect(page.locator('.dashboard-ranking-row.heading')).toContainText('Folder');
  await ranking.getByRole('button', { name: 'Errors', exact: true }).click();
  await expect(page.locator('.dashboard-ranking-row.heading')).toContainText('Last seen');
  await ranking.getByRole('button', { name: 'Transfers', exact: true }).click();
  await expect(page.locator('.peers-table').first().locator('.heading')).toContainText('Downloaded');
  await page.getByRole('button', { name: '90d', exact: true }).click();
  await expect(page.locator('.chart-x-labels span').first()).toHaveText(/^[A-Z][a-z]{2}$/);
  await expect(page.locator('.chart-x-labels span').last()).toHaveText(/^(Late )?[A-Z][a-z]{2}$/);
});

test('charts transferred amounts and shows both directions for the hovered interval', async ({ page }) => {
  let reads = 0;
  await page.route('**/api/dashboard/analytics?*', async route => {
    reads++;
    const response = await route.fetch();
    const data = await response.json() as Schema<'DashboardAnalyticsDto'>;
    // Adjacent buckets cross midnight and stay within one day.
    const end = Date.parse('2026-11-05T01:00:00Z');
    data.bandwidth = [0, 1, 2].map(index => ({
      startUtc: new Date(end - (3 - index) * 3_600_000).toISOString(),
      endUtc: new Date(end - (2 - index) * 3_600_000).toISOString(),
      downloadBytes: [6_000_000, 12_000_000, 0][index]!,
      uploadBytes: [0, 3_000_000, 30_000_000][index]!,
    }));
    await route.fulfill({ response, json: data });
  });
  await page.goto('/dashboard');
  const chart = page.getByRole('img', { name: 'Bytes transferred per interval for 30d' });
  await expect(page.locator('.chart-y-labels')).toContainText('30.0 MB');
  await expect(page.locator('.chart-y-labels')).not.toContainText('/s');
  const bounds = (await chart.boundingBox())!;
  await chart.hover({ position: { x: bounds.width / 2, y: 75 } });
  const tooltip = page.getByRole('tooltip');
  await expect(tooltip.locator('.download')).toHaveText('Downloaded 12.0 MB');
  await expect(tooltip.locator('.upload')).toHaveText('Uploaded 3.0 MB');
  await expect(tooltip.locator('strong')).not.toBeEmpty();
  await expect(tooltip.locator('strong')).toHaveCSS('white-space', 'nowrap');
  await expect(page.locator('.chart-hover-dot')).toHaveCount(2);
  const downloadPoint = (await page.locator('.chart-hover-dot.download').boundingBox())!;
  const uploadPoint = (await page.locator('.chart-hover-dot.upload').boundingBox())!;
  expect(downloadPoint.y).toBeLessThan(uploadPoint.y);
  expect(Math.abs(downloadPoint.width - downloadPoint.height)).toBeLessThan(1);
  const middleTooltip = (await tooltip.boundingBox())!;
  expect(middleTooltip.y + middleTooltip.height).toBeLessThan(bounds.y);
  await chart.hover({ position: { x: bounds.width - 1, y: 75 } });
  await expect(tooltip.locator('.upload')).toHaveText('Uploaded 30.0 MB');
  const tooltipBounds = (await tooltip.boundingBox())!;
  expect(tooltipBounds.x + tooltipBounds.width).toBeLessThanOrEqual(bounds.x + bounds.width + 1);
  expect(tooltipBounds.width).toBe(middleTooltip.width);
  expect(tooltipBounds.height).toBe(middleTooltip.height);
  await expect(tooltip.locator('time').first()).toHaveAttribute('datetime', '2026-11-05T00:00:00.000Z');
  await expect(tooltip.locator('time').last()).toHaveAttribute('datetime', '2026-11-05T01:00:00.000Z');
  await page.getByRole('heading', { name: 'Dashboard', exact: true }).hover();
  await expect(tooltip).toHaveCount(0);
  await expect(page.locator('.chart-hover-dot')).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  const mobileBounds = (await chart.boundingBox())!;
  await chart.hover({ position: { x: mobileBounds.width / 2, y: 75 } });
  const mobileTooltip = (await tooltip.boundingBox())!;
  expect(mobileTooltip.x).toBeGreaterThanOrEqual(0);
  expect(mobileTooltip.x + mobileTooltip.width).toBeLessThanOrEqual(390);
  expect(mobileTooltip.y + mobileTooltip.height).toBeLessThan(mobileBounds.y);
  await chart.hover({ position: { x: mobileBounds.width - 1, y: 75 } });
  const mobileNext = (await tooltip.boundingBox())!;
  expect(mobileNext.width).toBe(mobileTooltip.width);
  expect(mobileNext.height).toBe(mobileTooltip.height);
  for (const time of await tooltip.locator('time').all()) {
    const timeBounds = (await time.boundingBox())!;
    expect(timeBounds.x + timeBounds.width).toBeLessThan(mobileNext.x + mobileNext.width);
  }
  expect(reads).toBe(1);
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
  await ranking.getByRole('button', { name: 'Transfers', exact: true }).click();
  await ranking.getByRole('button', { name: 'Actions for fixture-peer', exact: true }).click();
  await ranking.getByRole('menuitem', { name: 'Profile' }).click();
  await expect(page).toHaveURL(/\/users\/fixture-peer$/);
});
