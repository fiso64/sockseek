import { performance } from 'node:perf_hooks';
import { request as httpRequest } from 'node:http';
import { randomUUID } from 'node:crypto';
import { readdir, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { test, expect, measureApi } from './fixtures';
import type { Schema } from '../src/api/types';
import type { Request } from '@playwright/test';

// DOM trace snapshots dominate large-page interaction timings; functional
// suites retain traces, while this opt-in probe measures the application.
test.use({ daemonLogMetrics: true, trace: 'off' });

test('measures common UI requests and a 10k-file search @Load', async ({ page, daemon }, testInfo) => {
  test.setTimeout(90_000);
  type ApiRequest = { phase: string; method: string; path: string; elapsedMs: number };
  const requests: ApiRequest[] = [];
  const requestPhases = new WeakMap<object, string>();
  let phase = 'dashboard';
  page.on('request', request => requestPhases.set(request, phase));
  page.on('requestfinished', request => {
    const url = new URL(request.url());
    if (!url.pathname.startsWith('/api/')) return;
    requests.push({ phase: requestPhases.get(request) ?? phase, method: request.method(), path: url.pathname + url.search,
      elapsedMs: Math.round(request.timing().responseEnd) });
  });

  const dashboardStart = performance.now();
  await page.goto('/dashboard');
  await expect(page.locator('.dashboard-metric-card').first()).toBeVisible();
  await expect(page.locator('.dashboard-metric-card').first().locator('strong')).toHaveText('0 B');
  const dashboardMs = performance.now() - dashboardStart;
  // Diagnostic API calls are separate from browser UI request counts. Compare
  // first-use costs with repeated reads, rather than calling cold JIT time SQL.
  const warmReadSamples: Record<string, number[]> = {};
  for (const path of ['/api/dashboard/analytics?range=24h', '/api/daemon/snapshot']) {
    warmReadSamples[path] = [];
    for (let sample = 0; sample < 3; sample++) {

      const response = await measureApi(daemon.url, path);
      expect(response.ok, response.body).toBe(true);
      warmReadSamples[path].push(response.elapsedMs);
    }
  }

  phase = 'jobs';
  const jobsStart = performance.now();
  await page.getByRole('navigation', { name: 'Primary navigation' })
    .getByRole('button', { name: 'Jobs' }).click();
  await expect(page.locator('.resource-state-notice.empty.blocking')).toContainText('No jobs yet');
  expect(requests.filter(item => item.method === 'GET' && item.path.startsWith('/api/jobs?'))).toHaveLength(1);
  expect(requests.filter(item => item.method === 'GET' && item.path.startsWith('/api/dashboard/analytics?'))).toHaveLength(1);
  const jobsMs = performance.now() - jobsStart;

  await daemon.command('large-search');
  phase = 'large search';
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'File Search', exact: true }).click();
  const searchStart = performance.now();
  const viewResponse = page.waitForResponse(response =>
    response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname));
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  const createdView = await viewResponse;
  expect(createdView.ok(), await createdView.text()).toBe(true);
  await expect(page.locator('.folder-item-card').first()).toBeVisible({ timeout: 60_000 });
  const firstResultMs = performance.now() - searchStart;
  const firstResultStatus = await page.request.get('/api/server/status');
  expect(firstResultStatus.ok()).toBe(true);
  const firstResultPersistence = (await firstResultStatus.json() as {
    persistence: { rowsWritten: number; bufferedSearchResultCount: number }
  }).persistence;
  await expect(page.locator('.search-results-summary')).toContainText('Complete', { timeout: 60_000 });
  await expect(page.locator('.folder-item-card')).toHaveCount(100, { timeout: 60_000 });
  const searchMs = performance.now() - searchStart;
  expect(requests.filter(item => item.phase === 'large search'
    && item.path.includes('/directories?revision=0'))).toHaveLength(0);
  const searchPages = requests.filter(item => item.phase === 'large search'
    && item.method === 'GET' && item.path.includes('/directories?'));
  expect(new Set(searchPages.map(item => item.path)).size).toBe(searchPages.length);
  const renderedFileRows = await page.locator('.folder-file-row').count();
  const statusResponse = await page.request.get('/api/server/status');
  expect(statusResponse.ok()).toBe(true);
  const persistence = (await statusResponse.json() as {
    persistence: { state: string; lastFailure: string | null; successfulCommitCount: number;
      commitLatencyHistogram: number[]; bufferedSearchResultCount: number; droppedSearchResultCount: number;
      databaseSizeBytes: number; walSizeBytes: number; rowsWritten: number };
  }).persistence;
  expect(persistence.state).not.toBe('Unhealthy');
  expect(persistence.droppedSearchResultCount).toBe(0);

  const pageUrl = requests.filter(item => item.path.includes('/api/search-views/')
    && item.path.includes('/directories?')).at(-1)?.path;
  expect(pageUrl).toBeTruthy();
  const pageSamples: number[] = [];
  let firstPageBytes = 0;
  for (let index = 0; index < 3; index++) {

    const response = await measureApi(daemon.url, pageUrl!);
    expect(response.ok, response.body).toBe(true);
    if (index === 0) firstPageBytes = response.bodyBytes;
    pageSamples.push(response.elapsedMs);
  }
  const compressedPage = await new Promise<{ bytes: number; encoding: string | undefined }>((resolve, reject) => {
    const request = httpRequest(new URL(pageUrl!, daemon.url), { headers: { 'Accept-Encoding': 'br' } }, response => {
      let bytes = 0;
      response.on('data', (chunk: Buffer) => { bytes += chunk.byteLength; });
      response.on('end', () => response.statusCode === 200
        ? resolve({ bytes, encoding: response.headers['content-encoding'] })
        : reject(new Error(`Compressed page failed with HTTP ${response.statusCode}`)));
      response.on('error', reject);
    });
    request.on('error', reject);
    request.end();
  });
  expect(compressedPage.encoding).toBe('br');
  let drainedPersistence = persistence;
  await expect.poll(async () => {
    const response = await page.request.get('/api/server/status');
    expect(response.ok(), await response.text()).toBe(true);
    drainedPersistence = (await response.json() as { persistence: typeof persistence }).persistence;
    return drainedPersistence.bufferedSearchResultCount === 0
      && drainedPersistence.rowsWritten >= 10_004;
  }, { timeout: 30_000, intervals: [200, 500] }).toBe(true);
  const rawDrainMs = Math.round(performance.now() - searchStart);

  const profiling = await page.context().newCDPSession(page);
  await profiling.send('Performance.enable');
  const interactionMetrics: Record<string, Record<string, number>> = {};
  async function rendererMetrics(): Promise<Record<string, number>> {
    const { metrics } = await profiling.send('Performance.getMetrics');
    return Object.fromEntries(metrics.map(metric => [metric.name, metric.value]));
  }
  async function measureRenderer(name: string, action: () => Promise<unknown>): Promise<void> {
    const before = await rendererMetrics();
    const started = performance.now();
    await action();
    await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => resolve())));
    const after = await rendererMetrics();
    interactionMetrics[name] = { wallMs: Math.round(performance.now() - started) };
    for (const field of ['ScriptDuration', 'LayoutDuration', 'RecalcStyleDuration', 'TaskDuration'])
      interactionMetrics[name][field + 'Ms'] = Math.round((after[field]! - before[field]!) * 1_000);
  }

  phase = 'selection';
  const countResponse = () => page.waitForResponse(response => response.request().method() === 'POST'
    && /\/api\/search-views\/[^/]+\/selection-counts$/.test(new URL(response.url()).pathname));
  const selectStart = performance.now();
  const allCount = countResponse();
  // Keep locator traversal out of the application-latency measurement: the
  // 2,400 file checkboxes are not candidates for either of these controls.
  const selectionControls = page.locator('.result-refine-row');
  await measureRenderer('selectAll', () => selectionControls.getByRole('button', { name: 'Select all', exact: true }).click());
  const all = await allCount;
  expect(all.ok(), await all.text()).toBe(true);
  expect(Number((await all.json() as { publicFileCount: number }).publicFileCount)).toBe(10_000);
  const allMs = Math.round(performance.now() - selectStart);
  const excludeStart = performance.now();
  const excludedCount = countResponse();
  const distantFolder = page.locator('.folder-result-summary')
    .getByRole('checkbox', { name: 'Select all files in Album-000', exact: true });
  await measureRenderer('roleLookup', async () => expect(await distantFolder.count()).toBe(1));
  await measureRenderer('scroll', () => distantFolder.scrollIntoViewIfNeeded());
  await measureRenderer('uncheck', () => distantFolder.uncheck());
  const excluded = await excludedCount;
  expect(excluded.ok(), await excluded.text()).toBe(true);
  expect(Number((await excluded.json() as { publicFileCount: number }).publicFileCount)).toBe(9_900);
  const excludeMs = Math.round(performance.now() - excludeStart);
  const selectionMs = Math.round(performance.now() - selectStart);
  expect(requests.filter(item => item.phase === 'selection' && item.method === 'POST'
    && /\/api\/search-views\/[^/]+\/selection-counts$/.test(item.path))).toHaveLength(2);
  await testInfo.attach('large-result-renderer-metrics', {
    contentType: 'application/json', body: JSON.stringify(interactionMetrics),
  });
  await profiling.detach();
  await selectionControls.getByRole('button', { name: 'Deselect all', exact: true }).click();

  phase = 'refine';
  const refineStart = performance.now();
  const refinedViewResponse = page.waitForResponse(response =>
    response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname));
  await page.getByRole('textbox', { name: 'Filter search results' }).fill('Album-000');
  const refinedView = await refinedViewResponse;
  expect(refinedView.ok(), await refinedView.text()).toBe(true);
  await expect(page.locator('.folder-item-card')).toHaveCount(1, { timeout: 60_000 });
  const refineFirstResultMs = performance.now() - refineStart;
  await expect(page.locator('.search-results-summary')).toContainText('Complete', { timeout: 60_000 });
  const refineMs = performance.now() - refineStart;
  const refineRequests = requests.filter(item => item.phase === 'refine');
  expect(refineRequests.filter(item => item.method === 'POST'
    && /\/api\/jobs\/[^/]+\/search-views$/.test(item.path))).toHaveLength(1);
  expect(refineRequests.filter(item => item.path.includes('/directories?revision=0'))).toHaveLength(0);
  const refinedPages = refineRequests.filter(item => item.method === 'GET'
    && item.path.includes('/directories?'));
  expect(new Set(refinedPages.map(item => item.path)).size).toBe(refinedPages.length);

  phase = 'downloads';
  const downloadsStart = performance.now();
  await page.getByRole('navigation', { name: 'Primary navigation' })
    .getByRole('button', { name: 'Downloads' }).click();
  await expect(page.getByRole('heading', { name: 'Downloads', exact: true })).toBeVisible();
  await expect(page.locator('.resource-state-notice.empty.blocking')).toContainText('No downloads');
  expect(requests.filter(item => item.phase === 'downloads' && item.method === 'POST'
    && item.path === '/api/transfers/timeline')).toHaveLength(0);
  const downloadsMs = performance.now() - downloadsStart;

  phase = 'shares';
  const sharesStart = performance.now();
  await page.goto('/users/fixture-peer/shares');
  await expect(page.getByText('Selected.txt', { exact: true })).toBeVisible();
  const sharesMs = performance.now() - sharesStart;
  expect(requests.filter(item => item.phase === 'shares'
    && /^\/api\/users\/[^/]+\/profile\?/.test(item.path))).toHaveLength(0);

  // Commit the large all-except plan only after the navigation timings above:
  // accepted-plan construction is the measurement, not 9,900 live transfers.
  await daemon.command('hold-download');
  const selectedPageResponse = await page.request.get(pageUrl!);
  expect(selectedPageResponse.ok(), await selectedPageResponse.text()).toBe(true);
  const selectedPage = await selectedPageResponse.json() as {
    items: { ref: { ref: string } }[]
  };
  expect(selectedPage.items).toHaveLength(100);
  const viewPath = new URL(pageUrl!, daemon.url).pathname;
  const viewId = viewPath.split('/')[3];
  const revision = new URL(pageUrl!, daemon.url).searchParams.get('revision');
  expect(viewId).toBeTruthy();
  expect(revision).toBeTruthy();

  const commitResponse = await measureApi(daemon.url, `/api/search-views/${viewId}/commit`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ revision, selection: { mode: 'AllExcept', refs: [selectedPage.items[0]!.ref.ref] },
      idempotencyKey: randomUUID() }) });
  expect(commitResponse.ok, commitResponse.body).toBe(true);
  const commit = JSON.parse(commitResponse.body) as {
    requestedCount: number; submittedCount: number; rejectedCount: number;
    skippedCount: number; submissionId: string | null
  };
  const commitMs = commitResponse.elapsedMs;
  expect(Number(commit.submittedCount)).toBe(99);
  expect(Number(commit.rejectedCount)).toBe(0);
  expect(Number(commit.skippedCount)).toBe(0);
  expect(commit.submissionId).toBeTruthy();
  let acceptedRoot: { jobId: string } | undefined;
  await expect.poll(async () => {
    const rootsResponse = await page.request.get(
      `/api/jobs?submissionId=${commit.submissionId}&includeAll=false&archived=false&limit=100`);
    expect(rootsResponse.ok(), await rootsResponse.text()).toBe(true);
    const roots = await rootsResponse.json() as { jobId: string }[];
    acceptedRoot = roots[0];
    return roots.length;
  }, { timeout: 10_000, intervals: [200, 500] }).toBe(1);
  const rootDetailResponse = await page.request.get(`/api/jobs/${acceptedRoot!.jobId}`);
  expect(rootDetailResponse.ok(), await rootDetailResponse.text()).toBe(true);
  const rootDetail = await rootDetailResponse.json() as { payload: { count: number } };
  expect(Number(rootDetail.payload.count)).toBe(99);
  const cancellation = await page.request.post(`/api/jobs/${acceptedRoot!.jobId}/cancel`);
  expect(cancellation.ok()).toBe(true);
  await daemon.command('release-download');

  console.log('PERF_AUDIT ' + JSON.stringify({ dashboardMs: Math.round(dashboardMs), jobsMs: Math.round(jobsMs), warmReadSamples,
    searchMs: Math.round(searchMs), firstResultMs: Math.round(firstResultMs),
    rawRowsAtFirstResult: firstResultPersistence.rowsWritten,
    bufferedRawAtFirstResult: firstResultPersistence.bufferedSearchResultCount,
    selectionMs, refineFirstResultMs: Math.round(refineFirstResultMs), refineMs: Math.round(refineMs),
    downloadsMs: Math.round(downloadsMs),
    sharesMs: Math.round(sharesMs), pageSamples, firstPageBytes,
    compressedPageBytes: compressedPage.bytes,
    renderedFileRows,
    allMs, excludeMs, rawDrainMs, commitMs, acceptedDirectoryRoots: rootDetail.payload.count,
    persistenceAtViewCompletion: persistence, drainedPersistence,
    daemonMetrics: daemon.logs().filter(line => line.startsWith('E2E_METRIC ')), requests }));
});

test('pages 2 named tracks and 1001 supplemental images without child-detail requests @Load', async ({ page, daemon }, testInfo) => {
  test.setTimeout(120_000);
  await daemon.command('large-album-art-search');
  await daemon.command('hold-art-download');
  const reads: { path: string; milliseconds: number }[] = [];
  const files: { after: string | null; count: number; total: number; revision: number | string; milliseconds: number }[] = [];
  const destination = testInfo.outputPath('organized-album');
  let validateFinalPaths = false;
  let documentGeneration = 0;
  const requestDocuments = new WeakMap<Request, number>();
  const recordDocument = (request: Request) => requestDocuments.set(request, documentGeneration);
  page.on('request', recordDocument);
  const finalPaths = new Set<string>();
  const captured: Promise<void>[] = [];
  const captureErrors: unknown[] = [];
  const capture = (read: Promise<void>) => captured.push(read.catch(cause => { captureErrors.push(cause); }));
  let completedApiBytes = 0;
  let liveFrames = 0, liveBytes = 0;
  page.on('websocket', socket => socket.on('framereceived', frame => {
    liveFrames++;
    liveBytes += typeof frame.payload === 'string' ? Buffer.byteLength(frame.payload) : frame.payload.byteLength;
  }));
  const observeFinished = (request: Request) => {
    const url = new URL(request.url());
    if (!url.pathname.startsWith('/api/')) return;
    const milliseconds = Math.round(request.timing().responseEnd);
    reads.push({ path: url.pathname + url.search, milliseconds });
    capture(request.sizes().then(size => { completedApiBytes += size.responseBodySize; }));
    if (!url.pathname.endsWith('/directory-files')) return;
    // Navigation can finish an old request after Chromium releases its body.
    // Keep counting the request, but validate only the current document's pages.
    if (requestDocuments.get(request) !== documentGeneration) return;
    const verifyFinalPaths = validateFinalPaths;
    capture(request.response().then(async response => {
      if (!response?.ok()) return;
      const value = await response.json() as Schema<'DirectoryManifestPageDto'>;
      expect(url.searchParams.get('limit')).toBe('500');
      expect(value.items.length).toBeLessThanOrEqual(500);
      if (verifyFinalPaths) for (const entry of value.items) {
        expect(entry.child?.terminalOutcome).toBe('Succeeded');
        const filename = entry.target!.filename.replaceAll('\\', '/').split('/').at(-1)!;
        const expectedPath = join(destination, 'Organized', 'Fixture Album', filename);
        expect(entry.file?.downloadPath).toBe(expectedPath);
        finalPaths.add(expectedPath);
      }
      files.push({ after: url.searchParams.get('afterOrdinal'), count: value.items.length,
        total: Number(value.entryCount) + Number(value.supplementalEntryCount), revision: value.jobRevision, milliseconds });
    }));
  };
  page.on('requestfinished', observeFinished);

  const response = await measureApi(daemon.url, '/api/jobs/downloads/album', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    albumQuery: { album: 'Fixture Album', artistMaybeWrong: false },
    options: { outputParentDir: destination, downloadSettings: { search: { noBrowseFolder: true },
      output: { albumArtOption: 1, nameFormat: 'Organized/{foldername}/{filename}' } } },
  } satisfies Schema<'SubmitAlbumJobRequestDto'>) });
  expect(response.status, response.body).toBe(202);
  const acceptedMs = response.elapsedMs;
  const job = JSON.parse(response.body) as Schema<'JobSummaryDto'>;
  const opening = performance.now();
  await page.goto(`/jobs/${job.jobId}`);
  const folder = page.locator('.folder-item-card');
  await expect(folder.locator('.folder-file-row')).toHaveCount(500);
  await expect(folder.locator('.folder-transfer-file-count strong')).toHaveText('500/1003');
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'Art0000.jpg' }).getByLabel('50% complete', { exact: true })).toBeVisible();
  expect(await folder.getByText('File details unavailable', { exact: true }).count()).toBe(0);
  const initialDomMs = Math.round(performance.now() - opening);
  const continuationMs: number[] = [];
  for (const expected of [1000, 1003]) {
    const start = performance.now();
    await page.getByRole('button', { name: 'Load more files', exact: true }).click();
    await expect(folder.locator('.folder-file-row')).toHaveCount(expected);
    continuationMs.push(Math.round(performance.now() - start));
  }
  await expect(folder).toContainText('Art1000.jpg');
  await expect(page.getByRole('button', { name: 'Load more files', exact: true })).toHaveCount(0);
  await folder.locator('.folder-transfer-summary').scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath('queued-artwork.png') });
  const finishing = performance.now();
  await daemon.command('release-art-download');
  await daemon.command('wait-directory-complete');
  const backendCompletionMs = Math.round(performance.now() - finishing);
  await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
  const completionUiLagMs = Math.round(performance.now() - finishing) - backendCompletionMs;
  await expect(folder.locator('.folder-file-row')).toHaveCount(1003);
  await expect(folder.locator('.folder-file-state.complete')).toHaveCount(1003);
  // Finish reading the old document's response bodies before navigation lets
  // Chromium release them. DOM completion alone does not drain this observer.
  await Promise.all(captured);
  expect(captureErrors).toEqual([]);
  validateFinalPaths = true;
  documentGeneration++;
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(500);
  await expect(folder.locator('.folder-transfer-file-count strong')).toHaveText('500/1003');
  await page.getByRole('button', { name: 'Load more files', exact: true }).click();
  await expect(folder.locator('.folder-file-row')).toHaveCount(1000);
  await page.getByRole('button', { name: 'Load more files', exact: true }).click();
  await expect(folder.locator('.folder-file-row')).toHaveCount(1003);
  await expect(folder).toContainText('Art1000.jpg');
  await expect(folder.locator('.folder-file-state.complete')).toHaveCount(1003);
  await folder.locator('.folder-transfer-summary').scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath('completed-directory.png') });
  page.off('requestfinished', observeFinished);
  page.off('request', recordDocument);
  await Promise.all(captured);
  expect(captureErrors).toEqual([]);
  const outputs = (await readdir(destination, { recursive: true, withFileTypes: true }))
    .filter(file => file.isFile()).map(file => join(file.parentPath, file.name));
  expect(finalPaths.size).toBe(1003);
  expect(outputs.sort()).toEqual([...finalPaths].sort());
  for (const output of outputs) expect((await stat(output)).size).toBe(128);
  const childDetails = reads.filter(read => /^\/api\/jobs\/[^/?]+$/.test(read.path) && read.path !== `/api/jobs/${job.jobId}`);
  expect(childDetails, 'File pages carry supplemental targets and state; no per-file job fetches.').toEqual([]);
  console.log('PERF_ARTWORK ' + JSON.stringify({ acceptedMs, initialDomMs, continuationMs, backendCompletionMs, completionUiLagMs, completedApiBytes, liveFrames, liveBytes, files, reads }));
});

test('opens a 1k-file directory and verifies paged downloads and retained outcomes @Load', async ({ page, daemon }, testInfo) => {
  test.setTimeout(180_000);
  await daemon.command('hold-download');
  const count = 1_000;
  const entries = Array.from({ length: count }, (_, index) => {
    const part = `Part${String(Math.floor(index / 200)).padStart(2, '0')}`;
    return { target: { username: 'fixture-peer', filename: `Fixture\\Large\\${part}\\File${String(index).padStart(5, '0')}.bin`,
      size: 128, extension: '.bin' }, relativeDirectoryComponents: [part] };
  });
  const reads: { path: string; milliseconds: number }[] = [];
  let liveFrames = 0, liveBytes = 0;
  page.on('websocket', socket => socket.on('framereceived', frame => {
    liveFrames++;
    liveBytes += typeof frame.payload === 'string' ? Buffer.byteLength(frame.payload) : frame.payload.byteLength;
  }));
  page.on('requestfinished', request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/')) reads.push({ path: url.pathname + url.search,
      milliseconds: Math.round(request.timing().responseEnd) });
  });
  const started = performance.now();
  const accepted = await measureApi(daemon.url, '/api/jobs/lists', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    name: 'Large directory plan', jobs: [{ kind: 'remote-directory', plan: {
      displayRoot: 'Large directory', entries, totalKnownBytes: count * 128,
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'>) });
  expect(accepted.status, accepted.body).toBe(202);
  const acceptedMs = accepted.elapsedMs;
  const root = JSON.parse(accepted.body) as Schema<'JobSummaryDto'>;
  const navigation = performance.now();
  await page.goto(`/jobs/${root.jobId}`);
  await expect(page.locator('.job-child-open').filter({ hasText: 'Large directory' })).toBeVisible();
  const rootNavigationMs = Math.round(performance.now() - navigation);
  const displayTiming = await page.evaluateHandle(() => {
    let clickedAt: number | null = null, readyMs: number | null = null;
    const clicked = (event: MouseEvent) => {
      if ((event.target as Element)?.closest('.job-child-open')) clickedAt ??= performance.now();
    };
    document.addEventListener('click', clicked, true);
    const observer = new MutationObserver(() => {
      if (clickedAt !== null && document.querySelectorAll('.folder-file-row').length === 500)
        readyMs ??= Math.round(performance.now() - clickedAt);
    });
    observer.observe(document.body, { childList: true, subtree: true });
    return { finish: () => { observer.disconnect(); document.removeEventListener('click', clicked, true); return readyMs; } };
  });
  const opening = performance.now();
  await page.locator('.job-child-open').filter({ hasText: 'Large directory' }).click();
  const directoryId = new URL(page.url()).pathname.split('/').at(-1)!;
  const folder = page.locator('.folder-item-card');
  await expect(folder.locator('.folder-file-row')).toHaveCount(500);
  await expect(folder.locator('.folder-transfer-file-count strong')).toHaveText(`500/${count}`);
  const initialObservedMs = Math.round(performance.now() - opening);
  const initialDomMs = await displayTiming.evaluate(timing => timing.finish());
  await displayTiming.dispose();
  expect(initialDomMs).not.toBeNull();
  const continuing = performance.now();
  await page.getByRole('button', { name: 'Load more files', exact: true }).click();
  await expect(folder.locator('.folder-file-row')).toHaveCount(1000);
  const continuationMs = Math.round(performance.now() - continuing);
  await daemon.command('wait-download-started');
  const readyToDownloadMs = Math.round(performance.now() - started);
  await expect(folder.locator('.folder-file-row').first().getByLabel('50% complete', { exact: true })).toBeVisible();
  await expect(folder.locator('.transfer-state').first()).toHaveText('Running');
  await folder.locator('.folder-transfer-summary').scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath('large-directory-active.png') });
  const finishing = performance.now();
  await daemon.command('release-download');
  // Exercise a full page during transfer/persistence churn, not only after the
  // job has completed. Keep its latency separate from backend completion time.
  const [backendCompletionMs, activeManifest] = await Promise.all([
    daemon.command('wait-directory-complete').then(() => Math.round(performance.now() - finishing)),
    (async () => {

      const response = await measureApi(daemon.url, `/api/jobs/${directoryId}/directory-files?attemptNumber=1&limit=500`);
      expect(response.ok, response.body).toBe(true);
      const body = JSON.parse(response.body) as Schema<'DirectoryManifestPageDto'>;
      const milliseconds = response.elapsedMs;
      expect(Number(body.entryCount)).toBe(count);
      expect(body.items).toHaveLength(500);
      expect(body.items.every((item, index) => item.ordinal === index
        && item.target?.filename === entries[index]!.target.filename
        && item.relativeDirectoryComponents?.[0] === entries[index]!.relativeDirectoryComponents[0])).toBe(true);
      return { milliseconds, rows: body.items.length,
        succeeded: body.items.filter(item => item.child?.terminalOutcome === 'Succeeded').length };
    })(),
  ]);
  await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
  await expect(folder.locator('.folder-file-state.complete')).toHaveCount(1000);
  const completionUiLagMs = Math.round(performance.now() - finishing) - backendCompletionMs;

  const samples: { path: string; milliseconds: number }[] = [];
  let completed!: Schema<'JobDetailDto'>;
  for (const path of [`/api/jobs/${directoryId}`,
    `/api/jobs/${directoryId}/directory-files?attemptNumber=1&afterOrdinal=${count - 501}&limit=500`]) {

    const response = await measureApi(daemon.url, path);
    expect(response.ok, response.body).toBe(true);
    const body = JSON.parse(response.body);
    samples.push({ path, milliseconds: response.elapsedMs });
    if (path.endsWith(directoryId)) completed = body as Schema<'JobDetailDto'>;
    else {
      const tail = body as Schema<'DirectoryManifestPageDto'>;
      expect(tail.items).toHaveLength(500);
      expect(Number(tail.entryCount)).toBe(count);
      expect(tail.nextOrdinal ?? null).toBeNull();
      expect(tail.items.every(item => item.child?.terminalOutcome === 'Succeeded')).toBe(true);
    }
  }

  const timelineResponse = await measureApi(daemon.url, '/api/transfers?direction=download&limit=500');
  expect(timelineResponse.ok, timelineResponse.body).toBe(true);
  const timeline = JSON.parse(timelineResponse.body) as Schema<'TransferTimelinePageDto'>;
  samples.push({ path: '/api/transfers?direction=download&limit=500',
    milliseconds: timelineResponse.elapsedMs });
  expect(timeline.items).toHaveLength(500);
  expect(timeline.nextCursor).toBeTruthy();
  expect(new Set(timeline.items.map(item => item.transferId)).size).toBe(500);
  const targets = new Set(entries.map(entry => entry.target.filename));
  expect(timeline.items.every(item => item.workflowId === root.workflowId && item.terminalOutcome === 'Succeeded'
    && item.transferredBytes === 128 && item.attemptCount === 1 && targets.has(item.remotePath!))).toBe(true);
  for (const transfer of [timeline.items[0]!, timeline.items.at(-1)!]) {

    const path = `/api/transfers/${transfer.transferId}/attempts?afterAttemptNumber=0&limit=500`;
    const response = await measureApi(daemon.url, path);
    expect(response.ok, response.body).toBe(true);
    const attempts = JSON.parse(response.body) as Schema<'TransferAttemptHistoryDto'>[];
    samples.push({ path, milliseconds: response.elapsedMs });
    expect(attempts).toHaveLength(1);
    expect(attempts[0]).toMatchObject({ transferId: transfer.transferId, attemptNumber: 1,
      sourceUsername: 'fixture-peer', sourcePath: transfer.remotePath, state: 'Completed', failureReason: 'None' });
    expect(attempts[0]!.completedAtUtc).toBeTruthy();
  }
  expect(completed.summary.terminalOutcome).toBe('Succeeded');
  expect(completed.payload?.kind).toBe('remote-directory');
  if (completed.payload?.kind !== 'remote-directory') throw new Error('Expected a completed directory.');
  expect(Number(completed.payload.directory.successfulFileCount)).toBe(count);
  expect(Number(completed.payload.directory.bytesTransferred)).toBe(count * 128);
  const output = completed.payload.directory.downloadPath!;
  const names = (await readdir(output, { recursive: true })).filter(name => name.endsWith('.bin')).sort();
  const expected = entries.map(entry => join(...entry.relativeDirectoryComponents, entry.target.filename.split('\\').at(-1)!)).sort();
  expect(names).toEqual(expected);
  const sizes = await Promise.all(names.map(name => stat(join(output, name))));
  expect(sizes.every(file => file.size === 128)).toBe(true);
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(500);
  await expect(folder.locator('.folder-transfer-file-count strong')).toHaveText(`500/${count}`);
  await expect(folder.locator('.folder-file-state.complete')).toHaveCount(500);
  await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
  await page.screenshot({ path: testInfo.outputPath('large-directory-retained.png') });
  await page.goto('/downloads');
  const downloadsFolder = page.locator('.folder-item-card').filter({ hasText: 'Large directory' });
  await expect(downloadsFolder.locator('.folder-file-row')).toHaveCount(500);
  await expect(downloadsFolder.locator('.folder-file-path').first()).toHaveText(/^\s*Part\d{2}\/File\d{5}\.bin\s*$/);
  expect(reads.filter(read => /^\/api\/jobs\/[^/?]+$/.test(read.path)
    && ![root.jobId, directoryId].some(id => read.path === `/api/jobs/${id}`)), 'File pages carry their state.').toEqual([]);
  console.log('PERF_DIRECTORY_HISTORY ' + JSON.stringify({ count, acceptedMs, readyToDownloadMs, rootNavigationMs,
    initialDomMs, initialObservedMs, continuationMs,
    backendCompletionMs, completionUiLagMs, activeManifest, outputFiles: names.length, samples, liveFrames, liveBytes, reads }));
});
