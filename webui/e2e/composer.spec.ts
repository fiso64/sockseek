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
  await page.getByRole('checkbox', { name: 'download-preset', exact: true }).check();
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
  expect(started.options?.profileNames).toEqual(['download-preset']);
  expect(started.options?.downloadSettings?.skip).toBeUndefined();
  expect(started.options?.downloadSettings?.search?.necessaryCond?.formats?.replace).toEqual(['flac']);
  expect(started.options?.downloadSettings?.output?.nameFormat).toBe('direct/{stitle}');
  await expect(page.locator('.search-history-open').filter({ hasText: 'Fixture song' })).toBeVisible();
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Downloads', exact: true }).click();
  await expect(page.locator('.file-item-card').getByRole('button', { name: 'Complete', exact: true })).toHaveClass(/folder-file-state/);
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

test('Ctrl+Enter prepares music downloads with the current query and edited search conditions', async ({ page }) => {
  let searches = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.startsWith('/api/jobs/search')) searches++;
  });
  for (const mode of ['Album Search', 'Track Search']) {
    await page.goto('/jobs');
    await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
    await page.getByRole('menuitemradio', { name: mode, exact: true }).click();
    await page.locator('#global-search-simple').fill(mode === 'Album Search' ? 'Fixture artist - Fixture album' : 'Fixture song');
    await page.getByRole('button', { name: 'Search configuration', exact: true }).click();
    const panel = page.locator('.search-config-popover');
    await panel.getByRole('button', { name: 'Any', exact: true }).click();
    await panel.getByRole('button', { name: 'FLAC', exact: true }).click();
    const input = page.locator(mode === 'Album Search' ? '#global-search-title' : '#global-search-simple');
    await input.press('Control+Enter');
    const dialog = page.getByRole('dialog', { name: 'New job', exact: true });
    await expect(dialog).toBeVisible();
    await expect(page.getByRole('tab', { name: 'Download jobs', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(dialog.getByPlaceholder(mode === 'Album Search' ? 'Album title…' : 'Song title…')).toHaveValue(mode === 'Album Search' ? 'Fixture album' : 'Fixture song');
    await expect(dialog.getByPlaceholder('Artist…', { exact: true })).toHaveValue(mode === 'Album Search' ? 'Fixture artist' : '');
    await dialog.locator('#new-job-options-filtering').click();
    const filtering = dialog.locator('#new-job-options-panel');
    await expect(filtering.getByRole('button', { name: 'FLAC', exact: true })).toHaveClass(/active/);
    await expect(filtering.getByRole('button', { name: 'MP3', exact: true })).not.toHaveClass(/active/);
    // Further edits in the modal must override, not discard or resurrect, the copied settings.
    if (mode === 'Track Search') await filtering.getByRole('button', { name: 'MP3', exact: true }).click();
    const request = page.waitForRequest(candidate => candidate.method() === 'POST'
      && new URL(candidate.url()).pathname === '/api/job-previews');
    await dialog.getByRole('button', { name: 'Review', exact: true }).click();
    const preview = (await request).postDataJSON() as Schema<'CreateJobPreviewRequestDto'>;
    expect(preview.job.kind).toBe(mode === 'Album Search' ? 'album' : 'song');
    expect(preview.options?.downloadSettings?.search?.necessaryCond?.formats?.replace)
      .toEqual(mode === 'Album Search' ? ['flac'] : ['flac', 'mp3']);
    await dialog.getByRole('button', { name: 'Close new job', exact: true }).click();
  }
  expect(searches).toBe(0);
});

test('keeps Review reachable with edited Album settings on narrow screens', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  const dialog = page.getByRole('dialog', { name: 'New job' });
  await expect(dialog.locator('.new-job-source-disclosure')).not.toHaveAttribute('open', '');
  await expect(dialog.getByRole('button', { name: 'Spotify', exact: true })).not.toBeVisible();
  await page.mouse.click(5, 5);
  await expect(dialog).toHaveCount(0);
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await dialog.getByRole('button', { name: 'Close new job', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await page.getByRole('button', { name: 'New job', exact: false }).click();
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
  await expect(filtering.getByLabel('Require track title in filename')).toBeVisible();
  await expect(filtering.getByText('Expected length', { exact: false })).toHaveCount(0);
  await expect(filtering.getByText('Required track title', { exact: true })).toHaveCount(0);
  await expect(filtering.getByLabel('Min tracks')).toBeVisible();
  await expect(filtering.getByLabel('Max tracks')).toBeVisible();
  const provisionalResponse = await provisional;
  expect(provisionalResponse.ok()).toBeTruthy();
  expect((await provisionalResponse.json()).isProvisional).toBe(true);
  await filtering.getByLabel('Require track title in filename').check();
  const concrete = page.waitForResponse(response => new URL(response.url()).pathname === '/api/jobs/effective-settings'
    && response.request().method() === 'POST' && Boolean(response.request().postDataJSON()?.job?.artifactId));
  await page.locator('input[type=file]').setInputFiles({
    name: 'configure-first.csv', mimeType: 'text/csv',
    buffer: Buffer.from('artist,title\nFixture Artist,Configured Song\n'),
  });
  await expect(filtering.getByLabel('Require track title in filename')).toBeChecked();
  expect((await (await concrete).json()).isProvisional).toBe(false);
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
    await expect(page.locator('.new-job-start-button')).toBeDisabled();
  } finally { release(); }
  await page.locator('#new-job-options-filtering').click();
  const filtering = page.locator('#new-job-options-panel');
  await expect(filtering.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review', exact: true })).toBeDisabled();
  await filtering.getByTestId('new-job-settings-retry').click();
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
  await page.getByRole('checkbox', { name: 'download-preset', exact: true }).check();
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
  expect(originalRequest.options?.profileNames).toEqual(['download-preset']);
  expect(originalRequest.options?.downloadSettings?.extraction).toEqual({ offset: 0 });
  await expect(page.locator('.new-job-start-button')).toBeEnabled();
  await expect(page.locator('.job-preview-row').first()).toContainText('browser-input.csv');
  await expect(page.locator('.job-preview-copy small').first()).toHaveText('Import · Uploaded artifact');
  // The real paged hierarchy expands automatically when the complete plan is small.
  await expect(page.locator('.job-preview-row').filter({ hasText: 'browser-input' })).toHaveCount(2);
  await expect(page.locator('.job-preview-row').filter({ hasText: 'browser-input' }).last().locator('.job-preview-copy small')).toHaveText('Job List · Mixed jobs');
  await expect(page.locator('.job-preview-row').filter({ hasText: 'Fixture CSV Song' }).locator('.job-preview-copy small')).toHaveText('Song · CSV row 2');
  await expect(page.locator('.new-job-preview .job-preview-tree')).toHaveCount(1);
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
  await expect(page.getByRole('button', { name: 'Start 2 jobs', exact: true })).toBeEnabled();
  expect({ uploads, plans, commits, settingsReads }).toEqual({ uploads: 1, plans: 2, commits: 1, settingsReads: resolvedSettingsReads });
  await expect(page.getByRole('button', { name: 'Deselect all', exact: true })).toBeVisible();
  // Temporary preview data can also disappear across daemon restarts while
  // the durable uploaded artifact survives. This is a real 404, not a mock.
  await daemon.command('forget-job-previews');
  const missing = page.waitForResponse(reply => new URL(reply.url()).pathname
    === `/api/job-previews/${next.preview.previewId}/commit`);
  await expect(page.getByRole('button', { name: 'Start 2 jobs', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Start 2 jobs', exact: true }).press('Control+Enter');
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
  await expect(page.getByRole('button', { name: 'Start 2 jobs', exact: true })).toBeEnabled();
  expect({ uploads, plans, commits, settingsReads }).toEqual({ uploads: 1, plans: 3, commits: 2, settingsReads: resolvedSettingsReads });
  // Re-extraction may produce different nodes; old subtree choices cannot be
  // silently applied to this new preview. Choose the desired row again.
  await page.getByTestId('new-job-selection-toggle').click();
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
  const importRow = page.locator('.search-history-row').filter({ hasText: 'browser-input.csv' });
  await expect(importRow).toBeVisible();
  await importRow.locator('.search-history-open').click();
  await expect(page.locator('.automatic-job-detail h1')).toHaveText('browser-input');
  await expect(page.locator('[data-testid="job-row"]')).toHaveCount(1);
  await expect(page.locator('[data-testid="job-row"]')).toContainText('Fixture CSV Song');
  await expect(page.locator('[data-testid="job-row"]')).not.toContainText('Second CSV Song');
  const jobsResponse = await page.request.get(`/api/jobs?submissionId=${receipt.submissionId}&includeAll=true`);
  expect(jobsResponse.ok()).toBeTruthy();
  const jobs = await jobsResponse.json() as Schema<'JobSummaryDto'>[];
  expect(jobs.some(job => job.queryText?.includes('Fixture CSV Song') || job.itemName?.includes('Fixture CSV Song'))).toBe(true);
  expect(jobs.some(job => job.queryText?.includes('Second CSV Song') || job.itemName?.includes('Second CSV Song'))).toBe(false);
});

test('pages a CSV Review and commits an item beyond the first preview page', async ({ page }) => {
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await page.getByRole('button', { name: 'CSV file', exact: true }).click();
  const rows = Array.from({ length: 101 }, (_, index) => `Fixture Artist,Paged Item ${String(index + 1).padStart(3, '0')}`);
  await page.locator('input[type=file]').setInputFiles({
    name: 'paged-review.csv', mimeType: 'text/csv',
    buffer: Buffer.from(['artist,title', ...rows, ''].join('\n')),
  });
  await page.getByRole('button', { name: 'Review', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Start 101 jobs' })).toBeEnabled();
  await expect(page.locator('.job-preview-row').filter({ hasText: 'Paged Item 101' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Load jobs' }).click();
  const tail = page.locator('.job-preview-row').filter({ hasText: 'Paged Item 101' });
  await expect(tail).toHaveCount(1);
  await page.getByRole('button', { name: 'Deselect all' }).click();
  await tail.locator('input[type=checkbox]').check();
  await expect(page.getByRole('button', { name: 'Start 1 job' })).toBeEnabled();
  await page.getByRole('button', { name: 'Start 1 job' }).click();
  const root = page.locator('.search-history-row').filter({ hasText: 'paged-review.csv' });
  await expect(root).toBeVisible();
  await root.locator('.search-history-open').click();
  await expect(page.locator('[data-testid="job-row"]')).toHaveCount(1);
  await expect(page.locator('[data-testid="job-row"]')).toContainText('Paged Item 101');
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
