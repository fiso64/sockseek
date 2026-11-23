import { nestedPageSize } from '../api-defaults';
import { test, expect } from './fixtures';
import type { Schema } from '../src/api/types';
import { readdir, stat, mkdir, writeFile } from 'node:fs/promises';
import { join, relative } from 'node:path';

test('uses filtering and output settings for a direct Start download', async ({ page, daemon }, testInfo) => {
  await daemon.command('music-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await expect(page.getByRole('button', { name: 'Start', exact: true })).toBeDisabled();
  await page.getByPlaceholder('Song title…').press('Control+Enter');
  await expect(page.getByRole('dialog', { name: 'New job' })).toBeVisible();
  await expect(page.locator('#new-job-options-download')).toHaveAttribute('aria-selected', 'true');
  await page.getByPlaceholder('Song title…').fill('Fixture song');
  await page.locator('#new-job-options-filtering').click();
  const filtering = page.locator('#new-job-options-panel');
  await expect(filtering.getByRole('button', { name: 'FLAC', exact: true })).toBeVisible();
  await page.getByTestId('search-config-clear').click();
  await expect(filtering.getByRole('button', { name: 'Any', exact: true })).toHaveClass(/active/);
  await page.getByTestId('search-config-reset').click();
  for (const format of ['AAC', 'ALAC']) {
    const button = filtering.getByRole('button', { name: format, exact: true });
    await expect(button).toHaveClass(/active/);
    await button.click();
    await expect(button).toHaveCount(0);
  }
  await filtering.getByRole('button', { name: 'custom…', exact: true }).click();
  await filtering.getByRole('textbox', { name: 'Comma-separated required formats' }).fill('flac, ape, flac');
  await filtering.getByRole('button', { name: 'buttons', exact: true }).click();
  await expect(filtering.getByRole('button', { name: 'FLAC', exact: true })).toHaveCount(1);
  const customFormat = filtering.getByRole('button', { name: 'APE', exact: true });
  await expect(customFormat).toHaveClass(/active/);
  await customFormat.click();
  await expect(customFormat).toHaveCount(0);
  await filtering.getByRole('button', { name: 'Any', exact: true }).click();
  await filtering.getByRole('button', { name: 'FLAC', exact: true }).click();
  await page.locator('#new-job-options-download').click();
  let tabSettingsReads = 0;
  const countTabSettingsReads = (request: import('@playwright/test').Request) => {
    if (new URL(request.url()).pathname === '/api/jobs/effective-settings') tabSettingsReads++;
  };
  page.on('request', countTabSettingsReads);
  await page.locator('#new-job-options-ranking').click();
  await page.locator('#new-job-options-ranking').press('ArrowLeft');
  await expect(page.locator('#new-job-options-filtering')).toBeFocused();
  await expect(filtering.getByRole('button', { name: 'FLAC', exact: true })).toHaveClass(/active/);
  await expect(filtering.getByRole('button', { name: 'MP3', exact: true })).not.toHaveClass(/active/);
  await page.locator('#new-job-options-download').click();
  await expect(page.getByLabel('Output directory', { exact: true })).toBeVisible();
  expect(tabSettingsReads).toBe(0);
  page.off('request', countTabSettingsReads);
  const destination = testInfo.outputPath('direct-song');
  await page.getByLabel('Output directory', { exact: true }).fill(destination);
  await page.getByLabel('Name format', { exact: true }).fill('direct/{stitle}');
  await page.getByRole('button', { name: 'Profiles', exact: true }).click();
  await page.getByRole('checkbox', { name: 'download-options', exact: true }).check();
  await page.getByRole('button', { name: 'Profiles', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: 'Skip existing downloads', exact: true })).not.toBeChecked();
  await expect(page.getByLabel('Output directory', { exact: true })).toHaveValue(destination);
  await expect(page.getByLabel('Name format', { exact: true })).toHaveValue('direct/{stitle}');
  await expect(page.getByPlaceholder('Song title…')).toHaveValue('Fixture song');
  const request = page.waitForRequest(candidate => candidate.method() === 'POST'
    && new URL(candidate.url()).pathname === '/api/job-previews');
  await expect(page.getByRole('button', { name: 'Start', exact: true })).toBeEnabled();
  await page.getByLabel('Name format', { exact: true }).press('Control+Enter');
  const started = (await request).postDataJSON() as Schema<'CreateJobPreviewRequestDto'>;
  expect(started.options?.profileNames).toEqual(['download-options']);
  expect(started.options?.downloadSettings?.skip).toBeUndefined();
  expect(started.options?.downloadSettings?.search?.necessaryCond?.formats?.replace).toEqual(['flac']);
  expect(started.options?.downloadSettings?.output?.nameFormat).toBe('direct/{stitle}');
  await expect(page.locator('.automatic-job-detail h1')).toHaveText('Fixture song');
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Downloads', exact: true }).click();
  await expect(page.locator('.folder-item-card').getByRole('button', { name: 'Complete', exact: true })).toHaveClass(/folder-file-state/);
  const transfersResponse = await page.request.get('/api/transfers?direction=download&limit=500');
  expect(transfersResponse.ok(), await transfersResponse.text()).toBe(true);
  const transfers = (await transfersResponse.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(transfers).toHaveLength(1);
  expect(transfers[0]!.status.terminalOutcome).toBe('Succeeded');
  expect(transfers[0]!.identity.remotePath).toBe('Fixture\\Fixture song.flac');
  expect(transfers[0]!.status.localPath).toBe(join(destination, 'direct', 'Fixture song.flac'));
  expect((await stat(transfers[0]!.status.localPath!)).size).toBe(128);
  const files = await readdir(destination, { recursive: true, withFileTypes: true });
  expect(files.filter(file => file.isFile()).map(file => relative(destination, join(file.parentPath, file.name))))
    .toEqual([join('direct', 'Fixture song.flac')]);
});

test('Ctrl+Enter opens New Job with the current query and edited search conditions', async ({ page }) => {
  let searches = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.startsWith('/api/jobs/search')) searches++;
  });
  for (const mode of ['File Search', 'Album Search', 'Track Search']) {
    const format = mode === 'File Search' ? 'PDF' : 'FLAC';
    const origin = mode === 'Album Search' ? '/dashboard' : '/system';
    await page.goto(origin);
    await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
    await page.getByRole('menuitemradio', { name: mode, exact: true }).click();
    await page.locator('#global-search-simple').fill(mode === 'Album Search' ? 'Fixture artist - Fixture album' : 'Fixture song');
    await page.getByRole('button', { name: 'Search configuration', exact: true }).click();
    const panel = page.locator('.search-config-popover');
    await panel.getByRole('button', { name: 'Any', exact: true }).click();
    await panel.getByRole('button', { name: format, exact: true }).click();
    const input = page.locator(mode === 'Album Search' ? '#global-search-title' : '#global-search-simple');
    await input.press('Control+Enter');
    const dialog = page.getByRole('dialog', { name: 'New job', exact: true });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByPlaceholder('Artist…', { exact: true })).toBeFocused();
    await expect(page).toHaveURL(new RegExp(`${origin}$`));
    await expect(page.locator(mode === 'Album Search' ? '.dashboard-page' : '.system-page')).toBeVisible();
    await expect(dialog.getByPlaceholder(mode === 'Album Search' ? 'Album title…' : 'Song title…')).toHaveValue(mode === 'Album Search' ? 'Fixture album' : 'Fixture song');
    await expect(dialog.getByPlaceholder('Artist…', { exact: true })).toHaveValue(mode === 'Album Search' ? 'Fixture artist' : '');
    await dialog.locator('#new-job-options-filtering').click();
    const filtering = dialog.locator('#new-job-options-panel');
    await expect(filtering.getByRole('button', { name: format, exact: true })).toHaveClass(/active/);
    await expect(filtering.getByRole('button', { name: 'MP3', exact: true })).not.toHaveClass(/active/);
    // Further edits in the modal must override, not discard or resurrect, the copied settings.
    if (mode === 'Track Search') await filtering.getByRole('button', { name: 'MP3', exact: true }).click();
    const request = page.waitForRequest(candidate => candidate.method() === 'POST'
      && new URL(candidate.url()).pathname === '/api/job-previews');
    await dialog.getByRole('button', { name: 'Review', exact: true }).click();
    const preview = (await request).postDataJSON() as Schema<'CreateJobPreviewRequestDto'>;
    expect(preview.job.kind).toBe(mode === 'Album Search' ? 'album' : 'song');
    expect(preview.options?.downloadSettings?.search?.necessaryCond?.formats?.replace)
      .toEqual(mode === 'Track Search' ? ['flac', 'mp3'] : [format.toLowerCase()]);
    await dialog.getByRole('button', { name: 'Close new job', exact: true }).click();
    await expect(page.locator('.new-job-composer')).toHaveCount(0);
    await expect(page).toHaveURL(new RegExp(`${origin}$`));
  }
  expect(searches).toBe(0);
  await page.locator('#global-search-simple').press('Control+Enter');
  const dialog = page.getByRole('dialog', { name: 'New job', exact: true });
  await expect(dialog).toBeVisible();
  const origin = page.url();
  const committed = page.waitForResponse(reply => reply.request().method() === 'POST'
    && /\/api\/job-previews\/[^/]+\/commit$/.test(new URL(reply.url()).pathname));
  const jobReads: string[] = [];
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === 'GET' && path.startsWith('/api/jobs')) jobReads.push(path);
  });
  const start = dialog.getByRole('button', { name: 'Start', exact: true });
  await expect(start).toBeEnabled();
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  let requested!: () => void;
  const pending = new Promise<void>(resolve => { requested = resolve; });
  const holdSettings = async (route: import('@playwright/test').Route) => { requested(); await held; await route.continue(); };
  await page.route('**/api/jobs/effective-settings', holdSettings);
  await dialog.getByPlaceholder('Artist…', { exact: true }).fill('Latest artist');
  await pending;
  await expect(start).toBeEnabled();
  await expect(dialog.getByTestId('new-job-options-reset')).toBeEnabled();
  const plans: Schema<'CreateJobPreviewRequestDto'>[] = [];
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/job-previews') plans.push(request.postDataJSON());
  });
  try {
    await start.click();
    expect(plans).toEqual([]);
  } finally { release(); }
  const receipt = await (await committed).json() as Schema<'CommitJobPreviewResponseDto'>;
  expect(receipt.rootJobId).toBeTruthy();
  await expect(page).toHaveURL(new RegExp(`/jobs/${receipt.rootJobId}$`));
  await expect(dialog).toHaveCount(0);
  await expect(page.locator('.automatic-job-detail h1')).toContainText('Fixture song');
  await page.unroute('**/api/jobs/effective-settings', holdSettings);
  expect(plans).toHaveLength(1);
  expect(plans[0]!.job.kind === 'song' && plans[0]!.job.songQuery.artist).toBe('Latest artist');
  expect(jobReads.filter(path => path === '/api/jobs' || path === '/api/jobs/snapshot')).toEqual([]);
  // Browser Back returns to the page beneath the composer, with no intermediate list.
  await page.goBack();
  await expect(page).toHaveURL(origin);
  await expect(dialog).toHaveCount(0);
});

test('keeps Review reachable with edited Album settings on narrow screens', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  const dialog = page.getByRole('dialog', { name: 'New job' });
  await expect(dialog.locator('.new-job-source-disclosure')).not.toHaveAttribute('open', '');
  await expect(dialog.getByRole('button', { name: 'Spotify', exact: true })).not.toBeVisible();
  await dialog.getByPlaceholder('Song title…').fill('Unsubmitted song');
  await page.mouse.click(5, 5);
  await expect(dialog).toHaveCount(0);
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await expect(dialog.getByPlaceholder('Song title…')).toHaveValue('');
  await dialog.getByRole('button', { name: 'Album', exact: true }).click();
  await dialog.getByPlaceholder('Album title…').fill('Fixture Album');
  await dialog.locator('#new-job-options-filtering').click();
  await dialog.locator('#new-job-options-download').click();
  await dialog.locator('#new-job-options-panel').getByLabel('Name format', { exact: true }).fill('fixture/{album}');
  await expect(dialog.locator('#new-job-options-download .settings-disclosure-customized')).toBeVisible();
  const modalBounds = await dialog.boundingBox();
  const footerBounds = await dialog.locator('.new-job-footer').boundingBox();
  expect(modalBounds).not.toBeNull();
  expect(footerBounds).not.toBeNull();
  expect(footerBounds!.y + footerBounds!.height).toBeLessThanOrEqual(modalBounds!.y + modalBounds!.height + 1);
  const request = page.waitForRequest(candidate => candidate.method() === 'POST'
    && new URL(candidate.url()).pathname === '/api/job-previews');
  await dialog.getByRole('button', { name: 'Review', exact: true }).click();
  const reviewed = (await request).postDataJSON() as Schema<'CreateJobPreviewRequestDto'>;
  expect(reviewed.job.kind).toBe('album');
  expect(reviewed.options?.downloadSettings?.output?.nameFormat).toBe('fixture/{album}');
  await expect(dialog.locator('.new-job-preview')).toBeVisible();
});

test('shows a failed source preview and allows correcting the same draft', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await page.locator('.new-job-source-disclosure summary').click();
  await page.getByRole('complementary', { name: 'Job type' }).getByRole('button', { name: 'Soulseek link', exact: true }).click();
  await expect(page.locator('#new-job-options-filtering, #new-job-options-ranking')).toHaveCount(0);
  await expect(page.locator('#new-job-options-download')).toBeVisible();
  const source = page.getByPlaceholder('Paste a slsk:// link…');
  await source.fill('slsk://fixture-peer');
  await page.getByRole('button', { name: 'Review', exact: true }).click();

  const preview = page.locator('.new-job-preview');
  await expect(preview.locator('.job-preview-row')).toContainText('missing path');
  await expect(preview.locator('.job-preview-row input[type=checkbox]')).toBeDisabled();
  await expect(page.getByRole('button', { name: /^Start.*job/ })).toBeDisabled();
  const dialog = await page.getByRole('dialog', { name: 'New job' }).boundingBox();
  const failedRow = await preview.locator('.job-preview-row').boundingBox();
  expect(dialog).not.toBeNull();
  expect(failedRow).not.toBeNull();
  expect(failedRow!.x + failedRow!.width).toBeLessThanOrEqual(dialog!.x + dialog!.width + 1);

  await source.fill('slsk://fixture-peer/Fixture/');
  await expect(preview).toHaveCount(0);
  await expect(page.locator('#new-job-options-review')).toHaveCount(0);
  await expect(page.locator('#new-job-options-download')).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('button', { name: 'Review', exact: true }).click();
  await expect(preview.locator('.job-preview-row').filter({ hasText: 'fixture-peer — Fixture' })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Start 1 job', exact: true })).toBeEnabled();
});

test('edits source settings before upload and rebases them when the artifact arrives', async ({ page }) => {
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  const provisional = page.waitForResponse(response => new URL(response.url()).pathname === '/api/jobs/effective-settings'
    && response.request().method() === 'POST' && response.request().postDataJSON()?.job?.inputType === 'csv'
    && !response.request().postDataJSON()?.job?.artifactId);
  await page.getByRole('button', { name: 'CSV file', exact: true }).click();
  await page.locator('#new-job-options-filtering').click();
  const filtering = page.locator('#new-job-options-panel');
  const strictTitle = filtering.getByTestId('strict-title-conditions');
  await expect(filtering.getByLabel('Track count', { exact: true })).toBeVisible();
  await expect(filtering.getByLabel(/Expected length/i)).toHaveCount(0);
  await expect(filtering.getByLabel('Required track title', { exact: true })).toHaveCount(0);
  const provisionalResponse = await provisional;
  expect(provisionalResponse.ok()).toBeTruthy();
  expect((await provisionalResponse.json()).isProvisional).toBe(true);
  await strictTitle.check();
  const concrete = page.waitForResponse(response => new URL(response.url()).pathname === '/api/jobs/effective-settings'
    && response.request().method() === 'POST' && Boolean(response.request().postDataJSON()?.job?.artifactId));
  await page.locator('input[type=file]').setInputFiles({
    name: 'configure-first.csv', mimeType: 'text/csv',
    buffer: Buffer.from('artist,title\nFixture Artist,Configured Song\n'),
  });
  expect((await (await concrete).json()).isProvisional).toBe(false);
  await expect(strictTitle).toBeChecked();
  const request = page.waitForRequest(candidate => candidate.method() === 'POST'
    && new URL(candidate.url()).pathname === '/api/job-previews');
  await page.getByRole('button', { name: 'Review', exact: true }).click();
  const reviewed = (await request).postDataJSON() as Schema<'CreateJobPreviewRequestDto'>;
  expect(reviewed.options?.downloadSettings?.search?.necessaryCond?.strictTitle).toBe(true);
  await expect(page.locator('.new-job-start-button')).toBeEnabled();
});

test('blocks stale settings after a failed refresh and retries the shared resolver', async ({ page }) => {
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await expect(page.getByLabel('Output directory', { exact: true })).toBeVisible();
  const output = page.getByLabel('Output directory', { exact: true });
  const originalControl = await output.elementHandle();
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  let requested!: () => void;
  const pending = new Promise<void>(resolve => { requested = resolve; });
  let attempts = 0;
  await page.route('**/api/jobs/effective-settings', async route => {
    attempts++;
    if (attempts === 1) {
      requested();
      await held;
      await route.abort();
    } else await route.continue();
  });
  await page.getByPlaceholder('Song title…').fill('Retry settings fixture');
  await pending;
  try {
    await expect(output).toBeVisible();
    expect(await originalControl!.evaluate(node => node.isConnected)).toBe(true);
    await output.fill('/tmp/edited-during-settings-refresh');
    await expect(page.locator('.new-job-start-button')).toBeEnabled();
    await expect(page.getByTestId('new-job-options-reset')).toBeEnabled();
    await page.getByRole('button', { name: 'Review', exact: true }).click();
  } finally { release(); }
  await page.locator('#new-job-options-filtering').click();
  const filtering = page.locator('#new-job-options-panel');
  await expect(filtering.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review', exact: true })).toBeDisabled();
  const retried = page.waitForResponse(response => new URL(response.url()).pathname === '/api/jobs/effective-settings' && response.ok());
  await filtering.getByTestId('new-job-settings-retry').click();
  await retried;
  await expect(filtering.getByRole('button', { name: 'FLAC', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review', exact: true })).toBeEnabled();
  expect(attempts).toBe(2);
  await page.locator('#new-job-options-download').click();
  await expect(output).toHaveValue('/tmp/edited-during-settings-refresh');
});

test('clears source-specific drafts and resets the options tab when choosing a job type', async ({ page }) => {
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await page.locator('.new-job-source-disclosure summary').click();
  await page.getByRole('button', { name: 'YouTube', exact: true }).click();
  await page.getByPlaceholder('Paste a YouTube URL…').fill('https://www.youtube.com/playlist?list=fixture');
  const importTab = page.locator('#new-job-options-import');
  await importTab.click();
  const filteringTab = page.locator('#new-job-options-filtering');
  await filteringTab.click();
  await expect(filteringTab).toHaveAttribute('aria-selected', 'true');

  await page.getByRole('button', { name: 'Bandcamp', exact: true }).click();
  await expect(page.getByPlaceholder('Paste a Bandcamp URL…')).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Review', exact: true })).toBeDisabled();
  await expect(importTab).toHaveAttribute('aria-selected', 'false');
  await expect(filteringTab).toHaveAttribute('aria-selected', 'false');
  await page.getByPlaceholder('Paste a Bandcamp URL…').fill('https://example.bandcamp.com/album/fixture');
  await page.getByRole('button', { name: 'Bandcamp', exact: true }).click();
  await expect(page.getByPlaceholder('Paste a Bandcamp URL…')).toHaveValue('');

  await page.getByRole('button', { name: 'Spotify', exact: true }).click();
  await page.getByRole('button', { name: 'Liked songs' }).click();
  await page.getByRole('button', { name: 'YouTube', exact: true }).click();
  await page.getByRole('button', { name: 'Spotify', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Playlist / album URL' })).toHaveClass(/active/);
  await expect(page.getByPlaceholder('Paste a Spotify URL…')).toHaveValue('');
});

test('uploads and reviews a CSV with custom column mapping', async ({ page, daemon }) => {
  let uploads = 0, plans = 0, commits = 0, settingsReads = 0;
  page.on('request', request => {
    if (request.method() !== 'POST') return;
    const path = new URL(request.url()).pathname;
    if (path === '/api/input-artifacts') uploads++;
    if (path === '/api/job-previews') plans++;
    if (/^\/api\/job-previews\/[^/]+\/commit$/.test(path)) commits++;
    if (path === '/api/jobs/effective-settings') settingsReads++;
  });
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await page.getByRole('button', { name: 'CSV file', exact: true }).click();
  await page.locator('input[type=file]').setInputFiles({
    name: 'browser-input.csv', mimeType: 'text/csv',
    buffer: Buffer.from('performer,recording\nFixture Artist,Fixture CSV Song\nFixture Artist,Second CSV Song\n'),
  });
  await page.locator('#new-job-options-import').click();
  const mappingIndicator = page.locator('#new-job-options-import .settings-disclosure-customized');
  const artistMapping = page.locator('.new-job-csv-grid').getByLabel('Artist', { exact: true });
  await expect(artistMapping).toHaveValue('');
  await expect(mappingIndicator).toHaveCount(0);
  await artistMapping.fill('performer');
  await expect(mappingIndicator).toBeVisible();
  await artistMapping.fill('');
  await expect(mappingIndicator).toHaveCount(0);
  await page.getByRole('button', { name: 'Profiles', exact: true }).click();
  await page.getByRole('checkbox', { name: 'download-options', exact: true }).check();
  await page.getByRole('button', { name: 'Profiles', exact: true }).click();
  await page.locator('#new-job-options-import').click();
  await expect(page.getByLabel('Offset', { exact: true })).toHaveValue('2');
  await expect(page.getByLabel('Limit items', { exact: true })).toHaveValue('20');
  await expect(page.locator('#new-job-options-import .settings-disclosure-customized')).toBeVisible();
  await expect(page.locator('#new-job-options-download .settings-disclosure-customized')).toBeVisible();
  await expect(page.locator('#new-job-options-ranking .settings-disclosure-customized')).toHaveCount(0);

  const defaultArtistColumn = await page.locator('.new-job-csv-grid').getByLabel('Artist', { exact: true }).inputValue();
  await page.getByLabel('Offset', { exact: true }).fill('0');
  const mappingHelp = page.getByTestId('csv-mapping-help');
  const mappingExplanation = page.getByTestId('csv-mapping-help-content');
  await expect(mappingExplanation).toHaveCount(0);
  await mappingHelp.click();
  await page.mouse.move(0, 0);
  await expect(mappingExplanation).toBeVisible();
  await mappingHelp.press('Escape');
  await expect(mappingExplanation).toHaveCount(0);
  await expect(page.getByRole('dialog', { name: 'New job', exact: true })).toBeVisible();
  await expect(mappingHelp).toBeFocused();
  await mappingHelp.press('Enter');
  await expect(mappingExplanation).toBeVisible();
  await page.locator('.new-job-csv-grid').getByLabel('Artist', { exact: true }).click();
  await page.locator('.new-job-csv-grid').getByLabel('Artist', { exact: true }).fill('performer');
  await expect(mappingExplanation).toHaveCount(0);
  await page.getByTestId('new-job-options-reset').click();
  await expect(page.getByLabel('Offset', { exact: true })).toHaveValue('2');
  await expect(page.locator('.new-job-csv-grid').getByLabel('Artist', { exact: true })).toHaveValue(defaultArtistColumn);
  await page.getByLabel('Offset', { exact: true }).fill('0');
  await page.locator('.new-job-csv-grid').getByLabel('Artist', { exact: true }).fill('performer');
  await page.locator('.new-job-csv-grid').getByLabel('Title', { exact: true }).fill('recording');
  await expect(page.getByRole('button', { name: 'Review', exact: true })).toBeEnabled();
  const firstCreated = page.waitForResponse(reply => reply.request().method() === 'POST'
    && new URL(reply.url()).pathname === '/api/job-previews');
  await page.getByRole('button', { name: 'Review', exact: true }).click();
  const firstCreation = await firstCreated;
  const original = await firstCreation.json() as Schema<'CreateJobPreviewResponseDto'>;
  const originalRequest = firstCreation.request().postDataJSON() as Schema<'CreateJobPreviewRequestDto'>;
  expect(originalRequest.options?.profileNames).toEqual(['download-options']);
  expect(originalRequest.options?.downloadSettings?.extraction).toEqual({ offset: 0 });
  await expect(page.locator('.new-job-start-button')).toBeEnabled();
  await expect(page.locator('.job-preview-row').first()).toContainText('browser-input.csv');
  await expect(page.locator('.job-preview-details').first().locator(':scope > span')).toHaveText(['2 jobs', '·', 'CSV']);
  // The real paged hierarchy expands automatically when the complete plan is small.
  await expect(page.locator('.job-preview-row').filter({ hasText: 'browser-input' })).toHaveCount(2);
  await expect(page.locator('.job-preview-row').filter({ hasText: 'browser-input' }).last().locator('.job-preview-details > span')).toHaveText(['2 jobs', '·', 'Job List']);
  await expect(page.locator('.job-preview-row').filter({ hasText: 'Fixture CSV Song' }).locator('.job-preview-details')).toHaveText('Song');
  await expect(page.locator('.new-job-preview .job-preview-tree')).toHaveCount(1);
  const rowLayout = await page.locator('.job-preview-row').first().evaluate(row => {
    const title = row.querySelector('.job-preview-title')!.getBoundingClientRect();
    const details = row.querySelector('.job-preview-details')!.getBoundingClientRect();
    return { titleMiddle: title.top + title.height / 2, detailsMiddle: details.top + details.height / 2,
      titleRight: title.right, detailsLeft: details.left, height: row.getBoundingClientRect().height };
  });
  expect(Math.abs(rowLayout.titleMiddle - rowLayout.detailsMiddle)).toBeLessThan(1);
  expect(rowLayout.titleRight).toBeLessThan(rowLayout.detailsLeft);
  expect(rowLayout.height).toBeLessThan(40);

  await expect(page.locator('.job-preview-children').getByRole('button', { name: 'Load jobs', exact: true })).toHaveCount(0);

  // Keep the uploaded artifact and non-default mapping, but make the reviewed
  // plan genuinely expired before commit. A stale plan must never be accepted.
  await page.getByTestId('new-job-selection-toggle').click();
  const firstSong = page.locator('.job-preview-row').filter({ hasText: 'Fixture CSV Song' });
  await firstSong.locator('input[type=checkbox]').check();
  const resolvedSettingsReads = settingsReads;
  await expect(page.locator('#new-job-options-review')).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('new-job-options-reset')).toHaveCount(0);
  await page.locator('#new-job-options-download').click();
  await expect(page.locator('.new-job-preview')).toHaveCount(0);
  await page.locator('#new-job-options-review').click();
  await expect(firstSong.locator('input[type=checkbox]')).toBeChecked();
  expect({ plans, settingsReads }).toEqual({ plans: 1, settingsReads: resolvedSettingsReads });
  await daemon.command('expire-job-previews');
  const rejected = page.waitForResponse(reply => new URL(reply.url()).pathname
    === `/api/job-previews/${original.preview.previewId}/commit`);
  await page.getByRole('button', { name: 'Start 1 job', exact: true }).click();
  expect((await rejected).status()).toBe(409);
  await expect(page.getByRole('alert')).toContainText('expired');
  const noJobs = await page.request.get('/api/jobs?includeAll=true');
  expect(noJobs.ok()).toBeTruthy();
  expect(await noJobs.json()).toEqual([]);
  await expect(page.locator('.new-job-start-button')).toBeDisabled();
  await page.setViewportSize({ width: 390, height: 900 });
  const rebuild = page.getByRole('button', { name: 'Review again', exact: true });
  await expect(rebuild).toBeEnabled();

  const rebuilt = page.waitForResponse(reply => reply.request().method() === 'POST'
    && new URL(reply.url()).pathname === '/api/job-previews');
  await page.getByRole('button', { name: 'Review again', exact: true }).click();
  const rebuilding = await rebuilt;
  const next = await rebuilding.json() as Schema<'CreateJobPreviewResponseDto'>;
  expect(next.preview.previewId).not.toBe(original.preview.previewId);
  expect(rebuilding.request().postDataJSON()).toEqual(originalRequest);
  expect(uploads, 'Rebuilding a preview must reuse its uploaded artifact.').toBe(1);
  await expect(page.getByRole('alert')).toHaveCount(0);
  await expect(page.locator('.new-job-start-button')).toBeDisabled();
  await expect(firstSong.getByRole('checkbox')).not.toBeChecked();
  expect({ uploads, plans, commits, settingsReads }).toEqual({ uploads: 1, plans: 2, commits: 1, settingsReads: resolvedSettingsReads });
  // A reviewed shortlist must not turn into a whole-playlist download.
  await firstSong.getByRole('checkbox').check();
  await daemon.command('forget-job-previews');
  const missing = page.waitForResponse(reply => reply.request().method() === 'POST'
    && new URL(reply.url()).pathname === `/api/job-previews/${next.preview.previewId}/commit`);
  await page.getByRole('button', { name: 'Start 1 job', exact: true }).click();
  expect((await missing).status()).toBe(404);
  await expect(page.getByRole('alert')).toContainText('not found');
  await expect(page.locator('.new-job-start-button')).toBeDisabled();
  const restored = page.waitForResponse(reply => reply.request().method() === 'POST'
    && new URL(reply.url()).pathname === '/api/job-previews');
  await page.getByRole('button', { name: 'Review again', exact: true }).click();
  const restoring = await restored;
  const finalPreview = await restoring.json() as Schema<'CreateJobPreviewResponseDto'>;
  expect(finalPreview.preview.previewId).not.toBe(next.preview.previewId);
  expect(restoring.request().postDataJSON()).toEqual(originalRequest);
  await expect(page.locator('.new-job-start-button')).toBeDisabled();
  expect({ uploads, plans, commits, settingsReads }).toEqual({ uploads: 1, plans: 3, commits: 2, settingsReads: resolvedSettingsReads });
  // Re-extraction may produce different nodes; old subtree choices cannot be
  // silently applied to this new preview. Choose the desired row again.
  await firstSong.locator('input[type=checkbox]').check();
  const committed = page.waitForResponse(reply => new URL(reply.url()).pathname
    === `/api/job-previews/${finalPreview.preview.previewId}/commit`);
  await page.getByRole('button', { name: 'Start 1 job', exact: true }).press('Control+Enter');
  const accepted = await committed;
  expect(accepted.status()).toBe(202);
  const receipt = await accepted.json() as Schema<'CommitJobPreviewResponseDto'>;
  expect(Number(receipt.requestedCount)).toBe(1);
  expect(Number(receipt.submittedCount)).toBe(1);
  await expect(page.getByRole('dialog', { name: 'New job' })).toHaveCount(0);
  expect(receipt.rootJobId).toBeTruthy();
  await expect(page.locator('.automatic-job-detail h1')).toHaveText('browser-input');
  await expect(page.locator('[data-testid="job-row"]')).toHaveCount(1);
  await expect(page.locator('[data-testid="job-row"]')).toContainText('Fixture CSV Song');
  await expect(page.locator('[data-testid="job-row"]')).not.toContainText('Second CSV Song');
  const jobsResponse = await page.request.get(`/api/jobs?submissionId=${receipt.submissionId}&includeAll=true`);
  expect(jobsResponse.ok()).toBeTruthy();
  const jobs = await jobsResponse.json() as Schema<'JobSummaryDto'>[];
  expect(jobs.some(job => job.display.queryText?.includes('Fixture CSV Song') || job.display.itemName?.includes('Fixture CSV Song'))).toBe(true);
  expect(jobs.some(job => job.display.queryText?.includes('Second CSV Song') || job.display.itemName?.includes('Second CSV Song'))).toBe(false);
});

test('pages a CSV Review and commits an item beyond the first preview page', async ({ page }) => {
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await page.getByRole('button', { name: 'CSV file', exact: true }).click();
  const total = nestedPageSize + 1;
  const lastTitle = `Paged Item ${String(total).padStart(3, '0')}`;
  const rows = Array.from({ length: total }, (_, index) => `Fixture Artist,Paged Item ${String(index + 1).padStart(3, '0')}`);
  await page.locator('input[type=file]').setInputFiles({
    name: 'paged-review.csv', mimeType: 'text/csv',
    buffer: Buffer.from(['artist,title', ...rows, ''].join('\n')),
  });
  await page.getByRole('button', { name: 'Review', exact: true }).click();
  await expect(page.getByRole('button', { name: `Start ${total} jobs` })).toBeEnabled();
  await expect(page.locator('.job-preview-row').filter({ hasText: lastTitle })).toHaveCount(0);
  await page.getByRole('button', { name: 'Load jobs' }).click();
  const tail = page.locator('.job-preview-row').filter({ hasText: lastTitle });
  await expect(tail).toHaveCount(1);
  await page.getByRole('button', { name: 'Deselect all' }).click();
  await tail.locator('input[type=checkbox]').check();
  await expect(tail.getByRole('checkbox')).toBeChecked();
  await expect(page.getByRole('button', { name: 'Start 1 job' })).toBeEnabled();
  await page.getByRole('button', { name: 'Start 1 job' }).click();
  await expect(page.locator('.automatic-job-detail h1')).toHaveText('paged-review');
  await expect(page.locator('[data-testid="job-row"]')).toHaveCount(1);
  await expect(page.locator('[data-testid="job-row"]')).toContainText(lastTitle);
});


test('reviews and starts daemon CSV and list paths without uploading', async ({ page }, testInfo) => {
  const folder = testInfo.outputPath('daemon inputs');
  await mkdir(folder, { recursive: true });
  let uploads = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/input-artifacts') uploads++;
  });
  for (const choice of ['csv', 'list'] as const) {
    const path = join(folder, choice === 'csv' ? 'songs.csv' : 'wishlist.txt');
    await writeFile(path, choice === 'csv'
      ? 'artist,title\nFixture Artist,Fixture Song\n'
      : ['s:"Fixture Artist - Fixture Song"', 'a:"Fixture Artist - Fixture Album"',
        'slsk://fixture-peer/Fixture/', 'slsk://fixture-peer/Fixture/Other.flac',
        `"${join(folder, 'songs.csv')}"`, ''].join('\n'));
    await page.goto('/jobs');
    await page.getByRole('button', { name: 'New job', exact: false }).click();
    await page.getByTestId(`job-type-${choice}`).click();
    await page.getByTestId('job-file-path').click();
    await expect(page.locator('.new-job-start-button')).toBeDisabled();
    await page.getByTestId('job-file-path-input').fill(path);
    const planned = page.waitForResponse(reply => reply.request().method() === 'POST'
      && new URL(reply.url()).pathname === '/api/job-previews');
    const firstPage = page.waitForResponse(reply => new URL(reply.url()).pathname.endsWith('/nodes')
      && new URL(reply.url()).searchParams.get('parentRef') === '0/result');
    await page.locator('.new-job-review-button').click();
    const response = await planned;
    expect(response.ok()).toBeTruthy();
    expect(response.request().postDataJSON().job).toEqual({ kind: 'extract', input: path, inputType: choice });
    await expect(page.locator('.job-preview-row').filter({ hasText: 'Fixture Song' }).last()).toBeVisible();
    await expect(page.locator('.new-job-start-button')).toBeEnabled();
    const nodes = await (await firstPage).json() as Schema<'JobPreviewNodeDto'>[];
    expect(nodes.map(node => node.kind)).toEqual(choice === 'csv'
      ? ['song'] : ['song', 'album', 'remote-directory', 'remote-file', 'extract']);
    if (choice === 'list') {
      // The nested CSV stays a collection even though it contains only one song.
      expect(nodes[4]!.isSelectable).toBe(false);
      expect(nodes[4]!.inputType).toBe('CSV');
      await expect(page.locator('.job-preview-row').filter({ hasText: 'songs.csv' }).locator('.job-preview-details')).toContainText('CSV');
      expect(Number(nodes[4]!.selectableSubtreeCount)).toBe(1);
      await expect(page.locator('.job-preview-row').filter({ hasText: 'Fixture Album' })).toHaveCount(1);
      await page.getByTestId('new-job-selection-toggle').click();
      await page.locator('.job-preview-row').filter({ hasText: 'Fixture Song' }).first().locator('input').check();
    }
    const committed = page.waitForResponse(reply => reply.request().method() === 'POST'
      && /\/api\/job-previews\/[^/]+\/commit$/.test(new URL(reply.url()).pathname));
    await page.locator('.new-job-start-button').click();
    const accepted = await committed;
    expect(accepted.status()).toBe(202);
    expect(Number((await accepted.json()).submittedCount)).toBe(1);
    await expect(page.locator('.new-job-composer')).toHaveCount(0);
  }
  expect(uploads).toBe(0);
});
