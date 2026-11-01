import { expect, test } from './fixtures';
import type { WebSocketRoute } from '@playwright/test';

test('shows prototype offline states and recovers history after daemon startup returns', async ({ page }) => {
  let unavailable = true;
  await page.route('**/api/**', (route) => unavailable ? route.abort('failed') : route.continue());
  await page.goto('/downloads');

  const daemonStatus = page.getByLabel('Connection status').locator('strong').nth(1);
  const offline = page.locator('.resource-state-notice.offline.blocking');
  await expect(daemonStatus).toHaveText('Offline');
  await expect(offline).toContainText('Daemon unavailable');
  await expect(offline).toContainText('Current download state cannot be loaded while the daemon is offline.');
  await expect(offline.getByRole('button', { name: 'Retry' })).toHaveCount(0);
  await expect(page.getByText('No downloads', { exact: true })).toHaveCount(0);

  await page.getByRole('button', { name: 'Jobs', exact: true }).click();
  await expect(offline).toContainText('This resource cannot be loaded while the daemon is offline.');
  await expect(page.getByText('No jobs yet', { exact: true })).toHaveCount(0);

  await page.getByRole('button', { name: 'Uploads', exact: true }).click();
  await expect(offline).toContainText('Current upload state cannot be loaded while the daemon is offline.');
  unavailable = false;

  await expect(daemonStatus).toHaveText('Connected', { timeout: 20_000 });
  await expect(offline).toHaveCount(0);
  await expect(page.getByText('No uploads', { exact: true })).toBeVisible();
});

test('reloads an open Jobs page when the daemon first becomes reachable', async ({ page }) => {
  let unavailable = true;
  let socket: WebSocketRoute | undefined;
  let outage = false;
  let release!: () => void;
  const restored = new Promise<void>(resolve => { release = resolve; });
  let historyReads = 0;
  page.on('request', request => {
    if (request.method() === 'GET' && new URL(request.url()).pathname === '/api/jobs') historyReads++;
  });
  await page.route('**/api/**', (route) => unavailable ? route.abort('failed') : route.continue());
  await page.route('**/api/events/negotiate?**', async route => {
    if (outage) await restored;
    await route.fallback();
  });
  await page.routeWebSocket('**/api/events?**', route => {
    if (outage) void route.close({ code: 1012, reason: 'E2E connection unavailable' });
    else { socket = route; route.connectToServer(); }
  });
  await page.goto('/jobs');
  const offline = page.locator('.resource-state-notice.offline.blocking');
  await expect(offline).toContainText('Daemon unavailable');
  unavailable = false;

  await expect(page.getByLabel('Connection status').locator('strong').nth(1))
    .toHaveText('Connected', { timeout: 20_000 });
  await expect(offline).toHaveCount(0);
  await expect(page.getByText('No jobs yet', { exact: true })).toBeVisible();
  historyReads = 0;
  try {
    outage = true;
    await socket!.close({ code: 1012, reason: 'E2E reconnect history check' });
    await expect(page.getByLabel('Connection status').locator('strong').nth(1)).toHaveText('Reconnecting');
    outage = false;
    release();
    await expect(page.getByLabel('Connection status').locator('strong').nth(1)).toHaveText('Connected');
    await expect(page.getByText('No jobs yet', { exact: true })).toBeVisible();
    expect(historyReads).toBe(1);
  } finally { outage = false; release(); }
});

test('reopens a search deep link after a fresh offline read fails', async ({ page, daemon }) => {
  await daemon.command('directory-selection-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'File Search', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-item-card')).toHaveCount(1);
  await expect(page).toHaveURL(/\/jobs\/[^/]+\?results=generic$/);

  let unavailable = true;
  await page.route('**/api/**', (route) => unavailable ? route.abort('failed') : route.continue());
  await page.reload();
  const offline = page.locator('.resource-state-notice.offline.blocking');
  await expect(offline).toContainText('Daemon unavailable');
  await expect(offline).toContainText('This resource cannot be loaded while the daemon is offline.');
  await expect(page.locator('.search-results-empty')).toHaveCount(0);
  unavailable = false;

  await expect(page.getByLabel('Connection status').locator('strong').nth(1))
    .toHaveText('Connected', { timeout: 20_000 });
  await expect(offline).toHaveCount(0);
  await expect(page.locator('.folder-item-card')).toHaveCount(1);
  await expect(page).toHaveURL(/\?results=generic$/);
});

test('recovers an open Chat list and disables sending during a fresh offline start', async ({ page }) => {
  const seeded = await page.request.post('/api/chat/private-messages', {
    data: { username: 'Offline Chat Peer', text: 'Retained hello', messageId: crypto.randomUUID() },
  });
  expect(seeded.ok()).toBeTruthy();
  let unavailable = true;
  await page.route('**/api/**', (route) => unavailable ? route.abort('failed') : route.continue());
  await page.goto('/chat');
  const offline = page.locator('.chat-resource-state .resource-state-notice.offline');
  await expect(offline).toContainText('Daemon unavailable');
  await expect(page.locator('.composer-state')).toHaveText('Chat disabled · Daemon unavailable');
  await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toBeDisabled();
  unavailable = false;

  await expect(page.getByLabel('Connection status').locator('strong').nth(1))
    .toHaveText('Connected', { timeout: 20_000 });
  await expect(offline).toHaveCount(0);
  await expect(page.locator('.chat-messages')).toContainText('Retained hello');
  await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toBeEnabled();
});
