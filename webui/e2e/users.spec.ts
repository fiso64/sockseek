import { test, expect, deferInitialDaemonSnapshot } from './fixtures';
import { stat } from 'node:fs/promises';
import type { Schema } from '../src/api/types';

test('renders populated and partial peer profiles without inventing missing facts', async ({ page }) => {
  const profileReads: string[] = [];
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === 'GET' && /^\/api\/users\/[^/]+\/profile$/.test(path)) profileReads.push(path);
  });
  const browseRequests: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/user-browses')) browseRequests.push(request.url());
  });
  const navigation = page.getByRole('navigation', { name: 'Primary navigation' });
  const input = page.locator('#global-user-search');
  await page.goto('/jobs');
  await navigation.getByRole('button', { name: 'Users', exact: true }).click();
  await expect(input).toHaveAttribute('aria-label', 'Search users');
  await input.fill('fixture-peer');
  await navigation.getByRole('button', { name: 'Users', exact: true }).click();
  await expect(input).toHaveAttribute('aria-label', 'Search users');
  await expect(input).toHaveValue('fixture-peer');
  await navigation.getByRole('button', { name: 'Shares', exact: true }).click();
  await expect(input).toHaveAttribute('aria-label', 'Search user shares');
  await expect(navigation.getByRole('button', { name: 'Shares', exact: true })).toHaveAttribute('aria-current', 'page');
  await navigation.getByRole('button', { name: 'Users', exact: true }).click();
  expect(profileReads).toEqual([]);
  expect(browseRequests).toEqual([]);
  await input.press('Enter');
  const card = page.locator('.user-profile-card');
  await expect(card.getByRole('heading', { name: 'fixture-peer' })).toBeVisible();
  await navigation.getByRole('button', { name: 'Users', exact: true }).click();
  await expect(input).toHaveAttribute('aria-label', 'Search users');
  await expect(card).toBeVisible();
  await expect(page).toHaveURL(/\/users\/fixture-peer$/);
  expect(profileReads).toEqual(['/api/users/fixture-peer/profile']);
  expect(browseRequests).toEqual([]);

  await expect(card.locator('.user-profile-description')).toContainText('A small shared library.');
  await expect(card.locator('.user-profile-description')).toHaveCSS('white-space', 'pre-wrap');
  expect(await card.locator('.user-profile-description').textContent()).toContain('library.\n\nMore');
  await expect(card.locator('.user-profile-description a')).toHaveAttribute('href', 'https://example.org/fixture');
  await expect(card.locator('.user-profile-name-line .user-capacity-inline')).toContainText('Free slot');
  await expect(card.locator('.user-capacity-inline')).toContainText('slots');
  await expect(card.locator('.user-capacity-inline')).toContainText('queued');
  await expect(card.locator('.user-profile-stats > div').nth(0).locator('strong')).toHaveText('5');
  await expect(card.locator('.user-profile-stats > div').nth(1).locator('strong')).toHaveText('3');
  await expect(card.locator('.user-profile-stats > div').nth(2).locator('strong')).toHaveText('8.45 MB/s');
  await expect(card.locator('.user-profile-stats > div').nth(3).locator('strong')).toHaveText('48,231');
  const picture = card.getByRole('button', { name: "View fixture-peer's profile picture" });
  await expect(picture).toBeVisible();
  await expect(picture.locator('img')).toHaveCSS('object-fit', 'contain');
  await expect(picture).toHaveCSS('width', '190px');
  await expect.poll(() => picture.locator('img').evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
  await picture.click();
  await expect(page.getByRole('dialog', { name: 'fixture-peer profile picture' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'fixture-peer profile picture' })).toHaveCount(0);

  await page.locator('.user-view-tabs').getByRole('button', { name: 'Shares', exact: true }).click();
  await expect(navigation.getByRole('button', { name: 'Shares', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page).toHaveURL(/\/shares\/fixture-peer$/);
  await expect(card).toHaveCount(0);
  await page.locator('.user-view-tabs').getByRole('button', { name: 'User', exact: true }).click();
  await expect(navigation.getByRole('button', { name: 'Users', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(card).toBeVisible();

  // Actual peer-section failures, not fabricated HTTP DTOs. A successful zero
  // must remain zero/false, whereas an omitted unknown must not become either.
  const infoResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/users/info-unavailable-peer/profile');
  await page.goto('/users/info-unavailable-peer');
  const info = await (await infoResponse).json();
  expect(info).not.toHaveProperty('hasFreeUploadSlot');
  expect(info.statistics.state).toBe('available');
  await expect(card.locator('.slot-state')).toHaveText('Slot unknown');
  await expect(card.locator('.user-capacity-inline strong')).toHaveText(['—', '—']);
  await expect(card.locator('.user-profile-stats strong')).toHaveText(['0', '0', '—', '0']);
  await expect(card.getByLabel('Profile section availability')).toContainText('Info unavailable');

  const statisticsResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/users/statistics-unavailable-peer/profile');
  await page.goto('/users/statistics-unavailable-peer');
  const statistics = await (await statisticsResponse).json();
  expect(statistics).not.toHaveProperty('sharedFileCount');
  expect(statistics.info.state).toBe('available');
  await expect(card.locator('.slot-state')).toHaveText('No free slot');
  await expect(card.locator('.user-capacity-inline strong')).toHaveText(['0', '0']);
  await expect(card.locator('.user-profile-stats strong')).toHaveText(['—', '—', '—', '—']);
  await expect(card.getByLabel('Profile section availability')).toContainText('Statistics unavailable');
  expect(profileReads).toEqual([
    '/api/users/fixture-peer/profile', '/api/users/info-unavailable-peer/profile',
    '/api/users/statistics-unavailable-peer/profile',
  ]);
  await page.setViewportSize({ width: 390, height: 900 });
  await page.emulateMedia({ colorScheme: 'dark' });
});

test('filters shared files through the daemon and restores the tree when cleared', async ({ page, daemon }) => {
  await page.clock.install();
  let measuringLiveBrowse = false;
  const lifecycleReads: string[] = [];
  const browseSnapshots: string[] = [];
  const profileReads: string[] = [];
  const filterQueries: string[] = [];
  page.on('request', request => {
    if (request.method() === 'GET' && new URL(request.url()).pathname.endsWith('/search'))
      filterQueries.push(new URL(request.url()).searchParams.get('query')!);
    if (request.method() === 'GET' && /^\/api\/users\/[^/]+\/profile$/.test(new URL(request.url()).pathname))
      profileReads.push(request.url());
    if (request.method() === 'GET' && /^\/api\/user-browses\/[^/]+\/snapshot$/.test(new URL(request.url()).pathname))
      browseSnapshots.push(request.url());
    if (measuringLiveBrowse && request.method() === 'GET'
      && /^\/api\/user-browses\/[^/]+$/.test(new URL(request.url()).pathname)) lifecycleReads.push(request.url());
  });
  await daemon.command('hold-directory-browse');
  try {
    await page.goto('/shares/fixture-peer');
    await expect(page.getByLabel('Connection status').locator('strong')).toContainText('Connected');
    await expect(page.locator('.share-tree-row.file')).toHaveCount(0);
    measuringLiveBrowse = true;
    await page.clock.fastForward(5_000);
    expect(lifecycleReads).toEqual([]);
    expect(profileReads).toEqual([]);
  } finally {
    await daemon.command('release-directory-browse');
  }
  await expect(page.getByText('Selected.txt', { exact: true })).toBeVisible();
  expect(lifecycleReads).toEqual([]);
  expect(browseSnapshots).toHaveLength(1);
  const navigation = page.getByRole('navigation', { name: 'Primary navigation' });
  await navigation.getByRole('button', { name: 'Shares', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Search user shares', exact: true })).toBeVisible();
  await expect(page).toHaveURL(/\/shares\/fixture-peer$/);
  await expect(page.getByText('Selected.txt', { exact: true })).toBeVisible();
  expect(profileReads).toEqual([]);
  expect(browseSnapshots).toHaveLength(1);
  expect(lifecycleReads).toEqual([]);
  const tree = page.locator('.share-tree');
  await expect(tree.locator('.share-tree-name')).toHaveText(['Fixture', 'Selected.txt', 'Unselected.txt', 'Nested']);
  await page.getByRole('button', { name: 'Expand Nested', exact: true }).click();
  await expect(tree.locator('.share-tree-name')).toHaveText(['Fixture', 'Selected.txt', 'Unselected.txt', 'Nested', 'Nested.txt', 'Deep']);
  await page.getByRole('button', { name: 'Collapse Nested', exact: true }).click();
  await expect(page.getByText('Nested.txt', { exact: true })).toHaveCount(0);
  const filter = page.getByRole('textbox', { name: 'Filter user shares', exact: true });
  // Rapid edits should coalesce, then reach the daemon promptly after typing.
  // Freeze time so a slow debounce cannot pass through assertion backoff.
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1_000));
  await filter.fill('Uns');
  await page.clock.runFor(60);
  await filter.fill('Unse');
  await page.clock.runFor(60);
  await filter.fill('Unselected');
  await page.clock.runFor(150);
  await expect(page.locator('.shares-filter-count')).toHaveText('1 matching file');
  expect(filterQueries).toEqual(['Unselected']);
  await filter.fill('');
  await page.clock.resume();
  const filterRoute = '**/api/user-browses/*/search?**';
  await page.route(filterRoute, (request) => request.abort());
  await filter.fill('Unselected');
  await expect(page.getByRole('alert')).toContainText('Share filter failed');
  await expect(page.locator('.shares-filter-count')).toHaveCount(0);
  await expect(page.getByText('No matching shared files', { exact: true })).toHaveCount(0);
  await page.unroute(filterRoute);
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.locator('.shares-filter-count')).toHaveText('1 matching file');
  await expect(page.getByText('Unselected.txt', { exact: true })).toBeVisible();
  await expect(page.getByText('Selected.txt', { exact: true })).toHaveCount(0);
  const folderRow = page.locator('.share-tree-row.folder').filter({ has: page.getByText('Fixture', { exact: true }) });
  await expect(folderRow.getByRole('button', { name: 'Collapse Fixture', exact: true })).toBeDisabled();
  await expect(folderRow.getByRole('button')).toHaveAttribute('aria-expanded', 'true');
  await expect(folderRow.locator('.share-tree-folder-meta')).toHaveText('5 files');
  await expect(folderRow.locator('.share-tree-size')).toHaveText('640 B');
  const fileRow = page.locator('.share-tree-row.file');
  const folderIndent = await folderRow.evaluate((element) => getComputedStyle(element).paddingLeft);
  const fileIndent = await fileRow.evaluate((element) => getComputedStyle(element).paddingLeft);
  expect(parseFloat(fileIndent)).toBeGreaterThan(parseFloat(folderIndent));
  await page.locator('.share-tree-row.file').getByRole('checkbox').check();
  await filter.fill('NoMatchingFilename');
  await expect(page.locator('.shares-filter-count')).toHaveText('0 matching files');
  await expect(page.getByText('No matching shared files', { exact: true })).toBeVisible();
  await expect(page.getByText('Unselected.txt', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  await filter.fill('');
  await expect(page.getByText('Selected.txt', { exact: true })).toBeVisible();
  await expect(page.getByText('Unselected.txt', { exact: true })).toBeVisible();
  await expect(page.locator('.shares-filter-count')).toHaveCount(0);
  await expect(page.locator('.share-tree-row.file').filter({ has: page.getByText('Unselected.txt', { exact: true }) }).getByRole('checkbox')).toBeChecked();
  await expect(tree.locator('.share-tree-name')).toHaveText(['Fixture', 'Selected.txt', 'Unselected.txt', 'Nested']);
  await expect(page.getByRole('checkbox', { name: 'Select Fixture', exact: true })).toHaveJSProperty('indeterminate', true);
  expect(profileReads).toEqual([]);
  await page.getByRole('button', { name: 'User', exact: true }).click();
  await expect(page.locator('.user-profile-description')).toContainText('A small shared library.');
  expect(profileReads).toHaveLength(1);
  await navigation.getByRole('button', { name: 'Shares', exact: true }).click();
  await expect(tree.locator('.share-tree-name')).toHaveText(['Fixture', 'Selected.txt', 'Unselected.txt', 'Nested']);
  await expect(page.locator('.share-tree-row.file').filter({ has: page.getByText('Unselected.txt', { exact: true }) }).getByRole('checkbox')).toBeChecked();
  expect(profileReads).toHaveLength(1);
  expect(browseSnapshots).toHaveLength(1);
});

test('filters Unicode share names without changing the selected download identity', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto('/shares/unicode-peer');
  await expect(page.locator('.share-tree-row.file')).toHaveCount(4);
  const filter = page.getByRole('textbox', { name: 'Filter user shares', exact: true });
  async function applyFilter(query: string) {
    const completed = page.waitForResponse(response => response.request().method() === 'GET'
      && new URL(response.url()).pathname.endsWith('/search')
      && new URL(response.url()).searchParams.get('query') === query);
    await filter.fill(query);
    const response = await completed;
    expect(response.ok(), await response.text()).toBe(true);
    await response.finished();
  }
  await applyFilter('აbc');
  await expect(page.locator('.shares-filter-count')).toHaveText('2 matching files');
  await expect(page.locator('.share-tree-row.file .share-tree-name')).toHaveText(['აbc.txt', 'Აbc.txt']);
  await page.locator('.share-tree-row.file').filter({ has: page.getByText('Აbc.txt', { exact: true }) })
    .getByRole('checkbox').check();
  await applyFilter('Empty');
  await expect(page.locator('.shares-filter-count')).toHaveText('0 matching files');
  await expect(page.locator('.share-tree-row.file')).toHaveCount(0);
  const empty = page.locator('.share-tree-row.folder').filter({ has: page.getByText('Empty', { exact: true }) });
  await expect(empty).toContainText('0 files');
  await applyFilter('ſound');
  await expect(page.locator('.shares-filter-count')).toHaveText('1 matching file');
  await expect(page.locator('.share-tree-row.file .share-tree-name')).toHaveText(['ſound.txt']);
  const tiedDirectory = page.locator('.share-tree-row.folder')
    .filter({ has: page.getByText('ſound.txt', { exact: true }) });
  await expect(tiedDirectory).toContainText('0 files');
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  await applyFilter('sound');
  await expect(page.locator('.shares-filter-count')).toHaveText('1 matching file');
  await expect(page.locator('.share-tree-row.file .share-tree-name')).toHaveText(['sound.txt']);
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Download 1', exact: true }).click();
  await expect(page.locator('.mutation-status')).toContainText('1 download requested');
  await page.goto('/downloads');
  const file = page.locator('.file-item-card');
  await expect(file).toHaveCount(1);
  await expect(file.locator('.transfer-state')).toHaveText('Complete');
  await expect(file).toContainText('Აbc.txt');
  const response = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
  expect(response.ok(), await response.text()).toBe(true);
  const transfers = (await response.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(transfers).toHaveLength(1);
  expect(transfers[0]).toMatchObject({
    identity: { username: 'unicode-peer', remotePath: 'Library\\Აbc.txt' }, status: { terminalOutcome: 'Succeeded' },
  });
  expect((await stat(transfers[0]!.status.localPath!)).size).toBe(128);
});

test('excludes an unloaded subfolder and restores a deeper branch with a file exception', async ({ page }) => {
  await page.route('**/api/user-browses/*/directories/*/files?*', route => {
    const url = new URL(route.request().url());
    url.searchParams.set('limit', '1');
    return route.continue({ url: url.toString() });
  });
  await page.goto('/shares/fixture-peer');
  await expect(page.getByText('Selected.txt', { exact: true })).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('.share-tree-row').first()).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(page.locator('.share-tree-row.keyboard-current')).toHaveCount(0);
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('.share-tree-row').first()).toBeFocused();
  await page.getByRole('heading', { name: 'User browser' }).click();
  await expect(page.locator('.share-tree-row.keyboard-current')).toHaveCount(0);
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('.share-tree-row').first()).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('.share-tree-row.file').first()).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('.share-tree-row.folder').filter({ hasText: 'Nested' })).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('.share-tree-row.file').filter({ hasText: 'Unselected.txt' })).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('Space');
  await expect(page.getByRole('checkbox', { name: 'Select Fixture', exact: true })).toBeChecked();
  const nested = page.locator('.share-tree-row.folder').filter({ has: page.getByText('Nested', { exact: true }) }).getByRole('checkbox');
  await nested.uncheck();
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  await page.locator('.share-tree-row.folder').filter({ has: page.getByText('Nested', { exact: true }) }).focus();
  await page.keyboard.press('ArrowRight');
  const deep = page.locator('.share-tree-row.folder').filter({ has: page.getByText('Deep', { exact: true }) }).getByRole('checkbox');
  await deep.check();
  await expect(page.getByRole('button', { name: 'Download 4', exact: true })).toBeEnabled();
  await expect(nested).toHaveJSProperty('indeterminate', true);
  await page.getByRole('button', { name: 'Expand Deep', exact: true }).click();
  await page.locator('.share-tree-row.file').filter({ has: page.getByText('DeepOne.txt', { exact: true }) }).getByRole('checkbox').uncheck();
  await page.locator('.share-tree-row.file').filter({ has: page.getByText('DeepOne.txt', { exact: true }) }).getByRole('checkbox').focus();
  await expect(page.getByRole('button', { name: 'Download 3', exact: true })).toBeEnabled();
  await page.keyboard.press('Enter');
  await expect(page.getByText('3 downloads requested', { exact: true })).toHaveCount(0);
  await page.keyboard.press('Control+Enter');
  await expect(page.getByText('3 downloads requested', { exact: true })).toBeVisible();
  await page.goto('/downloads');
  await expect(page.locator('.folder-item-card')).toHaveCount(1);
  await expect(page.locator('.folder-file-row')).toHaveCount(3);
  for (const path of ['Selected.txt', 'Unselected.txt', 'Nested/Deep/DeepTwo.txt']) {
    await expect(page.locator('.folder-file-row').filter({ has: page.getByText(path, { exact: true }) })).toBeVisible();
  }
  await expect(page.locator('.folder-file-row').filter({ hasText: 'Nested.txt' })).toHaveCount(0);
  await expect(page.locator('.folder-file-row').filter({ hasText: 'DeepOne.txt' })).toHaveCount(0);
});

test('retries a failed profile request and opens a conversation with that exact user', async ({ page }) => {
  const username = 'Profile Peer';
  const route = '**/api/users/*/profile?**';
  const releaseSnapshot = await deferInitialDaemonSnapshot(page);
  try {
    await page.route(route, (request) => request.abort());
    await page.goto(`/users/${encodeURIComponent(username)}`);
    await expect(page.getByRole('alert')).toContainText('Profile unavailable');
    await page.unroute(route);
    await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(page.getByRole('heading', { name: username, exact: true })).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
  } finally { releaseSnapshot(); }
  await expect(page.getByLabel('Connection status')).toContainText('Connected');
  await page.getByRole('button', { name: 'Message', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveAttribute('placeholder', `Message ${username}…`);
  await expect(page).toHaveURL(new RegExp(`/chat/users/${encodeURIComponent(username)}$`));
});
