import { test, expect } from './fixtures';
import type { Schema } from '../src/api/types';
import type { Page } from '@playwright/test';
import { stat } from 'node:fs/promises';
import { basename, join } from 'node:path';

test('opens a CLI print-results run as searchable results on navigation and reload', async ({ page }) => {
  const response = await page.request.post('/api/jobs/downloads/song', { data: {
    songQuery: { title: 'Fixture song', artistMaybeWrong: false }, options: { downloadSettings: { printOption: 6 } },
  } satisfies Schema<'SubmitSongJobRequestDto'> });
  expect(response.status()).toBe(202);
  const job = await response.json() as Schema<'JobSummaryDto'>;
  await page.goto(`/jobs/${job.jobId}`);
  await expect(page.locator('.file-item-card')).toHaveCount(1);
  await expect(page.locator('.automatic-job-detail')).toHaveCount(0);
  await page.reload();
  await expect(page.locator('.file-item-card')).toHaveCount(1);
  await page.locator('.file-item-card input[type=checkbox]').check();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
});

for (const mode of ['song', 'album'] as const) {
  test(`shows the existing path of a history-skipped ${mode} on reload`, async ({ page, daemon }, testInfo) => {
    await daemon.command(mode === 'song' ? 'music-search' : 'album-art-search');
    const options = { outputParentDir: testInfo.outputPath(mode), downloadSettings: {
      output: { writeIndex: true }, search: { noBrowseFolder: true },
    } };
    const data = mode === 'song'
      ? { songQuery: { title: 'Fixture song', artistMaybeWrong: false }, options }
      : { albumQuery: { album: 'Fixture Album', artistMaybeWrong: false }, options };
    let firstPath: string | null | undefined;
    for (let run = 0; run < 2; run++) {
      const response = await page.request.post(`/api/jobs/downloads/${mode}`, { data });
      expect(response.status(), await response.text()).toBe(202);
      const job = await response.json() as Schema<'JobSummaryDto'>;
      await daemon.command(`wait-job-complete:${job.jobId}`);
      const detail = await (await page.request.get(`/api/jobs/${job.jobId}`)).json() as Schema<'JobDetailDto'>;
      const output = detail.payload?.kind === 'song' ? detail.payload.file
        : detail.payload?.kind === 'album' ? detail.payload.directory : undefined;
      expect(output).toBeDefined();
      if (run === 0) {
        expect(detail.summary.terminalOutcome).toBe('Succeeded');
        firstPath = output!.downloadPath;
        expect(firstPath).toBeTruthy();
        await page.goto(`/jobs/${job.jobId}`);
        await expect(page.getByTestId('job-info-card')).toHaveCount(0);
        continue;
      }
      expect(detail.summary.terminalOutcome).toBe('Skipped');
      expect(output!.existingFileSource).toBe('CompletionHistory');
      expect(output!.downloadPath).toBe(firstPath);
      await page.goto(`/jobs/${job.jobId}`);
      const existingPath = page.getByTestId('job-info-card').locator('dd');
      await expect(existingPath).toHaveText(firstPath!);
      await page.reload();
      await expect(existingPath).toHaveText(firstPath!);
      await page.getByTestId('job-info-card').getByRole('button').click();
      await expect(page.getByRole('dialog').locator('dd').filter({ hasText: firstPath! })).toHaveCount(1);
    }
  });
}

// Record browser collection reads separately from direct API assertions.
function observeUnfilteredChildReads(page: Page): string[] {
  const parents: string[] = [];
  page.on('request', request => {
    const url = new URL(request.url());
    const parent = url.searchParams.get('parentJobId');
    if (request.method() === 'GET' && url.pathname === '/api/jobs' && parent && !url.searchParams.has('kind'))
      parents.push(parent);
  });
  return parents;
}

function observeJobCompletion(page: Page, job: Schema<'JobSummaryDto'>): Promise<void> {
  return new Promise(resolve => page.on('websocket', socket => socket.on('framereceived', ({ payload }) => {
    for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
      const message = JSON.parse(frame) as { target?: string; arguments?: Schema<'StateUpdateBatchDto'>[] };
      if (message.target?.toLowerCase() !== 'stateupdatebatch') continue;
      const state = message.arguments?.[0]?.state;
      if (state?.removedJobIds.includes(job.jobId!) || state?.jobs.some(delta => delta.jobId === job.jobId
        && (delta.lifecycle ?? delta.added?.lifecycle)?.terminalOutcome === 'Succeeded')) resolve();
    }
  })));
}

test('removes a terminal user root from detail, preserving rejected actions and deleting accepted work', async ({ page }) => {
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Deletion detail fixture', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  const root = await response.json() as Schema<'JobSummaryDto'>;
  expect(root.submissionId).toBeTruthy();
  await page.goto(`/jobs/${root.jobId}`);
  const remove = page.locator('.job-detail-heading-summary').getByRole('button', { name: 'Delete', exact: true });
  await expect(remove).toBeVisible();
  await page.locator('[data-testid="job-open"]').click();
  await expect(page.locator('.job-content-section .job-source-summary')).toContainText('From: fixture-peer');
  await expect(page.locator('.job-detail-meta')).not.toContainText('fixture-peer');
  await expect(page.locator('.job-detail-heading-summary').getByRole('button', { name: 'Delete', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Back to Deletion detail fixture', exact: true }).click();

  let rejectDeletion = true;
  let release!: () => void;
  const released = new Promise<void>((resolve) => { release = resolve; });
  let admitted!: () => void;
  const admission = new Promise<void>((resolve) => { admitted = resolve; });
  await page.route(`**/api/submissions/${root.submissionId}`, async (route) => {
    if (!rejectDeletion) return route.continue();
    admitted();
    await released;
    // Exercise the wire receipt failure path, not just a non-2xx request. The
    // real daemon handles the successful retry below; this receipt is injected.
    const rejected: Schema<'DeleteSubmissionResponseDto'> = {
      submissionId: root.submissionId!, affectedSubmissionCount: 0,
      affectedJobCount: 0, rejectedSubmissionCount: 1,
      rejectionReasons: [{ reason: 'nonterminal-jobs', count: 1 }],
    };
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rejected) });
  });
  await remove.click();
  await admission;
  await expect(remove).toBeDisabled();
  release();
  const alert = page.getByRole('alert').filter({ hasText: 'Job action failed' });
  await expect(alert).toContainText('nonterminal-jobs');
  await expect(page).toHaveURL(new RegExp(`/jobs/${root.jobId}$`));
  await expect(remove).toBeEnabled();
  await expect(page.locator('[data-testid="job-row"]')).toHaveCount(1);
  rejectDeletion = false;
  await remove.click();
  await expect(page).toHaveURL(/\/jobs\?tab=downloads$/);
  await expect(page.locator('.search-history-row').filter({ hasText: 'Deletion detail fixture' })).toHaveCount(0);
  await page.reload();
  await expect(page.locator('.search-history-row').filter({ hasText: 'Deletion detail fixture' })).toHaveCount(0);
  const retained = await page.request.get(`/api/jobs/${root.jobId}`);
  expect(retained.status()).toBe(404);
});

test('does not navigate away from a new page when an earlier detail deletion finishes', async ({ page }) => {
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Background deletion fixture', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status(), await response.text()).toBe(202);
  const root = await response.json() as Schema<'JobSummaryDto'>;
  await page.goto(`/jobs/${root.jobId}`);
  let admitted!: () => void, release!: () => void;
  const admission = new Promise<void>((resolve) => { admitted = resolve; });
  const released = new Promise<void>((resolve) => { release = resolve; });
  const deletionPath = `/api/submissions/${root.submissionId}`;
  await page.route(`**${deletionPath}`, async (route) => { admitted(); await released; await route.continue(); });
  const deletiond = page.waitForResponse((reply) => new URL(reply.url()).pathname === deletionPath && reply.ok());
  await page.locator('.job-detail-heading-summary').getByRole('button', { name: 'Delete', exact: true }).click();
  await admission;
  await page.getByRole('button', { name: 'Dashboard', exact: true }).click();
  await expect(page).toHaveURL(/\/dashboard$/);
  release();
  const receipt = await (await deletiond).json() as Schema<'DeleteSubmissionResponseDto'>;
  expect(receipt.affectedSubmissionCount).toBe(1);
  await expect(page).toHaveURL(/\/dashboard$/);
  await page.getByRole('button', { name: 'Jobs', exact: true }).click();
  await expect(page.locator('.search-history-row').filter({ hasText: 'Background deletion fixture' })).toHaveCount(0);
});

test('retains a child-page failure through active and terminal detail and recovers on retry', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Retry fixture', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  const root = await response.json() as Schema<'JobSummaryDto'>;
  let interrupted = true;
  // Simulate a transport failure only; successful responses still come from
  // the real daemon, including its independently refreshed job detail.
  await page.route('**/api/jobs?**', (route) => {
    const url = new URL(route.request().url());
    return interrupted && url.searchParams.get('parentJobId') === root.jobId
      ? route.abort('failed') : route.continue();
  });
  await page.goto(`/jobs/${root.jobId}`);
  const alert = page.getByRole('alert').filter({ hasText: 'Child jobs unavailable' });
  await expect(alert).toBeVisible();
  await expect(page.getByTestId('job-info-card').getByRole('progressbar')).toHaveAttribute('aria-valuemax', '1');
  await page.waitForResponse((reply) => new URL(reply.url()).pathname === `/api/jobs/${root.jobId}` && reply.ok());
  await expect(alert).toBeVisible();
  await expect(page.locator('.automatic-job-detail h1')).toHaveText('Retry fixture');
  await daemon.command('release-download');
  await expect(page.locator('.job-detail-heading-summary > .search-status-badge.complete')).toBeVisible();
  await expect(alert).toBeVisible();
  interrupted = false;
  await alert.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.locator('.job-child-list')).toContainText('Fixture song.txt');
  await expect(alert).toHaveCount(0);
});

test('reconciles an open job when a recovery snapshot misses its entire live workflow', async ({ page, daemon }) => {
  await page.clock.install();
  let dropState = false;
  let target: Schema<'JobSummaryDto'> | undefined;
  let complete!: () => void;
  const completed = new Promise<void>(resolve => { complete = resolve; });
  let snapshots = 0;
  await page.route('**/api/daemon/snapshot*', async route => {
    const response = await route.fetch();
    const snapshot = await response.json() as Schema<'StateSnapshotDto'>;
    snapshots++;
    if (!target) expect(snapshot.jobs).toHaveLength(0);
    expect(snapshot.jobs.some(job => job.jobId === target?.jobId)).toBe(false);
    await route.fulfill({ response });
  });
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    server.onMessage(message => {
      const forwarded: string[] = [];
      for (const frame of String(message).split('\u001e').filter(Boolean)) {
        const value = JSON.parse(frame) as { target?: string; arguments?: Schema<'StateUpdateBatchDto'>[] };
        const batch = value.target?.toLowerCase() === 'stateupdatebatch' ? value.arguments?.[0] : undefined;
        if (dropState && batch?.scope.kind === 'daemon') {
          if (target && batch.state.removedJobIds.includes(target.jobId!)) complete();
        } else forwarded.push(frame);
      }
      if (forwarded.length) route.send(forwarded.join('\u001e') + '\u001e');
    });
  });
  await page.goto('/dashboard');
  await expect(page.getByLabel('Connection status').locator('strong')).toContainText('Connected');
  expect(snapshots).toBe(1);
  dropState = true;
  await daemon.command('hold-download');
  const accepted = await page.request.post('/api/jobs/lists', { data: {
    name: 'Missed workflow', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Missed-workflow.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(accepted.status()).toBe(202);
  target = await accepted.json() as Schema<'JobSummaryDto'>;
  await daemon.command('wait-download-started');
  await page.evaluate(path => {
    history.pushState({}, '', path);
    window.dispatchEvent(new PopStateEvent('popstate'));
  }, `/jobs/${target.jobId}`);
  await expect(page.locator('[data-testid="job-row"] .search-status-badge')).toHaveText('Downloading');
  let observed!: () => void, release!: () => void;
  const captured = new Promise<void>(resolve => { observed = resolve; });
  const released = new Promise<void>(resolve => { release = resolve; });
  let capture = true, reads = 0;
  await page.route(`**/api/jobs/${target.jobId}`, async route => {
    reads++;
    if (!capture) return route.continue();
    capture = false;
    const response = await route.fetch();
    expect((await response.json() as Schema<'JobDetailDto'>).summary.lifecycleState).toBe('Running');
    observed();
    await released;
    await route.fulfill({ response });
  });
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 2_000));
  await captured;
  try {
    await daemon.command('release-download');
    await completed;
    dropState = false;
    await daemon.command('hold-download');
    const next = await page.request.post('/api/jobs/lists', { data: {
      name: 'After missed workflow', jobs: [{ kind: 'remote-file', target: {
        username: 'fixture-peer', filename: 'Fixture\\After-gap.txt', size: 128, extension: '.txt',
      } }],
    } satisfies Schema<'SubmitJobListRequestDto'> });
    expect(next.status()).toBe(202);
    // This badge arrives only after the gap-triggered snapshot is applied.
    await expect(page.getByRole('button', { name: /^Downloads/ })
      .locator('.nav-count')).toHaveText('1');
    expect(snapshots).toBe(2);
  } finally { release(); }
  await page.clock.runFor(100); // Paint, without advancing the fallback second.
  await expect(page.locator('.job-detail-heading-summary > .search-status-badge.complete')).toBeVisible();
  await expect(page.locator('[data-testid="job-row"] .search-status-badge')).toHaveText('Complete');
  expect(reads, 'Recovery must reconcile once even when both live maps omit the target.').toBe(2);
  await daemon.command('release-download');
});

test('shows the accepted remote-directory files and keeps them after completion', async ({ page, daemon }) => {
  const childReads = observeUnfilteredChildReads(page);
  await daemon.command('hold-download');
  await daemon.command('hold-art-download');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Directory detail fixture', jobs: [{ kind: 'remote-directory', plan: {
      displayRoot: 'Fixture', totalKnownBytes: 256, entries: [
        { target: { username: 'fixture-peer', filename: 'Fixture\\Selected.txt', size: 128, extension: '.txt' }, relativeDirectoryComponents: [] },
        { target: { username: 'fixture-peer', filename: 'Fixture\\Nested\\Nested.jpg', size: 128, extension: '.jpg' }, relativeDirectoryComponents: ['Nested'] },
      ],
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  const root = await response.json() as Schema<'JobSummaryDto'>;
  await page.goto(`/jobs/${root.jobId}`);
  await expect(page.locator('[data-testid="job-row"] [data-testid="job-stats"]')).toContainText('0 / 2 files finished');
  await expect(page.locator('[data-testid="job-row"] [data-testid="job-progress"]')).toHaveAttribute('aria-valuenow', /\d+/);
  await page.locator('[data-testid="job-open"]').filter({ hasText: 'Fixture' }).click();
  await expect(page.locator('.job-detail-meta')).toContainText('Remote Directory');
  await expect(page.locator('.job-detail-meta')).not.toContainText('fixture-peer');
  await expect(page.locator('.job-content-section .job-source-summary')).toContainText('From: fixture-peer');
  await expect(page.locator('.job-related-section')).toHaveCount(0);
  const folder = page.locator('.folder-item-card');
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(folder).toContainText('Selected.txt');
  await expect(folder).toContainText('Nested/Nested.jpg');
  await expect(folder.locator('.folder-transfer-footer').getByLabel('50% complete', { exact: true })).toBeVisible();
  await expect(folder.locator('.folder-file-progress')).toHaveCount(2);
  const jobId = new URL(page.url()).pathname.split('/').at(-1)!;
  const firstResponse = await page.request.get(`/api/jobs/${jobId}/directory-files?limit=1`);
  expect(firstResponse.ok()).toBeTruthy();
  const first = await firstResponse.json() as Schema<'DirectoryManifestPageDto'>;
  expect(first.items).toHaveLength(1);
  expect(first.nextOrdinal).toBe(0);
  const nextResponse = await page.request.get(`/api/jobs/${jobId}/directory-files?attemptNumber=${first.attemptNumber}&afterOrdinal=${first.nextOrdinal}&limit=1`);
  expect(nextResponse.ok()).toBeTruthy();
  const next = await nextResponse.json() as Schema<'DirectoryManifestPageDto'>;
  expect(next.items[0]?.target?.filename).toBe('Fixture\\Nested\\Nested.jpg');
  expect(next.nextOrdinal ?? null).toBeNull();
  // Keep one real file running while another completes. Its live outcome must
  // appear without rereading the unchanged directory membership.
  let releaseManifest!: () => void;
  let manifestRequests = 0;
  const manifestReleased = new Promise<void>(resolve => { releaseManifest = resolve; });
  const manifestPath = `**/api/jobs/${jobId}/directory-files?**`;
  await page.route(manifestPath, async route => {
    manifestRequests++;
    await manifestReleased;
    await route.continue();
  });
  try {
    const rootRefreshed = page.waitForResponse(async response => {
      if (!response.ok() || new URL(response.url()).pathname !== `/api/jobs/${jobId}`) return false;
      const value = await response.json() as Schema<'JobDetailDto'>;
      return value.payload?.kind === 'remote-directory' && Number(value.payload.directory.terminalFileCount) === 1;
    });
    await daemon.command('release-download');
    await rootRefreshed;
    await expect(folder.locator('.folder-file-row').filter({ hasText: 'Selected.txt' })
      .getByLabel('Complete', { exact: true })).toBeVisible();
    await expect(folder.locator('.folder-file-row').filter({ hasText: 'Nested/Nested.jpg' })
      .getByLabel('Downloading', { exact: true })).toBeVisible();
    expect(manifestRequests, 'Live child outcomes do not change the file-page membership.').toBe(0);
  } finally {
    releaseManifest();
    await daemon.command('release-art-download');
  }
  await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
  await page.unroute(manifestPath);

  let manifestRead: 'failed' | 'missing' | 'ready' = 'failed';
  await page.route(manifestPath, route => manifestRead === 'failed' ? route.abort('failed')
    : manifestRead === 'missing' ? route.fulfill({ status: 404, contentType: 'application/json',
      body: JSON.stringify({ message: 'Accepted plan not retained' }) }) : route.continue());
  await page.reload();
  await expect(page.locator('.job-content-section .job-source-summary')).toContainText('From: fixture-peer');
  const manifestAlert = page.getByRole('alert').filter({ hasText: 'Directory files unavailable' });
  await expect(manifestAlert).toBeVisible();
  await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
  manifestRead = 'ready';
  await manifestAlert.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(manifestAlert).toHaveCount(0);
  await expect(folder).toContainText('Nested/Nested.jpg');
  await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
  // A retained legacy job with no manifest has no recoverable file plan.
  // Preserve its root outcome and say what is missing, without a child fallback.
  manifestRead = 'missing';
  await page.reload();
  await expect(folder).toContainText('The accepted file plan is not available for this job.');
  await expect(folder.locator('.folder-file-row')).toHaveCount(0);
  await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
  manifestRead = 'ready';
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await page.setViewportSize({ width: 390, height: 900 });
  await expect(page.locator('.job-content-section .job-source-summary')).toContainText('From: fixture-peer');
  await expect(page.locator('.job-detail-age time')).toBeVisible();
  expect(childReads.filter(id => id === jobId), 'Folder files already carry their child state in the manifest.').toHaveLength(0);
});

test('isolates a directory file failure and retains both independent outcomes', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Partial directory fixture', jobs: [{ kind: 'remote-directory', plan: {
      displayRoot: 'Fixture', totalKnownBytes: 256, entries: [
        { target: { username: 'fixture-peer', filename: 'Fixture\\Fails.txt', size: 128, extension: '.txt' }, relativeDirectoryComponents: [] },
        { target: { username: 'fixture-peer', filename: 'Fixture\\Selected.txt', size: 128, extension: '.txt' }, relativeDirectoryComponents: [] },
      ],
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status(), await response.text()).toBe(202);
  const root = await response.json() as Schema<'JobSummaryDto'>;
  await page.goto(`/jobs/${root.jobId}`);
  await page.locator('[data-testid="job-open"]').filter({ hasText: 'Fixture' }).click();
  const folder = page.locator('.folder-item-card');
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(folder.locator('.folder-transfer-footer').getByLabel('50% complete', { exact: true })).toBeVisible();
  await daemon.command('release-download');
  await expect(folder.locator('.transfer-state').first()).toHaveText('Partially complete');
  await expect(folder.locator('.folder-transfer-footer')).toHaveClass(/partial/);
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'Fails.txt' })).toContainText('Failed');
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'Selected.txt' }).getByLabel('Complete', { exact: true })).toBeVisible();
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(folder.locator('.transfer-state').first()).toHaveText('Partially complete');
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'Fails.txt' })).toContainText('Failed');
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'Selected.txt' }).getByLabel('Complete', { exact: true })).toBeVisible();
});

test('retains named album outputs and pages appended artwork through missing history', async ({ page, daemon }, testInfo) => {
  const childReads = observeUnfilteredChildReads(page);
  const detailRequests: string[] = [];
  page.on('request', request => {
    if (request.method() === 'GET') detailRequests.push(new URL(request.url()).pathname);
  });
  await daemon.command('album-art-search');
  await daemon.command('hold-art-download');
  const destination = testInfo.outputPath('organized-album');
  const response = await page.request.post('/api/jobs/downloads/album', { data: {
    albumQuery: { album: 'Fixture Album', artistMaybeWrong: false },
    options: { outputParentDir: destination, downloadSettings: {
      search: { noBrowseFolder: true }, output: {
        albumArtOption: 1, nameFormat: 'Organized/{foldername}/{filename}', // Most (numeric wire enum)
      },
    } },
  } satisfies Schema<'SubmitAlbumJobRequestDto'> });
  expect(response.status(), await response.text()).toBe(202);
  const job = await response.json() as Schema<'JobSummaryDto'>;
  await page.goto(`/jobs/${job.jobId}`);
  await expect(page.locator('.job-content-section .job-source-summary')).toContainText('From: fixture-peer');
  const folder = page.locator('.folder-item-card');
  await expect(folder.locator('.folder-file-row')).toHaveCount(3);
  const art = folder.locator('.folder-file-row').filter({ hasText: 'Cover.jpg' });
  await expect(art.locator('.folder-file-path')).toHaveAttribute('title', 'Second\\Fixture Album\\Cover.jpg');
  // The browser presents the daemon's active state with the prototype's label.
  await expect(art.getByLabel('Downloading', { exact: true })).toBeVisible();
  await expect(art.getByLabel('50% complete', { exact: true })).toBeVisible();
  await expect(folder).toContainText('3 files');
  const manifestResponse = await page.request.get(`/api/jobs/${job.jobId}/directory-files`);
  expect(manifestResponse.ok()).toBeTruthy();
  const manifest = await manifestResponse.json() as Schema<'DirectoryManifestPageDto'>;
  expect(manifest.entryCount).toBe(2);
  expect(Number(manifest.supplementalEntryCount)).toBe(1);
  expect(manifest.items).toHaveLength(3);
  const artEntry = manifest.items.find(entry => Number(entry.ordinal) >= Number(manifest.entryCount))!;
  expect(artEntry.target?.username).toBe('second-peer');
  expect(Number(artEntry.file?.progressPercent)).toBe(50);
  const artId = artEntry.childJobId!;
  const artResponse = await page.request.get(`/api/jobs/${artId}`);
  expect(artResponse.ok()).toBeTruthy();
  const artDetail = await artResponse.json() as Schema<'JobDetailDto'>;
  expect(artDetail.payload?.kind).toBe('song');
  if (artDetail.payload?.kind === 'song') expect(artDetail.payload.resolvedUsername).toBe('second-peer');

  let pageState: 'missing' | 'failed' | 'ready' = 'missing';
  // Hide the missing row on both read paths. The scoped owner can otherwise
  // recover its terminal summary from history even when the manifest lacks it.
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    server.onMessage(payload => {
      const frames = payload.toString().split('\u001e').filter(Boolean).map(encoded => {
        const message = JSON.parse(encoded);
        if (pageState === 'missing' && message.type === 2 && message.item?.state?.jobs)
          message.item.state.jobs = message.item.state.jobs.filter((row: { jobId: string }) => row.jobId !== artId);
        return JSON.stringify(message);
      });
      route.send(frames.join('\u001e') + '\u001e');
    });
  });
  await page.route(`**/api/jobs/${job.jobId}/directory-files*`, async route => {
    if (pageState === 'failed') return route.abort('failed');
    const response = await route.fetch();
    if (pageState === 'ready') return route.fulfill({ response });
    const body = await response.json() as Schema<'DirectoryManifestPageDto'>;
    body.items = body.items.map(entry => entry.childJobId === artId
      ? { ...entry, target: null, child: null, file: null } : entry);
    return route.fulfill({ response, json: body });
  });
  await page.reload();
  // The active transfer already carries the target and progress. Missing child
  // history must not force another detail request or hide this known live data.
  await expect(art).toContainText('Cover.jpg');
  await expect(art.getByLabel('50% complete', { exact: true })).toBeVisible();
  pageState = 'failed';
  await page.reload();
  const alert = page.getByRole('alert').filter({ hasText: 'Directory files unavailable' });
  await expect(alert).toBeVisible();
  await expect(folder).toContainText('3 files');
  pageState = 'ready';
  await alert.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(art).toContainText('Cover.jpg');
  await expect(folder.locator('.folder-file-row')).toHaveCount(3);
  await expect(folder).toContainText('01 - Intro.flac');
  await expect(alert).toHaveCount(0);
  await daemon.command('release-art-download');
  await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
  await expect(art.getByLabel('Complete', { exact: true })).toBeVisible();
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(3);
  await expect(art.getByLabel('Complete', { exact: true })).toBeVisible();
  const finalManifestResponse = await page.request.get(`/api/jobs/${job.jobId}/directory-files`);
  expect(finalManifestResponse.ok(), await finalManifestResponse.text()).toBe(true);
  const finalManifest = await finalManifestResponse.json() as Schema<'DirectoryManifestPageDto'>;
  expect(finalManifest.items).toHaveLength(3);
  const historyResponse = await page.request.get('/api/transfers?direction=download&limit=500');
  expect(historyResponse.ok(), await historyResponse.text()).toBe(true);
  const history = (await historyResponse.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(history).toHaveLength(3);
  for (const entry of finalManifest.items) {
    expect(entry.child?.terminalOutcome).toBe('Succeeded');
    const transfer = history.find(item => item.identity.jobId === entry.childJobId)!;
    expect(transfer).toBeDefined();
    expect(transfer.status.localPath).toBe(entry.file?.downloadPath);
    expect(transfer.status.localPath).toBe(join(destination, 'Organized', 'Fixture Album',
      basename(entry.target!.filename.replaceAll('\\', '/'))));
    expect((await stat(transfer.status.localPath!)).size).toBe(128);
  }

  // The page retains its slot/count when supplemental child history is missing.
  pageState = 'missing';
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(3);
  await expect(folder).toContainText('01 - Intro.flac');
  await expect(folder).toContainText('3 files');
  const unavailableArt = folder.locator('.folder-file-row').filter({ hasText: 'File details unavailable' });
  await expect(unavailableArt).toContainText('Outcome unavailable');
  await expect(unavailableArt).not.toContainText('Complete');
  pageState = 'ready';
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(3);
  await expect(art.getByLabel('Complete', { exact: true })).toBeVisible();
  expect(childReads.filter(id => id === job.jobId), 'Folder files already carry their child state in the manifest.').toHaveLength(0);
  expect(detailRequests.filter(path => path === `/api/jobs/${artId}`),
    'Supplemental file targets and state come in the same bounded file page.').toHaveLength(0);
});
