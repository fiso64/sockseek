import { test, expect } from './fixtures';
import type { Page, Request } from '@playwright/test';
import { readFile, readdir, stat } from 'node:fs/promises';
import { basename, dirname, join, sep } from 'node:path';
import { randomUUID } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import type { Schema } from '../src/api/types';

for (const mode of [
  { label: 'Song Groups', placeholder: 'Search songs by artist…', endpoint: '/api/jobs/search/tracks', field: 'songQuery', title: 'title' },
  { label: 'Album Groups', placeholder: 'Search albums by artist…', endpoint: '/api/jobs/search/albums', field: 'albumQuery', title: 'album' },
] as const) {
  test(`${mode.label} keeps the artist meaning when switching input layouts`, async ({ page }) => {
    await page.goto('/dashboard');
    await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
    await page.getByRole('menuitemradio', { name: mode.label, exact: true }).click();
    const input = page.locator('#global-search-simple');
    await expect(input).toHaveAttribute('placeholder', mode.placeholder);
    await input.fill('Casiopea');
    await page.getByRole('button', { name: 'split', exact: true }).click();
    await expect(page.locator('#global-search-artist')).toHaveValue('Casiopea');
    await page.locator('#global-search-artist').fill('');
    await page.locator('#global-search-title').fill('Take Me');
    await expect(page.getByRole('button', { name: 'merge', exact: true })).toBeDisabled();
    await page.locator('#global-search-artist').press('Backspace');
    await expect(page.locator('#global-search-title')).toHaveValue('Take Me');
    await page.locator('#global-search-artist').fill('Casiopea');
    await page.locator('#global-search-title').fill('');
    await page.getByRole('button', { name: 'merge', exact: true }).click();
    await expect(input).toHaveValue('Casiopea');
    const request = page.waitForRequest(req => new URL(req.url()).pathname === mode.endpoint && req.method() === 'POST');
    await input.press('Enter');
    expect((await request).postDataJSON()[mode.field]).toMatchObject({ artist: 'Casiopea', [mode.title]: null });
  });
}

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
      await page.getByRole('button', { name: 'Profiles', exact: true }).click();
  await page.getByRole('checkbox', { name: 'download-preset', exact: true }).check();
  await page.getByRole('button', { name: 'Profiles', exact: true }).click();
      await expect(page.getByRole('checkbox', { name: 'Skip existing downloads', exact: true })).not.toBeChecked();

    const commit = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
      && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
    await page.getByRole('button', { name: 'Download 1', exact: true }).click();
    const commitResponse = await commit;
    expect(commitResponse.request().postDataJSON().options.profileNames).toEqual(['download-preset']);
    const receipt = await commitResponse.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
    expect(Number(receipt.submittedCount)).toBe(1);
    expect(Number(receipt.rejectedCount)).toBe(0);
    await page.locator('.floating-download-options-popover').getByRole('button', { name: 'Close download options', exact: true }).click();
    await page.getByRole('button', { name: 'View download jobs', exact: true }).click();
    await expect(page.getByRole('tab', { name: 'Download jobs', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(page).toHaveURL(new RegExp(`submission=${receipt.submissionId}`));
    await expect(page.locator('.search-history-row')).toHaveCount(1);
    await page.locator('.search-history-open').click();
    const sourceLink = page.getByRole('button', { name: 'Open source job', exact: true });
    await expect(sourceLink).toBeVisible();
    const downloadJobUrl = page.url();
    const source = await (await page.request.get(`/api/jobs/${view.sourceJobId}`)).json() as Schema<'JobDetailDto'>;
    if (source.summary.lifecycleState !== 'Terminal') await daemon.command(`wait-job-complete:${view.sourceJobId}`);
    expect(source.summary.submissionId).toBeTruthy();
    const reads: string[] = [];
    page.on('request', request => {
      if (request.method() === 'GET' && new URL(request.url()).pathname === `/api/jobs/${view.sourceJobId}`) reads.push(request.url());
    });
    const deleteUrl = `/api/submissions/${source.summary.submissionId}`;
    const deletion = await page.request.delete(deleteUrl);
    expect((await deletion.json()).rejectedSubmissionCount).toBe(0);
    await expect(sourceLink).toHaveCount(0);
    expect(page.url()).toBe(downloadJobUrl);
    expect(reads).toEqual([]);
    await page.reload();
    await expect(page.locator('.job-detail-heading')).toBeVisible();
    await expect(sourceLink).toHaveCount(0);
    await page.goto('/downloads');
    const file = page.locator('.file-item-card').filter({ hasText: 'Artist - Bare.flac' });
    await expect(file).toHaveCount(1);
    await expect(file.getByRole('button', { name: 'Complete', exact: true })).toHaveClass(/folder-file-state/);
    await expect(page.locator('.folder-item-card')).toHaveCount(0);
    const response = await page.request.get('/api/transfers?direction=download');
    expect(response.ok(), await response.text()).toBe(true);
    const transfers = (await response.json() as Schema<'TransferTimelinePageDto'>).items;
    expect(transfers).toHaveLength(1);
    expect(transfers[0]!.identity.remotePath).toBe('Artist - Bare.flac');
    const output = transfers[0]!.status.localPath!;
    expect(basename(dirname(output))).toBe('downloads');
    expect((await stat(output)).size).toBe(128);
    await daemon.command('report-peer-browses');
    expect(daemon.logs().filter(line => line.startsWith('E2E_METRIC peer-browses ')).at(-1)).toBe('E2E_METRIC peer-browses count=0');
    await page.reload();
    await expect(file.getByRole('button', { name: 'Complete', exact: true })).toHaveClass(/folder-file-state/);

      const jobsResponse = await page.request.get(`/api/jobs?submissionId=${receipt.submissionId}&includeAll=true&limit=100`);
      expect(jobsResponse.ok()).toBe(true);
      const jobs = await jobsResponse.json() as Schema<'JobSummaryDto'>[];
      const directory = jobs.find(job => job.kind === 'remote-directory')!;
      expect(directory.itemName).toBe('Selected files');
      await page.goto(`/jobs/${directory.jobId}`);
      await expect(page.locator('.folder-transfer-path')).toHaveText('(peer root)');
      await expect(page.locator('.folder-file-row')).toHaveCount(1);
      await page.reload();
      await expect(page.locator('.folder-transfer-path')).toHaveText('(peer root)');

  });

test('keeps previous results usable while a refinement loads and explicitly recovers a failed page', async ({ page, daemon }) => {
  await daemon.command('directory-selection-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-item-card')).toHaveCount(1);
  const peerGroup = page.locator('.peer-item-group').first();
  const peerHeader = peerGroup.locator('.result-peer-header');
  const toggle = peerHeader.locator('.peer-header-toggle');
  const groupBounds = (await peerGroup.boundingBox())!;
  const headerBounds = (await peerHeader.boundingBox())!;
  expect(headerBounds.width).toBeLessThan(groupBounds.width);
  await page.mouse.click(groupBounds.x + groupBounds.width - 2, headerBounds.y + headerBounds.height / 2);
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await toggle.click({ position: { x: 3, y: 3 } });
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await page.mouse.click(headerBounds.x + 3, headerBounds.y + 3);
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  const folderHeader = page.locator('.folder-summary-select-target');
  const selectFolder = folderHeader.getByRole('checkbox');
  const header = page.locator('.folder-result-summary');
  await header.click({ position: { x: 2, y: 2 } });
  await expect(selectFolder).toBeChecked();
  const bounds = await header.boundingBox();
  await header.click({ position: { x: bounds!.width - 2, y: bounds!.height - 2 } });
  await expect(selectFolder).not.toBeChecked();
  await folderHeader.locator('.folder-heading-meta').click();
  await expect(selectFolder).toBeChecked();
  await folderHeader.locator('.folder-heading-meta').click();
  await expect(selectFolder).not.toBeChecked();
  await folderHeader.locator('.folder-heading-path').click();
  await expect(selectFolder).toBeChecked();
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
    await expect(page.locator('.result-refinement-status')).toHaveCount(0);
    await expect(page.locator('.folder-file-row')).toHaveCount(2);
    await expect(page.locator('.search-results-summary')).toContainText('2 files');
    await expect(page.locator('.loading-indicator.blocking')).toHaveCount(0);
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
  await page.clock.install(); // Advance refinement debounce timers explicitly.
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
  await page.clock.fastForward(350);
  await expect(page.locator('.folder-item-card')).toHaveCount(0);
  await expect(page.locator('.search-results-empty')).toContainText('2 files received, but none match the current result conditions.');
  await page.getByRole('button', { name: 'Remove PDF format' }).click();
  await page.clock.fastForward(350);
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
  await expect(page.locator('.search-results-summary .search-status-badge')).toBeVisible();
  await page.getByRole('button', { name: 'Filtering', exact: true }).click();
  // These are the daemon's resolved defaults, not a fabricated UI settings object.
  await expect(panel.getByRole('button', { name: 'AAC', exact: true })).toHaveClass(/active/);
  await expect(page.locator('.result-condition-pills')).toHaveCount(0);
  await panel.getByRole('button', { name: 'FLAC', exact: true }).click();
  await expect(page.locator('.result-condition-pills .search-condition-pill')).toHaveCount(7);
  await panel.getByRole('button', { name: 'FLAC', exact: true }).click();
  await expect(page.locator('.result-condition-pills')).toHaveCount(0);
  await panel.getByRole('button', { name: 'custom…', exact: true }).click();
  const required = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
    && response.request().postDataJSON()?.refinementSettings?.necessaryCond?.formats?.replace?.join(',') === 'flac');
  await panel.getByRole('textbox', { name: 'Comma-separated required formats', exact: true }).fill('flac');
  await page.clock.fastForward(350);
  await required;
  await expect(page.locator('.file-item-card')).toHaveCount(2);
  await panel.getByRole('button', { name: 'Ranking', exact: true }).click();
  await panel.getByRole('button', { name: 'custom…', exact: true }).click();
  const preferred = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
    && response.request().postDataJSON()?.refinementSettings?.preferredCond?.formats?.replace?.join(',') === 'flac');
  await panel.getByRole('textbox', { name: 'Comma-separated preferred formats', exact: true }).fill('flac');
  await page.clock.fastForward(350);
  await preferred;
  const refined = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
    && response.request().postDataJSON()?.refinementSettings?.preferredCond?.minBitrate === 256);
  await panel.getByLabel('Min bitrate kbps').fill('256');
  await page.clock.fastForward(350);
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
  await page.clock.fastForward(350);
  const filteredView = await (await filtered).json() as Schema<'SearchViewSummaryDto'>;
  await expect(page.locator('.file-item-card')).toHaveCount(1);
  await page.getByRole('textbox', { name: 'Filter search results', exact: true }).blur();
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('.file-item-card')).toBeFocused();
  await page.keyboard.press('Space');
  await expect(page.getByRole('button', { name: 'Select all', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();

  await daemon.command('next-search-batch');
  await expect(page.locator('.file-item-card')).toHaveCount(3);
  await expect(page.locator('.file-item-card.locked')).toHaveCount(1);
  await expect(page.locator('.file-item-card.locked')).toContainText('03 - Locked - Track.flac');
  await expect(page.locator('.file-item-card.preferred')).toHaveCount(1);
  await expect(page.locator('.search-results-summary .search-status-badge')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Deselect all', exact: true }).click();
  await page.locator('.file-item-card:not(.locked) input[type=checkbox]').first().check();
  await page.getByRole('button', { name: 'Select all', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  await daemon.command('finish-search');
  await expect(page.locator('.search-results-summary')).toBeVisible();
  await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
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
  let files: Schema<'TransferStateDto'>[] = [];
  await expect.poll(async () => {
    const response = await page.request.get('/api/transfers?direction=download&limit=100');
    expect(response.ok(), await response.text()).toBe(true);
    files = (await response.json() as Schema<'TransferTimelinePageDto'>).items;
    return files.map(file => `${file.identity.username}:${file.status.terminalOutcome}:${file.identity.remotePath}`).sort();
  }).toEqual([
    'fixture-peer:Succeeded:Music\\Artist\\Album\\01 - Track.flac',
    'second-peer:Succeeded:Music\\Artist\\Album\\02 - Track.flac',
  ]);
  for (const file of files) expect((await stat(file.status.localPath!)).size).toBe(128);
});

test.describe('live album conditions', () => {

  test('refines live album quality without dropping mixed-format members from selected late folders', async ({ page, daemon }) => {
    await page.clock.install(); // Advance refinement debounce timers explicitly.
    await daemon.command('staged-conditioned-music-search');
    let searches = 0;
    page.on('request', request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/jobs/search/albums') searches++;
    });
    await page.goto('/jobs');
    await page.locator('#global-search-simple').fill('Album');
    await page.locator('#global-search-simple').press('Enter');
    await expect(page.locator('.folder-item-card')).toHaveCount(1);
    await expect(page.locator('.folder-file-row')).toHaveCount(2);
    await expect(page.locator('.search-results-summary .search-status-badge')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Filtering', exact: true })).toBeEnabled();
    await page.locator('.folder-item-card').focus();
    await page.keyboard.press('Space');
    await expect(page.locator('.folder-item-card')).toHaveClass(/selected/);
    await page.keyboard.press('Space');
    await expect(page.locator('.folder-item-card')).not.toHaveClass(/selected/);

    await page.getByRole('button', { name: 'Filtering', exact: true }).click();
    const panel = page.getByRole('region', { name: 'Result search configuration' });
    await panel.getByRole('button', { name: 'custom…', exact: true }).click();
    const required = page.waitForResponse(response => response.ok()
      && response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
      && response.request().postDataJSON()?.refinementSettings?.necessaryCond?.formats?.replace?.join(',') === 'flac');
    await panel.getByRole('textbox', { name: 'Comma-separated required formats', exact: true }).fill('flac');
    await page.clock.fastForward(350);
    await required;
    // Album quality describes folder coverage, unlike Track Search's per-file
    // filtering. A mixed-format folder retains both files until strict coverage
    // requires every track to match.
    await expect(page.locator('.folder-file-row')).toHaveCount(2);
    await panel.getByRole('checkbox', { name: 'Every album track must satisfy quality' }).check();
    await page.clock.fastForward(350);
    await expect(page.locator('.folder-item-card')).toHaveCount(0);
    await panel.getByRole('checkbox', { name: 'Every album track must satisfy quality' }).uncheck();
    await page.clock.fastForward(350);
    await expect(page.locator('.folder-file-row')).toHaveCount(2);
    await panel.getByRole('button', { name: 'Ranking', exact: true }).click();
    await panel.getByRole('button', { name: 'custom…', exact: true }).click();
    const preferred = page.waitForResponse(response => response.ok()
      && response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
      && response.request().postDataJSON()?.refinementSettings?.preferredCond?.formats?.replace?.join(',') === 'flac');
    await panel.getByRole('textbox', { name: 'Comma-separated preferred formats', exact: true }).fill('flac');
    await page.clock.fastForward(350);
    await preferred;
    const refined = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
      && response.request().postDataJSON()?.refinementSettings?.preferredCond?.minBitrate === 256);
    await panel.getByLabel('Min bitrate kbps').fill('256');
    await page.clock.fastForward(350);
    const view = await (await refined).json() as Schema<'SearchViewSummaryDto'>;
    await page.keyboard.press('Escape');
    await expect(page.locator('.result-tier.preferred-tier .folder-item-card')).toHaveCount(1);
    const preferredHelp = page.getByRole('button', { name: 'About preferred matches', exact: true });
    const preferredExplanation = page.getByRole('dialog', { name: 'Preferred matches', exact: true });
    await preferredHelp.hover();
    await preferredExplanation.getByRole('button', { name: 'Edit preferences', exact: true }).click();
    await expect(panel.getByRole('button', { name: 'Ranking', exact: true })).toHaveAttribute('aria-current', 'page');
    await expect(panel.getByLabel('Min bitrate kbps')).toHaveValue('256');
    await page.keyboard.press('Escape');
    await expect(panel).toHaveCount(0);
    await preferredHelp.hover();
    await expect(preferredExplanation).toBeVisible();
    await page.mouse.move(0, 0);
    await expect(preferredExplanation).toHaveCount(0);
    await preferredHelp.click();
    await expect(preferredHelp).toHaveAttribute('aria-expanded', 'true');
    await page.mouse.move(0, 0);
    await expect(preferredExplanation).toBeVisible();
    await page.getByRole('heading', { name: 'Album', exact: true }).click();
    await expect(preferredExplanation).toHaveCount(0);
    await preferredHelp.click();
    await page.keyboard.press('Escape');
    await expect(preferredExplanation).toHaveCount(0);
    await preferredHelp.focus();
    await expect(preferredExplanation).toBeVisible();
    await page.keyboard.press('Tab');
    await expect(preferredExplanation.getByRole('button', { name: 'Edit preferences', exact: true })).toBeFocused();
    await page.getByRole('heading', { name: 'Album', exact: true }).click();
    await expect(preferredExplanation).toHaveCount(0);
    await page.locator('.folder-file-row input[type=checkbox]').first().check();
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
    await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
    await page.getByRole('button', { name: 'Deselect all', exact: true }).click();
    await page.locator('.folder-file-row:not(.locked) input[type=checkbox]').first().check();
    await page.getByRole('button', { name: 'Select all', exact: true }).click();
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
    await expect(page.locator('.search-results-summary')).toBeVisible();
    await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
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
    await expect(page.locator('.folder-file-state.complete')).toHaveCount(4);
    const transfersResponse = await page.request.get('/api/transfers?direction=download');
    expect(transfersResponse.ok(), await transfersResponse.text()).toBe(true);
    const files = (await transfersResponse.json() as Schema<'TransferTimelinePageDto'>).items;
    expect(files.map(file => `${file.identity.username}:${file.status.terminalOutcome}:${file.identity.remotePath}`).sort()).toEqual([
      'fixture-peer:Succeeded:Music\\Artist\\Album\\01 - Track.flac',
      'fixture-peer:Succeeded:Music\\Artist\\Album\\01 - Track.mp3',
      'second-peer:Succeeded:Music\\Artist\\Album\\02 - Track.flac',
      'second-peer:Succeeded:Music\\Artist\\Album\\02 - Track.mp3',
    ]);
    for (const file of files) expect((await stat(file.status.localPath!)).size).toBe(128);
  });
});

test('keeps a grouped track choice and all-except plan through live group reordering', async ({ page, daemon }, testInfo) => {
  await daemon.command('staged-rich-group-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Song Groups', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture');
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await page.locator('#global-search-artist').press('Enter');

  const groups = page.locator('.group-results-list > .group-result-group');
  await expect(groups).toHaveCount(3);
  await expect(page.locator('.search-results-summary')).toContainText('3 groups');
  await expect(groups.nth(0)).toContainText('Fixture Alpha');
  await expect(groups.nth(1)).toContainText('Fixture Beta');
  await expect(groups.nth(2)).toContainText('Fixture Gamma');
  await expect(groups.locator('.audio-detail')).toHaveText(['320 kbps', '320 kbps', '320 kbps']);
  await expect(groups.locator('.item-length-detail')).toHaveText(['3:30', '3:30', '3:30']);
  await expect(groups.first().locator('.group-options-button')).toContainText('2 options');
  const alpha = groups.filter({ hasText: 'Fixture Alpha' });
  const beta = groups.filter({ hasText: 'Fixture Beta' });
  const alternatives = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/alternatives'));
  await alpha.locator('.group-options-button').click();
  const optionsPage = await (await alternatives).json() as Schema<'SearchViewTrackGroupOptionPageDto'>;
  const chosen = optionsPage.items.find(item => item.peer.username === 'second-peer')!;
  expect(chosen).toBeTruthy();
  await page.getByRole('dialog').locator('.group-option').filter({ hasText: 'second-peer' })
    .getByRole('button', { name: 'Use this option', exact: true }).click();
  await page.getByRole('button', { name: 'Select all', exact: true }).click();
  await beta.getByRole('button', { name: 'Deselect Fixture Beta', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  const outputDirectory = testInfo.outputPath('live-track-groups');
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  const options = page.getByRole('region', { name: 'Download options' });
  await options.getByRole('textbox', { name: 'Output directory' }).fill(outputDirectory);
  await options.getByRole('button', { name: 'Close download options', exact: true }).click();

  await daemon.command('next-search-batch');
  await expect(groups.locator('.group-result-identity > strong')).toHaveText([
    'Fixture Alpha', 'Fixture Gamma', 'Fixture Beta', 'Fixture Delta',
  ]);
  await expect(page.locator('.search-results-summary')).toContainText('10 files');
  await expect(alpha.locator('.group-options-button')).toContainText('3 options');
  await expect(alpha.locator('.group-result-source')).toContainText('second-peer');
  await expect(groups.filter({ hasText: 'Fixture Gamma' }).locator('.group-result-source')).toContainText('late-peer');
  await expect(beta.getByRole('button', { name: 'Select Fixture Beta', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  await daemon.command('finish-search');
  await expect(page.locator('.search-results-summary')).toBeVisible();
  await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
  const committed = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Download 2', exact: true }).click();
  const response = await committed;
  const request = response.request().postDataJSON() as Schema<'CommitSearchViewSelectionRequestDto'>;
  expect(request.selection.mode).toBe('AllExcept');
  expect(request.selection.refs).toHaveLength(1);
  expect(request.useRepresentativeWhenUnchosen).toBe(true);
  expect(request.chosenOptions).toEqual({ [optionsPage.groupRef]: chosen.ref });
  expect(request.options?.outputParentDir).toBe(outputDirectory);
  const receipt = await response.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(receipt.submittedCount)).toBe(2);
  expect(Number(receipt.rejectedCount)).toBe(0);
  await page.goto('/downloads');
  let transfers: Schema<'TransferStateDto'>[] = [];
  await expect.poll(async () => {
    const result = await page.request.get('/api/transfers?direction=download&limit=100');
    expect(result.ok(), await result.text()).toBe(true);
    transfers = (await result.json() as Schema<'TransferTimelinePageDto'>).items;
    return transfers.map(item => `${item.identity.username}:${item.status.terminalOutcome}:${item.identity.remotePath}`).sort();
  }).toEqual([
    'fixture-peer:Succeeded:Fixture\\Fixture Gamma.mp3',
    'second-peer:Succeeded:Fixture\\Fixture Alpha.mp3',
  ]);
  for (const transfer of transfers) {
    expect(transfer.status.localPath!.startsWith(outputDirectory + sep)).toBe(true);
    expect((await stat(transfer.status.localPath!)).size).toBe(8_000_000);
  }
  const jobsResponse = await page.request.get(
    `/api/jobs?submissionId=${receipt.submissionId}&includeAll=true&limit=100`);
  expect(jobsResponse.ok(), await jobsResponse.text()).toBe(true);
  const songs = (await jobsResponse.json() as Schema<'JobSummaryDto'>[]).filter(job => job.kind === 'song');
  expect(songs).toHaveLength(2);
  for (const song of songs) {
    const detailResponse = await page.request.get(`/api/jobs/${song.jobId}`);
    expect(detailResponse.ok(), await detailResponse.text()).toBe(true);
    const detail = await detailResponse.json() as Schema<'JobDetailDto'>;
    expect(detail.payload?.kind).toBe('song');
    if (detail.payload?.kind !== 'song') throw new Error('Expected a selected song download.');
    expect(Number(detail.payload.candidateCount)).toBe(1);
  }
});

test('downloads only checked public files from a mixed chosen group album folder', async ({ page, daemon }) => {
  const searchSubmissions: string[] = [];
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/jobs/search/albums')
      searchSubmissions.push(request.url());
  });
  await daemon.command('mixed-album-group-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Groups', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture Album');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.search-results-summary')).toContainText('4 files');
  await expect(page.locator('.search-results-summary')).toContainText('2 locked');
  await page.getByRole('button', { name: 'Filtering', exact: true }).click();
  const config = page.getByRole('region', { name: 'Result search configuration' });
  await config.getByLabel('Hide locked files', { exact: true }).check();
  await expect(page.locator('.folder-file-row.locked')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Remove locked files filter' })).toBeVisible();
  await config.getByRole('button', { name: 'Ranking', exact: true }).click();
  await expect(config.getByLabel('Prefer unlocked files', { exact: true })).toBeChecked();
  await page.getByRole('button', { name: 'Remove locked files filter' }).click();
  await expect(page.locator('.folder-file-row.locked')).toHaveCount(1);
  await page.keyboard.press('Escape');
  await page.locator('.group-options-button').click();
  const dialog = page.getByRole('dialog', { name: /Fixture Album.*options/i });
  await dialog.locator('.group-option').filter({ hasText: 'second-peer' })
    .locator('.group-option-card-picker').click();
  await expect(dialog).toHaveCount(0);
  const album = page.locator('.group-result-group');
  await expect(album.locator('.folder-file-row')).toHaveCount(3);
  await album.focus();
  await page.keyboard.press('Space');
  await expect(album).not.toHaveClass(/selected/);
  await page.keyboard.press('Space');
  await expect(album).toHaveClass(/selected/);
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
  await expect(page.locator('.folder-file-state.complete')).toHaveCount(1);
  await expect(page.locator('.folder-item-card')).toContainText('Intro.flac');
  await expect(page.locator('.folder-item-card')).not.toContainText('Finale.flac');
  await expect(page.locator('.folder-item-card')).not.toContainText('Bonus.flac');
  await expect(page.locator('.folder-item-card')).not.toContainText('Locked.flac');
  const transfers = await page.request.get('/api/transfers?direction=download&limit=100');
  expect(transfers.ok(), await transfers.text()).toBe(true);
  const timeline = await transfers.json() as Schema<'TransferTimelinePageDto'>;
  expect(timeline.items).toHaveLength(1);
  expect(timeline.items[0]).toMatchObject({
    identity: { username: 'second-peer', remotePath: 'Second\\Fixture Album\\01 - Intro.flac' },
    status: { terminalOutcome: 'Succeeded' },
    progress: { totalBytes: 128, bytesTransferred: 128 },
  });
  expect((await stat(timeline.items[0]!.status.localPath!)).size).toBe(128);
  expect(searchSubmissions).toHaveLength(1);
});

test('pages album alternative files without hiding the card picker', async ({ page, daemon }) => {
  await daemon.command('paged-album-group-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Groups', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture Album');
  await page.locator('#global-search-simple').press('Enter');
  await page.locator('.group-options-button').click();
  const dialog = page.getByRole('dialog', { name: /Fixture Album.*options/i });
  await expect(dialog.locator('.group-option')).toHaveCount(100);
  await page.getByRole('button', { name: 'Load more', exact: true }).click();
  await expect(dialog.locator('.group-option')).toHaveCount(101);
  const alternative = dialog.locator('.group-option').filter({ hasText: 'second-peer' });
  await expect(alternative.locator('.folder-file-row')).toHaveCount(25);
  await alternative.locator('.group-option-card-picker').click();
  await expect(dialog).toHaveCount(0);
  await expect(page.locator('.group-result-group')).toContainText('25 - Track.flac');
});

test('preserves an independent global draft when opening a retained search', async ({ page, daemon }) => {
  await daemon.command('album-group-search');
  await page.goto('/jobs');
  await page.locator('#global-search-simple').fill('Fixture Album');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-item-card')).toHaveCount(2);
  // A direct link opens the result without populating the new-search composer.
  await page.reload();
  await expect(page.locator('.folder-item-card')).toHaveCount(2);
  await expect(page.locator('#global-search-simple')).toHaveValue('');
  await page.getByRole('button', { name: 'Back to jobs', exact: true }).click();
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await page.locator('#global-search-artist').fill('Fixture');
  await expect(page.locator('#global-search-title')).toHaveValue('');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Groups', exact: true }).click();
  await page.locator('.search-history-open').filter({ hasText: 'Fixture Album' }).click();
  await expect(page.locator('.folder-item-card')).toHaveCount(2);
  await expect(page.locator('#global-search-artist')).toHaveValue('Fixture');
  await expect(page.locator('#global-search-title')).toHaveValue('');
  await expect(page.getByRole('button', { name: /Search result mode: Album Groups/ })).toBeVisible();
  const accepted = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/jobs/search/albums');
  await page.locator('#global-search-artist').press('Enter');
  const response = await accepted;
  expect(response.request().postDataJSON().albumQuery).toMatchObject({ artist: 'Fixture', album: null, searchHint: null });
  const job = await response.json() as Schema<'JobSummaryDto'>;
  const title = job.itemName ?? job.queryText!;
  expect(title).toBe('Fixture');
  await expect(page.locator('.search-results-title h1')).toHaveText(title);
  await expect(page.locator('.group-results-list > .group-result-group')).not.toHaveCount(0);
  await expect(page.locator('.search-results-summary')).toContainText(/\d+ groups?/);
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Jobs' }).click();
  await expect(page.locator('.search-history-row').filter({ hasText: title }).getByText(title, { exact: true })).toBeVisible();
});

test('distinguishes waiting for peers from a completed search with no returned files', async ({ page, daemon }) => {
  // Delay real live updates during the browser-boundary projection check below.
  let holdUpdates = false;
  let releaseUpdates!: () => void;
  const released = new Promise<void>(resolve => { releaseUpdates = resolve; });
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    server.onMessage(async message => {
      if (holdUpdates) await released;
      route.send(message);
    });
  });
  await daemon.command('empty-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Empty fixture');
  await page.locator('#global-search-simple').press('Enter');
  const loading = page.locator('.loading-indicator.blocking');
  await expect(loading).toHaveAttribute('aria-label', 'Searching');
  await expect(loading).toHaveText('');
  await expect(loading).toHaveCSS('border-style', 'none');
  await expect(loading.locator('.resource-state-dot')).toBeVisible();
  await expect(page.locator('.search-results-empty')).toHaveCount(0);
  await daemon.command('finish-search');
  await expect(loading).toHaveCount(0);
  await expect(page.locator('.search-results-empty')).toHaveText('No resultsNo files were returned by peers.');
  const refined = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname));
  await page.getByRole('textbox', { name: 'Filter search results' }).fill('No such file');
  await refined;
  await expect(page.locator('.search-results-summary')).toBeVisible();
  await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
  await expect(page.locator('.search-results-empty')).toHaveText('No resultsNo files were returned by peers.');

  // A finished job can have a fresh projection that has not caught up yet.
  // Simulate pending projection/count fields, keeping real job state and empty pages.
  await page.route('**/api/jobs/*/search-views', async route => {
    const response = await route.fetch();
    const summary = await response.json() as Schema<'SearchViewSummaryDto'>;
    await route.fulfill({ response, json: { ...summary, isComplete: false,
      counters: { ...summary.counters, publicFileCount: 12 } } });
  });
  await page.route('**/api/search-views/*/updates?**', async route => {
    await released;
    await route.continue();
  });
  holdUpdates = true;
  try {
    await page.reload();
    await expect(page.locator('.search-results-summary .search-status-badge')).toHaveText('Updating results');
    await expect(loading).toHaveAttribute('aria-label', 'Loading search results');
    await expect(loading).toHaveText('');
    await expect(page.getByText('Searching', { exact: true })).toHaveCount(0);
  } finally {
    holdUpdates = false;
    releaseUpdates();
  }
  await expect(loading).toHaveCount(0);
  await expect(page.locator('.search-results-empty')).toHaveText('No resultsNo files were returned by peers.');
});

test('explains search modes in selectable cards without submitting a search', async ({ page }) => {
  let submissions = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.startsWith('/api/jobs/search')) submissions++;
  });
  await page.goto('/jobs');
  await page.locator('#global-search-simple').fill('Fixture');
  const chooser = page.getByRole('button', { name: /Search result mode:.*Choose mode/ });
  const help = page.getByRole('dialog', { name: 'Search modes', exact: true });
  async function openHelp() {
    await chooser.click();
    await page.getByRole('menuitem', { name: 'About search modes', exact: true }).click();
    await expect(help).toBeVisible();
    await expect(page.getByRole('menu', { name: 'Search result mode', exact: true })).toHaveCount(0);
  }
  for (const mode of ['File Search', 'Track Search', 'Album Search', 'Song Groups', 'Album Groups']) {
    await openHelp();
    await expect(help.locator('.search-mode-help-card')).toHaveCount(5);
    await help.getByRole('button', { name: `Use ${mode}`, exact: true }).click();
    await expect(help).toHaveCount(0);
    await expect(chooser).toHaveAttribute('aria-label', `Search result mode: ${mode}. Choose mode.`);
    await expect(page.locator('#global-search-simple')).toHaveValue('Fixture');
  }
  await openHelp();
  await help.getByRole('button', { name: 'Close search modes', exact: true }).click();
  await expect(help).toHaveCount(0);
  await openHelp();
  await page.mouse.click(5, 5);
  await expect(help).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  await openHelp();
  for (const card of await help.locator('.search-mode-help-card').all()) {
    expect(await card.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    await expect(card.locator('.search-mode-help-icon svg')).toHaveCSS('width', '42px');
  }
  await expect(help.getByRole('button', { name: 'Use Album Groups', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await page.keyboard.press('Escape');
  await expect(help).toHaveCount(0);
  expect(submissions).toBe(0);
});

test('cycles search modes from focused inputs', async ({ page }) => {
  await page.goto('/jobs');
  await chooseFileSearch(page);
  const simple = page.locator('#global-search-simple');
  const mode = page.getByRole('button', { name: /Search result mode:.*Choose mode/ });
  await simple.focus();
  await simple.press('ArrowUp');
  await expect(mode).toHaveAttribute('aria-label', /Album Groups/);
  await simple.press('ArrowDown');
  await expect(mode).toHaveAttribute('aria-label', /File Search/);
  await simple.press('ArrowDown');
  await expect(mode).toHaveAttribute('aria-label', /Track Search/);
  await simple.fill('Artist - Title');
  const title = page.locator('#global-search-title');
  await expect(title).toBeFocused();
  await title.press('ArrowUp');
  await expect(mode).toHaveAttribute('aria-label', /File Search/);
  await expect(simple).toBeFocused();
  await expect(simple).toHaveValue('Artist Title');
  await page.goto('/users');
  const user = page.locator('#global-user-search');
  await user.focus();
  await user.press('ArrowDown');
  await expect(page.getByRole('button', { name: /User browse mode: Shares/ })).toBeVisible();
  await user.press('ArrowUp');
  await expect(page.getByRole('button', { name: /User browse mode: User/ })).toBeVisible();
});

for (const mode of ['Track Search', 'Song Groups'] as const) {
  test(`Backspace merges an empty second field in ${mode}`, async ({ page }) => {
    await page.goto('/jobs');
    await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
    await page.getByRole('menuitemradio', { name: mode, exact: true }).click();
    const input = page.locator('#global-search-simple');
    await input.fill('Casiopea');
    await page.getByRole('button', { name: 'split', exact: true }).click();
    await expect(page.locator('#global-search-title')).toHaveValue('');
    await page.locator('#global-search-title').press('Backspace');
    await expect(input).toHaveValue('Casiopea');
    await expect(input).toBeFocused();
    expect(await input.evaluate((element: HTMLInputElement) => element.selectionStart)).toBe('Casiopea'.length);
  });
}

test('keeps the search highlight steady while splitting and merging', async ({ page }) => {
  await page.goto('/jobs');
  const input = page.locator('#global-search-simple');
  await input.fill('Casiopea');
  const bar = await page.locator('.search-bar').elementHandle();
  if (!bar) throw new Error('Search bar is missing');
  await bar.evaluate(async element => { await Promise.all(element.getAnimations().map(animation => animation.finished)); });
  const highlight = await bar.evaluate(element => ({ border: getComputedStyle(element).borderColor, shadow: getComputedStyle(element).boxShadow }));
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await expect(page.locator('#global-search-title')).toBeFocused();
  expect(await bar.evaluate(element => element === document.querySelector('.search-bar'))).toBe(true);
  expect(await bar.evaluate(element => ({ border: getComputedStyle(element).borderColor, shadow: getComputedStyle(element).boxShadow }))).toEqual(highlight);
  await page.locator('#global-search-title').press('Backspace');
  await expect(input).toBeFocused();
  expect(await bar.evaluate(element => element === document.querySelector('.search-bar'))).toBe(true);
  expect(await bar.evaluate(element => ({ border: getComputedStyle(element).borderColor, shadow: getComputedStyle(element).boxShadow }))).toEqual(highlight);
});

test('keeps search configuration resolved when splitting and merging', async ({ page }) => {
  await page.goto('/jobs');
  await page.locator('#global-search-simple').fill('Casiopea');
  await page.getByRole('button', { name: 'Search configuration', exact: true }).click();
  const panel = page.locator('.search-config-popover');
  await expect(panel.getByRole('button', { name: 'Ranking', exact: true })).toBeVisible();
  let requests = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname === '/api/jobs/effective-settings') requests++;
  });
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'Ranking', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'merge', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'Ranking', exact: true })).toBeVisible();
  expect(requests).toBe(0);
});

test('merges empty album and artist fields without losing input or focus', async ({ page }) => {
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
  await page.locator('#global-search-title').press('Backspace');
  await expect(input).toHaveValue('Correct artist');
  await expect(input).toBeFocused();
  expect(await input.evaluate((element: HTMLInputElement) => element.selectionStart)).toBe('Correct artist'.length);
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await page.locator('#global-search-title').fill('Retained album');
  await page.locator('#global-search-artist').fill('');
  await page.locator('#global-search-artist').press('Backspace');
  await expect(input).toHaveValue('Retained album');
  await expect(input).toBeFocused();
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Album Groups', exact: true }).click();
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await page.locator('#global-search-title').press('Backspace');
  await expect(input).toHaveValue('Retained album');
  await expect(input).toBeFocused();
});

test('cancels the active search and deletes it from the result header', async ({ page, daemon }) => {
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
  await expect(page.getByRole('status', { name: 'Opening job', exact: true })).toBeVisible();
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
    await expect(page.locator('.search-results-summary')).toBeVisible();
    await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
    await expect(page.locator('.folder-file-row')).toHaveCount(0);
    await page.reload();
    await page.locator('#global-search-simple').fill('Unsubmitted draft');
    const replayResponse = page.waitForResponse(response => response.request().method() === 'POST'
      && new URL(response.url()).pathname === `/api/jobs/${originalChildId}/rerun-search`);
    await page.getByRole('button', { name: 'Search again', exact: true }).click();
    const accepted = await replayResponse;
    expect(accepted.status(), await accepted.text()).toBe(202);
    await expect(page).not.toHaveURL(originalChildUrl);
    await expect(page.locator('#global-search-simple')).toHaveValue('Unsubmitted draft');
    expect(replayRequests).toContain(`/api/jobs/${originalChildId}/rerun-search`);
    await expect(page.locator('.search-results-summary')).toBeVisible();
    await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
    await expect(page.locator('.folder-file-row')).toHaveCount(0);

    const replayedJobId = new URL(page.url()).pathname.split('/').at(-1)!;
    const replayedDetail = await page.request.get(`/api/jobs/${replayedJobId}`);
    expect(replayedDetail.ok()).toBeTruthy();
    const replayed = await replayedDetail.json() as { summary: { submissionId: string } };
    expect(replayed.summary.submissionId).toBeTruthy();
    expect(replayed.summary.submissionId).not.toBe(root.submissionId);
    const replayedJobs = await page.request.get(
      `/api/jobs?submissionId=${replayed.summary.submissionId}&includeAll=true`);
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
  await expect(page.locator('.search-results-summary')).toBeVisible();
  await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
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
  await expect(page.getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
  const timelineResponse = await page.request.get('/api/transfers?direction=download&limit=100');
  expect(timelineResponse.ok(), await timelineResponse.text()).toBe(true);
  const timeline = await timelineResponse.json() as Schema<'TransferTimelinePageDto'>;
  expect(timeline.items).toHaveLength(1);
  const completed = timeline.items.find(item => item.identity.remotePath?.endsWith('Fixture song.txt'));
  expect(completed?.status.terminalOutcome).toBe('Succeeded');
  expect(completed?.status.localPath).toBeTruthy();
  const bytes = await readFile(completed!.status.localPath!);
  expect(bytes).toHaveLength(128);
  expect(bytes.every((byte) => byte === 0)).toBe(true);
  await expect(page.getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.locator('.page')).toContainText('Fixture song.txt');
  await expect(page.getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
  await page.getByRole('navigation', { name: 'Primary navigation' })
    .getByRole('button', { name: 'Jobs' }).click();
  const reopened = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/jobs/${sourceId}/search-views`);
  await page.locator('.search-history-row:not(.automatic-history-row)').filter({ hasText: 'Fixture' })
    .locator('.search-history-open').click();
  let cached = await (await reopened).json() as Schema<'SearchViewSummaryDto'>;
  await expect(page.locator('.folder-file-row').filter({ hasText: 'Fixture song.txt' })).toBeVisible();
  await expect(page.locator('.search-results-summary')).toBeVisible();
  await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
  const filteredAgain = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/jobs/${sourceId}/search-views`
    && response.request().postDataJSON().filterText === 'song');
  await page.getByRole('textbox', { name: 'Filter search results', exact: true }).fill('song');
  await filteredAgain;
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
  await expect(page.getByRole('textbox', { name: 'Filter search results', exact: true })).toHaveValue('song');
  await expect(page.getByLabel('Sort', { exact: true })).toHaveValue('speed');
  await expect(page).toHaveURL(new RegExp(`/jobs/${sourceId}(?:[?]|$)`));
  await expect(page.locator('.search-results-summary')).toBeVisible();
  await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
  const cachedSummary = await (await page.request.get(`/api/search-views/${cached.viewId}`)).json() as Schema<'SearchViewSummaryDto'>;
  await daemon.command(`prune-search-results:${sourceId}`);
  // An active read snapshot stays usable when only its raw source is pruned.
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
  await expect(page.locator('.search-results-summary')).toBeVisible();
  await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
  await page.getByRole('navigation', { name: 'Primary navigation' })
    .getByRole('button', { name: 'Dashboard' }).click();
  await expect(page.locator('.dashboard-metric-card').first()).toContainText('128 B');
});
test('retrieves complete folder contents with retry while preserving selection', async ({ page, daemon }) => {
  await daemon.command('directory-selection-search');
  let childPageRequests = 0;
  page.on('request', request => {
    if (/\/directories\/[^/]+\/files$/.test(new URL(request.url()).pathname)) childPageRequests += 1;
  });
  await page.goto('/jobs');
  expect(await page.evaluate(() => typeof crypto.randomUUID)).toBe('undefined');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-file-row')).toHaveCount(2);
  expect(childPageRequests).toBe(0);
  const selected = page.locator('.folder-file-row').filter({ has: page.getByText('Selected.txt', { exact: true }) }).getByRole('checkbox');
  await selected.check();
  const jobList = await page.context().newPage();
  await jobList.goto('/jobs');
  await expect(jobList.locator('.search-history-row')).toHaveCount(1);
  // Opening the Jobs tab focused it. Return to the results like a user would;
  // background tabs throttle animation frames and make actionability checks slow.
  await page.bringToFront();
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
  const retrieved = page.waitForResponse(response => response.request().method() === 'POST'
    && /\/directories\/retrieve$/.test(new URL(response.url()).pathname) && response.ok());
  await retry.click();
  const retrievalResponse = await retrieved;
  expect(retrievalResponse.request().postDataJSON()).toEqual(originalRequest);
  const retrieval = await retrievalResponse.json() as Schema<'JobSummaryDto'>;
  expect(retrieval.jobId).toBe(acceptedJobId);
  await expect(page.getByLabel('Full folder loaded', { exact: true })).toBeVisible();
  await expect(page.locator('.folder-file-row')).toHaveCount(5);
  await expect(page.locator('.folder-file-count strong')).toHaveText('5');
  await expect(page.getByText('Selection from earlier results', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeVisible();
  await expect(page.locator('.folder-file-row').filter({ hasText: 'DeepTwo.txt' })).toBeVisible();
  await expect(page.locator('.folder-subfolder-row')).toHaveCount(2);
  await page.getByRole('button', { name: 'Deselect all', exact: true }).click();
  await selected.check();
  const nested = page.getByRole('checkbox', { name: 'Select all matching files in Nested', exact: true });
  await nested.check();
  await expect(page.getByRole('button', { name: 'Download 4', exact: true })).toBeVisible();
  await nested.uncheck();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeVisible();
  await page.getByRole('checkbox', { name: 'Select all files in Fixture', exact: true }).check();
  await expect(page.getByRole('button', { name: 'Download 5', exact: true })).toBeVisible();
  // Retrieval remains observable directly, but is not a separate main-list task.
  await expect(jobList.locator('.search-history-row')).toHaveCount(1);
  await expect(jobList.locator('.search-history-row')).not.toContainText('Retrieve Folder');
  await jobList.close();
  await page.goto('/jobs');
  await expect(page.locator('.search-history-row')).toHaveCount(1);
  await expect(page.locator('.search-history-row')).not.toContainText('Retrieve Folder');
  const ordinary = await page.request.get('/api/jobs?excludeKinds=retrieve-folder');
  expect(await ordinary.json()).toHaveLength(1);
  const all = await page.request.get('/api/jobs');
  expect((await all.json()).some((job: Schema<'JobSummaryDto'>) => job.jobId === retrieval.jobId)).toBe(true);
  await page.goto(`/jobs/${retrieval.jobId}`);
  const facts = page.locator('.job-detail-facts');
  await expect(page.locator('.job-detail-heading-summary .search-status-badge')).toHaveCount(0);
  await expect(page.locator('.job-detail-heading-summary')).toContainText('3 new files');
  await expect(facts.locator('dt')).toHaveText(['From', 'Folder', 'Outcome', 'New files']);
  await expect(facts.locator('dd')).toHaveText(['fixture-peer', 'Fixture', 'Completed', '3']);
  await expect(facts.locator('dd').nth(1)).toHaveAttribute('title', 'Fixture');
  await page.reload();
  await expect(facts.locator('dd')).toHaveText(['fixture-peer', 'Fixture', 'Completed', '3']);
  await facts.getByRole('button', { name: 'Actions for fixture-peer', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Shares', exact: true }).click();
  await expect(page).toHaveURL(/\/shares\/fixture-peer$/);
});

test('downloads a File Search subfolder with a snapshot-bound path rule and a file exclusion', async ({ page, daemon }) => {
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

  // Arrivals update the listing but cannot expand the selected path's snapshot.
  // The callback is still live: no completion-dependent projection is allowed.
  await daemon.command('next-search-batch');
  await expect(page.locator('.folder-file-row')).toHaveCount(7);
  await expect(page.locator('.search-results-summary .search-status-badge')).toBeVisible();
  await expect(page.getByText('Selection from earlier results', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
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
  await expect(page.locator('.search-results-summary')).toBeVisible();
  await expect(page.locator('.search-results-summary .search-status-badge')).toHaveCount(0);
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
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  expect(retrievalRequests).toBe(1);
  expect(searchRequests).toBe(1);
  expect(countRevisions, 'Unpublished projections cannot provide a useful selection count.').not.toContain(0);
  const committed = page.waitForRequest(request => request.method() === 'POST'
    && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(request.url()).pathname));
  await page.getByRole('button', { name: 'Download 2', exact: true }).click();
  const body = (await committed).postDataJSON();
  expect(new URL((await committed).url()).pathname).toBe(`/api/search-views/${restoredView.viewId}/commit`);
  expect(body.selection.mode).toBe('Only');
  expect(body.directoryPathRules).toEqual(expect.arrayContaining([
    expect.objectContaining({ relativePath: 'Nested', selected: true }),
  ]));
  await expect(page.locator('.mutation-status')).toContainText('2 downloads requested');
  await page.goto('/downloads');
  await expect(page.locator('.page')).toContainText('Nested.txt');
  await expect(page.locator('.page')).toContainText('DeepOne.txt');
  await expect(page.locator('.page')).not.toContainText('LateNested.txt');
  await expect(page.locator('.page')).not.toContainText('DeepTwo.txt');
  await expect(page.locator('.page')).not.toContainText('LateRoot.txt');
  await expect(page.locator('.page')).not.toContainText('Selected.txt');
  await expect(page.locator('.page')).not.toContainText('Unselected.txt');
  await expect(page.locator('.folder-file-state.complete')).toHaveCount(2);
  const transfersResponse = await page.request.get('/api/transfers?direction=download&limit=100');
  expect(transfersResponse.ok(), await transfersResponse.text()).toBe(true);
  const transfers = (await transfersResponse.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(transfers.map(item => `${item.identity.username}:${item.status.terminalOutcome}:${item.identity.remotePath}`).sort()).toEqual([
    'fixture-peer:Succeeded:Fixture\\Nested\\Deep\\DeepOne.txt',
    'fixture-peer:Succeeded:Fixture\\Nested\\Nested.txt',
  ].sort());
  for (const transfer of transfers) expect((await stat(transfer.status.localPath!)).size).toBe(128);
});

test('paints the Jobs tab before remounting cached search results and cancels on navigation', async ({ page, daemon }) => {
  await daemon.command('directory-selection-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.folder-item-card')).toHaveCount(1);
  const navigation = page.getByRole('navigation', { name: 'Primary navigation' });
  await navigation.getByRole('button', { name: 'Dashboard', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();

  await page.evaluate(() => {
    const originalRequest = window.requestAnimationFrame;
    const originalCancel = window.cancelAnimationFrame;
    const callbacks = new Map<number, FrameRequestCallback>();
    let next = 0;
    window.requestAnimationFrame = callback => { callbacks.set(++next, callback); return next; };
    window.cancelAnimationFrame = id => { callbacks.delete(id); };
    Object.assign(window, {
      advanceSearchFrame: () => {
        const queued = [...callbacks.values()];
        callbacks.clear();
        for (const callback of queued) callback(performance.now());
      },
      restoreSearchFrames: () => { window.requestAnimationFrame = originalRequest; window.cancelAnimationFrame = originalCancel; },
    });
  });
  const clickNav = (label: string) => page.evaluate(name => {
    document.querySelector<HTMLButtonElement>(`nav[aria-label="Primary navigation"] button[aria-label="${name}"]`)!.click();
  }, label);
  const advance = () => page.evaluate(() => (window as unknown as { advanceSearchFrame: () => void }).advanceSearchFrame());
  try {
    await clickNav('Jobs');
    await expect(navigation.getByRole('button', { name: 'Jobs', exact: true })).toHaveAttribute('aria-current', 'page');
    await expect(page.locator('.folder-item-card')).toHaveCount(0);
    await advance();
    await expect(page.locator('.folder-item-card')).toHaveCount(0);
    await clickNav('Dashboard');
    await advance();
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    await expect(page.locator('.folder-item-card')).toHaveCount(0);
    await clickNav('Jobs');
    await advance();
    await expect(page.locator('.folder-item-card')).toHaveCount(0);
    await advance();
    await expect(page.locator('.folder-item-card')).toHaveCount(1);
  } finally {
    await page.evaluate(() => (window as unknown as { restoreSearchFrames: () => void }).restoreSearchFrames());
  }
});

test('navigates result pages by keyboard without stealing editing or modal controls', async ({ page, daemon }) => {
  await daemon.command('rich-group-search');
  // Exercise real server cursors with a small fixture, rather than creating a
  // load test merely to cross the production page size.
  await page.route('**/api/search-views/*/track-groups?*', route => {
    const url = new URL(route.request().url());
    url.searchParams.set('limit', '1');
    return route.continue({ url: url.toString() });
  });
  await page.goto('/');
  const input = page.locator('#global-search-simple');
  await input.fill('2');
  await input.press('ArrowDown');
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
  await input.blur();
  await page.keyboard.press('2');
  await expect(page.getByRole('heading', { name: 'Jobs', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await page.keyboard.press('1');
  await expect(page.getByRole('heading', { name: 'Jobs', exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Song Groups', exact: true }).click();
  await input.fill('Fixture');
  await page.getByRole('button', { name: 'split', exact: true }).click();
  await page.locator('#global-search-artist').press('Enter');
  const groups = page.locator('.group-results-list > .group-result-group');
  await expect(groups).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Filtering', exact: true })).toBeEnabled();
  await page.locator('#global-search-artist').blur();
  await page.keyboard.press('ArrowDown');
  await expect(groups.first()).toBeFocused();
  await page.keyboard.press('Space');
  await expect(groups.first()).toHaveClass(/selected/);
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  await page.keyboard.press('Escape');
  await expect(groups.first()).not.toHaveClass(/keyboard-current/);
  await expect(groups.first()).toHaveClass(/selected/);
  await page.keyboard.press('ArrowDown');
  await expect(groups.first()).toHaveClass(/keyboard-current/);
  await page.locator('.search-results-heading h1').click();
  await expect(groups.first()).not.toHaveClass(/keyboard-current/);
  await expect(groups.first()).toHaveClass(/selected/);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Tab');
  await expect(groups.first().locator('.group-header-select-button')).toBeFocused();
  // Enter on a native button toggles it; it must not submit the download.
  let commits = 0;
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/commit')) commits++; });
  await page.keyboard.press('Enter');
  await expect(groups.first()).not.toHaveClass(/selected/);
  expect(commits).toBe(0);
  await page.keyboard.press('ArrowDown');
  await expect(groups).toHaveCount(2);
  await expect(groups.nth(1)).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await expect(groups.first()).toBeFocused();
  await page.getByRole('button', { name: 'Filtering', exact: true }).click();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Space');
  await expect(groups.first()).not.toHaveClass(/selected/);
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Back to jobs', exact: true }).click();
  await expect(page.locator('.search-history-open')).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('.search-history-open')).toBeFocused();
  await expect(page.locator('.search-history-open')).toHaveCSS('outline-style', 'none');
  await page.keyboard.press('Escape');
  await expect(page.locator('.search-history-row.keyboard-current')).toHaveCount(0);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Download jobs', exact: true })).toBeFocused();
  await page.keyboard.press('ArrowLeft');
  await expect(page.getByRole('tab', { name: 'Searches', exact: true })).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(groups.first()).toBeVisible();
});

test('retrieves selected folders with R and downloads only with Ctrl+Enter', async ({ page, daemon }) => {
  await daemon.command('directory-selection-search');
  await page.goto('/jobs');
  await chooseFileSearch(page);
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  const folder = page.locator('.folder-item-card');
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(page.getByRole('button', { name: 'Filtering', exact: true })).toBeEnabled();
  await folder.focus();
  await page.keyboard.press('Space');
  await expect(page.getByRole('button', { name: 'Download 2', exact: true })).toBeEnabled();
  await page.keyboard.press('R');
  await expect(page.getByLabel('Full folder loaded', { exact: true })).toBeVisible();
  await expect(folder.locator('.folder-file-row')).toHaveCount(5);
  // Explicitly select the refreshed snapshot, including the newly retrieved files.
  await page.getByRole('button', { name: 'Deselect all', exact: true }).click();
  await folder.focus();
  await page.keyboard.press('Space');
  await expect(page.getByRole('button', { name: 'Download 5', exact: true })).toBeEnabled();
  let commits = 0;
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/commit')) commits++; });
  await page.keyboard.press('Enter');
  await expect(folder).toBeFocused();
  expect(commits).toBe(0);
  const accepted = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname.endsWith('/commit'));
  await page.getByRole('button', { name: 'Download 5', exact: true }).focus();
  await page.keyboard.press('Control+Enter');
  expect((await accepted).ok()).toBe(true);
  await expect(page.getByRole('button', { name: 'View download jobs', exact: true })).toBeVisible();
  expect(commits).toBe(1);
});
