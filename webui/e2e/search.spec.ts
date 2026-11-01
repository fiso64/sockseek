import { test, expect } from './fixtures';
import type { Page, Request, WebSocketRoute } from '@playwright/test';
import { readFile, readdir, stat } from 'node:fs/promises';
import { basename, dirname, join, sep } from 'node:path';
import { randomUUID } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import type { Schema } from '../src/api/types';

async function chooseFileSearch(page: Page): Promise<void> {
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'File Search', exact: true }).click();
}

async function sortWithoutCreatingView(page: Page, value: string, order: Schema<'ServerSearchViewSortOrder'>) {
  let creates = 0;
  const observe = (request: Request) => {
    if (request.method() === 'POST' && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(request.url()).pathname)) creates++;
  };
  page.on('request', observe);
  try {
    const sorted = page.waitForResponse(response => {
      const url = new URL(response.url());
      return response.request().method() === 'GET' && /\/api\/search-views\/[^/]+\/(files|directories)$/.test(url.pathname)
        && url.searchParams.get('sortOrder') === order;
    });
    await page.getByLabel('Sort', { exact: true }).selectOption(value);
    const response = await sorted;
    expect(response.ok(), await response.text()).toBe(true);
    await expect(page.locator('.result-refinement-status')).toHaveCount(0);
    expect(creates, 'Ordering must read existing projected facts.').toBe(0);
    return response;
  } finally { page.off('request', observe); }
}

  test('downloads bare filenames without browsing an invented directory', async ({ page, daemon }) => {
    await daemon.command('bare-filename-search');
    await page.goto('/jobs');
    await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
    await page.getByRole('menuitemradio', { name: 'File Search', exact: true }).click();
    await page.locator('#global-search-simple').fill('Bare');
    const creation = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname));
    await page.locator('#global-search-simple').press('Enter');
    const view = await (await creation).json() as Schema<'SearchViewSummaryDto'>;

      const folder = page.locator('.folder-item-card').filter({ hasText: 'Files without a folder' }).first();
      await expect(folder.locator('.folder-file-row')).toHaveCount(2);
      await expect(page.locator('.search-results-summary')).toContainText('4 groups');
      await expect(folder.getByRole('button', { name: 'Load all files in this folder', exact: true })).toHaveCount(0);
      const summaryResponse = await page.request.get(`/api/search-views/${view.viewId}`);
      const summary = await summaryResponse.json() as Schema<'SearchViewSummaryDto'>;
      const pageResponse = await page.request.get(`/api/search-views/${view.viewId}/directories?revision=${summary.revision}&limit=100`);
      const directories = await pageResponse.json() as Schema<'SearchViewDirectoryPageDto'>;
      const bare = directories.items.find(item => item.ref.folderPath === '')!;
      const retrieval = await page.request.post(`/api/search-views/${view.viewId}/directories/retrieve`, { data: {
        revision: summary.revision, directory: bare.ref, idempotencyKey: randomUUID(),
      } satisfies Schema<'RetrieveSearchViewDirectoryRequestDto'> });
      expect(retrieval.status(), await retrieval.text()).toBe(400);
      await folder.getByRole('checkbox', { name: 'Select all files in Files without a folder', exact: true }).check();
      const settings = page.waitForResponse(response => response.request().method() === 'POST'
        && new URL(response.url()).pathname.endsWith('/selection-settings'));
      await page.getByRole('button', { name: 'Download options', exact: true }).click();
      expect((await settings).ok()).toBe(true);

    const commit = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
      && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
    await page.getByRole('button', { name: 'Download 1', exact: true }).click();
    const receipt = await (await commit).json() as Schema<'CommitSearchViewSelectionResponseDto'>;
    expect(Number(receipt.submittedCount)).toBe(1);
    expect(Number(receipt.rejectedCount)).toBe(0);
    await page.goto('/downloads');
    const file = page.locator('.file-item-card').filter({ hasText: 'Artist - Bare.flac' });
    await expect(file).toHaveCount(1);
    await expect(file.locator('.transfer-state')).toHaveText('Complete');
    await expect(page.locator('.folder-item-card')).toHaveCount(0);
    const response = await page.request.get('/api/transfers?direction=download&archived=false');
    expect(response.ok(), await response.text()).toBe(true);
    const transfers = (await response.json() as Schema<'TransferTimelinePageDto'>).items;
    expect(transfers).toHaveLength(1);
    expect(transfers[0]!.remotePath).toBe('Artist - Bare.flac');
    const output = transfers[0]!.localPath!;
    expect(basename(dirname(output))).toBe('downloads');
    expect((await stat(output)).size).toBe(128);
    await daemon.command('report-peer-browses');
    expect(daemon.logs().filter(line => line.startsWith('E2E_METRIC peer-browses ')).at(-1)).toBe('E2E_METRIC peer-browses count=0');
    await page.reload();
    await expect(file.locator('.transfer-state')).toHaveText('Complete');

      const jobsResponse = await page.request.get(`/api/jobs?submissionId=${receipt.submissionId}&includeAll=true&limit=100`);
      expect(jobsResponse.ok()).toBe(true);
      const jobs = await jobsResponse.json() as Schema<'JobSummaryDto'>[];
      const directory = jobs.find(job => job.kind === 'remote-directory')!;
      expect(directory.itemName).toBe('Selected files');
      await page.goto(`/jobs/${directory.jobId}`);
      await expect(page.locator('.folder-transfer-path')).toHaveText('Selected files');
      await expect(page.locator('.folder-file-row')).toHaveCount(1);
      await page.reload();
      await expect(page.locator('.folder-transfer-path')).toHaveText('Selected files');

  });
test('pauses hidden search reads and catches up with the selection intact on return', async ({ page, daemon }) => {
  await daemon.command('staged-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  const created = page.waitForResponse(response => response.request().method() === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname));
  await page.locator('#global-search-simple').fill('song');
  await page.locator('#global-search-simple').press('Enter');
  const original = await (await created).json() as Schema<'SearchViewSummaryDto'>;
  await expect(page.locator('.folder-file-row')).toHaveCount(1);
  const selected = page.locator('.folder-file-row').getByRole('checkbox');
  await selected.check();
  const requests: string[] = [];
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith(`/api/search-views/${original.viewId}`)) requests.push(path);
  });
  await page.getByRole('button', { name: 'Dashboard', exact: true }).click();
  await expect(page).toHaveURL(/\/dashboard$/);
  requests.length = 0;
  await daemon.command('next-search-batch');
  // Another reader advances its own projection. Raw collection continues while
  // this browser is away, without asking it to refresh the original View.
  const response = await page.request.post(`/api/jobs/${original.sourceJobId}/search-views`, {
    data: { kind: 'Files', includeFullResults: true },
  });
  expect(response.status()).toBe(202);
  let other = await response.json() as Schema<'SearchViewSummaryDto'>;
  while (Number(other.counters.publicFileCount) < 2) {
    const update = await page.request.get(`/api/search-views/${other.viewId}/updates?afterRevision=${other.revision}&waitForChange=true`);
    expect(update.ok()).toBe(true);
    other = (await update.json() as Schema<'SearchViewUpdateDto'>).summary;
  }
  expect(requests).toEqual([]);
  await page.getByRole('button', { name: 'Jobs', exact: true }).click();
  await expect(page.locator('.folder-file-row')).toHaveCount(2);
  await expect(page.locator('.folder-file-row').filter({ hasText: 'Fixture song.txt' }).getByRole('checkbox')).toBeChecked();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeVisible();
  await daemon.command('finish-search');
});

test.describe('generic grouping', () => {
  test('merges live nested File Search hits without broadening an earlier folder selection', async ({ page, daemon }) => {
    let searchRequests = 0, commits = 0;
    page.on('request', request => {
      if (request.method() !== 'POST') return;
      const path = new URL(request.url()).pathname;
      if (path === '/api/jobs/search') searchRequests++;
      if (/\/api\/search-views\/[^/]+\/commit$/.test(path)) commits++;
    });
    await daemon.command('staged-search');
    await daemon.command('nested-generic-search');
    await page.goto('/jobs');
    await expect(page.locator('#global-search-simple')).toHaveAttribute('placeholder', 'Search albums…');
    await expect(page.getByRole('button', { name: /Search result mode: Album Search/ })).toBeVisible();
    await chooseFileSearch(page);
    await page.locator('#global-search-simple').fill('Track');
    await page.locator('#global-search-simple').press('Enter');
    await expect(page.locator('.folder-item-card')).toHaveCount(2);
    await page.getByRole('checkbox', { name: 'Select all files in B', exact: true }).check();
    await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeVisible();
    await daemon.command('next-search-batch');
    await expect(page.locator('.folder-item-card')).toHaveCount(1);
    await expect(page.locator('.folder-file-row')).toHaveCount(3);
    await expect(page.getByText('Selection from earlier results')).toBeVisible();
    await expect(page.getByRole('checkbox', { name: 'Select all files in A', exact: true })).toHaveCount(0);
    const commit = page.waitForRequest(request => request.method() === 'POST'
      && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(request.url()).pathname));
    await page.getByRole('button', { name: 'Download 1', exact: true }).click();
    const body = (await commit).postDataJSON();
    expect(body.selection.mode).toBe('Only');
    expect(body.selection.refs).toHaveLength(1);
    await expect(page.locator('.mutation-status')).toContainText('1 download requested');
    await daemon.command('finish-search');
    await page.getByRole('button', { name: 'Clear selection' }).click();
    await expect(page.getByText('Selection from earlier results')).toHaveCount(0);
    await page.getByRole('checkbox', { name: 'Select all files in A', exact: true }).check();
    await expect(page.getByRole('button', { name: 'Download 3', exact: true })).toBeVisible();

    // A filtered projection can split A into B/C. An excluded ancestor must
    // remain excluded rather than turning into an unrelated stale ref.
    await page.getByRole('button', { name: 'Select all', exact: true }).click();
    await page.getByRole('checkbox', { name: 'Select all files in A', exact: true }).uncheck();
    const filter = page.getByRole('textbox', { name: 'Filter search results' });
    await filter.fill('one');
    await expect(page.locator('.folder-item-card')).toHaveCount(1);
    await expect(page.getByText('Selection needs review', { exact: true })).toBeVisible();
    await expect(page.getByRole('checkbox', { name: 'Select all files in B', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Download [0-9]+$/ })).toHaveCount(0);
    await filter.fill('');
    await expect(page.getByRole('checkbox', { name: 'Select all files in A', exact: true })).not.toBeChecked();
    await expect(page.getByText('Selection needs review', { exact: true })).toHaveCount(0);

    await page.getByRole('button', { name: 'Filtering', exact: true }).click();
    const panel = page.getByRole('region', { name: 'Result search configuration' });
    await panel.getByRole('button', { name: 'TXT', exact: true }).click();
    await page.keyboard.press('Escape');
    await expect(page.locator('.folder-item-card')).toHaveCount(2);
    await expect(page.getByText('Selection needs review', { exact: true })).toBeVisible();
    await expect(page.locator('.folder-item-card').getByRole('checkbox')).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Download [0-9]+$/ })).toHaveCount(0);
    expect(commits).toBe(1);
    await page.setViewportSize({ width: 390, height: 900 });
    const notice = page.locator('.resource-state-notice').filter({ hasText: 'Selection needs review' });
    await expect(notice.getByRole('button', { name: 'Clear selection', exact: true })).toBeVisible();
    const bounds = await notice.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(390);
    await page.getByRole('button', { name: 'Clear selection', exact: true }).click();
    await expect(page.getByText('Selection needs review', { exact: true })).toHaveCount(0);
    await page.getByRole('checkbox', { name: 'Select all files in C', exact: true }).check();
    await page.getByRole('button', { name: 'Download 1', exact: true }).click();
    await expect(page.locator('.mutation-status')).toContainText('1 download requested');
    expect(searchRequests).toBe(1);
    expect(commits).toBe(2);
    await page.goto('/downloads');
    for (const filename of ['Track one.txt', 'Track two.txt']) {
      const folder = page.locator('.folder-item-card').filter({ hasText: filename });
      await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
    }
    const response = await page.request.get('/api/transfers?direction=download&archived=false&limit=500');
    expect(response.ok()).toBe(true);
    const transfers = (await response.json() as Schema<'TransferTimelinePageDto'>).items;
    expect(transfers.map(item => `${item.remotePath}:${item.terminalOutcome}`).sort())
      .toEqual(['A\\B\\Track one.txt:Succeeded', 'A\\C\\Track two.txt:Succeeded']);
    for (const transfer of transfers) {
      expect(transfer.localPath).toBeTruthy();
      expect((await stat(transfer.localPath!)).size).toBe(128);
    }
  });
});

test('keeps case-distinct peers and locked search files visible without downloading locked files', async ({ page, daemon }) => {
  await daemon.command('mixed-visibility-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');

  await expect(page.locator('.search-results-summary')).toContainText('2 files');
  await expect(page.locator('.search-results-summary')).toContainText('3 locked');
  await expect(page.locator('.folder-item-card')).toHaveCount(3);
  await expect(page.getByRole('button', { name: 'Actions for CasePeer', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Actions for casepeer', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Actions for LockedOnly', exact: true })).toBeVisible();

  const counted = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/selection-counts$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Select all', exact: true }).click();
  const counts = await (await counted).json() as Schema<'CountSearchViewSelectionResponseDto'>;
  expect(Number(counts.publicFileCount)).toBe(2);
  expect(Number(counts.lockedFileCount)).toBe(3);
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();

  const committed = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 2', exact: true }).click();
  const commit = await committed;
  expect(commit.request().postDataJSON().selection.mode).toBe('AllExcept');
  const receipt = await commit.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(receipt.submittedCount)).toBe(2);
  await page.goto('/downloads');
  let files: Schema<'TransferHistoryDto'>[] = [];
  await expect.poll(async () => {
    const response = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
    expect(response.ok(), await response.text()).toBe(true);
    const timeline = await response.json() as Schema<'TransferTimelinePageDto'>;
    files = timeline.items.filter(item => item.remotePath?.endsWith('-Public.flac'));
    return files.map(item => `${item.username}:${item.terminalOutcome}:${item.remotePath}`).sort();
  }).toEqual([
    'CasePeer:Succeeded:Fixture\\Album\\CasePeer-Public.flac',
    'casepeer:Succeeded:Fixture\\Album\\casepeer-Public.flac',
  ]);
  expect(files.map(item => item.username).sort()).toEqual(['CasePeer', 'casepeer']);
  expect(files.every(item => item.remotePath?.endsWith('-Public.flac'))).toBe(true);
  expect(new Set(files.map(item => item.localPath)).size).toBe(2);
  await expect(page.locator('.page')).not.toContainText('Locked.flac');
  await expect(page.locator('.page')).not.toContainText('Only-locked.flac');
});

test('keeps previous results usable while a refinement loads and explicitly recovers a failed page', async ({ page, daemon }) => {
  await daemon.command('directory-selection-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-item-card')).toHaveCount(1);
  await page.getByRole('checkbox', { name: 'Select all files in Fixture', exact: true }).check();
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();

  const pageRoute = '**/api/search-views/*/directories?**';
  let release!: () => void;
  const released = new Promise<void>(resolve => { release = resolve; });
  let requested!: () => void;
  const held = new Promise<void>(resolve => { requested = resolve; });
  let entered = false;
  let finish!: () => void;
  const handled = new Promise<void>(resolve => { finish = resolve; });
  await page.route(pageRoute, async route => {
    entered = true;
    requested();
    try { await released; await route.abort('failed'); }
    finally { finish(); }
  });
  try {
    const filter = page.getByRole('textbox', { name: 'Filter search results' });
    await filter.fill('Unselected');
    await held;
    await expect(page.locator('.folder-file-row')).toHaveCount(2);
    await expect(page.locator('.search-results-summary')).toContainText('2 files');
    await expect(page.locator('.resource-state-notice.loading.blocking')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeDisabled();
    release();
    await handled;
    const unavailable = page.getByRole('alert').filter({ hasText: 'Search results could not be updated' });
    await expect(unavailable).toContainText('Previous results are still shown');
    await expect(page.locator('.folder-file-row')).toHaveCount(2);
    await expect(page.locator('.page-search > .search-results-empty')).toHaveCount(0);
    await expect(filter).toHaveValue('Unselected');
    await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeDisabled();
    await page.unroute(pageRoute);
    const replacement = page.waitForResponse(response => response.ok()
      && new URL(response.url()).pathname.endsWith('/directories'));
    await unavailable.getByRole('button', { name: 'Retry' }).click();
    const result = await (await replacement).json() as Schema<'SearchViewDirectoryPageDto'>;
    expect(Number(result.revision.counters.publicFileCount)).toBe(2);
    expect(Number(result.revision.counters.projectedFileCount)).toBe(1);
    await expect(unavailable).toHaveCount(0);
    await expect(page.locator('.folder-file-row')).toHaveCount(1);
    await expect(page.locator('.folder-file-row')).toContainText('Unselected.txt');
    await expect(page.locator('.search-results-summary')).toContainText('2 files');
    await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  } finally {
    release();
    if (entered) await handled;
    await page.unroute(pageRoute);
  }
});

test('applies projection-only sort and live track conditions through an all-except download', async ({ page, daemon }) => {
  await daemon.command('directory-selection-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-item-card')).toHaveCount(1);
  await sortWithoutCreatingView(page, 'name', 'DirectoryNameAscending');
  await page.getByRole('button', { name: 'Filtering', exact: true }).click();
  const panel = page.getByRole('region', { name: 'Result search configuration' });
  await panel.getByRole('button', { name: 'PDF', exact: true }).click();
  await expect(page.locator('.folder-item-card')).toHaveCount(0);
  await page.getByRole('button', { name: 'Remove PDF format' }).click();
  await expect(page.locator('.folder-item-card')).toHaveCount(1);

  await page.keyboard.press('Escape');
  await daemon.command('staged-conditioned-music-search');
  let trackSubmissions = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/jobs/search/tracks')
      trackSubmissions++;
  });
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Track Search', exact: true }).click();
  await page.locator('#global-search-simple').fill('Track');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.file-item-card')).toHaveCount(2);
  await expect(page.locator('.search-results-summary')).not.toContainText('Complete');
  await page.getByRole('button', { name: 'Filtering', exact: true }).click();
  await panel.getByRole('button', { name: 'custom…', exact: true }).click();
  await panel.getByRole('textbox', { name: 'Comma-separated required formats', exact: true }).fill('flac');
  await expect(page.locator('.file-item-card')).toHaveCount(2);
  await panel.getByRole('button', { name: 'Ranking', exact: true }).click();
  const refined = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
    && response.request().postDataJSON()?.refinementSettings?.preferredCond?.minBitrate === 256);
  await panel.getByRole('button', { name: 'custom…', exact: true }).click();
  await panel.getByRole('textbox', { name: 'Comma-separated preferred formats', exact: true }).fill('flac');
  await panel.getByLabel('Min bitrate kbps').fill('256');
  const refinedView = await (await refined).json() as Schema<'SearchViewSummaryDto'>;
  await page.keyboard.press('Escape');
  await expect(page.locator('.file-item-card.preferred')).toHaveCount(1);
  await expect(page.locator('.file-item-card.preferred')).toContainText('01 - Track.flac');
  const summaryResponse = await page.request.get(`/api/search-views/${refinedView.viewId}`);
  expect(summaryResponse.ok(), await summaryResponse.text()).toBe(true);
  const summary = await summaryResponse.json() as Schema<'SearchViewSummaryDto'>;
  const fullResponse = await page.request.get(`/api/search-views/${refinedView.viewId}/files?revision=${summary.revision}&limit=100`);
  expect(fullResponse.ok(), await fullResponse.text()).toBe(true);
  const full = await fullResponse.json() as Schema<'SearchViewFilePageDto'>;
  expect(full.items).toHaveLength(2);
  expect(full.items.find(file => file.remoteFilename.endsWith('.mp3'))?.necessaryConditionsSatisfied).toBe(false);
  expect(full.items.find(file => file.remoteFilename.endsWith('.flac'))?.necessaryConditionsSatisfied).toBe(true);
  const filtered = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
    && response.request().postDataJSON()?.filterText === 'flac');
  await page.getByRole('textbox', { name: 'Filter search results', exact: true }).fill('flac');
  const filteredView = await (await filtered).json() as Schema<'SearchViewSummaryDto'>;
  await expect(page.locator('.file-item-card')).toHaveCount(1);
  await page.getByRole('button', { name: 'Select all', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();

  await daemon.command('next-search-batch');
  await expect(page.locator('.file-item-card')).toHaveCount(3);
  await expect(page.locator('.file-item-card.locked')).toHaveCount(1);
  await expect(page.locator('.file-item-card.locked')).toContainText('03 - Locked - Track.flac');
  await expect(page.locator('.file-item-card.preferred')).toHaveCount(1);
  await expect(page.locator('.search-results-summary')).not.toContainText('Complete');
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  await daemon.command('finish-search');
  await expect(page.locator('.search-results-summary')).toContainText('Complete');
  expect(trackSubmissions).toBe(1);
  const committed = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 2', exact: true }).click();
  const accepted = await committed;
  expect(new URL(accepted.url()).pathname).toBe(`/api/search-views/${filteredView.viewId}/commit`);
  expect(accepted.request().postDataJSON().selection).toEqual({ mode: 'AllExcept', refs: [] });
  const receipt = await accepted.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(receipt.submittedCount)).toBe(2);
  expect(Number(receipt.rejectedCount)).toBe(1);
  expect(receipt.rejectionReasons).toEqual([{ reason: 'locked', count: 1 }]);
  await page.goto('/downloads');
  let files: Schema<'TransferHistoryDto'>[] = [];
  await expect.poll(async () => {
    const response = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
    expect(response.ok(), await response.text()).toBe(true);
    files = (await response.json() as Schema<'TransferTimelinePageDto'>).items;
    return files.map(file => `${file.username}:${file.terminalOutcome}:${file.remotePath}`).sort();
  }).toEqual([
    'fixture-peer:Succeeded:Music\\Artist\\Album\\01 - Track.flac',
    'second-peer:Succeeded:Music\\Artist\\Album\\02 - Track.flac',
  ]);
  for (const file of files) expect((await stat(file.localPath!)).size).toBe(128);
});

test.describe('live album conditions', () => {
  test('refines live album quality without dropping mixed-format members from selected late folders', async ({ page, daemon }) => {
    await daemon.command('staged-conditioned-music-search');
    let searches = 0;
    page.on('request', request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/jobs/search/albums') searches++;
    });
    await page.goto('/jobs');
    await page.locator('#global-search-simple').fill('Track');
    await page.locator('#global-search-simple').press('Enter');
    await expect(page.locator('.folder-item-card')).toHaveCount(1);
    await expect(page.locator('.folder-file-row')).toHaveCount(2);
    await expect(page.locator('.search-results-summary')).not.toContainText('Complete');

    await page.getByRole('button', { name: 'Filtering', exact: true }).click();
    const panel = page.getByRole('region', { name: 'Result search configuration' });
    await panel.getByRole('button', { name: 'custom…', exact: true }).click();
    await panel.getByRole('textbox', { name: 'Comma-separated required formats', exact: true }).fill('flac');
    // Album quality describes folder coverage, unlike Track Search's per-file
    // filtering. A mixed-format folder retains both files until strict coverage
    // requires every track to match.
    await expect(page.locator('.folder-file-row')).toHaveCount(2);
    await panel.getByRole('checkbox', { name: 'Every album track must satisfy quality' }).check();
    await expect(page.locator('.folder-item-card')).toHaveCount(0);
    await panel.getByRole('checkbox', { name: 'Every album track must satisfy quality' }).uncheck();
    await expect(page.locator('.folder-file-row')).toHaveCount(2);
    await panel.getByRole('button', { name: 'Ranking', exact: true }).click();
    const refined = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
      && response.request().postDataJSON()?.refinementSettings?.preferredCond?.minBitrate === 256);
    await panel.getByRole('button', { name: 'custom…', exact: true }).click();
    await panel.getByRole('textbox', { name: 'Comma-separated preferred formats', exact: true }).fill('flac');
    await panel.getByLabel('Min bitrate kbps').fill('256');
    const view = await (await refined).json() as Schema<'SearchViewSummaryDto'>;
    await page.keyboard.press('Escape');
    await expect(page.locator('.result-tier.preferred-tier .folder-item-card')).toHaveCount(1);
    await page.getByRole('button', { name: 'Select all', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();

    const lateSelection = page.waitForResponse(async response => response.ok()
      && response.request().method() === 'POST'
      && /\/api\/search-views\/[^/]+\/selection-counts$/.test(new URL(response.url()).pathname)
      && Number((await response.json()).lockedFileCount) === 2);
    await daemon.command('next-search-batch');
    await expect(page.locator('.folder-item-card')).toHaveCount(2);
    await expect(page.locator('.folder-file-row')).toHaveCount(6);
    await expect(page.locator('.folder-file-row.locked')).toHaveCount(2);
    await expect(page.locator('.folder-item-card').filter({ hasText: '03 - Locked' })).toHaveCount(1);
    await expect(page.locator('.result-tier.preferred-tier .folder-item-card')).toHaveCount(1);
    await expect(page.locator('.result-tier:not(.preferred-tier) .folder-item-card')).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Download 4', exact: true })).toBeEnabled();
    const selected = await (await lateSelection).json() as Schema<'CountSearchViewSelectionResponseDto'>;
    expect(selected).toMatchObject({ publicFileCount: 4, lockedFileCount: 2 });
    const liveResponse = await page.request.get(`/api/search-views/${view.viewId}`);
    expect(liveResponse.ok(), await liveResponse.text()).toBe(true);
    const live = await liveResponse.json() as Schema<'SearchViewSummaryDto'>;
    expect(live.isComplete).toBe(false);
    expect(live.counters).toMatchObject({ publicFileCount: 4, lockedFileCount: 2,
      projectedFileCount: 6, projectedPublicFileCount: 4, projectedLockedFileCount: 2,
      preferredFileCount: 1, otherFileCount: 5, topLevelItemCount: 2 });
    await page.setViewportSize({ width: 390, height: 900 });
    const toolbar = page.getByLabel('Selection actions');
    await expect(toolbar).toContainText('6 selected · 2 locked');
    const bounds = await toolbar.locator('button, .floating-selection-detail').evaluateAll(elements => elements.map(element => {
      const box = element.getBoundingClientRect();
      return { left: box.left, right: box.right, top: box.top, bottom: box.bottom };
    }));
    for (const [index, box] of bounds.entries()) {
      expect(box.left).toBeGreaterThanOrEqual(70);
      expect(box.right).toBeLessThanOrEqual(376);
      for (const other of bounds.slice(index + 1))
        expect(box.right <= other.left || other.right <= box.left || box.bottom <= other.top || other.bottom <= box.top).toBe(true);
    }
    await daemon.command('finish-search');
    await expect(page.locator('.search-results-summary')).toContainText('Complete');
    const completedResponse = await page.request.get(`/api/search-views/${view.viewId}`);
    expect(completedResponse.ok(), await completedResponse.text()).toBe(true);
    const completed = await completedResponse.json() as Schema<'SearchViewSummaryDto'>;
    expect(completed.isComplete).toBe(true);
    expect(completed.counters).toEqual(live.counters);
    expect(searches).toBe(1);

    await page.getByRole('button', { name: 'Download options', exact: true }).click();
    await page.getByRole('region', { name: 'Download options' })
      .getByRole('checkbox', { name: 'Load full album folder before downloading' }).uncheck();
    const committed = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
      && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
    await page.getByRole('button', { name: 'Download 4', exact: true }).click();
    const accepted = await committed;
    expect(accepted.request().postDataJSON().selection).toEqual({ mode: 'AllExcept', refs: [] });
    const receipt = await accepted.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
    expect(Number(receipt.submittedCount)).toBe(2);
    // Album roots are public folders; their locked children are excluded from
    // the materialized plan rather than rejected as separate requested roots.
    expect(Number(receipt.rejectedCount)).toBe(0);
    expect(receipt.rejectionReasons).toEqual([]);
    await page.goto('/downloads');
    await expect(page.locator('.folder-file-row')).toHaveCount(4);
    await expect(page.locator('.folder-item-card .transfer-state')).toHaveText(['Complete', 'Complete']);
    const transfersResponse = await page.request.get('/api/transfers?direction=download&archived=false');
    expect(transfersResponse.ok(), await transfersResponse.text()).toBe(true);
    const files = (await transfersResponse.json() as Schema<'TransferTimelinePageDto'>).items;
    expect(files.map(file => `${file.username}:${file.terminalOutcome}:${file.remotePath}`).sort()).toEqual([
      'fixture-peer:Succeeded:Music\\Artist\\Album\\01 - Track.flac',
      'fixture-peer:Succeeded:Music\\Artist\\Album\\01 - Track.mp3',
      'second-peer:Succeeded:Music\\Artist\\Album\\02 - Track.flac',
      'second-peer:Succeeded:Music\\Artist\\Album\\02 - Track.mp3',
    ]);
    for (const file of files) expect((await stat(file.localPath!)).size).toBe(128);
  });
});

test('preserves download defaults and corrects unsupported naming without losing selection', async ({ page, daemon }, testInfo) => {
  await daemon.command('directory-selection-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-file-row')).toHaveCount(2);
  await page.getByRole('checkbox', { name: 'Select all files in Fixture', exact: true }).check();
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  const options = page.getByRole('region', { name: 'Download options' });
  await expect(options.getByRole('checkbox', { name: 'Skip existing downloads' })).toBeVisible();
  await expect(options.getByRole('checkbox', { name: 'Load full album folder before downloading' })).toHaveCount(0);
  const skip = options.getByRole('checkbox', { name: 'Skip existing downloads' });
  const next = !(await skip.isChecked());
  await skip.setChecked(next);
  await options.getByRole('button', { name: 'Close download options', exact: true }).click();
  const resultUrl = page.url();
  const committed = page.waitForResponse(response => response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 2', exact: true }).click();
  const acceptedDefaults = await committed;
  expect(acceptedDefaults.status(), await acceptedDefaults.text()).toBe(202);
  const body = acceptedDefaults.request().postDataJSON() as Schema<'CommitSearchViewSelectionRequestDto'>;
  expect(body.options).toEqual({ downloadSettings: { skip: { skipExisting: next } } });
  await expect(page.locator('.mutation-status')).toContainText('2 downloads requested');
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Downloads', exact: true }).click();
  await expect(page.locator('.folder-item-card .transfer-state')).toHaveText('Complete');
  const originalResponse = await page.request.get('/api/transfers?direction=download&limit=500');
  expect(originalResponse.ok(), await originalResponse.text()).toBe(true);
  const originals = (await originalResponse.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(originals.map(file => file.remotePath).sort()).toEqual(['Fixture\\Selected.txt', 'Fixture\\Unselected.txt']);
  for (const file of originals) {
    expect(file.terminalOutcome).toBe('Succeeded');
    expect(file.localPath!.endsWith(join('Fixture', basename(file.remotePath!.replaceAll('\\', '/'))))).toBe(true);
    expect((await stat(file.localPath!)).size).toBe(128);
  }

  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Jobs', exact: true }).click();
  await expect(page).toHaveURL(resultUrl);
  const selected = page.getByRole('checkbox', { name: 'Select all files in Fixture', exact: true });
  await expect(selected).toBeChecked();
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  const destination = testInfo.outputPath('renamed-download');
  await options.getByRole('checkbox', { name: 'Skip existing downloads' }).setChecked(!next);
  await options.getByLabel('Output directory', { exact: true }).fill(destination);
  await options.getByLabel('Name format', { exact: true }).fill('{artist}/{filename}');
  const rejected = page.waitForResponse(response => response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 2', exact: true }).click();
  const rejection = await rejected;
  expect(rejection.status(), await rejection.text()).toBe(202);
  const rejectedReceipt = await rejection.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(rejectedReceipt.submittedCount)).toBe(0);
  expect(Number(rejectedReceipt.rejectedCount)).toBe(1);
  expect(rejectedReceipt.rejectionReasons[0]?.message).toContain('{artist}');
  await expect(page.getByRole('alert').filter({ hasText: '{artist}' })).toContainText('not supported for this download type');
  await expect(selected).toBeChecked();
  const viewport = page.viewportSize()!;
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('alert').filter({ hasText: '{artist}' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.setViewportSize(viewport);
  // Two distinct files deliberately map to the same name: placement must retain
  // both, and editing the rejected command must use a new idempotency key.
  const nameFormat = '{peer-username}/{foldername}/named';
  await options.getByLabel('Name format', { exact: true }).fill(nameFormat);
  await options.getByRole('button', { name: 'Close download options', exact: true }).click();
  const corrected = page.waitForResponse(response => response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 2', exact: true }).click();
  const acceptedCorrection = await corrected;
  expect(acceptedCorrection.status(), await acceptedCorrection.text()).toBe(202);
  const correctedReceipt = await acceptedCorrection.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(correctedReceipt.submittedCount)).toBe(1);
  expect(Number(correctedReceipt.rejectedCount)).toBe(0);
  const correctedBody = acceptedCorrection.request().postDataJSON() as Schema<'CommitSearchViewSelectionRequestDto'>;
  expect(correctedBody.idempotencyKey).not.toBe(rejection.request().postDataJSON().idempotencyKey);
  expect(correctedBody.options).toEqual({ outputParentDir: destination, downloadSettings: {
    output: { nameFormat },
  } });
  await expect(page.getByRole('alert').filter({ hasText: '{artist}' })).toHaveCount(0);
  const jobsResponse = await page.request.get(`/api/jobs?submissionId=${correctedReceipt.submissionId}&includeAll=true&limit=100`);
  expect(jobsResponse.ok(), await jobsResponse.text()).toBe(true);
  const directory = (await jobsResponse.json() as Schema<'JobSummaryDto'>[]).find(job => job.kind === 'remote-directory');
  expect(directory).toBeDefined();
  await page.goto(`/jobs/${directory!.jobId}`);
  await expect(page.locator('.folder-item-card .transfer-state')).toHaveText('Complete');
  const manifestResponse = await page.request.get(`/api/jobs/${directory!.jobId}/directory-files?attemptNumber=1&limit=500`);
  expect(manifestResponse.ok(), await manifestResponse.text()).toBe(true);
  const renamed = (await manifestResponse.json() as Schema<'DirectoryManifestPageDto'>).items;
  expect(renamed.map(file => file.target?.filename).sort()).toEqual(originals.map(file => file.remotePath).sort());
  expect(renamed.map(file => file.file?.downloadPath).sort()).toEqual([
    join(destination, 'fixture-peer', 'Fixture', 'named.txt'),
    join(destination, 'fixture-peer', 'Fixture', 'named (2).txt'),
  ].sort());
  for (const file of renamed) {
    expect(file.child?.terminalOutcome).toBe('Succeeded');
    expect((await stat(file.file!.downloadPath!)).size).toBe(128);
  }
  expect((await readdir(join(destination, 'fixture-peer', 'Fixture'))).sort()).toEqual(['named (2).txt', 'named.txt']);
  // The second download copies known payloads locally. It must not fabricate
  // another pair of network transfers just to present its new output paths.
  const allResponse = await page.request.get('/api/transfers?direction=download&limit=500');
  expect(allResponse.ok(), await allResponse.text()).toBe(true);
  const all = (await allResponse.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(all.map(file => file.transferId).sort()).toEqual(originals.map(file => file.transferId).sort());
});

test('keeps a partially selected nested album tree through download and retention', async ({ page, daemon }) => {
  await daemon.command('nested-album-search');
  await page.goto('/jobs');
  await page.locator('#global-search-simple').fill('Geogaddi');
  await page.locator('#global-search-simple').press('Enter');

  const folder = page.locator('.folder-item-card');
  await expect(folder).toHaveCount(1);
  await expect(folder.locator('.folder-file-row')).toHaveCount(5);
  await expect(folder).toContainText(/Artwork[\\/]cover\.jpg/);
  await expect(folder.locator('.folder-file-audio')).toHaveText([
    '16-bit · 44.1 kHz · 944 kbps', '16-bit · 44.1 kHz · 944 kbps',
    '16-bit · 44.1 kHz · 932 kbps', '16-bit · 44.1 kHz · 956 kbps',
  ]);
  await expect(folder.locator('.folder-file-count strong')).toHaveText('5');
  await folder.getByRole('checkbox', { name: 'Select all files in Geogaddi', exact: true }).check();
  await expect(page.getByRole('button', { name: 'Download 5', exact: true })).toBeVisible();
  for (const row of await folder.locator('.folder-file-row').filter({ has: page.locator('.folder-file-audio') }).all())
    await row.getByRole('checkbox').uncheck();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  await page.getByRole('region', { name: 'Download options' }).getByRole('checkbox', {
    name: 'Load full album folder before downloading', exact: true,
  }).uncheck();
  const commit = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 1', exact: true }).click();
  const receipt = await (await commit).json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(receipt.submittedCount)).toBe(1);
  await page.goto('/downloads');
  const downloaded = page.locator('.folder-item-card');
  await expect(downloaded).toHaveCount(1);
  await expect(downloaded.locator('.folder-file-row')).toHaveCount(1);
  await expect(downloaded.locator('.transfer-state').first()).toHaveText('Complete');
  await expect(downloaded.locator('.folder-file-path')).toContainText('Artwork/cover.jpg');
  const jobsResponse = await page.request.get(`/api/jobs?submissionId=${receipt.submissionId}&includeAll=true&limit=100`);
  expect(jobsResponse.ok(), await jobsResponse.text()).toBe(true);
  const jobs = await jobsResponse.json() as Schema<'JobSummaryDto'>[];
  const directory = jobs.find(job => job.kind === 'remote-directory');
  expect(directory).toBeDefined();
  const manifestResponse = await page.request.get(`/api/jobs/${directory!.jobId}/directory-files?attemptNumber=1&limit=500`);
  expect(manifestResponse.ok(), await manifestResponse.text()).toBe(true);
  const manifest = await manifestResponse.json() as Schema<'DirectoryManifestPageDto'>;
  expect(manifest.items).toHaveLength(1);
  const file = manifest.items[0]!;
  expect(file.target?.filename).toBe('Music\\Boards of Canada\\Geogaddi\\Artwork\\cover.jpg');
  expect(file.relativeDirectoryComponents).toEqual(['Artwork']);
  expect(file.child?.terminalOutcome).toBe('Succeeded');
  const output = file.file!.downloadPath!;
  expect(output.endsWith(join('Geogaddi', 'Artwork', 'cover.jpg'))).toBe(true);
  expect((await stat(output)).size).toBe(1_400_000);
  expect((await readdir(dirname(dirname(output)), { recursive: true })).sort())
    .toEqual(['Artwork', join('Artwork', 'cover.jpg')].sort());
  await page.reload();
  await expect(downloaded.locator('.folder-file-row')).toHaveCount(1);
  await expect(downloaded.locator('.folder-file-path')).toContainText('Artwork/cover.jpg');
  await expect(downloaded.locator('.transfer-state').first()).toHaveText('Complete');
});

test('applies the album-folder toggle beside a selected exact file', async ({ page, daemon }) => {
  await daemon.command('aggregate-album-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Search', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture Album');
  await page.locator('#global-search-simple').press('Enter');
  const firstPeer = page.locator('.result-peer-group').filter({ hasText: 'fixture-peer' });
  const secondPeer = page.locator('.result-peer-group').filter({ hasText: 'second-peer' });
  await expect(firstPeer.locator('.folder-file-row')).toHaveCount(2);
  await expect(secondPeer.locator('.folder-file-row')).toHaveCount(2);
  await firstPeer.locator('.folder-file-row').filter({ hasText: 'Intro.flac' })
    .locator('input[type="checkbox"]').check();
  await secondPeer.getByRole('checkbox', { name: 'Select all files in Fixture Album', exact: true }).check();
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  const option = page.getByRole('region', { name: 'Download options' })
    .getByRole('checkbox', { name: 'Load full album folder before downloading' });
  await expect(option).toBeVisible();
  await option.uncheck();
  const committed = page.waitForRequest(request => request.method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(request.url()).pathname));
  await page.getByRole('button', { name: 'Download 3', exact: true }).click();
  expect((await committed).postDataJSON().options?.downloadSettings?.search?.noBrowseFolder).toBe(true);
  await expect(page.locator('.mutation-status')).toContainText('2 downloads requested');
  await page.goto('/downloads');
  const exactFilePeer = page.locator('.result-peer-group').filter({ hasText: 'fixture-peer' });
  const albumPeer = page.locator('.result-peer-group').filter({ hasText: 'second-peer' });
  await expect(exactFilePeer.locator('.file-item-card')).toHaveCount(1);
  await expect(exactFilePeer).toContainText('Intro.flac');
  await expect(albumPeer.locator('.folder-item-card')).toHaveCount(1);
  await expect(albumPeer.locator('.folder-file-row')).toHaveCount(2);
  await expect(albumPeer).not.toContainText('Bonus.flac');
});

test.describe('aggregate failure plans', () => {
for (const scenario of [
  { mode: 'Song Aggregate', command: 'aggregate-failure-search', kind: 'song',
    alpha: 'Fixture Alpha', beta: 'Fixture Beta',
    alphaPath: 'Fixture\\Fixture Alpha.mp3', betaPath: 'Fixture\\Fixture Beta.mp3' },
  { mode: 'Album Aggregate', command: 'aggregate-album-failure-search', kind: 'album',
    alpha: 'Fixture Album Alpha', beta: 'Fixture Album Beta',
    alphaPath: 'Fixture\\Fixture Album Alpha\\01 - Alpha.flac',
    betaPath: 'Fixture\\Fixture Album Beta\\01 - Beta.flac' },
] as const) {
test(`downloads a chosen ${scenario.mode} source and isolates a failed sibling`, async ({ page, daemon }) => {
  await daemon.command(scenario.command);
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: scenario.mode, exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture');
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await page.locator('#global-search-artist').press('Enter');
  const groups = page.locator('.aggregate-results-list > .aggregate-result-group');
  await expect(groups).toHaveCount(scenario.kind === 'song' ? 3 : 2);
  const alpha = groups.filter({ hasText: scenario.alpha });
  const alternatives = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/alternatives'));
  await alpha.locator('.aggregate-options-button').click();
  await alternatives;
  await page.getByRole('dialog').locator('.aggregate-option').filter({ hasText: 'fixture-peer' })
    .getByRole('button', { name: 'Use this option', exact: true }).click();
  if (scenario.kind === 'album') {
    await alpha.getByRole('button', { name: `Deselect ${scenario.alpha}`, exact: true }).click();
    await alpha.locator('.folder-file-row input[type="checkbox"]').first().check();
  }
  const beta = groups.filter({ hasText: scenario.beta });
  await beta.locator('.aggregate-options-button').click();
  await page.getByRole('dialog').locator('.aggregate-option').filter({ hasText: 'second-peer' })
    .getByRole('button', { name: 'Use this option', exact: true }).click();
  if (scenario.kind === 'album') {
    await beta.getByRole('button', { name: `Deselect ${scenario.beta}`, exact: true }).click();
    await beta.locator('.folder-file-row input[type="checkbox"]').first().check();
  }
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  const downloadOptions = page.getByRole('region', { name: 'Download options' });
  await downloadOptions.getByRole('checkbox', { name: 'Skip existing downloads' }).uncheck();
  await downloadOptions.getByRole('button', { name: 'Close download options', exact: true }).click();
  const committed = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 2', exact: true }).click();
  const accepted = await committed;
  expect(Object.keys(accepted.request().postDataJSON().chosenOptions)).toHaveLength(2);
  if (scenario.kind === 'album')
    expect(Object.keys(accepted.request().postDataJSON().albumFileSelections)).toHaveLength(2);
  const receipt = await accepted.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(receipt.submittedCount)).toBe(2);
  expect(Number(receipt.rejectedCount)).toBe(0);
  await page.goto('/downloads');
  let transfers: Schema<'TransferHistoryDto'>[] = [];
  async function readOutcomes(): Promise<string[]> {
    const response = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
    expect(response.ok(), await response.text()).toBe(true);
    transfers = (await response.json() as Schema<'TransferTimelinePageDto'>).items;
    return transfers.map(item => `${item.username}:${item.terminalOutcome}:${item.remotePath}`).sort();
  }
  const failedAlpha = `fixture-peer:Failed:${scenario.alphaPath}`;
  const successfulBeta = `second-peer:Succeeded:${scenario.betaPath}`;
  await expect.poll(readOutcomes).toEqual([failedAlpha, successfulBeta]);
  const cards = page.locator(scenario.kind === 'song' ? '.file-item-card' : '.folder-item-card');
  await expect(cards).toHaveCount(2);
  await expect(cards.filter({ hasText: scenario.alpha })).toContainText('Failed');
  await expect(cards.filter({ hasText: scenario.beta })).toContainText('Complete');
  expect((await stat(transfers.find(item => item.terminalOutcome === 'Succeeded')!.localPath!)).size).toBe(8_000_000);
  const failedPath = transfers.find(item => item.terminalOutcome === 'Failed')!.localPath!;
  await expect(stat(failedPath)).rejects.toMatchObject({ code: 'ENOENT' });
  await page.reload();
  await expect(cards.filter({ hasText: scenario.alpha })).toContainText('Failed');
  await expect(cards.filter({ hasText: scenario.beta })).toContainText('Complete');
  if (scenario.kind === 'album') {
    await expect(cards.locator('.folder-transfer-file-count'))
      .toHaveText([/^\s*1\s*file\s*$/, /^\s*1\s*file\s*$/]);
    await expect(cards.locator('.item-data-state')).toHaveCount(0);
  }

});
}
});

test('keeps an aggregate track choice and all-except plan through live group reordering', async ({ page, daemon }, testInfo) => {
  await daemon.command('staged-rich-aggregate-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Song Aggregate', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture');
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await page.locator('#global-search-artist').press('Enter');

  const groups = page.locator('.aggregate-results-list > .aggregate-result-group');
  await expect(groups).toHaveCount(3);
  await expect(page.locator('.search-results-summary')).toContainText('3 groups');
  await expect(groups.nth(0)).toContainText('Fixture Alpha');
  await expect(groups.nth(1)).toContainText('Fixture Beta');
  await expect(groups.nth(2)).toContainText('Fixture Gamma');
  await expect(groups.locator('.audio-detail')).toHaveText(['320 kbps', '320 kbps', '320 kbps']);
  await expect(groups.locator('.item-length-detail')).toHaveText(['3:30', '3:30', '3:30']);
  await expect(groups.first().locator('.aggregate-options-button')).toContainText('2 options');
  const alpha = groups.filter({ hasText: 'Fixture Alpha' });
  const beta = groups.filter({ hasText: 'Fixture Beta' });
  const alternatives = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/alternatives'));
  await alpha.locator('.aggregate-options-button').click();
  const optionsPage = await (await alternatives).json() as Schema<'SearchViewAggregateTrackOptionPageDto'>;
  const chosen = optionsPage.items.find(item => item.peer.username === 'second-peer')!;
  expect(chosen).toBeTruthy();
  await page.getByRole('dialog').locator('.aggregate-option').filter({ hasText: 'second-peer' })
    .getByRole('button', { name: 'Use this option', exact: true }).click();
  await page.getByRole('button', { name: 'Select all', exact: true }).click();
  await beta.getByRole('button', { name: 'Deselect Fixture Beta', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  const outputDirectory = testInfo.outputPath('live-aggregate-tracks');
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  const options = page.getByRole('region', { name: 'Download options' });
  await options.getByRole('textbox', { name: 'Output directory' }).fill(outputDirectory);
  await options.getByRole('button', { name: 'Close download options', exact: true }).click();

  await daemon.command('next-search-batch');
  await expect(groups.locator('.aggregate-result-identity > strong')).toHaveText([
    'Fixture Alpha', 'Fixture Gamma', 'Fixture Beta', 'Fixture Delta',
  ]);
  await expect(page.locator('.search-results-summary')).toContainText('10 files');
  await expect(alpha.locator('.aggregate-options-button')).toContainText('3 options');
  await expect(alpha.locator('.aggregate-result-source')).toContainText('second-peer');
  await expect(groups.filter({ hasText: 'Fixture Gamma' }).locator('.aggregate-result-source')).toContainText('late-peer');
  await expect(beta.getByRole('button', { name: 'Select Fixture Beta', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download 3', exact: true })).toBeEnabled();
  await daemon.command('finish-search');
  await expect(page.locator('.search-results-summary .search-status-badge')).toHaveText('Complete');
  const committed = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 3', exact: true }).click();
  const response = await committed;
  const request = response.request().postDataJSON() as Schema<'CommitSearchViewSelectionRequestDto'>;
  expect(request.selection.mode).toBe('AllExcept');
  expect(request.selection.refs).toHaveLength(1);
  expect(request.useRepresentativeWhenUnchosen).toBe(true);
  expect(request.chosenOptions).toEqual({ [optionsPage.groupRef]: chosen.ref });
  expect(request.options?.outputParentDir).toBe(outputDirectory);
  const receipt = await response.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(receipt.submittedCount)).toBe(3);
  expect(Number(receipt.rejectedCount)).toBe(0);
  await page.goto('/downloads');
  let transfers: Schema<'TransferHistoryDto'>[] = [];
  await expect.poll(async () => {
    const result = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
    expect(result.ok(), await result.text()).toBe(true);
    transfers = (await result.json() as Schema<'TransferTimelinePageDto'>).items;
    return transfers.map(item => `${item.username}:${item.terminalOutcome}:${item.remotePath}`).sort();
  }).toEqual([
    'late-peer:Succeeded:Fixture\\Fixture Delta.mp3',
    'late-peer:Succeeded:Fixture\\Fixture Gamma.mp3',
    'second-peer:Succeeded:Fixture\\Fixture Alpha.mp3',
  ]);
  for (const transfer of transfers) {
    expect(transfer.localPath!.startsWith(outputDirectory + sep)).toBe(true);
    expect((await stat(transfer.localPath!)).size).toBe(8_000_000);
  }
  const jobsResponse = await page.request.get(
    `/api/jobs?submissionId=${receipt.submissionId}&includeAll=true&archived=false&limit=100`);
  expect(jobsResponse.ok(), await jobsResponse.text()).toBe(true);
  const songs = (await jobsResponse.json() as Schema<'JobSummaryDto'>[]).filter(job => job.kind === 'song');
  expect(songs).toHaveLength(3);
  for (const song of songs) {
    const detailResponse = await page.request.get(`/api/jobs/${song.jobId}`);
    expect(detailResponse.ok(), await detailResponse.text()).toBe(true);
    const detail = await detailResponse.json() as Schema<'JobDetailDto'>;
    expect(detail.payload?.kind).toBe('song');
    if (detail.payload?.kind !== 'song') throw new Error('Expected a selected song download.');
    expect(Number(detail.payload.candidateCount)).toBe(1);
  }
});

test('keeps a chosen aggregate album source, output option and exact file plan through a later live update', async ({ page, daemon }, testInfo) => {
  await daemon.command('staged-aggregate-album-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Aggregate', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture');
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await page.locator('#global-search-artist').press('Enter');

  const alpha = page.locator('.aggregate-results-list > .aggregate-result-group')
    .filter({ hasText: 'Fixture Album Alpha' });
  await expect(alpha.locator('.aggregate-options-button')).toContainText('2 options');
  await alpha.locator('.aggregate-options-button').click();
  const dialog = page.getByRole('dialog', { name: /Fixture Album Alpha.*options/i });
  await dialog.locator('.aggregate-option').filter({ hasText: 'second-peer' })
    .locator('.aggregate-option-card-picker').click();
  await expect(alpha.locator('.aggregate-result-source')).toContainText('second-peer');
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  const outputDirectory = testInfo.outputPath('chosen-album');
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  const options = page.getByRole('region', { name: 'Download options' });
  await options.getByRole('textbox', { name: 'Output directory' }).fill(outputDirectory);
  await options.getByRole('button', { name: 'Close download options', exact: true }).click();

  await daemon.command('next-search-batch');
  await expect(page.locator('.search-results-summary')).toContainText('5 files');
  await expect(alpha.locator('.aggregate-result-source')).toContainText('second-peer');
  await expect(alpha.locator('.folder-file-row')).toHaveCount(1);
  await expect(alpha).not.toContainText('Cover.jpg');
  await daemon.command('finish-search');
  await expect(page.locator('.search-results-summary')).toContainText('Complete');

  const committed = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 1', exact: true }).click();
  const request = (await committed).request().postDataJSON();
  expect(Object.values(request.chosenOptions ?? {})).toHaveLength(1);
  expect(request.options?.outputParentDir).toBe(outputDirectory);
  await page.goto('/downloads');
  await expect(page.locator('.folder-file-row')).toHaveCount(1);
  await expect(page.locator('.folder-item-card')).toContainText('Alpha.flac');
  await expect(page.locator('.folder-item-card')).not.toContainText('Cover.jpg');
  await expect(page.getByRole('button', { name: 'Actions for second-peer', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Actions for fixture-peer', exact: true })).toHaveCount(0);
  let transfer: Schema<'TransferHistoryDto'> | undefined;
  await expect.poll(async () => {
    const response = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
    expect(response.ok(), await response.text()).toBe(true);
    const timeline = await response.json() as Schema<'TransferTimelinePageDto'>;
    transfer = timeline.items.find(item => item.remotePath?.endsWith('Alpha.flac'));
    return transfer?.terminalOutcome;
  }).toBe('Succeeded');
  expect(transfer?.username).toBe('second-peer');
  expect(transfer?.localPath?.startsWith(outputDirectory + sep)).toBe(true);
  expect((await stat(transfer!.localPath!)).size).toBe(8_000_000);
});

test('opens a retained aggregate album from a fresh browser without a result-mode URL', async ({ page, daemon, browser }) => {
  await daemon.command('aggregate-album-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Aggregate', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture Album');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.aggregate-results-list > .aggregate-result-group')).toHaveCount(1);

  const fresh = await browser.newContext({ baseURL: daemon.url });
  try {
    const reopened = await fresh.newPage();
    await reopened.goto('/jobs');
    await reopened.locator('.search-history-open').filter({ hasText: 'Fixture Album' }).click();
    await expect(reopened).toHaveURL(/\?results=album-aggregate$/);
    await expect(reopened.locator('.aggregate-results-list > .aggregate-result-group')).toHaveCount(1);
  } finally {
    await fresh.close();
  }
});

test('invalidates a chosen aggregate album folder without persisting the browser choice', async ({ page, daemon }) => {
  await daemon.command('aggregate-album-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Aggregate', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture Album');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.aggregate-options-button')).toContainText('2 options');
  await page.locator('.aggregate-options-button').click();
  await page.getByRole('dialog', { name: /Fixture Album.*options/i })
    .locator('.aggregate-option').filter({ hasText: 'second-peer' })
    .getByRole('button', { name: 'Use this option', exact: true }).click();
  await expect(page.locator('.aggregate-result-source')).toContainText('second-peer');
  await page.getByRole('button', { name: 'Filtering', exact: true }).click();
  const panel = page.getByRole('region', { name: 'Result search configuration' });
  await panel.getByLabel('Minimum sharers').fill('1');
  await panel.getByLabel('Banned users').fill('second-peer');
  await expect(page.locator('.aggregate-result-group')).toHaveCount(1);
  await expect(page.locator('.aggregate-choice-notice')).toContainText('Chosen folder is no longer in these results');
  await expect(page.locator('.aggregate-result-source')).toContainText('fixture-peer');
  await expect(page.locator('.floating-download-action')).toBeDisabled();
  await page.keyboard.press('Escape');
  await page.locator('.aggregate-choice-notice').getByRole('button', { name: 'Choose another' }).click();
  const refreshed = page.getByRole('dialog', { name: /Fixture Album.*options/i });
  await expect(refreshed.locator('.aggregate-option')).toHaveCount(1);
  await refreshed.getByRole('button', { name: 'Use this option', exact: true }).click();
  await expect(page.locator('.aggregate-choice-notice')).toHaveCount(0);
  await expect(page.locator('.floating-download-action')).toBeEnabled();
});

test('downloads only checked public files from a mixed chosen aggregate album folder', async ({ page, daemon }) => {
  const searchSubmissions: string[] = [];
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/jobs/search/albums')
      searchSubmissions.push(request.url());
  });
  await daemon.command('mixed-aggregate-album-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Aggregate', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture Album');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.search-results-summary')).toContainText('4 files');
  await expect(page.locator('.search-results-summary')).toContainText('2 locked');
  await page.locator('.aggregate-options-button').click();
  const dialog = page.getByRole('dialog', { name: /Fixture Album.*options/i });
  await dialog.locator('.aggregate-option').filter({ hasText: 'second-peer' })
    .locator('.aggregate-option-card-picker').click();
  await expect(dialog).toHaveCount(0);
  const album = page.locator('.aggregate-result-group');
  await expect(album.locator('.folder-file-row')).toHaveCount(3);
  const intro = album.locator('.folder-file-row').filter({ hasText: 'Intro.flac' }).getByRole('checkbox');
  const finale = album.locator('.folder-file-row').filter({ hasText: 'Finale.flac' }).locator('input[type="checkbox"]');
  const lockedRow = album.locator('.folder-file-row').filter({ hasText: 'Locked.flac' });
  const locked = lockedRow.getByRole('checkbox');
  await expect(lockedRow).toHaveClass(/locked/);
  await expect(lockedRow.getByText('Locked', { exact: true })).toBeVisible();
  await expect(locked).toBeChecked();
  await expect(finale).toBeChecked();
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  await expect(page.locator('.floating-selection-detail')).toHaveText('3 selected · 1 locked');
  const allExceptCount = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/selection-counts')
    && Object.values((response.request().postDataJSON() as Schema<'CountSearchViewSelectionRequestDto'>).albumFileSelections ?? {})
      .some(selection => selection.mode === 'AllExcept' && selection.refs.length === 1));
  await finale.uncheck();
  await expect(album).toHaveClass(/partial/);
  const excludedCount = await (await allExceptCount).json() as Schema<'CountSearchViewSelectionResponseDto'>;
  expect(Number(excludedCount.publicFileCount)).toBe(1);
  expect(Number(excludedCount.lockedFileCount)).toBe(1);
  await expect(page.locator('.floating-selection-detail')).toHaveText('2 selected · 1 locked');

  // Build the same exact child set with explicit Only refs, not just an
  // all-except rule. The locked choice counts but must never enter execution.
  await page.locator('.floating-deselect-action').click();
  await expect(intro).not.toBeChecked();
  await expect(locked).not.toBeChecked();
  await intro.check();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  const onlyCount = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/selection-counts')
    && Object.values((response.request().postDataJSON() as Schema<'CountSearchViewSelectionRequestDto'>).albumFileSelections ?? {})
      .some(selection => selection.mode === 'Only' && selection.refs.length === 2));
  await locked.check();
  const includedCount = await (await onlyCount).json() as Schema<'CountSearchViewSelectionResponseDto'>;
  expect(Number(includedCount.publicFileCount)).toBe(1);
  expect(Number(includedCount.lockedFileCount)).toBe(1);
  await expect(page.locator('.floating-selection-detail')).toHaveText('2 selected · 1 locked');
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  const downloadOptions = page.getByRole('region', { name: 'Download options' });
  await expect(downloadOptions.getByRole('checkbox', { name: 'Skip existing downloads' })).toBeVisible();
  await expect(downloadOptions
    .getByRole('checkbox', { name: 'Load full album folder before downloading' })).toHaveCount(0);
  await downloadOptions.getByRole('button', { name: 'Close download options', exact: true }).click();
  const committed = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && new URL(response.url()).pathname.endsWith('/commit'));
  await page.getByRole('button', { name: 'Download 1', exact: true }).click();
  const commit = await committed;
  const expression = commit.request().postDataJSON() as Schema<'CommitSearchViewSelectionRequestDto'>;
  expect(expression.selection.mode).toBe('Only');
  expect(expression.selection.refs).toHaveLength(1);
  expect(Object.values(expression.chosenOptions ?? {})).toHaveLength(1);
  expect(Object.values(expression.albumFileSelections ?? {})).toMatchObject([{ mode: 'Only' }]);
  expect(Object.values(expression.albumFileSelections ?? {})[0]!.refs).toHaveLength(2);
  const receipt = await commit.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(receipt.submittedCount)).toBe(1);
  expect(Number(receipt.rejectedCount)).toBe(0);
  await expect(page.locator('.mutation-status')).toContainText('1 download requested');
  await page.goto('/downloads');
  await expect(page.locator('.folder-item-card')).toHaveCount(1);
  await expect(page.locator('.folder-file-row')).toHaveCount(1);
  await expect(page.locator('.folder-item-card .transfer-state')).toHaveText('Complete');
  await expect(page.locator('.folder-item-card')).toContainText('Intro.flac');
  await expect(page.locator('.folder-item-card')).not.toContainText('Finale.flac');
  await expect(page.locator('.folder-item-card')).not.toContainText('Bonus.flac');
  await expect(page.locator('.folder-item-card')).not.toContainText('Locked.flac');
  const transfers = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
  expect(transfers.ok(), await transfers.text()).toBe(true);
  const timeline = await transfers.json() as Schema<'TransferTimelinePageDto'>;
  expect(timeline.items).toHaveLength(1);
  expect(timeline.items[0]).toMatchObject({
    username: 'second-peer', remotePath: 'Second\\Fixture Album\\01 - Intro.flac',
    terminalOutcome: 'Succeeded', totalBytes: 128, transferredBytes: 128,
  });
  expect((await stat(timeline.items[0]!.localPath!)).size).toBe(128);
  expect(searchSubmissions).toHaveLength(1);
});

test('refreshes a chosen aggregate album count and files after full-folder retrieval', async ({ page, daemon }) => {
  await daemon.command('aggregate-album-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Aggregate', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture Album');
  await page.locator('#global-search-simple').press('Enter');
  await page.locator('.aggregate-options-button').click();
  await page.getByRole('dialog', { name: /Fixture Album.*options/i })
    .locator('.aggregate-option').filter({ hasText: 'second-peer' })
    .getByRole('button', { name: 'Use this option', exact: true }).click();
  const album = page.locator('.aggregate-result-group');
  await album.getByRole('button', { name: 'Load all files in this folder' }).click();
  await expect(album.locator('.folder-file-count strong')).toHaveText('3');
  await expect(album).toContainText('Bonus.flac');
  await expect(album.getByLabel('Full folder loaded')).toBeVisible();
});

test('pages album alternative files without hiding the card picker', async ({ page, daemon }) => {
  await daemon.command('paged-aggregate-album-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Aggregate', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture Album');
  await page.locator('#global-search-simple').press('Enter');
  await page.locator('.aggregate-options-button').click();
  const dialog = page.getByRole('dialog', { name: /Fixture Album.*options/i });
  await expect(dialog.locator('.aggregate-option')).toHaveCount(100);
  await page.getByRole('button', { name: 'Load more options', exact: true }).click();
  await expect(dialog.locator('.aggregate-option')).toHaveCount(101);
  const alternative = dialog.locator('.aggregate-option').filter({ hasText: 'second-peer' });
  await expect(alternative.locator('.folder-file-row')).toHaveCount(24);
  await alternative.getByRole('button', { name: 'Load more matching files' }).click();
  await expect(alternative.locator('.folder-file-row')).toHaveCount(25);
  await alternative.locator('.aggregate-option-card-picker').click();
  await expect(dialog).toHaveCount(0);
  await expect(page.locator('.aggregate-result-group')).toContainText('25 - Track.flac');
});

test('keeps the prototype split and merge keyboard behavior in global search', async ({ page }) => {
  await page.goto('/jobs');
  await page.locator('#global-search-simple').fill('Artist - Title');
  await expect(page.locator('#global-search-artist')).toHaveValue('Artist');
  await expect(page.locator('#global-search-title')).toHaveValue('Title');
  await expect(page.locator('#global-search-artist')).toHaveAttribute('spellcheck', 'false');
  await expect(page.locator('#global-search-title')).toHaveAttribute('spellcheck', 'false');
  await page.getByRole('button', { name: 'merge', exact: true }).click();
  const input = page.locator('#global-search-simple');
  await expect(input).toBeFocused();
  await expect(input).toHaveValue('Artist - Title');
  expect(await input.evaluate((element: HTMLInputElement) => element.selectionStart)).toBe('Artist - Title'.length);
  await input.press('End');
  await input.type(' more');
  await expect(input).toHaveValue('Artist - Title more');
  await expect(page.locator('#global-search-artist')).toHaveCount(0);
  await input.fill('Original artist');
  // Model choosing a field before an already-queued animation frame runs.
  // Delayed autofocus must not move subsequent typing into another field.
  const frames = await page.evaluateHandle(() => {
    const request = window.requestAnimationFrame.bind(window);
    const cancel = window.cancelAnimationFrame.bind(window);
    const pending = new Map<number, FrameRequestCallback>();
    let nextId = -1;
    window.requestAnimationFrame = callback => { const id = nextId--; pending.set(id, callback); return id; };
    window.cancelAnimationFrame = id => { if (!pending.delete(id)) cancel(id); };
    return {
      release: () => {
        window.requestAnimationFrame = request;
        window.cancelAnimationFrame = cancel;
        return new Promise<void>(resolve => request(time => {
          for (const callback of pending.values()) callback(time);
          pending.clear();
          resolve();
        }));
      },
    };
  });
  try {
    await page.getByRole('button', { name: 'split', exact: true }).click();
    await page.locator('#global-search-artist').selectText();
  } finally { await frames.evaluate(controller => controller.release()); await frames.dispose(); }
  await page.keyboard.insertText('Correct artist');
  await expect(page.locator('#global-search-artist')).toHaveValue('Correct artist');
  await expect(page.locator('#global-search-title')).toHaveValue('');
});

test('cancels the active search and archives it from the result header', async ({ page, daemon }) => {
  await daemon.command('staged-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('song');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-file-row')).toContainText('Fixture song.txt');
  await page.getByRole('button', { name: 'Cancel song', exact: true }).click();
  await expect(page.locator('.search-results-summary')).toContainText('Cancelled');
  await page.getByRole('button', { name: 'Delete song', exact: true }).click();
  await expect(page).toHaveURL(/\/jobs$/);
  await expect(page.locator('.search-history-row').filter({ hasText: 'song' })).toHaveCount(0);
});

test('recovers the live connection after a browser network outage', async ({ page }) => {
  let socket: WebSocketRoute | undefined;
  let outage = false;
  let restoreConnection!: () => void;
  const connectionRestored = new Promise<void>(resolve => { restoreConnection = resolve; });
  await page.route('**/api/events/negotiate?**', async route => {
    if (outage) await connectionRestored;
    await route.continue();
  });
  await page.routeWebSocket('**/api/events?**', (route) => {
    // A routed socket without connectToServer is a successfully opened mock
    // socket, not a pending network connection: SignalR's handshake can be
    // lost there. Reject attempts during the outage and let its retry policy
    // establish a fresh real connection when connectivity is restored.
    if (outage) void route.close({ code: 1012, reason: 'E2E network unavailable' });
    else { socket = route; route.connectToServer(); }
  });
  await page.goto('/jobs');
  await chooseFileSearch(page);
  const status = page.locator('.connection-status').locator('strong').last();
  await expect(status).toHaveText('Connected');
  outage = true;
  try {
    await socket!.close({ code: 1012, reason: 'E2E connection interruption' });
    await expect(status).toContainText('Reconnecting');
  } finally {
    outage = false;
    restoreConnection();
  }
  await expect(status).toHaveText('Connected');
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-file-row')).toContainText('Fixture song.txt');
});

test('does not expose the previous search while browser history opens another job', async ({ page }) => {
  const first = await page.request.post('/api/jobs/search', { data: { queryText: 'Fixture' } });
  const second = await page.request.post('/api/jobs/search', { data: { queryText: 'Different' } });
  expect(first.status()).toBe(202);
  expect(second.status()).toBe(202);
  const firstJob = await first.json() as Schema<'JobSummaryDto'>;
  const secondJob = await second.json() as Schema<'JobSummaryDto'>;
  await page.goto(`/jobs/${firstJob.jobId}`);
  await expect(page.locator('.folder-file-row')).toContainText('Fixture song.txt');

  let markRequested!: () => void;
  const requested = new Promise<void>((resolve) => { markRequested = resolve; });
  let release!: () => void;
  const held = new Promise<void>((resolve) => { release = resolve; });
  await page.route(`**/api/jobs/${secondJob.jobId}`, async (route) => {
    markRequested();
    await held;
    await route.continue();
  });
  await page.evaluate((path) => {
    window.history.pushState(null, '', path);
    window.dispatchEvent(new PopStateEvent('popstate'));
  }, `/jobs/${secondJob.jobId}`);
  await requested;
  await expect(page.getByRole('status').filter({ hasText: 'Opening job' })).toBeVisible();
  await expect(page.locator('.folder-file-row')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^Download \d+$/ })).toHaveCount(0);
  release();
  await expect(page.locator('.search-results-title h1')).toHaveText('Different');
});

test('searches again from one conditioned child, including after a whole-submission rerun, without restarting its sibling', async ({ page, daemon }) => {
  await daemon.command('music-search');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Search replay batch',
    jobs: [
      { kind: 'search-generic', queryText: 'Fixture', downloadSettings: {
        search: { necessaryCond: { formats: { replace: ['txt'] } } },
      } },
      { kind: 'search-generic', queryText: 'Sibling' },
    ],
  } });
  expect(response.status(), await response.text()).toBe(202);
  const original = await response.json() as { jobId: string; submissionId: string };
  let root = original;
  const replayRequests: string[] = [];
  page.on('request', (request) => {
    if (request.method() === 'POST' && /\/rerun(?:-search)?$/.test(new URL(request.url()).pathname))
      replayRequests.push(new URL(request.url()).pathname);
  });
  for (const fromRerun of [false, true]) {
    if (fromRerun) {
      const rerun = await page.request.post(`/api/submissions/${original.submissionId}/rerun`);
      expect(rerun.status(), await rerun.text()).toBe(202);
      root = await rerun.json() as typeof original;
      expect(root.jobId).not.toBe(original.jobId);
      expect(root.submissionId).not.toBe(original.submissionId);
    }
    await page.goto(`/jobs/${root.jobId}`);
    await expect(page.locator('.job-child-row')).toHaveCount(2);
    await page.locator('.job-child-open').filter({ hasText: 'Fixture' }).click();
    await expect(page).toHaveURL(/\/jobs\/[^/]+\?results=/);
    const originalChildUrl = page.url();
    const originalChildId = new URL(originalChildUrl).pathname.split('/').at(-1)!;
    await expect(page.locator('.search-results-summary')).toContainText('Complete');
    await expect(page.locator('.folder-file-row')).toHaveCount(0);
    await page.reload();
    const replayResponse = page.waitForResponse(response => response.request().method() === 'POST'
      && new URL(response.url()).pathname === `/api/jobs/${originalChildId}/rerun-search`);
    await page.getByRole('button', { name: 'Search again', exact: true }).click();
    const accepted = await replayResponse;
    expect(accepted.status(), await accepted.text()).toBe(202);
    await expect(page).not.toHaveURL(originalChildUrl);
    expect(replayRequests).toContain(`/api/jobs/${originalChildId}/rerun-search`);
    await expect(page.locator('.search-results-summary')).toContainText('Complete');
    await expect(page.locator('.folder-file-row')).toHaveCount(0);

    const replayedJobId = new URL(page.url()).pathname.split('/').at(-1)!;
    const replayedDetail = await page.request.get(`/api/jobs/${replayedJobId}`);
    expect(replayedDetail.ok()).toBeTruthy();
    const replayed = await replayedDetail.json() as { summary: { submissionId: string } };
    expect(replayed.summary.submissionId).toBeTruthy();
    expect(replayed.summary.submissionId).not.toBe(root.submissionId);
    const replayedJobs = await page.request.get(
      `/api/jobs?submissionId=${replayed.summary.submissionId}&includeAll=true&archived=false`);
    if (!replayedJobs.ok()) throw new Error(`Replayed jobs query ${replayedJobs.status()}: ${await replayedJobs.text()}`);
    expect((await replayedJobs.json() as unknown[])).toHaveLength(1);
  }
});

test('retains a downloaded result and recovers after raw-search retention', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  const file = page.locator('.folder-file-row').filter({ hasText: 'Fixture song.txt' });
  await expect(file).toBeVisible();
  await file.getByRole('checkbox').check();
  await expect(page.locator('.search-results-summary')).toContainText('Complete');
  await expect(page).toHaveURL(/\/jobs\/[^/?]+/);
  const sourceId = new URL(page.url()).pathname.split('/').at(-1)!;
  const commitRoute = '**/api/search-views/*/commit';
  let acceptedRequest: unknown;
  let acceptedReceipt: Schema<'CommitSearchViewSelectionResponseDto'> | undefined;
  let issuingView: string | undefined;
  await page.route(commitRoute, async route => {
    acceptedRequest = route.request().postDataJSON();
    issuingView = new URL(route.request().url()).pathname.split('/')[3];
    const response = await route.fetch();
    acceptedReceipt = await response.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
    await route.abort();
  });
  await page.getByRole('button', { name: 'Download 1', exact: true }).click();
  const retry = page.getByRole('button', { name: 'Retry download request', exact: true });
  await expect(retry).toBeEnabled();
  expect(acceptedReceipt?.submittedCount).toBe(1);
  await page.unroute(commitRoute);
  await daemon.command('expire-search-views');
  expect((await page.request.get(`/api/search-views/${issuingView}`)).status()).toBe(404);
  const replayed = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname.endsWith('/commit'));
  await retry.click();
  const replay = await replayed;
  expect(replay.ok(), await replay.text()).toBe(true);
  expect(replay.request().postDataJSON()).toEqual(acceptedRequest);
  expect(await replay.json()).toEqual(acceptedReceipt);
  await expect(page.locator('.mutation-status')).toContainText('1 download requested');
  await page.goto('/downloads');
  await expect(page.locator('.page')).toContainText('Fixture song.txt');
  await expect(page.getByLabel('50% complete', { exact: true })).toBeVisible();
  await daemon.command('release-download');
  await expect(page.locator('.page')).toContainText('Complete');
  const timelineResponse = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
  expect(timelineResponse.ok(), await timelineResponse.text()).toBe(true);
  const timeline = await timelineResponse.json() as Schema<'TransferTimelinePageDto'>;
  expect(timeline.items).toHaveLength(1);
  const completed = timeline.items.find(item => item.remotePath?.endsWith('Fixture song.txt'));
  expect(completed?.terminalOutcome).toBe('Succeeded');
  expect(completed?.localPath).toBeTruthy();
  const bytes = await readFile(completed!.localPath!);
  expect(bytes).toHaveLength(128);
  expect(bytes.every((byte) => byte === 0)).toBe(true);
  await expect(page.locator('.page')).toContainText('Complete');
  await page.reload();
  await expect(page.locator('.page')).toContainText('Fixture song.txt');
  await expect(page.locator('.page')).toContainText('Complete');
  await page.getByRole('navigation', { name: 'Primary navigation' })
    .getByRole('button', { name: 'Jobs' }).click();
  const reopened = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/jobs/${sourceId}/search-views`);
  await page.locator('.search-history-row').filter({ hasText: 'Fixture' })
    .locator('.search-history-open').click();
  let cached = await (await reopened).json() as Schema<'SearchViewSummaryDto'>;
  await expect(page.locator('.folder-file-row').filter({ hasText: 'Fixture song.txt' })).toBeVisible();
  await expect(page.locator('.search-results-summary')).toContainText('Complete');
  await file.getByRole('checkbox').check();
  await daemon.command('expire-search-views');
  await page.getByLabel('Sort', { exact: true }).selectOption('speed');
  const expired = page.getByRole('alert').filter({ hasText: 'Results view unavailable' });
  await expect(expired).toContainText('selection will be cleared');
  await page.setViewportSize({ width: 390, height: 900 });
  const expiredBounds = await expired.boundingBox();
  expect(expiredBounds?.x).toBeGreaterThanOrEqual(0);
  expect(expiredBounds!.x + expiredBounds!.width).toBeLessThanOrEqual(390);
  const reloaded = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/jobs/${sourceId}/search-views`);
  await expired.getByRole('button', { name: 'Reload results', exact: true }).click();
  cached = await (await reloaded).json() as Schema<'SearchViewSummaryDto'>;
  await page.setViewportSize({ width: 1280, height: 720 });
  await expect(file).toBeVisible();
  await expect(file.getByRole('checkbox')).not.toBeChecked();
  await expect(page).toHaveURL(new RegExp(`/jobs/${sourceId}(?:[?]|$)`));
  await expect(page.locator('.search-results-summary')).toContainText('Complete');
  const cachedSummary = await (await page.request.get(`/api/search-views/${cached.viewId}`)).json() as Schema<'SearchViewSummaryDto'>;
  await daemon.command(`prune-search-results:${sourceId}`);
  // A complete disk snapshot stays usable when only its raw source is pruned.
  const retainedStarted = performance.now();
  const retainedResponse = await page.request.get(
    `/api/search-views/${cached.viewId}/directories?revision=${cachedSummary.revision}&limit=100`);
  expect(retainedResponse.ok(), await retainedResponse.text()).toBe(true);
  const retained = await retainedResponse.json() as Schema<'SearchViewDirectoryPageDto'>;
  const retainedPageMs = Math.round(performance.now() - retainedStarted);
  expect(retained.items).toHaveLength(1);
  expect(retained.revision.retentionState).toBe('Complete');
  await expect(file).toBeVisible();

  const prunedCreation = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/jobs/${sourceId}/search-views`);
  await page.reload();
  const pruned = await prunedCreation;
  await expect(page.locator('.search-results-empty')).toContainText('Search results were pruned');
  await expect(page.locator('.search-results-empty')).toContainText('Search again');
  await expect(page.locator('.search-results-empty')).not.toContainText('without a match');
  await expect(page.locator('.search-results-summary')).not.toContainText('0 files');
  await expect(page.locator('.folder-file-row')).toHaveCount(0);

  await page.getByRole('button', { name: 'Search again', exact: true }).click();
  await expect(page).not.toHaveURL(new RegExp(`/jobs/${sourceId}(?:[?]|$)`));
  await expect(file).toBeVisible();
  await expect(page.locator('.search-results-summary')).toContainText('Complete');
  await page.getByRole('navigation', { name: 'Primary navigation' })
    .getByRole('button', { name: 'Dashboard' }).click();
  await expect(page.locator('.dashboard-metric-card').first()).toContainText('128 B');
});
test('retries unavailable search pages and keeps partial results usable', async ({ page }) => {
  // Inject terminal data-loss metadata while retaining the real result page.
  // Raw pruning itself is exercised through the real retention owner above.
  let retention: Schema<'SearchViewRetentionState'> = 'Incomplete';
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    server.onMessage(message => {
      const frames = message.toString().split('\u001e').map(frame => {
        if (!frame) return frame;
        const value = JSON.parse(frame) as { type?: number; item?: Schema<'SearchViewSummaryDto'> };
        if (value.type === 2 && value.item?.viewId) value.item.retentionState = retention;
        return JSON.stringify(value);
      });
      route.send(frames.join('\u001e'));
    });
  });
  await page.route('**/api/search-views/*/updates?**', async route => {
    const response = await route.fetch();
    const body = await response.json() as Schema<'SearchViewUpdateDto'>;
    body.summary.retentionState = retention;
    await route.fulfill({ response, json: body });
  });
  let searchSubmissions = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/jobs/search') searchSubmissions++;
  });
  const pages = '**/api/search-views/*/directories?*';
  await page.route(pages, route => route.abort());
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  const alert = page.getByRole('alert').filter({ hasText: 'Search results could not be refreshed' });
  await expect(alert).toBeVisible();
  await expect(page.getByText('No matching results yet', { exact: true })).toHaveCount(0);
  await page.unroute(pages);
  await alert.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.locator('.folder-file-row')).toContainText('Fixture song.txt');
  await expect(alert).toHaveCount(0);
  for (const [state, title] of [
    ['Incomplete', 'Search results are incomplete'],
    ['Interrupted', 'Search was interrupted'],
    ['Pruned', 'Search results were pruned'],
  ] as const) {
    if (retention !== state) { retention = state; await page.reload(); }
    await expect(page.getByRole('status').filter({ hasText: title })).toBeVisible();
    await expect(page.locator('.folder-file-row')).toContainText('Fixture song.txt');
    await expect(page.locator('.search-results-summary')).toContainText('1 file');
    await page.locator('.folder-file-row').getByRole('checkbox').check();
    await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  }
  expect(searchSubmissions, 'Reloading retained or partial results must not start another search.').toBe(1);
});
test('retrieves complete folder contents with retry while preserving selection', async ({ page, daemon }) => {
  await daemon.command('directory-selection-search');
  let childPageRequests = 0;
  page.on('request', request => {
    if (/\/directories\/[^/]+\/files$/.test(new URL(request.url()).pathname)) childPageRequests += 1;
  });
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-file-row')).toHaveCount(2);
  expect(childPageRequests).toBe(0);
  const selected = page.locator('.folder-file-row').filter({ has: page.getByText('Selected.txt', { exact: true }) }).getByRole('checkbox');
  await selected.check();
  const route = '**/api/search-views/*/directories/retrieve';
  let acceptedJobId: string | null = null;
  let originalRequest: unknown = null;
  await page.route(route, async request => {
    originalRequest = request.request().postDataJSON();
    const accepted = await request.fetch();
    acceptedJobId = (await accepted.json() as Schema<'JobSummaryDto'>).jobId ?? null;
    await request.abort();
  });
  await page.getByRole('button', { name: 'Load all files in this folder', exact: true }).click();
  const retry = page.getByRole('button', { name: 'Retry loading all files in this folder', exact: true });
  await expect(retry).toBeVisible();
  expect(acceptedJobId).toBeTruthy();
  await page.unroute(route);
  const resultPages = '**/api/search-views/*/directories?**';
  await page.route(resultPages, request => request.abort());
  const retrieved = page.waitForResponse(response => response.request().method() === 'POST'
    && /\/directories\/retrieve$/.test(new URL(response.url()).pathname) && response.ok());
  await retry.click();
  const retrievalResponse = await retrieved;
  expect(retrievalResponse.request().postDataJSON()).toEqual(originalRequest);
  const retrieval = await retrievalResponse.json() as Schema<'JobSummaryDto'>;
  expect(retrieval.jobId).toBe(acceptedJobId);
  await expect(page.getByRole('alert')).toContainText('Search results could not be refreshed');
  await page.unroute(resultPages);
  await page.getByRole('alert').getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByLabel('Full folder loaded', { exact: true })).toBeVisible();
  await expect(page.locator('.folder-file-row')).toHaveCount(5);
  await expect(page.locator('.folder-file-count strong')).toHaveText('5');
  await expect(selected).toBeChecked();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeVisible();
  await expect(page.locator('.folder-file-row').filter({ hasText: 'DeepTwo.txt' })).toBeVisible();
  await expect(page.locator('.folder-subfolder-row')).toHaveCount(2);
  const nested = page.getByRole('checkbox', { name: 'Select all matching files in Nested', exact: true });
  await nested.check();
  await expect(page.getByRole('button', { name: 'Download 4', exact: true })).toBeVisible();
  await nested.uncheck();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeVisible();
  await page.getByRole('checkbox', { name: 'Select all files in Fixture', exact: true }).check();
  await expect(page.getByRole('button', { name: 'Download 5', exact: true })).toBeVisible();
  // The retrieval is also daemon-owned work with a retained detail, not just
  // a transient folder-card flag in the issuing search.
  await page.goto(`/jobs/${retrieval.jobId}`);
  const facts = page.locator('.job-detail-facts');
  await expect(page.locator('.job-detail-heading-summary .search-status-badge')).toHaveText('Complete');
  await expect(page.locator('.job-detail-heading-summary')).toContainText('3 new files');
  await expect(facts.locator('dt')).toHaveText(['User', 'Folder', 'Outcome', 'New files']);
  await expect(facts.locator('dd')).toHaveText(['fixture-peer', 'Fixture', 'Completed', '3']);
  await expect(facts.locator('dd').nth(1)).toHaveAttribute('title', 'Fixture');
  await page.reload();
  await expect(facts.locator('dd')).toHaveText(['fixture-peer', 'Fixture', 'Completed', '3']);
  await facts.getByRole('button', { name: 'Actions for fixture-peer', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Shares', exact: true }).click();
  await expect(page).toHaveURL(/\/users\/fixture-peer\/shares$/);
});

test('downloads a File Search subfolder with a live path rule and a file exclusion', async ({ page, daemon }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await daemon.command('directory-selection-search');
  await daemon.command('staged-search');
  let retrievalRequests = 0, searchRequests = 0;
  const countRevisions: number[] = [];
  page.on('request', request => {
    if (request.method() === 'POST'
      && /\/directories\/retrieve$/.test(new URL(request.url()).pathname)) retrievalRequests += 1;
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/jobs/search') searchRequests++;
    if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/selection-counts'))
      countRevisions.push(Number(request.postDataJSON().revision));
  });
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-file-row')).toHaveCount(2);
  await page.getByRole('button', { name: 'Load all files in this folder', exact: true }).click();
  await expect(page.locator('.folder-file-row')).toHaveCount(5);
  const subfolders = page.locator('.folder-subfolder-row');
  await expect(subfolders).toHaveCount(2);
  await expect(subfolders.locator('.folder-subfolder-path strong')).toHaveText(['Nested', 'Deep']);
  await expect(subfolders.locator('.folder-subfolder-count')).toHaveText(['3 files', '2 files']);

    const workspace = await page.locator('.workspace').boundingBox();
    const content = await page.locator('.page-content').boundingBox();
    const card = await page.locator('.folder-item-card').boundingBox();
    expect(workspace).not.toBeNull();
    expect(content).not.toBeNull();
    expect(card).not.toBeNull();
    expect(content!.x + content!.width).toBeLessThanOrEqual(workspace!.x + workspace!.width + 1);
    expect(card!.x + card!.width).toBeLessThanOrEqual(390);
    const parent = await subfolders.nth(0).locator('.folder-subfolder-path strong').boundingBox();
    const child = await subfolders.nth(1).locator('.folder-subfolder-path strong').boundingBox();
    expect(parent).not.toBeNull();
    expect(child).not.toBeNull();
    expect(child!.x).toBeGreaterThan(parent!.x + 10);

  const nested = page.getByRole('checkbox', { name: 'Select all matching files in Nested', exact: true });
  await nested.check();
  await expect(page.getByRole('button', { name: 'Download 3', exact: true })).toBeVisible();

  await sortWithoutCreatingView(page, 'name', 'DirectoryNameAscending');
  await expect(page.locator('.folder-file-row')).toHaveCount(5);
  await expect(page.getByLabel('Full folder loaded', { exact: true })).toBeVisible();
  await expect(nested).toBeChecked();
  await expect(page.getByRole('button', { name: 'Download 3', exact: true })).toBeEnabled();
  expect(retrievalRequests).toBe(1);
  const excluded = page.locator('.folder-file-row').filter({ hasText: 'DeepTwo.txt' }).getByRole('checkbox');
  await excluded.uncheck();
  await expect(nested).toBeChecked({ indeterminate: true });
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Filtering', exact: true }).click();
  const panel = page.getByRole('region', { name: 'Result search configuration' });
  const noMatches = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
    && response.request().postDataJSON()?.refinementSettings?.necessaryCond?.formats?.replace?.includes('pdf'));
  await panel.getByRole('button', { name: 'PDF', exact: true }).click();
  await noMatches;
  await expect(page.locator('.folder-item-card')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toHaveCount(0);
  const restored = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
    && JSON.stringify(response.request().postDataJSON()?.refinementSettings?.necessaryCond?.formats?.replace) === '["txt"]');
  await panel.getByRole('button', { name: 'custom…', exact: true }).click();
  await panel.getByRole('textbox', { name: 'Comma-separated required formats', exact: true }).fill('txt');
  const restoredView = await (await restored).json() as Schema<'SearchViewSummaryDto'>;
  await page.keyboard.press('Escape');
  await expect(page.locator('.folder-file-row')).toHaveCount(5);
  await expect(excluded).not.toBeChecked();
  await expect(nested).toBeChecked({ indeterminate: true });
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  await expect(page.getByLabel('Full folder loaded', { exact: true })).toBeVisible();
  expect(retrievalRequests).toBe(1);

  // New members inherit the selected path, not the excluded root or file.
  // The callback is still live: no completion-dependent projection is allowed.
  await daemon.command('next-search-batch');
  await expect(page.locator('.folder-file-row')).toHaveCount(7);
  await expect(page.locator('.search-results-summary')).not.toContainText('Complete');
  await expect(page.locator('.folder-file-row').filter({ hasText: 'LateNested.txt' }).getByRole('checkbox')).toBeChecked();
  await expect(page.locator('.folder-file-row').filter({ hasText: 'LateRoot.txt' }).getByRole('checkbox')).not.toBeChecked();
  await expect(excluded).not.toBeChecked();
  await expect(page.getByRole('button', { name: 'Download 3', exact: true })).toBeEnabled();
  const liveSummaryResponse = await page.request.get(`/api/search-views/${restoredView.viewId}`);
  expect(liveSummaryResponse.ok()).toBe(true);
  const liveSummary = await liveSummaryResponse.json() as Schema<'SearchViewSummaryDto'>;
  expect(liveSummary.isComplete).toBe(false);
  expect(Number(liveSummary.counters.publicFileCount)).toBe(4);
  expect(Number(liveSummary.counters.projectedFileCount)).toBe(4);
  expect(Number(liveSummary.counters.projectedPublicFileCount)).toBe(4);
  expect(Number(liveSummary.counters.topLevelItemCount)).toBe(1);
  // Projection counters describe search hits; retrieved totals include the
  // extra folder contents. Both facts stay authoritative and distinct.
  async function directoryAt(revision: number | string) {
    const response = await page.request.get(`/api/search-views/${restoredView.viewId}/directories?revision=${revision}&limit=100`);
    expect(response.ok()).toBe(true);
    const pageValue = await response.json() as Schema<'SearchViewDirectoryPageDto'>;
    expect(pageValue.items).toHaveLength(1);
    return pageValue.items[0]!;
  }
  const liveDirectory = await directoryAt(liveSummary.revision);
  expect(Number(liveDirectory.publicMatchingFileCount)).toBe(4);
  expect(Number(liveDirectory.publicMatchingBytes)).toBe(512);
  expect(Number(liveDirectory.retrievedFileCount)).toBe(7);
  expect(Number(liveDirectory.retrievedBytes)).toBe(896);
  expect(liveDirectory.initialFiles?.items).toHaveLength(7);
  await expect(page.locator('.search-results-summary')).toContainText('4 files');
  await daemon.command('finish-search');
  await expect(page.locator('.search-results-summary')).toContainText('Complete');
  await expect(page.locator('.search-results-summary')).toContainText('4 files');
  const completedSummaryResponse = await page.request.get(`/api/search-views/${restoredView.viewId}`);
  expect(completedSummaryResponse.ok()).toBe(true);
  const completedSummary = await completedSummaryResponse.json() as Schema<'SearchViewSummaryDto'>;
  expect(completedSummary.isComplete).toBe(true);
  expect(completedSummary.counters).toEqual(liveSummary.counters);
  const completedDirectory = await directoryAt(completedSummary.revision);
  expect(completedDirectory.publicMatchingFileCount).toBe(liveDirectory.publicMatchingFileCount);
  expect(completedDirectory.publicMatchingBytes).toBe(liveDirectory.publicMatchingBytes);
  expect(completedDirectory.retrievedFileCount).toBe(liveDirectory.retrievedFileCount);
  expect(completedDirectory.retrievedBytes).toBe(liveDirectory.retrievedBytes);
  expect(completedDirectory.initialFiles?.items.map(item => item.ref).sort())
    .toEqual(liveDirectory.initialFiles?.items.map(item => item.ref).sort());
  await expect(page.getByRole('button', { name: 'Download 3', exact: true })).toBeEnabled();
  expect(retrievalRequests).toBe(1);
  expect(searchRequests).toBe(1);
  expect(countRevisions, 'Unpublished projections cannot provide a useful selection count.').not.toContain(0);
  const committed = page.waitForRequest(request => request.method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(request.url()).pathname));
  await page.getByRole('button', { name: 'Download 3', exact: true }).click();
  const body = (await committed).postDataJSON();
  expect(new URL((await committed).url()).pathname).toBe(`/api/search-views/${restoredView.viewId}/commit`);
  expect(body.selection.mode).toBe('Only');
  expect(body.directoryPathRules).toEqual(expect.arrayContaining([
    expect.objectContaining({ relativePath: 'Nested', selected: true }),
  ]));
  await expect(page.locator('.mutation-status')).toContainText('3 downloads requested');
  await page.goto('/downloads');
  await expect(page.locator('.page')).toContainText('Nested.txt');
  await expect(page.locator('.page')).toContainText('DeepOne.txt');
  await expect(page.locator('.page')).toContainText('LateNested.txt');
  await expect(page.locator('.page')).not.toContainText('DeepTwo.txt');
  await expect(page.locator('.page')).not.toContainText('LateRoot.txt');
  await expect(page.locator('.page')).not.toContainText('Selected.txt');
  await expect(page.locator('.page')).not.toContainText('Unselected.txt');
  await expect(page.locator('.folder-transfer-summary .transfer-state')).toHaveText('Complete');
  const transfersResponse = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
  expect(transfersResponse.ok(), await transfersResponse.text()).toBe(true);
  const transfers = (await transfersResponse.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(transfers.map(item => `${item.username}:${item.terminalOutcome}:${item.remotePath}`).sort()).toEqual([
    'fixture-peer:Succeeded:Fixture\\Nested\\Deep\\DeepOne.txt',
    'fixture-peer:Succeeded:Fixture\\Nested\\LateNested.txt',
    'fixture-peer:Succeeded:Fixture\\Nested\\Nested.txt',
  ].sort());
  for (const transfer of transfers) expect((await stat(transfer.localPath!)).size).toBe(128);
});

test('selects unseen descendants of a paged File Search subfolder', async ({ page, daemon }) => {
  await daemon.command('paged-subfolder-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Paged');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-file-row')).toHaveCount(1);
  // The daemon has all 25 descendants; keep the browser at its embedded first
  // child page to prove a path rule does not require downloading page two.
  const childPages = '**/api/search-views/*/directories/*/files?*';
  await page.route(childPages, route => route.abort());
  await page.getByRole('button', { name: 'Load all files in this folder', exact: true }).click();
  await expect(page.locator('.folder-file-row')).toHaveCount(24);
  await expect(page.locator('.folder-subfolder-count')).toContainText('24 files loaded');
  await page.getByRole('checkbox', { name: 'Select all matching files in Nested', exact: true }).check();
  await expect(page.getByRole('button', { name: 'Download 25', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Download 25', exact: true }).click();
  await expect(page.locator('.mutation-status')).toContainText('25 downloads requested');
  await page.unroute(childPages);
  await page.goto('/downloads');
  await expect(page.locator('.page')).toContainText('25.txt');
  await expect(page.locator('.page')).not.toContainText('Root.txt');
});
