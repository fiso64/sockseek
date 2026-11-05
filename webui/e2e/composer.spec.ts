import { test, expect } from './fixtures';
import type { Schema } from '../src/api/types';
import { readdir, stat } from 'node:fs/promises';
import { join, relative } from 'node:path';

test('uses filtering and output settings for a direct Start download', async ({ page, daemon }, testInfo) => {
  await daemon.command('music-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await expect(page.getByRole('button', { name: 'Start', exact: true })).toBeDisabled();
  await page.getByPlaceholder('Song title…').press('Control+Enter');
  await expect(page.getByRole('dialog', { name: 'New job' })).toBeVisible();
  await page.getByPlaceholder('Song title…').fill('Fixture song');
  await page.getByRole('button', { name: 'Filtering Defaults', exact: true }).click();
  const filtering = page.locator('#new-job-filtering-options');
  await expect(filtering.getByRole('button', { name: 'FLAC', exact: true })).toBeVisible();
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
  await page.getByRole('button', { name: 'Download Defaults', exact: true }).click();
  const destination = testInfo.outputPath('direct-song');
  await page.getByLabel('Output directory', { exact: true }).fill(destination);
  await page.getByLabel('Name format', { exact: true }).fill('direct/{stitle}');
  const request = page.waitForRequest(candidate => candidate.method() === 'POST'
    && new URL(candidate.url()).pathname === '/api/job-previews');
  await expect(page.getByRole('button', { name: 'Start', exact: true })).toBeEnabled();
  await page.getByLabel('Name format', { exact: true }).press('Control+Enter');
  const started = (await request).postDataJSON() as Schema<'CreateJobPreviewRequestDto'>;
  expect(started.options?.downloadSettings?.search?.necessaryCond?.formats?.replace).toEqual(['flac']);
  expect(started.options?.downloadSettings?.output?.nameFormat).toBe('direct/{stitle}');
  await expect(page.locator('.search-history-open').filter({ hasText: 'Fixture song' })).toBeVisible();
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Downloads', exact: true }).click();
  await expect(page.locator('.file-item-card .transfer-state')).toHaveText('Complete');
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
    await dialog.getByRole('button', { name: 'Filtering Defaults', exact: true }).click();
    const filtering = dialog.locator('#new-job-filtering-options');
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

test('keeps Review reachable with edited Album settings panels open on narrow screens', async ({ page }) => {
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
  await dialog.getByRole('button', { name: 'Filtering Defaults', exact: true }).click();
  await dialog.getByRole('button', { name: 'Download Defaults', exact: true }).click();
  await dialog.locator('#new-job-download-options').getByLabel('Name format', { exact: true }).fill('fixture/{album}');
  await expect(dialog.getByRole('button', { name: 'Download Custom', exact: true })).toBeVisible();
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
  await page.getByRole('complementary', { name: 'Job type' }).getByRole('button', { name: 'Soulseek', exact: true }).click();
  const source = page.getByPlaceholder('Paste a slsk:// link…');
  await source.fill('slsk://fixture-peer');
  await page.getByRole('button', { name: 'Review', exact: true }).click();

  const preview = page.locator('.new-job-preview');
  await expect(preview.locator('header')).toContainText('1 failed');
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
  await page.getByRole('button', { name: 'Review', exact: true }).click();
  await expect(preview.locator('.job-preview-row').filter({ hasText: 'fixture-peer — Fixture' })).toHaveCount(1);
  await expect(preview.locator('header')).not.toContainText('failed');
  await expect(page.getByRole('button', { name: 'Start 1 job', exact: true })).toBeEnabled();
});

test('edits source settings before upload and rebases them when the artifact arrives', async ({ page }) => {
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await page.getByRole('button', { name: 'CSV file', exact: true }).click();
  const provisional = page.waitForResponse(response => new URL(response.url()).pathname === '/api/jobs/effective-settings'
    && response.request().method() === 'POST' && !response.request().postDataJSON()?.job?.artifactId);
  await page.getByRole('button', { name: 'Filtering Defaults', exact: true }).click();
  const filtering = page.locator('#new-job-filtering-options');
  await expect(filtering.getByLabel('Require track title in filename')).toBeVisible();
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
  await page.getByPlaceholder('Song title…').fill('Retry settings fixture');
  let attempts = 0;
  await page.route('**/api/jobs/effective-settings', async route => {
    attempts++;
    if (attempts === 1) await route.abort();
    else await route.continue();
  });
  await page.getByRole('button', { name: 'Filtering Defaults', exact: true }).click();
  const filtering = page.locator('#new-job-filtering-options');
  await expect(filtering.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review', exact: true })).toBeDisabled();
  await filtering.getByRole('button', { name: 'Retry settings' }).click();
  await expect(filtering.getByRole('button', { name: 'FLAC', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Review', exact: true })).toBeEnabled();
  expect(attempts).toBe(2);
});

test('clears source-specific drafts and disclosures when choosing a job type', async ({ page }) => {
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await page.locator('.new-job-source-disclosure summary').click();
  await page.getByRole('button', { name: 'YouTube', exact: true }).click();
  await page.getByPlaceholder('Paste a YouTube URL…').fill('https://www.youtube.com/playlist?list=fixture');
  const importDisclosure = page.locator('.new-job-import-options .settings-disclosure-toggle');
  await importDisclosure.click();
  const filteringDisclosure = page.locator('.new-job-job-settings .settings-disclosure-toggle').first();
  await filteringDisclosure.click();
  await expect(filteringDisclosure).toHaveAttribute('aria-expanded', 'true');

  await page.getByRole('button', { name: 'Bandcamp', exact: true }).click();
  await expect(page.getByPlaceholder('Paste a Bandcamp URL…')).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Review', exact: true })).toBeDisabled();
  await expect(importDisclosure).toHaveAttribute('aria-expanded', 'false');
  await expect(filteringDisclosure).toHaveAttribute('aria-expanded', 'false');
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
  await page.getByRole('button', { name: 'Column mapping Auto-detect' }).click();
  await page.locator('.new-job-csv-grid').getByLabel('Artist', { exact: true }).fill('performer');
  await page.locator('.new-job-csv-grid').getByLabel('Title', { exact: true }).fill('recording');
  await expect(page.getByRole('button', { name: 'Review', exact: true })).toBeEnabled();
  const firstCreated = page.waitForResponse(reply => reply.request().method() === 'POST'
    && new URL(reply.url()).pathname === '/api/job-previews');
  await page.getByRole('button', { name: 'Review', exact: true }).click();
  const firstCreation = await firstCreated;
  const original = await firstCreation.json() as Schema<'CreateJobPreviewResponseDto'>;
  const originalRequest = firstCreation.request().postDataJSON() as Schema<'CreateJobPreviewRequestDto'>;
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
  await page.getByRole('button', { name: 'Deselect all', exact: true }).click();
  const firstSong = page.locator('.job-preview-row').filter({ hasText: 'Fixture CSV Song' });
  await firstSong.locator('input[type=checkbox]').check();
  const resolvedSettingsReads = settingsReads;
  await daemon.command('expire-job-previews');
  const rejected = page.waitForResponse(reply => new URL(reply.url()).pathname
    === `/api/job-previews/${original.preview.previewId}/commit`);
  await page.getByRole('button', { name: 'Start 1 job', exact: true }).click();
  expect((await rejected).status()).toBe(409);
  await expect(page.getByRole('alert')).toContainText('expired');
  const noJobs = await page.request.get('/api/jobs?includeAll=true&archived=false');
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
  await page.getByRole('button', { name: 'Deselect all', exact: true }).click();
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
  await expect(page.locator('.job-child-row')).toHaveCount(1);
  await expect(page.locator('.job-child-row')).toContainText('Fixture CSV Song');
  await expect(page.locator('.job-child-row')).not.toContainText('Second CSV Song');
  const jobsResponse = await page.request.get(`/api/jobs?submissionId=${receipt.submissionId}&includeAll=true&archived=false`);
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
  await expect(page.locator('.job-child-row')).toHaveCount(1);
  await expect(page.locator('.job-child-row')).toContainText('Paged Item 101');
});
