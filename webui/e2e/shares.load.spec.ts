import { performance } from 'node:perf_hooks';
import { readFile } from 'node:fs/promises';
import { test, expect, measureApi } from './fixtures';
import type { Schema } from '../src/api/types';
import type { Page } from '@playwright/test';

test.use({ daemonLogMetrics: true, trace: 'off' });

// Capture inside the browser from its input event to the first matching frame.
// The handle keeps the pending promise local to this measurement.
async function observeFilterRender(page: Page, query: string, label: string, files: number) {
  return page.evaluateHandle(({ query, label, files }) => ({
    ready: new Promise<number>(resolve => {
      const input = document.querySelector<HTMLInputElement>('input[aria-label="Filter user shares"]')!;
      input.addEventListener('input', () => {
        const started = performance.now();
        const check = () => {
          if (input.value === query && document.querySelector('.shares-filter-count')?.textContent === label
            && document.querySelectorAll('.share-tree-row.file').length === files)
            resolve(performance.now() - started);
          else requestAnimationFrame(check);
        };
        requestAnimationFrame(check);
      }, { once: true });
    }),
  }), { query, label, files });
}

test('pages and filters a 100k-file peer share without browser fan-out @Load @Performance', async ({ page, daemon }, testInfo) => {
  test.setTimeout(90_000);
  await daemon.command('large-peer-shares');
  const requests: { method: string; path: string; elapsedMs: number }[] = [];
  let streaming = false;
  let lifecycleReadsAfterStreaming = 0;
  let profileReads = 0;
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === 'GET' && /^\/api\/users\/[^/]+\/profile$/.test(path)) profileReads++;
    if (request.method() === 'GET' && /^\/api\/user-browses\/[^/]+\/snapshot$/.test(path)) streaming = true;
    if (streaming && request.method() === 'GET' && /^\/api\/user-browses\/[^/]+$/.test(path)) lifecycleReadsAfterStreaming++;
  });
  page.on('requestfinished', request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/user-browses/'))
      requests.push({ method: request.method(), path: url.pathname + url.search,
        elapsedMs: Math.round(request.timing().responseEnd) });
  });

  const browseStart = performance.now();
  const initialFiles = page.waitForResponse(response => response.ok()
    && /\/api\/user-browses\/[^/]+\/directories\/\d+\/files$/.test(new URL(response.url()).pathname));
  await page.goto('/shares/large-peer');
  await (await initialFiles).finished();
  await expect(page.locator('.share-tree-row.folder')).toContainText('Library', { timeout: 60_000 });
  await expect(page.locator('.share-tree-folder-meta')).toContainText('100,000 files');
  await expect(page.locator('.share-tree-row.file')).toHaveCount(1000);
  const browseMs = Math.round(performance.now() - browseStart);
  expect(requests.filter(item => item.path.includes('/search?'))).toHaveLength(0);
  expect(requests.filter(item => item.path.includes('/directories?'))).toHaveLength(1);
  expect(requests.filter(item => item.path.includes('/files?'))).toHaveLength(1);
  expect(requests.filter(item => item.path.endsWith('/snapshot'))).toHaveLength(1);
  expect(lifecycleReadsAfterStreaming).toBe(0);

  const filter = page.getByRole('textbox', { name: 'Filter user shares', exact: true });
  const selectiveRender = await observeFilterRender(page, 'Track 099999', '1 matching file', 1);
  const selectiveStart = performance.now();
  const selectiveResponse = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/search')
    && new URL(response.url()).searchParams.get('query') === 'Track 099999');
  await filter.fill('Track 099999');
  const selective = await (await selectiveResponse).json() as Schema<'BrowseSearchPageDto'>;
  expect(Number(selective.publicMatchingFileCount)).toBe(1);
  const selectiveResponseMs = Math.round(performance.now() - selectiveStart);
  await expect(page.locator('.shares-filter-count')).toHaveText('1 matching file');
  await expect(page.locator('.share-tree-row.file')).toHaveCount(1);
  await expect(page.getByText('Track 099999.flac', { exact: true })).toBeVisible();
  const selectiveMs = Math.round(performance.now() - selectiveStart);
  const selectiveRenderedMs = Math.round(await selectiveRender.evaluate(observed => observed.ready));
  await selectiveRender.dispose();

  const broadRender = await observeFilterRender(page, 'flac', '100,000 matching files', 999);
  const broadStart = performance.now();
  const broadResponse = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/search')
    && new URL(response.url()).searchParams.get('query') === 'flac');
  await filter.fill('flac');
  // The request is the completion signal. Polling the DOM throughout the
  // debounce/request otherwise reports assertion backoff as rendering latency.
  const firstResponse = await broadResponse;
  const first = await firstResponse.json() as Schema<'BrowseSearchPageDto'>;
  const broadResponseMs = Math.round(performance.now() - broadStart);
  await expect(page.locator('.shares-filter-count')).toHaveText('100,000 matching files');
  await expect(page.locator('.share-tree-row.folder')).toHaveCount(1);
  await expect(page.locator('.share-tree-row.file')).toHaveCount(999);
  await expect(page.getByRole('button', { name: 'Load more', exact: true })).toBeVisible();
  const broadMs = Math.round(performance.now() - broadStart);
  const broadRenderedMs = Math.round(await broadRender.evaluate(observed => observed.ready));
  await broadRender.dispose();
  expect(selectiveRenderedMs, 'A selective filter must render promptly after its input event.').toBeLessThan(300);
  expect(broadRenderedMs, 'A 100k-file broad filter must render promptly after its input event.').toBeLessThan(300);
  expect(requests.filter(item => item.path.includes('/search?'))).toHaveLength(2);
  expect(requests.filter(item => item.path.includes('/directories?'))).toHaveLength(1);
  expect(requests.filter(item => item.path.includes('/files?'))).toHaveLength(1);
  const firstPageBytes = (await firstResponse.body()).byteLength;
  const moreStart = performance.now();
  const moreResponse = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/search')
    && new URL(response.url()).searchParams.get('cursor') !== null);
  await page.getByRole('button', { name: 'Load more', exact: true }).click();
  const nextResponse = await moreResponse;
  const next = await nextResponse.json() as Schema<'BrowseSearchPageDto'>;
  const continuationBytes = (await nextResponse.body()).byteLength;
  await expect(page.locator('.share-tree-row.file')).toHaveCount(1999);
  const moreMs = Math.round(performance.now() - moreStart);
  expect(next.publicMatchingFileCount).toBe(first.publicMatchingFileCount);
  expect(next.publicMatchingBytes).toBe(first.publicMatchingBytes);
  expect(new Set([...first.items, ...next.items].map(item => `${item.kind}:${item.entryId}`)).size).toBe(2000);
  expect(requests.filter(item => item.path.includes('/search?'))).toHaveLength(3);
  const shortStart = performance.now();
  const shortResponse = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/search')
    && new URL(response.url()).searchParams.get('query') === 'fl');
  await filter.fill('fl');
  const short = await (await shortResponse).json() as Schema<'BrowseSearchPageDto'>;
  await expect(page.locator('.share-tree-row.file')).toHaveCount(999);
  await expect(page.locator('.shares-filter-count')).toHaveText('100,000 matching files');
  const shortMs = Math.round(performance.now() - shortStart);
  expect(short.publicMatchingFileCount).toBe(first.publicMatchingFileCount);
  expect(short.publicMatchingBytes).toBe(12_800_000);
  expect(requests.filter(item => item.path.includes('/search?'))).toHaveLength(4);
  const screenshot = testInfo.outputPath('large-shares-filter.png');
  await page.screenshot({ path: screenshot });
  await testInfo.attach('large-shares-filter', { path: screenshot, contentType: 'image/png' });
  // Refine the broad/continued result back to one file. Keep this warm bounded
  // read distinct from the cold first filter and the still-slower broad reads.
  const warmStart = performance.now();
  const warmResponse = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname.endsWith('/search')
    && new URL(response.url()).searchParams.get('query') === 'Track 000001');
  await filter.fill('Track 000001');
  const warm = await warmResponse;
  const warmPage = await warm.json() as Schema<'BrowseSearchPageDto'>;
  const warmSelectiveApiMs = Math.round(warm.request().timing().responseEnd);
  expect(warmSelectiveApiMs).toBeGreaterThanOrEqual(0);
  expect(warmSelectiveApiMs, 'One matching file must stay a fast bounded read.').toBeLessThan(100);
  expect(Number(warmPage.publicMatchingFileCount)).toBe(1);
  expect(Number(warmPage.publicMatchingBytes)).toBe(128);
  expect(Number(warmPage.items.find(item => item.kind === 'directory')?.publicSubtreeFileCount)).toBe(100_000);
  await expect(page.locator('.shares-filter-count')).toHaveText('1 matching file');
  await expect(page.locator('.share-tree-row.file .share-tree-name')).toHaveText(['Track 000001.flac']);
  const warmSelectiveMs = Math.round(performance.now() - warmStart);
  expect(requests.filter(item => item.path.includes('/search?'))).toHaveLength(5);
  // Startup may briefly use the disconnected fallback. Once streaming starts,
  // no lifecycle polling is needed; data-page requests must always be unique.
  const dataPages = requests.filter(item => /\/(search|directories|files)\?/.test(item.path));
  expect(new Set(dataPages.map(item => `${item.method}:${item.path}`)).size).toBe(dataPages.length);
  expect(profileReads).toBe(0);
  console.log('PERF_SHARES ' + JSON.stringify({ browseMs, selectiveMs, selectiveResponseMs, selectiveRenderedMs,
    selectivePostResponseMs: selectiveMs - selectiveResponseMs,
    broadMs, broadResponseMs, broadRenderedMs, broadPostResponseMs: broadMs - broadResponseMs, moreMs, shortMs,
    warmSelectiveMs, warmSelectiveApiMs,
    firstPageBytes, continuationBytes, lifecycleReadsAfterStreaming, profileReads,
    initialFileRows: 1000, broadFilteredFileRows: 999, warmFilteredFileRows: 1, requests,
    backendReads: daemon.logs().filter(line => line.startsWith('E2E_METRIC read path=/api/user-browses/')) }));

  // Exercise a complete file-plan page, not just the smaller song/album plans
  // elsewhere. Keep actual 501-file execution in this opt-in Load journey.
  await page.goto('/shares/paged-peer');
  await expect(page.locator('.share-tree-row.file')).toHaveCount(501);
  await page.getByRole('checkbox', { name: 'Select Library', exact: true }).check();
  await daemon.command('hold-download');
  let rootApiMs = 0;
  const manifestApiMs: number[] = [];
  let directoryJobId = '';
  const childReadParents: string[] = [];
  page.on('request', request => {
    const url = new URL(request.url());
    const parent = url.searchParams.get('parentJobId');
    if (request.method() === 'GET' && url.pathname === '/api/jobs' && parent && !url.searchParams.has('kind'))
      childReadParents.push(parent);
  });
  try {
    const submitted = page.waitForResponse(response => response.ok() && response.request().method() === 'POST'
      && /\/api\/user-browses\/[^/]+\/downloads$/.test(new URL(response.url()).pathname));
    await page.getByRole('button', { name: 'Download 501', exact: true }).click();
    const accepted = await (await submitted).json() as Schema<'StartUserShareDownloadsResponseDto'>;
    expect(Number(accepted.resolution.totalPublicFiles)).toBe(501);
    await daemon.command('wait-download-started');
    const firstManifest = page.waitForResponse(response => response.ok()
      && new URL(response.url()).pathname.endsWith('/directory-files'));
    await page.goto(`/jobs/${accepted.workflow.jobId}`);
    await expect(page.locator('[data-testid="job-row"]')).toHaveCount(0);
    const first = await firstManifest;
    const firstPage = await first.json() as Schema<'DirectoryManifestPageDto'>;
    expect(new URL(first.url()).searchParams.get('limit')).toBe('500');
    expect(firstPage.items).toHaveLength(500);
    manifestApiMs.push(Math.round(first.request().timing().responseEnd));
    await expect(page.locator('.folder-file-row')).toHaveCount(500);
    directoryJobId = new URL(page.url()).pathname.split('/').at(-1)!;
    expect(directoryJobId).toBe(accepted.workflow.jobId);
    const rootResponse = await measureApi(daemon.url, `/api/jobs/${directoryJobId}`);
    expect(rootResponse.ok, rootResponse.body).toBe(true);
    expect((JSON.parse(rootResponse.body) as Schema<'JobDetailDto'>).childCount).toBe(501);
    rootApiMs = rootResponse.elapsedMs;
    expect(rootApiMs).toBeLessThan(100);
    const nextManifest = page.waitForResponse(response => response.ok()
      && new URL(response.url()).pathname.endsWith('/directory-files')
      && new URL(response.url()).searchParams.get('afterOrdinal') !== '-1');
    await page.getByRole('button', { name: 'Load more', exact: true }).click();
    const next = await nextManifest;
    const lastPage = await next.json() as Schema<'DirectoryManifestPageDto'>;
    expect(new URL(next.url()).searchParams.get('limit')).toBe('500');
    expect(lastPage.items).toHaveLength(1);
    manifestApiMs.push(Math.round(next.request().timing().responseEnd));
    for (const elapsedMs of manifestApiMs) expect(elapsedMs).toBeLessThan(100);
    expect([...firstPage.items, ...lastPage.items].map(item => item.target!.filename).sort())
      .toEqual(Array.from({ length: 501 }, (_, index) => `Library\\Item ${String(index + 1).padStart(3, '0')}.txt`));
    await expect(page.locator('.folder-file-row')).toHaveCount(501);
    await expect(page.getByRole('button', { name: 'Load more', exact: true })).toHaveCount(0);
  } finally { await daemon.command('release-download'); }
  await expect(page.locator('.job-detail-heading-summary > .search-status-badge')).toHaveCount(0, { timeout: 15_000 });
  await expect(page.locator('.folder-file-row').getByLabel('Complete', { exact: true })).toHaveCount(501);
  expect(childReadParents.filter(id => id === directoryJobId)).toHaveLength(0);
  const retainedFiles: Schema<'TransferStateDto'>[] = [];
  let cursor: string | null = null;
  do {
    const response = await page.request.get('/api/transfers?direction=download&username=paged-peer&limit=500'
      + (cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''));
    expect(response.ok(), await response.text()).toBe(true);
    const result = await response.json() as Schema<'TransferTimelinePageDto'>;
    retainedFiles.push(...result.items);
    cursor = result.nextCursor ?? null;
  } while (cursor);
  expect(retainedFiles).toHaveLength(501);
  expect(new Set(retainedFiles.map(item => item.status.localPath)).size).toBe(501);
  expect(retainedFiles.every(item => item.identity.username === 'paged-peer' && item.status.terminalOutcome === 'Succeeded')).toBe(true);
  for (const item of retainedFiles) expect(await readFile(item.status.localPath!)).toEqual(Buffer.alloc(128));
  console.log('PERF_DIRECTORY_PAGE ' + JSON.stringify({ directoryJobId, rootApiMs, manifestApiMs,
    firstPageFiles: 500, continuationFiles: 1, actualCompletedOutputs: retainedFiles.length }));
});
