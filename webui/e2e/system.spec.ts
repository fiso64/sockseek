import { test, expect } from './fixtures';
import type { Schema } from '../src/api/types';

test.use({ uploadShares: true });

test('browses the served catalog and confirms cleanup without deleting active work', async ({ page, daemon }) => {
  const requests: string[] = [];
  page.on('request', request => requests.push(`${request.method()} ${new URL(request.url()).pathname}`));
  const status = page.waitForResponse(response => new URL(response.url()).pathname === '/api/server/status');
  await page.goto('/system');
  await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Status', exact: true })).toHaveAttribute('aria-selected', 'true');
  expect((await status).ok()).toBe(true);
  expect(requests.filter(path => path.includes('/auth/keys'))).toEqual([]);
  // A scan publishes several unrelated daemon updates. Status must render those
  // without fetching the same connection/storage details again.
  await page.getByRole('button', { name: 'Rescan shares', exact: true }).click();
  await expect(page.locator('.system-page .resource-state-notice[role=status] .notice-dismiss')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Rescan shares', exact: true })).toBeEnabled();
  expect(requests.filter(path => path === 'GET /api/server/status')).toHaveLength(1);
  expect(requests.filter(path => path === 'GET /api/server/info')).toHaveLength(1);
  expect(requests.filter(path => path.includes('/sharing/catalogs/'))).toHaveLength(0);

  await page.getByRole('tablist', { name: 'System sections' }).getByRole('tab', { name: 'Shares', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Expand Album', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Expand Album', exact: true }).click();
  await expect(page.getByText('Uploaded.txt', { exact: true })).toBeVisible();
  // Root directories, Public's children, and Album's files: no requests for
  // Public's known-empty file list or Album's known-empty child directories.
  expect(requests.filter(path => path.includes('/sharing/catalogs/'))).toHaveLength(3);
  await expect(page.locator('.share-tree input[type=checkbox]')).toHaveCount(0);
  await page.getByRole('textbox', { name: 'Filter user shares' }).fill('Uploaded');
  await expect(page.locator('.share-tree-row.file')).toHaveCount(1);
  expect(requests.some(path => path.includes('/user-browses'))).toBe(false);
  const catalog = await (await page.request.get('/api/sharing')).json() as Schema<'SharingStateDto'>;
  const generation = catalog.catalog.generationId!;
  const oldPage = await page.request.get(`/api/sharing/catalogs/${generation}/directories?limit=1`);
  expect(oldPage.ok()).toBe(true);

  await page.getByRole('tab', { name: 'Management', exact: true }).click();
  expect(requests.filter(path => path.includes('/auth/keys'))).toEqual([]);
  await page.getByRole('tab', { name: 'Management', exact: true }).focus();
  await page.keyboard.press('End');
  await expect(page.getByRole('tab', { name: 'API', exact: true })).toBeFocused();
  await expect(page.getByRole('button', { name: 'Create API key', exact: true })).toBeEnabled();
  await page.getByRole('tablist', { name: 'System sections' }).getByRole('tab', { name: 'Shares', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Filter user shares' })).toHaveValue('Uploaded');
  const scanComplete = page.waitForResponse(response => new URL(response.url()).pathname.includes('/sharing/catalogs/')
    && !new URL(response.url()).pathname.includes(generation) && response.ok());
  await page.getByRole('button', { name: 'Rescan shares', exact: true }).click();
  await scanComplete;
  await expect(page.getByRole('textbox', { name: 'Filter user shares' })).toHaveValue('');
  expect((await page.request.get(`/api/sharing/catalogs/${generation}/directories`)).status()).toBe(409);

  expect((await page.request.post('/api/jobs/delete-completed', { data: {} })).status()).toBe(400);

  const completed = await (await page.request.post('/api/jobs/search', { data: { queryText: 'Fixture' } })).json() as Schema<'JobSummaryDto'>;
  await daemon.command(`wait-job-complete:${completed.jobId}`);
  await daemon.command('staged-search');
  const active = await (await page.request.post('/api/jobs/search', { data: { queryText: 'Still searching' } })).json() as Schema<'JobSummaryDto'>;
  await daemon.command('wait-search-started');
  await page.getByRole('tab', { name: 'Management', exact: true }).click();
  await page.getByRole('listitem').filter({ has: page.locator('#action-searches') }).getByRole('button', { name: 'Run', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Escape');
  expect(requests.filter(path => path === 'POST /api/jobs/delete-completed')).toEqual([]);
  await page.getByRole('listitem').filter({ has: page.locator('#action-searches') }).getByRole('button', { name: 'Run', exact: true }).click();
  const deleted = page.waitForResponse(response => new URL(response.url()).pathname === '/api/jobs/delete-completed');
  await dialog.getByRole('button', { name: 'Run', exact: true }).click();
  expect(await (await deleted).json()).toMatchObject({ deletedSubmissions: 1, deletedJobs: 1 });
  expect((await page.request.get(`/api/jobs/${completed.jobId}`)).status()).toBe(404);
  expect((await page.request.get(`/api/jobs/${active.jobId}`)).status()).toBe(200);
  await daemon.command('finish-search');

  await page.goto('/uploads');
  await daemon.command('fail-next-upload');
  await daemon.command('request-upload');
  await expect(page.locator('.file-item-card').filter({ hasText: 'Uploaded.txt' }).getByRole('button', { name: 'Failed', exact: true })).toBeVisible();
  await page.goto('/system/management');
  await page.getByRole('listitem').filter({ has: page.locator('#action-uploads') }).getByRole('button', { name: 'Run', exact: true }).click();
  const removed = page.waitForResponse(response => new URL(response.url()).pathname === '/api/transfers/remove');
  await page.getByRole('dialog').getByRole('button', { name: 'Run', exact: true }).click();
  const receipt = await removed;
  expect(receipt.request().postDataJSON()).toEqual({ direction: 'upload' });
  expect(await receipt.json()).toMatchObject({ succeededCount: 1, failedCount: 0 });
  await page.goto('/uploads');
  await expect(page.locator('.file-item-card')).toHaveCount(0);

});


test('applies configured search defaults and theme before JavaScript, including automatic theme changes', async ({ page, daemon, browser }) => {
  await page.emulateMedia({ colorScheme: 'light' });
  await page.goto('/');
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(14, 15, 16)');
  await expect(page.getByRole('button', { name: /^Search result mode:/ })).toHaveAttribute('aria-label', /Album Search/);

  await daemon.command('webui-settings:light:track');
  // Even the unauthenticated shell renders the configured theme without JS.
  const context = await browser.newContext({ javaScriptEnabled: false, colorScheme: 'dark' });
  try {
    const shell = await context.newPage();
    await shell.goto(new URL('/index.html', page.url()).href);
    await expect(shell.locator('body')).toHaveCSS('background-color', 'rgb(244, 244, 243)');
  } finally { await context.close(); }
  await page.reload();
  await expect(page.getByRole('button', { name: /^Search result mode:/ })).toHaveAttribute('aria-label', /Track Search/);
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(244, 244, 243)');

  await daemon.command('webui-settings:auto:album-groups');
  await page.goto('/system');
  await expect(page.getByRole('button', { name: /^Search result mode:/ })).toHaveAttribute('aria-label', /Album Groups/);
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(244, 244, 243)');
  await page.emulateMedia({ colorScheme: 'dark' });
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(14, 15, 16)');
});
