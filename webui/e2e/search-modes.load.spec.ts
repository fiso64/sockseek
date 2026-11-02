import { performance } from 'node:perf_hooks';
import { rm, stat } from 'node:fs/promises';
import { sep } from 'node:path';
import type { Page, Request } from '@playwright/test';
import { test, expect, measureApi } from './fixtures';
import type { Schema } from '../src/api/types';

test.use({ daemonLogMetrics: true, daemonAssertFastReads: true, trace: 'off' });

test.afterEach(async ({}, testInfo) => {
  if (testInfo.status !== 'passed') return;
  // Assertions already verified these real outputs. Keep failure evidence and
  // screenshots/logs, without retaining ~1.3 GiB after every passing scale run.
  for (const directory of ['selected-download', 'flexible-folder', 'folder-download', 'full-folder-download', 'automatic-albums'])
    await rm(testInfo.outputPath(directory), { recursive: true, force: true });
});

type ExpectedFile = { username: string; filename: string; size: number };
const expectedFile = (file: Schema<'SearchViewFileDto'>): ExpectedFile => ({
  username: file.peer.username, filename: file.remoteFilename, size: Number(file.file.size),
});

async function waitForCompleteView(page: Page): Promise<Schema<'SearchViewRevisionDto'>> {
  // Observe the page the UI actually displays. Summary updates can arrive by
  // SignalR; a test-only summary poll would itself create processing demand.
  type ResponseBody = Schema<'SearchViewSummaryDto'> | Schema<'SearchViewUpdateDto'>
    | { revision: Schema<'SearchViewRevisionDto'> };
  const revision = (body: ResponseBody): Schema<'SearchViewRevisionDto'> =>
    'summary' in body ? body.summary : typeof body.revision === 'object' ? body.revision : body as Schema<'SearchViewSummaryDto'>;
  const response = await page.waitForResponse(async response => {
    const path = new URL(response.url()).pathname;
    if (!response.ok() || !(/^\/api\/search-views\/[^/]+(?:\/updates|\/files|\/directories|\/aggregate-tracks|\/aggregate-albums)?$/.test(path)
      || /\/api\/jobs\/[^/]+\/search-views$/.test(path))) return false;
    const summary = revision(await response.json() as ResponseBody);
    return summary.isComplete && summary.retentionState === 'Complete';
  }, { timeout: 90_000 });
  return revision(await response.json() as ResponseBody);
}

const modes = [
  { label: 'File Search', kind: 'GenericDirectories', page: 'directories', selector: '.folder-item-card', expectedRoots: 1_000 },
  { label: 'Track Search', kind: 'Files', page: 'files', selector: '.file-item-card', expectedRoots: 10_000 },
  { label: 'Album Search', kind: 'AlbumDirectories', page: 'directories', selector: '.folder-item-card', expectedRoots: 1_000 },
  { label: 'Song Aggregate', kind: 'AggregateTracks', page: 'aggregate-tracks', selector: '.aggregate-result-group', expectedRoots: 100 },
  { label: 'Album Aggregate', kind: 'AggregateAlbums', page: 'aggregate-albums', selector: '.aggregate-result-group', expectedRoots: 10 },
] as const;

// Locator assertions remain the watchdogs. Observe DOM changes from the actual
// Enter event so their retry intervals and form setup aren't display latency.
async function observeSearchDisplay(page: Page, selector: string, expectedRows: number) {
  return page.evaluateHandle(({ selector, expectedRows }) => {
    let submittedAt: number | null = null;
    const timing = { firstResultDomMs: null as number | null,
      completeDomMs: null as number | null, fullPageDomMs: null as number | null };
    function submitted(event: KeyboardEvent) {
      if (event.key === 'Enter' && submittedAt === null) submittedAt = performance.now();
    }
    document.addEventListener('keydown', submitted, true);
    const observer = new MutationObserver(() => {
      if (submittedAt === null) return;
      const rows = document.querySelectorAll(selector).length;
      const elapsed = Math.round(performance.now() - submittedAt);
      if (rows > 0 && timing.firstResultDomMs === null) timing.firstResultDomMs = elapsed;
      if (document.querySelector('.search-results-summary')?.textContent?.includes('Complete')) {
        timing.completeDomMs ??= elapsed;
        if (rows === expectedRows) timing.fullPageDomMs ??= elapsed;
      }
    });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    return { finish: () => {
      observer.disconnect();
      document.removeEventListener('keydown', submitted, true);
      const started = submittedAt ?? performance.now();
      const resources = (performance.getEntriesByType('resource') as PerformanceResourceTiming[])
        .filter(entry => entry.startTime >= started && new URL(entry.name).pathname.startsWith('/api/'))
        .map(entry => ({ path: new URL(entry.name).pathname + new URL(entry.name).search,
          startMs: Math.round(entry.startTime - started), endMs: Math.round(entry.responseEnd - started),
          waitMs: Math.round(entry.responseStart - entry.requestStart),
          downloadMs: Math.round(entry.responseEnd - entry.responseStart),
          encodedBytes: entry.encodedBodySize, decodedBytes: entry.decodedBodySize }));
      return { ...timing, resources };
    } };
  }, { selector, expectedRows });
}

for (const mode of modes) {
  test(`projects, pages and downloads one of 10k results in ${mode.label} @Load`, async ({ page, daemon }, testInfo) => {
    test.setTimeout(120_000);
    await daemon.command('large-music-search');
    await page.goto('/jobs');
    await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
    await page.getByRole('menuitemradio', { name: mode.label, exact: true }).click();

    const pageRevisions: string[] = [];
    const updateReads: number[] = [];
    const optionRequests: string[] = [];
    page.on('requestfinished', request => {
      const url = new URL(request.url());
      if (url.pathname.endsWith('/updates')) updateReads.push(request.timing().responseEnd);
      if (url.pathname.endsWith(`/${mode.page}`)) pageRevisions.push(url.searchParams.get('revision') ?? 'missing');
      if (url.pathname.endsWith('/alternatives')) optionRequests.push(url.pathname + url.search);
    });
    const createdResponse = page.waitForResponse(response => response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname));
    // Observe the browser's own response, not a second test-only polling loop.
    // Include create for the valid case where projection already finished.
    const completedResponse = waitForCompleteView(page);
    const trackRequest = mode.label === 'Song Aggregate' ? page.waitForRequest(request => request.method() === 'POST'
      && new URL(request.url()).pathname === '/api/jobs/search/tracks') : null;
    await page.locator('#global-search-simple').fill('Fixture');
    let submitInput = page.locator('#global-search-simple');
    if (mode.label === 'Song Aggregate') {
      // Aggregate track projection applies strict title/artist semantics.
      // Search by the known artist so the synthetic tracks remain comparable.
      await page.getByRole('button', { name: 'split', exact: true }).click();
      await page.locator('#global-search-artist').fill('Fixture Artist');
      submitInput = page.locator('#global-search-artist');
    }
    const profiling = await page.context().newCDPSession(page);
    await profiling.send('Performance.enable');
    const beforeRenderer = await profiling.send('Performance.getMetrics');
    const displayObserver = await observeSearchDisplay(page, mode.selector,
      Math.min(mode.expectedRoots, mode.kind === 'Files' ? 500 : 100));
    const started = performance.now();
    await submitInput.press('Enter');
    const created = await createdResponse;
    expect(created.status(), await created.text()).toBe(202);
    const initial = await created.json() as Schema<'SearchViewSummaryDto'>;
    expect(initial.kind).toBe(mode.kind);
    if (trackRequest) {
      const submitted = (await trackRequest).postDataJSON() as Schema<'SubmitTrackSearchJobRequestDto'>;
      expect(submitted.songQuery.artist).toBe('Fixture Artist');
      expect(submitted.songQuery.title ?? '').toBe('');
    }

    const final = await completedResponse;
    const projectionObservedMs = Math.round(performance.now() - started);
    expect(final.viewId).toBe(initial.viewId);
    expect(final!.isComplete).toBe(true);
    expect(Number(final!.counters.publicFileCount)).toBe(10_000);
    expect(Number(final!.counters.observedPeerCount)).toBe(100);
    const topLevel = Number(final!.counters.topLevelItemCount);
    expect(topLevel).toBe(mode.expectedRoots);
    expect(Number(final!.counters.projectedFileCount)).toBe(10_000);
    expect(Number(final!.counters.projectedPublicFileCount)).toBe(10_000);
    expect(Number(final!.counters.projectedLockedFileCount)).toBe(0);

    await expect(page.locator('.search-results-summary')).toContainText('Complete');
    await expect(page.locator(mode.selector)).toHaveCount(Math.min(topLevel, mode.kind === 'Files' ? 500 : 100));
    const uiMs = Math.round(performance.now() - started);
    const displayTiming = await displayObserver.evaluate(observer => observer.finish());
    await displayObserver.dispose();
    expect(displayTiming.firstResultDomMs).not.toBeNull();
    expect(displayTiming.completeDomMs).not.toBeNull();
    expect(displayTiming.fullPageDomMs).not.toBeNull();
    const afterRenderer = await profiling.send('Performance.getMetrics');
    const renderer = Object.fromEntries(['ScriptDuration', 'LayoutDuration', 'RecalcStyleDuration', 'TaskDuration']
      .map(name => [name + 'Ms', Math.round(1_000 * (
        afterRenderer.metrics.find(metric => metric.name === name)!.value
        - beforeRenderer.metrics.find(metric => metric.name === name)!.value))]));
    await profiling.detach();
    console.log('PERF_MODE_DISPLAY ' + JSON.stringify({ mode: mode.label, projectionObservedMs, uiMs, displayTiming, renderer }));
    // Discovery/View completion can precede raw-history drain. Ordinary job
    // metadata must not wait for those unrelated rows. Exercise this before
    // screenshots or selection/download work give the writer time to catch up.
    const metadataReads: { path: string; elapsedMs: number }[] = [];
    for (const path of [`/api/jobs/${initial.sourceJobId}`, '/api/jobs?includeAll=false&archived=false&newestFirst=true&limit=100']) {
      const response = await measureApi(daemon.url, path);
      const elapsedMs = response.elapsedMs;
      metadataReads.push({ path, elapsedMs });
      console.log('PERF_MODE_JOB_READ ' + JSON.stringify({ mode: mode.label, path, elapsedMs }));
      expect(response.ok, `${response.status}: ${response.body}`).toBe(true);
      if (path.includes('?')) {
        const list = JSON.parse(response.body) as Schema<'JobSummaryDto'>[];
        expect(list.some(job => job.jobId === initial.sourceJobId)).toBe(true);
      } else {
        const detail = JSON.parse(response.body) as Schema<'JobDetailDto'>;
        expect(detail.summary.jobId).toBe(initial.sourceJobId);
      }
      expect(elapsedMs, 'Bounded job metadata must remain a fast read while raw search history drains.')
        .toBeLessThan(100);
    }
    // Exercise server query binding independently of the UI's explicit limit.
    // File rows default to 500, nested/group rows to 100.
    const defaultPage = await page.request.get(`/api/search-views/${final.viewId}/${mode.page}?revision=${final.revision}`);
    expect(defaultPage.ok(), await defaultPage.text()).toBe(true);
    const defaults = await defaultPage.json() as { items: unknown[]; nextCursor?: string | null };
    expect(defaults.items).toHaveLength(Math.min(topLevel, mode.kind === 'Files' ? 500 : 100));
    expect(Boolean(defaults.nextCursor)).toBe(topLevel > defaults.items.length);
    if (mode.kind === 'Files') {
      for (const limit of ['0', '-1', 'not-an-integer']) {
        const invalid = await page.request.get(`/api/search-views/${final.viewId}/files?revision=${final.revision}&limit=${limit}`);
        expect(invalid.status()).toBe(400);
      }
    }
    const screenshot = testInfo.outputPath(`${mode.page}.png`);
    await page.screenshot({ path: screenshot });
    await testInfo.attach(`${mode.label} first page`, { path: screenshot, contentType: 'image/png' });

    async function commitSelectedFiles(
      count: number, outputDirectory: string,
      { choice, folderMode }: {
        choice?: { group: string; option: string; albumFile: boolean }; folderMode?: 'observed' | 'full';
      } = {},
    ) {
      const download = page.getByRole('button', { name: `Download ${count}`, exact: true });
      await expect(download).toBeEnabled();
      await page.getByRole('button', { name: 'Download options', exact: true }).click();
      const options = page.getByRole('region', { name: 'Download options' });
      await options.getByRole('textbox', { name: 'Output directory' }).fill(outputDirectory);
      if (mode.kind === 'AlbumDirectories' && folderMode) {
        const fullFolder = options.getByRole('checkbox', { name: 'Load full album folder before downloading' });
        await expect(fullFolder).toBeVisible();
        await fullFolder.setChecked(folderMode === 'full');
      }
      await options.getByRole('button', { name: 'Close download options', exact: true }).click();
      const commitStart = performance.now();
      const committed = page.waitForResponse(result => result.request().method() === 'POST'
        && /\/api\/search-views\/[^/]+\/commit$/.test(new URL(result.url()).pathname));
      await download.click();
      const commit = await committed;
      expect(commit.status(), await commit.text()).toBe(202);
      const uiMs = Math.round(performance.now() - commitStart);
      const body = commit.request().postDataJSON() as Schema<'CommitSearchViewSelectionRequestDto'>;
      expect(body.options?.outputParentDir).toBe(outputDirectory);
      if (choice) {
        expect(body.chosenOptions?.[choice.group]).toBe(choice.option);
        if (choice.albumFile) {
          expect(body.albumFileSelections?.[choice.group]?.mode).toBe('Only');
          expect(body.albumFileSelections?.[choice.group]?.refs).toHaveLength(1);
        }
      }
      const receipt = await commit.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
      expect(Number(receipt.submittedCount)).toBe(1);
      expect(Number(receipt.rejectedCount)).toBe(0);
      return { receipt, body, uiMs, apiMs: Math.round(commit.request().timing().responseEnd) };
    }

    async function downloadOneFile(
      expected: { username: string; remotePath: string; size: number },
      choice?: { group: string; option: string; albumFile: boolean },
    ): Promise<number> {
      const outputDirectory = testInfo.outputPath('selected-download');
      await daemon.command('hold-download');
      let transfer: Schema<'TransferStateDto'> | undefined;
      let commitMs: number;
      try {
        const commit = await commitSelectedFiles(1, outputDirectory, { choice });
        commitMs = commit.uiMs;
        planApiMs = commit.apiMs;
        await expect.poll(async () => {
          const response = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
          expect(response.ok(), await response.text()).toBe(true);
          const timeline = await response.json() as Schema<'TransferTimelinePageDto'>;
          return timeline.items.map(item => `${item.identity.username}:${item.identity.remotePath}`);
        }, { timeout: 10_000, intervals: [200, 500] })
          .toEqual([`${expected.username}:${expected.remotePath}`]);
      } finally {
        await daemon.command('release-download');
      }
      await expect.poll(async () => {
        const response = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
        expect(response.ok(), await response.text()).toBe(true);
        const timeline = await response.json() as Schema<'TransferTimelinePageDto'>;
        transfer = timeline.items[0];
        return timeline.items.map(item => `${item.identity.username}:${item.status.terminalOutcome}:${item.identity.remotePath}`);
      }, { timeout: 10_000, intervals: [200, 500] })
        .toEqual([`${expected.username}:Succeeded:${expected.remotePath}`]);
      expect(transfer!.status.localPath!.startsWith(outputDirectory + sep)).toBe(true);
      expect((await stat(transfer!.status.localPath!)).size).toBe(expected.size);
      return commitMs;
    }

    async function verifyDirectoryPlan(
      directoryId: string, expectedFiles: ExpectedFile[], outputDirectory: string,
    ) {
      let detail: Schema<'JobDetailDto'> | undefined;
      let completedDetailApiMs = 0;
      let slowestDetailApiMs = 0;
      const reads: { wallMs: number; issuedAt: number; serverStartedAt: number; readId: number;
        beforeServerMs: number; serverMs: number; setupMs: number; clientLoopActiveMs: number; clientLoopIdleMs: number;
        clientCpuMs: number; runtime: string }[] = [];
      await expect.poll(async () => {
        const loopBefore = performance.eventLoopUtilization();
        const cpuBefore = process.cpuUsage();
        const response = await measureApi(daemon.url, `/api/jobs/${directoryId}`);
        const issuedAt = response.issuedAt;
        completedDetailApiMs = response.elapsedMs;
        const serverStartedAt = Number(response.headers.get('x-e2e-read-started-at'));
        const loop = performance.eventLoopUtilization(loopBefore);
        const cpu = process.cpuUsage(cpuBefore);
        reads.push({ wallMs: completedDetailApiMs, issuedAt, serverStartedAt,
          readId: Number(response.headers.get('x-e2e-read-id')), beforeServerMs: serverStartedAt - issuedAt,
          serverMs: Number(response.headers.get('server-timing')?.match(/dur=([\d.]+)/)?.[1]),
          setupMs: Number(response.headers.get('x-e2e-read-setup-ms')),
          clientLoopActiveMs: loop.active, clientLoopIdleMs: loop.idle, clientCpuMs: (cpu.user + cpu.system) / 1_000,
          runtime: response.headers.get('x-e2e-runtime') ?? '' });
        slowestDetailApiMs = Math.max(slowestDetailApiMs, completedDetailApiMs);
        expect(response.ok, response.body).toBe(true);
        detail = JSON.parse(response.body) as Schema<'JobDetailDto'>;
        return detail.summary.terminalOutcome;
      }, { timeout: 10_000, intervals: [100, 250] }).toBe('Succeeded');
      console.log('PERF_PLAN_JOB_READ ' + JSON.stringify({ mode: mode.label, directoryId,
        completedDetailApiMs, slowestDetailApiMs, reads }));
      expect(slowestDetailApiMs, 'Live and terminal plan detail must remain responsive during downloads and persistence.')
        .toBeLessThan(100);
      if (detail!.payload?.kind !== 'remote-directory' && detail!.payload?.kind !== 'album')
        throw new Error('Expected the accepted directory job.');
      const directory = detail!.payload.directory;
      const totalKnownBytes = expectedFiles.reduce((sum, file) => sum + file.size, 0);
      expect(directory).toMatchObject({ fileCount: expectedFiles.length, terminalFileCount: expectedFiles.length,
        successfulFileCount: expectedFiles.length, failedFileCount: 0, progressPercent: 100 });
      expect(Number(directory.totalKnownBytes)).toBe(totalKnownBytes);
      const manifestResponse = await measureApi(daemon.url, `/api/jobs/${directoryId}/directory-files?limit=100`);
      const manifestApiMs = manifestResponse.elapsedMs;
      console.log('PERF_PLAN_MANIFEST_READ ' + JSON.stringify({ mode: mode.label, directoryId, manifestApiMs,
        readId: manifestResponse.headers.get('x-e2e-read-id'),
        serverMs: Number(manifestResponse.headers.get('server-timing')?.match(/dur=([\d.]+)/)?.[1]),
        setupMs: Number(manifestResponse.headers.get('x-e2e-read-setup-ms')),
        runtime: manifestResponse.headers.get('x-e2e-runtime') ?? '' }));
      expect(manifestResponse.ok, manifestResponse.body).toBe(true);
      expect(manifestApiMs, 'A small directory manifest is a bounded metadata read.').toBeLessThan(100);
      const manifest = JSON.parse(manifestResponse.body) as Schema<'DirectoryManifestPageDto'>;
      expect(manifest.entryCount).toBe(expectedFiles.length);
      expect(manifest.nextOrdinal ?? null).toBeNull();
      expect(manifest.items.map(item => `${item.target!.username}:${item.target!.filename}`).sort())
        .toEqual(expectedFiles.map(file => `${file.username}:${file.filename}`).sort());
      for (const item of manifest.items) {
        expect(item.child?.terminalOutcome).toBe('Succeeded');
        expect(item.file?.downloadPath?.startsWith(outputDirectory + sep)).toBe(true);
        expect((await stat(item.file!.downloadPath!)).size).toBe(Number(item.target!.size));
      }
      return { manifest, detail: detail!, directory, manifestApiMs, completedDetailApiMs };
    }

    let optionsUiMs: number | undefined;
    let optionsApiMs: number | undefined;
    let optionsRows: number | undefined;
    let optionsBytes: number | undefined;
    let optionsHasNext: boolean | undefined;
    let optionsMoreUiMs: number | undefined;
    let optionsMoreApiMs: number | undefined;
    let optionsMoreRows: number | undefined;
    let planCommitMs: number | undefined;
    let planApiMs: number | undefined;
    let folderPlan: { commitApiMs: number; commitUiMs: number; manifestApiMs: number;
      completedDetailApiMs: number; fileCount: number; requests: { method: string; path: string; body: string | null }[] } | undefined;
    let fullFolderPlan: { browseUiMs: number; heldDetailApiMs: number; commitApiMs: number;
      manifestApiMs: number; fileCount: number; networkBytes: number;
      requests: { method: string; path: string; body: string | null }[] } | undefined;
    let fullFolderPeer: string | undefined;
    let flexiblePlan: { commitApiMs: number; manifestApiMs: number; completedDetailApiMs: number;
      initialAlternatives: number; remainingAlternatives: number; fileCount: number;
      rerunApiMs: number; replayedAlternatives: number } | undefined;
    if (mode.kind === 'AggregateTracks' || mode.kind === 'AggregateAlbums') {
      const optionsStart = performance.now();
      const optionsResponse = page.waitForResponse(response => response.ok()
        && response.request().method() === 'GET'
        && new URL(response.url()).pathname.endsWith('/alternatives'));
      await page.locator('.aggregate-options-button').first().click();
      const response = await optionsResponse;
      const optionPage = await response.json() as {
        groupRef: string; items: { ref: string | { ref: string; username: string; folderPath: string } }[];
        nextCursor?: string | null
      };
      optionsRows = optionPage.items.length;
      expect(optionsRows).toBeGreaterThan(0);
      expect(optionsRows).toBe(100);
      await expect(page.getByRole('dialog').locator('.aggregate-option')).toHaveCount(optionsRows);
      optionsUiMs = Math.round(performance.now() - optionsStart);
      optionsApiMs = Math.round(response.request().timing().responseEnd);
      optionsBytes = (await response.body()).byteLength;
      optionsHasNext = optionPage.nextCursor != null;
      expect(optionRequests).toHaveLength(1);
      if (optionsHasNext) {
        const moreStart = performance.now();
        const moreResponse = page.waitForResponse(next => next.ok()
          && next.request().method() === 'GET'
          && new URL(next.url()).pathname.endsWith('/alternatives'));
        await page.getByRole('button', { name: 'Load more options', exact: true }).click();
        const next = await moreResponse;
        const nextPage = await next.json() as typeof optionPage;
        optionsMoreRows = nextPage.items.length;
        expect(optionsMoreRows).toBeGreaterThan(0);
        const ref = (item: (typeof optionPage.items)[number]) =>
          typeof item.ref === 'string' ? item.ref : item.ref.ref;
        expect(new Set([...optionPage.items, ...nextPage.items].map(ref)).size)
          .toBe(optionsRows + optionsMoreRows);
        await expect(page.getByRole('dialog').locator('.aggregate-option'))
          .toHaveCount(optionsRows + optionsMoreRows);
        optionsMoreUiMs = Math.round(performance.now() - moreStart);
        optionsMoreApiMs = Math.round(next.request().timing().responseEnd);
        expect(optionRequests).toHaveLength(2);

      }
      if (mode.kind === 'AggregateAlbums') {
        const chosen = optionPage.items[25]!.ref;
        expect(typeof chosen).toBe('object');
        if (typeof chosen === 'string') throw new Error('Expected an album directory identity.');
        await page.getByRole('dialog').locator('.aggregate-option')
          .nth(25).getByRole('button', { name: 'Use this option', exact: true }).click();
        const album = page.locator('.aggregate-result-group').first();
        await expect(album.locator('.aggregate-result-source')).toContainText(chosen.username);
        await album.getByRole('button', { name: /Deselect Fixture Album-00/ }).click();
        await album.locator('.folder-file-row input[type="checkbox"]').first().check();
        await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();

        planCommitMs = await downloadOneFile({ username: chosen.username,
          remotePath: chosen.folderPath + '\\00 - Fixture Track-00.flac', size: 8_000_000 },
        { group: optionPage.groupRef, option: chosen.ref, albumFile: true });

        // The UI's default/explicit choices pin a folder. Exercise the public
        // flexible-plan contract separately: all 100 alternatives, one actual
        // source rejection, and the next ranked folder's exact ten files.
        const first = optionPage.items[0]!.ref;
        const second = optionPage.items[1]!.ref;
        if (typeof first === 'string' || typeof second === 'string')
          throw new Error('Expected album directory identities.');
        const expectedResponse = await page.request.get(`/api/search-views/${initial.viewId}/directories/${second.ref}/files?revision=${final!.revision}&limit=100`);
        expect(expectedResponse.ok(), await expectedResponse.text()).toBe(true);
        const expected = (await expectedResponse.json() as Schema<'SearchViewDirectoryFilePageDto'>).items.map(item => item.file);
        expect(expected).toHaveLength(10);
        const outputDirectory = testInfo.outputPath('flexible-folder');
        await daemon.command('hold-download');
        await daemon.command('reject-next-download');
        let albumId: string;
        let submissionId: string;
        let flexibleCommitApiMs: number;
        try {

          const response = await measureApi(daemon.url, `/api/search-views/${initial.viewId}/commit`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
            revision: final!.revision, selection: { mode: 'Only', refs: [optionPage.groupRef] },
            useRepresentativeWhenUnchosen: false, idempotencyKey: crypto.randomUUID(),
            options: { outputParentDir: outputDirectory, downloadSettings: {
              search: { noBrowseFolder: true }, skip: { skipExisting: false },
            } },
          } satisfies Schema<'CommitSearchViewSelectionRequestDto'>) });
          flexibleCommitApiMs = response.elapsedMs;
          expect(response.status, response.body).toBe(202);
          const receipt = JSON.parse(response.body) as Schema<'CommitSearchViewSelectionResponseDto'>;
          submissionId = receipt.submissionId!;
          expect(Number(receipt.submittedCount)).toBe(1);
          expect(Number(receipt.rejectedCount)).toBe(0);
          await daemon.command('wait-download-started');
          const jobsResponse = await page.request.get(`/api/jobs?submissionId=${receipt.submissionId}&includeAll=true&archived=false&limit=100`);
          expect(jobsResponse.ok(), await jobsResponse.text()).toBe(true);
          const albums = (await jobsResponse.json() as Schema<'JobSummaryDto'>[]).filter(job => job.kind === 'album');
          expect(albums).toHaveLength(1);
          albumId = albums[0]!.jobId!;
        } finally {
          await daemon.command('release-download');
        }
        const completed = await verifyDirectoryPlan(albumId, expected.map(expectedFile), outputDirectory);
        if (completed.detail.payload?.kind !== 'album') throw new Error('Expected the flexible album job.');
        // Execution removes a rejected folder from its mutable candidate list;
        // the public runtime count is not the immutable accepted specification.
        expect(completed.detail.payload.resultCount).toBe(99);
        expect(completed.detail.payload.resolvedFolderUsername).toBe(second.username);
        expect(Number(completed.directory.bytesTransferred)).toBe(84_500_000);
        const failures = await page.request.get('/api/transfers?direction=download&archived=false&terminalOutcome=Failed&limit=100');
        expect(failures.ok(), await failures.text()).toBe(true);
        const failed = (await failures.json() as Schema<'TransferTimelinePageDto'>).items;
        expect(failed.some(item => item.identity.username === first.username && item.identity.remotePath?.startsWith(first.folderPath + '\\'))).toBe(true);

        // Retaining the mutable post-failure list would silently discard the
        // first source. Rerun must restore all 100 accepted alternatives and
        // try that original source again, with the same output settings.
        const firstFilesResponse = await page.request.get(`/api/search-views/${initial.viewId}/directories/${first.ref}/files?revision=${final!.revision}&limit=100`);
        expect(firstFilesResponse.ok(), await firstFilesResponse.text()).toBe(true);
        const firstFiles = (await firstFilesResponse.json() as Schema<'SearchViewDirectoryFilePageDto'>).items.map(item => item.file);
        expect(firstFiles).toHaveLength(10);
        let rerunApiMs: number;
        let replayAlbumId: string;
        await daemon.command('hold-download');
        try {
          const response = await measureApi(daemon.url, `/api/submissions/${submissionId}/rerun`, { method: 'POST' });
          rerunApiMs = response.elapsedMs;
          expect(response.status, response.body).toBe(202);
          const root = JSON.parse(response.body) as Schema<'JobSummaryDto'>;
          expect(root.submissionId).not.toBe(submissionId);
          await daemon.command('wait-download-started');
          const jobsResponse = await page.request.get(`/api/jobs?submissionId=${root.submissionId}&includeAll=true&archived=false&limit=100`);
          expect(jobsResponse.ok(), await jobsResponse.text()).toBe(true);
          const albums = (await jobsResponse.json() as Schema<'JobSummaryDto'>[]).filter(job => job.kind === 'album');
          expect(albums).toHaveLength(1);
          replayAlbumId = albums[0]!.jobId!;
        } finally {
          await daemon.command('release-download');
        }
        const replayed = await verifyDirectoryPlan(replayAlbumId, firstFiles.map(expectedFile), outputDirectory);
        if (replayed.detail.payload?.kind !== 'album') throw new Error('Expected the replayed album job.');
        expect(replayed.detail.payload.resultCount).toBe(100);
        expect(replayed.detail.payload.resolvedFolderUsername).toBe(first.username);
        flexiblePlan = { commitApiMs: flexibleCommitApiMs, manifestApiMs: completed.manifestApiMs,
          completedDetailApiMs: completed.completedDetailApiMs, initialAlternatives: 100,
          remainingAlternatives: Number(completed.detail.payload.resultCount),
          fileCount: completed.manifest.items.length, rerunApiMs,
          replayedAlternatives: Number(replayed.detail.payload.resultCount) };
      }
      if (mode.kind === 'AggregateTracks') {
        const chosen = optionPage.items[5] as unknown as Schema<'SearchViewFileDto'>;
        await page.getByRole('dialog').locator('.aggregate-option').nth(5)
          .getByRole('button', { name: 'Use this option', exact: true }).click();
        planCommitMs = await downloadOneFile({ username: chosen.peer.username,
          remotePath: chosen.remoteFilename, size: Number(chosen.file.size) },
        { group: optionPage.groupRef, option: chosen.ref, albumFile: false });
      }
    } else if (mode.kind === 'Files') {
      const response = await page.request.get(`/api/search-views/${initial.viewId}/files?revision=${final!.revision}&limit=1`);
      expect(response.ok(), await response.text()).toBe(true);
      const firstFile = (await response.json() as Schema<'SearchViewFilePageDto'>).items[0]!;
      await page.locator('.file-item-card').first().getByRole('checkbox').check();
      planCommitMs = await downloadOneFile({ username: firstFile.peer.username,
        remotePath: firstFile.remoteFilename, size: Number(firstFile.file.size) });
    } else {
      const response = await page.request.get(`/api/search-views/${initial.viewId}/directories?revision=${final!.revision}&limit=1`);
      expect(response.ok(), await response.text()).toBe(true);
      const firstDirectory = (await response.json() as Schema<'SearchViewDirectoryPageDto'>).items[0]!;
      const childrenResponse = await page.request.get(`/api/search-views/${initial.viewId}/directories/${firstDirectory.ref.ref}/files?revision=${final!.revision}&limit=1`);
      expect(childrenResponse.ok(), await childrenResponse.text()).toBe(true);
      const firstFile = (await childrenResponse.json() as Schema<'SearchViewDirectoryFilePageDto'>).items[0]!.file;
      await page.locator('.folder-item-card').first().locator('.folder-file-row input[type="checkbox"]').first().check();
      planCommitMs = await downloadOneFile({ username: firstFile.peer.username,
        remotePath: firstFile.remoteFilename, size: Number(firstFile.file.size) });

      const filesResponse = await page.request.get(`/api/search-views/${initial.viewId}/directories/${firstDirectory.ref.ref}/files?revision=${final!.revision}&limit=100`);
      expect(filesResponse.ok(), await filesResponse.text()).toBe(true);
      const expectedFiles = (await filesResponse.json() as Schema<'SearchViewDirectoryFilePageDto'>).items.map(item => item.file);
      expect(expectedFiles).toHaveLength(10);
      const requests: { method: string; path: string; body: string | null }[] = [];
      const record = (request: Request) => {
        const url = new URL(request.url());
        if (url.pathname.startsWith(`/api/search-views/${initial.viewId}/`)
          || url.pathname.startsWith('/api/jobs/'))
          requests.push({ method: request.method(), path: url.pathname + url.search, body: request.postData() });
      };
      page.on('request', record);
      const counted = page.waitForResponse(response => response.request().method() === 'POST'
        && new URL(response.url()).pathname.endsWith('/selection-counts'));
      await page.locator('.folder-item-card').first().getByRole('checkbox', { name: 'Select all files in Fixture Album-00', exact: true }).check();
      const countResponse = await counted;
      expect(countResponse.ok(), await countResponse.text()).toBe(true);
      const counts = await countResponse.json() as Schema<'CountSearchViewSelectionResponseDto'>;
      expect(Number(counts.publicFileCount)).toBe(10);
      expect(Number(counts.lockedFileCount)).toBe(0);
      const outputDirectory = testInfo.outputPath('folder-download');
      await daemon.command('hold-download');
      let directoryId: string;
      let commit: Awaited<ReturnType<typeof commitSelectedFiles>>;
      try {
        commit = await commitSelectedFiles(10, outputDirectory, { folderMode: 'observed' });
        await daemon.command('wait-download-started');
        const response = await page.request.get(`/api/jobs?submissionId=${commit.receipt.submissionId}&includeAll=true&archived=false&limit=100`);
        expect(response.ok(), await response.text()).toBe(true);
        const jobs = await response.json() as Schema<'JobSummaryDto'>[];
        const directories = jobs.filter(job => job.kind === 'remote-directory');
        expect(directories).toHaveLength(1);
        directoryId = directories[0]!.jobId!;
        await page.goto(`/jobs/${directoryId}`);
        const folder = page.locator('.folder-item-card');
        await expect(folder.locator('.folder-file-row')).toHaveCount(10);
        await expect(folder).toContainText('00 - Fixture Track-00.flac');
        await expect(folder).toContainText('09 - Fixture Track-09.flac');
        const screenshot = testInfo.outputPath('held-folder-plan.png');
        await page.screenshot({ path: screenshot });
        await testInfo.attach(`${mode.label} accepted folder`, { path: screenshot, contentType: 'image/png' });
      } finally {
        await daemon.command('release-download');
      }
      const completed = await verifyDirectoryPlan(directoryId, expectedFiles.map(expectedFile), outputDirectory);
      expect(Number(completed.directory.bytesTransferred)).toBe(76_500_000);
      page.off('request', record);
      expect(requests.filter(request => request.path.endsWith('/commit'))).toHaveLength(1);
      expect(requests.filter(request => request.path.endsWith('/selection-counts'))).toHaveLength(1);
      expect(requests.filter(request => request.method === 'GET' && /\/(directories|files)(\/|\?|$)/.test(request.path)))
        .toHaveLength(0);
      const settingRequests = requests.filter(request => request.path.endsWith('/selection-settings'));
      expect(new Set(settingRequests.map(request => request.body)).size, 'No identical settings lookups in one folder commit.')
        .toBe(settingRequests.length);
      folderPlan = { commitApiMs: commit.apiMs, commitUiMs: commit.uiMs, manifestApiMs: completed.manifestApiMs,
        completedDetailApiMs: completed.completedDetailApiMs, fileCount: completed.manifest.items.length, requests };

      // Exercise the distinct full-peer path too. A new search View must include
      // a retrieved bonus file only for this exact folder, not its case variant
      // or the other albums and unrelated files in the 100k-file artifact.
      const reopened = waitForCompleteView(page);
      await page.goto(`/jobs/${initial.sourceJobId}`);
      const current = await reopened;
      const rootResponse = await page.request.get(`/api/search-views/${current.viewId}/directories?revision=${current.revision}&limit=1`);
      expect(rootResponse.ok(), await rootResponse.text()).toBe(true);
      const root = (await rootResponse.json() as Schema<'SearchViewDirectoryPageDto'>).items[0]!;
      fullFolderPeer = root.ref.username;
      const fullExpected: ExpectedFile[] = [
        ...Array.from({ length: 10 }, (_, track) => ({ username: root.ref.username,
          filename: `${root.ref.folderPath}\\${String(track).padStart(2, '0')} - Fixture Track-${String(track).padStart(2, '0')}.flac`,
          size: 8_000_000 + track * 100_000 })),
        { username: root.ref.username, filename: `${root.ref.folderPath}\\10 - Fixture Bonus.flac`, size: 128 },
      ];
      expect(root.ref.folderPath).toBe('Music\\Fixture Artist\\Fixture Album-00');
      await expect(page.locator('.folder-item-card')).toHaveCount(100);
      const fullRequests: typeof requests = [];
      const recordFull = (request: Request) => {
        const url = new URL(request.url());
        if (url.pathname.startsWith('/api/')) fullRequests.push({
          method: request.method(), path: url.pathname + url.search, body: request.postData(),
        });
      };
      page.on('request', recordFull);
      const countedFull = page.waitForResponse(response => response.request().method() === 'POST'
        && new URL(response.url()).pathname.endsWith('/selection-counts'));
      await page.locator('.folder-item-card').first().getByRole('checkbox', { name: 'Select all files in Fixture Album-00', exact: true }).check();
      expect(Number(((await (await countedFull).json()) as Schema<'CountSearchViewSelectionResponseDto'>).publicFileCount)).toBe(10);
      await daemon.command('hold-directory-browse');
      await daemon.command('hold-download');
      let fullDirectoryId: string;
      let heldDetailApiMs = 0;
      let browseUiMs = 0;
      let fullCommit: Awaited<ReturnType<typeof commitSelectedFiles>>;
      const fullOutput = testInfo.outputPath('full-folder-download');
      const readHeldMetadata = async (id: string) => {
        const response = await measureApi(daemon.url, `/api/jobs/${id}`);
        heldDetailApiMs = response.elapsedMs;
        expect(response.ok, response.body).toBe(true);
        expect(heldDetailApiMs, 'Metadata must remain responsive while peer directory retrieval is held.').toBeLessThan(100);
        const detail = JSON.parse(response.body) as Schema<'JobDetailDto'>;
        expect(['Pending', 'Running']).toContain(detail.summary.lifecycleState);
      };
      try {
        if (mode.kind === 'GenericDirectories') {
          const retrieving = page.waitForResponse(response => response.request().method() === 'POST'
            && new URL(response.url()).pathname.endsWith('/directories/retrieve'));
          await page.locator('.folder-item-card').first().getByRole('button', { name: 'Load all files in this folder', exact: true }).click();
          const response = await retrieving;
          expect(response.ok(), await response.text()).toBe(true);
          const retrieval = await response.json() as Schema<'JobSummaryDto'>;
          expect(retrieval.jobId).toBeTruthy();
          await readHeldMetadata(retrieval.jobId!);
          const browseStarted = performance.now();
          // The refreshed root page carries this small folder's initial files;
          // do not require a redundant child-page fetch from the browser.
          const filesLoaded = page.waitForResponse(async response => response.request().method() === 'GET'
            && new URL(response.url()).pathname === `/api/search-views/${current.viewId}/directories`
            && response.ok() && (await response.json() as Schema<'SearchViewDirectoryPageDto'>).items
              .some(item => item.ref.ref === root.ref.ref && item.retrievalState === 'Complete'));
          await daemon.command('release-directory-browse');
          const loaded = (await (await filesLoaded).json() as Schema<'SearchViewDirectoryPageDto'>).items
            .find(item => item.ref.ref === root.ref.ref)!;
          await expect(page.locator('.folder-item-card').first().locator('.folder-file-row')).toHaveCount(11, { timeout: 30_000 });
          await expect(page.locator('.folder-item-card').first().getByLabel('Full folder loaded', { exact: true })).toBeVisible();
          browseUiMs = Math.round(performance.now() - browseStarted);
          await expect(page.getByRole('button', { name: 'Download 10', exact: true })).toBeEnabled();
          await page.getByRole('button', { name: 'Clear selection', exact: true }).click();
          await page.locator('.folder-item-card').first().getByRole('checkbox', { name: 'Select all files in Fixture Album-00', exact: true }).check();
          await expect(page.getByRole('button', { name: 'Download 11', exact: true })).toBeEnabled();
          expect(Number(loaded.publicMatchingFileCount)).toBe(10);
          expect(Number(loaded.retrievedFileCount)).toBe(11);
          expect(loaded.initialFiles?.items).toHaveLength(11);
          const summaryResponse = await page.request.get(`/api/search-views/${current.viewId}`);
          expect(summaryResponse.ok(), await summaryResponse.text()).toBe(true);
          const retrieved = await summaryResponse.json() as Schema<'SearchViewSummaryDto'>;
          expect(Number(retrieved.revision)).toBeGreaterThan(Number(current.revision));
          expect(Number(retrieved.counters.publicFileCount)).toBe(10_000);
          expect(Number(retrieved.counters.projectedFileCount)).toBe(10_000);
        }
        const browseStarted = performance.now();
        fullCommit = await commitSelectedFiles(mode.kind === 'GenericDirectories' ? 11 : 10, fullOutput, { folderMode: 'full' });
        expect(fullCommit.body.options?.downloadSettings?.search?.noBrowseFolder).not.toBe(true);
        const response = await page.request.get(`/api/jobs?submissionId=${fullCommit.receipt.submissionId}&includeAll=true&archived=false&limit=100`);
        expect(response.ok(), await response.text()).toBe(true);
        const directories = (await response.json() as Schema<'JobSummaryDto'>[]).filter(job => job.kind === 'remote-directory');
        expect(directories).toHaveLength(1);
        fullDirectoryId = directories[0]!.jobId!;
        if (mode.kind === 'AlbumDirectories') {
          await readHeldMetadata(fullDirectoryId);
          await page.goto(`/jobs/${fullDirectoryId}`);
          await expect(page.locator('.automatic-job-detail h1')).toBeVisible();
          await expect(page.locator('.job-detail-heading-summary')).not.toContainText('0 files');
          await expect(page.locator('.folder-transfer-size')).toHaveText('—');
          await expect(page.locator('.folder-item-card')).toContainText('Waiting for the accepted file plan.');
          const resolvingScreenshot = testInfo.outputPath('resolving-full-folder-plan.png');
          await page.screenshot({ path: resolvingScreenshot });
          await testInfo.attach('Album Search directory still resolving', { path: resolvingScreenshot, contentType: 'image/png' });
          await daemon.command('release-directory-browse');
        }
        await daemon.command('wait-download-started');
        if (mode.kind === 'AlbumDirectories') browseUiMs = Math.round(performance.now() - browseStarted);
        if (mode.kind === 'GenericDirectories') await page.goto(`/jobs/${fullDirectoryId}`);
        await expect(page.locator('.folder-item-card .folder-file-row')).toHaveCount(11);
        await expect(page.locator('.folder-item-card')).toContainText('10 - Fixture Bonus.flac');
        await expect(page.locator('.folder-item-card .transfer-progress-track'))
          .toHaveAttribute('aria-label', '100% complete');
        if (mode.kind === 'GenericDirectories') {
          await page.setViewportSize({ width: 390, height: 900 });
          await page.emulateMedia({ colorScheme: 'dark' });
        }
        const screenshot = testInfo.outputPath('held-full-folder-plan.png');
        await page.screenshot({ path: screenshot, animations: 'disabled' });
        await testInfo.attach(`${mode.label} full peer folder`, { path: screenshot, contentType: 'image/png' });
      } finally {
        await daemon.command('release-directory-browse');
        await daemon.command('release-download');
        page.off('request', recordFull);
      }
      const fullCompleted = await verifyDirectoryPlan(fullDirectoryId, fullExpected, fullOutput);
      const networkBytes = root.ref.username === firstDirectory.ref.username ? 128 : 84_500_128;
      expect(Number(fullCompleted.directory.bytesTransferred)).toBe(networkBytes);
      expect(daemon.logs().filter(line => line === 'E2E_METRIC large-music-browse files=100000')).toHaveLength(1);
      expect(fullRequests.filter(request => request.method === 'POST' && request.path.endsWith('/directories/retrieve')))
        .toHaveLength(mode.kind === 'GenericDirectories' ? 1 : 0);
      expect(fullRequests.filter(request => request.path.endsWith('/selection-counts')))
        .toHaveLength(mode.kind === 'GenericDirectories' ? 2 : 1);
      expect(fullRequests.filter(request => request.method === 'GET'
        && /\/directories\/[^/]+\/files(?:\?|$)/.test(request.path))).toHaveLength(0);
      expect(fullRequests.filter(request => request.method === 'POST' && request.path.endsWith('/commit'))).toHaveLength(1);
      expect(fullRequests.filter(request => request.path.includes('/profile'))).toHaveLength(0);
      const settings = fullRequests.filter(request => request.path.endsWith('/selection-settings'));
      expect(new Set(settings.map(request => request.body)).size).toBe(settings.length);
      fullFolderPlan = { browseUiMs, heldDetailApiMs, commitApiMs: fullCommit.apiMs,
        manifestApiMs: fullCompleted.manifestApiMs, fileCount: fullCompleted.manifest.items.length, networkBytes, requests: fullRequests };
    }

    // Search View commits above use pinned/flexible plans. Direct automatic
    // jobs perform their own discovery, grouping, folder completion and child
    // execution; a successful RemoteDirectory plan does not qualify that path.
    let automaticPlan: { submitApiMs: number; heldRootApiMs: number; rootApiMs: number; jobsApiMs: number;
      summaryReads: { query: string; durationMs: number }[]; albums: number;
      files: number; browseCount: number; manifestApiMs: number[] } | undefined;
    if (mode.kind === 'AlbumDirectories' || mode.kind === 'AggregateAlbums') {
      const aggregate = mode.kind === 'AggregateAlbums';
      const outputDirectory = testInfo.outputPath('automatic-albums');
      const summaryReads: { query: string; durationMs: number }[] = [];
      const observeSummaryRead = (reply: import('@playwright/test').Response) => {
        const url = new URL(reply.url());
        if (!reply.ok() || reply.request().method() !== 'GET' || url.pathname !== '/api/jobs') return;
        const timing = reply.headers()['server-timing']?.match(/read;dur=([\d.]+)/);
        if (timing) summaryReads.push({ query: url.search, durationMs: Number(timing[1]) });
      };
      page.on('response', observeSummaryRead);
      async function readBrowseCount() {
        // Read the fixture's cumulative counter instead of counting its bounded
        // log tail, which may have evicted acquisition events during indexing.
        await daemon.command('report-peer-browses');
        const line = daemon.logs().filter(line => line.startsWith('E2E_METRIC peer-browses count=')).at(-1);
        expect(line).toBeTruthy();
        return Number(line!.split('count=')[1]);
      }
      const browsesBefore = await readBrowseCount();
      await daemon.command('hold-download');
      let root: Schema<'JobSummaryDto'>;
      let submitApiMs: number;
      let heldRootApiMs: number;
      try {
        const response = await measureApi(daemon.url, aggregate ? '/api/jobs/aggregate/albums' : '/api/jobs/downloads/album', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            albumQuery: { artist: 'Fixture Artist', album: aggregate ? null : 'Fixture Album-00', artistMaybeWrong: false },
            options: { outputParentDir: outputDirectory, downloadSettings: {
              search: { noBrowseFolder: false, necessaryCond: { strictAlbum: true,
                // Exclude the peer whose full share was acquired above. This
                // forces the automatic Album path through fresh acquisition,
                // and verifies that its condition override reaches discovery.
                bannedUsers: { replace: fullFolderPeer ? [fullFolderPeer] : [] },
              } },
            } },
          } satisfies Schema<'SubmitAlbumJobRequestDto'>),
        });
        submitApiMs = response.elapsedMs;
        expect(response.status, response.body).toBe(202);
        root = JSON.parse(response.body) as Schema<'JobSummaryDto'>;
        console.log('PERF_AUTOMATIC_ADMISSION ' + JSON.stringify({ mode: mode.label, jobId: root.jobId,
          phases: daemon.logs().filter(line => line.startsWith('E2E_METRIC submission-phases '))
            .map(line => JSON.parse(line.slice('E2E_METRIC submission-phases '.length)))
            .filter(activity => activity.jobId === root.jobId),
          writers: daemon.logs().filter(line => line.startsWith('E2E_METRIC writer-phases '))
            .map(line => JSON.parse(line.slice('E2E_METRIC writer-phases '.length))).slice(-20),
        }));
        expect(root.kind).toBe(aggregate ? 'album-aggregate' : 'album');
        await page.goto(`/jobs/${root.jobId}`);
        await expect(page.locator('.automatic-job-detail h1')).toBeVisible();
        const heldResponse = await measureApi(daemon.url, `/api/jobs/${root.jobId}`);
        heldRootApiMs = heldResponse.elapsedMs;
        expect(heldResponse.ok, heldResponse.body).toBe(true);
        expect(heldRootApiMs, 'Automatic root metadata must not wait for peer indexing or held transfers.').toBeLessThan(100);
        if (aggregate) {
          await expect(page.getByRole('heading', { name: 'Generated albums', exact: true })).toBeVisible();
          await expect(page.locator('.job-child-row')).toHaveCount(10);
          await expect(page.locator('.job-child-copy small')).toHaveText(Array(10).fill(/^Album ·/));
          await expect(page.getByRole('button', { name: 'Load more jobs', exact: true })).toHaveCount(0);
        } else {
          await expect(page.locator('.folder-item-card .folder-file-row')).toHaveCount(11, { timeout: 30_000 });
          await expect(page.locator('.folder-item-card')).toContainText('10 - Fixture Bonus.flac');
        }
      } finally {
        await daemon.command('release-download');
      }
      // Observe the browser's actual live detail reconciliation, without a
      // second test-only status polling loop or a fixed transfer wait.
      // The aggregate writes ~1 GB across 110 files and builds large peer
      // indexes. Its I/O completion budget is separate from the 100-ms reads.
      await expect(page.locator('.job-detail-heading-summary > .search-status-badge')).toHaveText('Complete', {
        timeout: mode.kind === 'AggregateAlbums' ? 60_000 : 30_000,
      });
      const rootResponse = await measureApi(daemon.url, `/api/jobs/${root.jobId}`);
      const rootApiMs = rootResponse.elapsedMs;
      expect(rootResponse.ok, rootResponse.body).toBe(true);
      expect(rootApiMs, 'Automatic root metadata must stay a bounded read after child execution.').toBeLessThan(100);
      const completedRoot = JSON.parse(rootResponse.body) as Schema<'JobDetailDto'>;
      expect(completedRoot.summary.terminalOutcome).toBe('Succeeded');
      const jobsResponse = await measureApi(daemon.url, `/api/jobs?submissionId=${root.submissionId}&includeAll=true&archived=false&limit=200`);
      const jobsApiMs = jobsResponse.elapsedMs;
      expect(jobsResponse.ok, jobsResponse.body).toBe(true);
      const jobs = JSON.parse(jobsResponse.body) as Schema<'JobSummaryDto'>[];
      expect(jobsApiMs, 'The small automatic workflow must have a fast job-summary read.').toBeLessThan(100);
      const albums = jobs.filter(job => job.kind === 'album');
      expect(albums).toHaveLength(aggregate ? 10 : 1);
      if (aggregate) expect(completedRoot.payload).toMatchObject({ kind: 'album-aggregate', resultCount: 10,
        completedAlbumCount: 10, succeededAlbumCount: 10, failedAlbumCount: 0 });
      const folders: string[] = [];
      const peers = new Set<string>();
      const physicalPaths: string[] = [];
      const manifestApiMs: number[] = [];
      for (const album of albums) {
        expect(album.submissionId).toBe(root.submissionId);
        expect(album.workflowId).toBe(root.workflowId);
        if (aggregate) expect(album.parentJobId).toBe(root.jobId);
        const detailResponse = await page.request.get(`/api/jobs/${album.jobId}`);
        expect(detailResponse.ok(), await detailResponse.text()).toBe(true);
        const detail = await detailResponse.json() as Schema<'JobDetailDto'>;
        if (detail.payload?.kind !== 'album') throw new Error('Expected an automatic Album child.');
        const username = detail.payload.resolvedFolderUsername!;
        const folder = detail.payload.resolvedFolderPath!;
        expect(username).toMatch(/^fixture-peer-\d{3}$/);
        if (fullFolderPeer) expect(username).not.toBe(fullFolderPeer);
        peers.add(username);
        expect(folder).toMatch(/^Music\\Fixture Artist\\Fixture Album-\d{2}$/);
        folders.push(folder);
        const expected: ExpectedFile[] = [
          ...Array.from({ length: 10 }, (_, track) => ({ username,
            filename: `${folder}\\${String(track).padStart(2, '0')} - Fixture Track-${String(track).padStart(2, '0')}.flac`,
            size: 8_000_000 + track * 100_000 })),
          { username, filename: `${folder}\\10 - Fixture Bonus.flac`, size: 128 },
        ];
        const verified = await verifyDirectoryPlan(album.jobId!, expected, outputDirectory);
        manifestApiMs.push(verified.manifestApiMs);
        physicalPaths.push(...verified.manifest.items.map(item => item.file!.downloadPath!));
      }
      expect(folders.sort()).toEqual(Array.from({ length: aggregate ? 10 : 1 }, (_, album) =>
        `Music\\Fixture Artist\\Fixture Album-${String(album).padStart(2, '0')}`));
      expect(new Set(physicalPaths).size, 'Every album retains its own exact output files.').toBe((aggregate ? 10 : 1) * 11);
      const screenshot = testInfo.outputPath('automatic-albums-complete.png');
      await page.screenshot({ path: screenshot, animations: 'disabled' });
      await testInfo.attach(`${mode.label} direct automatic execution`, { path: screenshot, contentType: 'image/png' });
      const browseCount = await readBrowseCount() - browsesBefore;
      expect(browseCount, 'A fresh completed share is reused for every album from that same peer.').toBeLessThanOrEqual(peers.size);
      if (!aggregate) expect(browseCount, 'The previous full-folder peer is excluded, so automatic Album acquires a fresh share.').toBe(1);
      page.off('response', observeSummaryRead);
      expect(summaryReads.length, 'Observe actual child-summary reads during automatic execution.').toBeGreaterThan(0);
      for (const read of summaryReads)
        expect(read.durationMs, `Child summaries must stay fast during peer indexing: ${read.query}`).toBeLessThan(100);
      automaticPlan = { submitApiMs, heldRootApiMs, rootApiMs, jobsApiMs, summaryReads, albums: albums.length, files: physicalPaths.length, browseCount,
        manifestApiMs };
      console.log('PERF_AUTOMATIC_ALBUMS ' + JSON.stringify({ mode: mode.label, ...automaticPlan }));
    }

    const persisted = await page.request.get('/api/server/status');
    expect(persisted.ok()).toBe(true);
    const health = (await persisted.json() as { persistence: {
      state: string; busyRetryCount: number; permanentlyFailedMutationCount: number;
      commitLatencyHistogram: number[];
    } }).persistence;
    expect(health.state).not.toBe('Unhealthy');
    expect(health.permanentlyFailedMutationCount).toBe(0);
    console.log('PERF_MODE ' + JSON.stringify({ mode: mode.label, projectionObservedMs, uiMs, displayTiming, renderer,
      topLevel, projected: final!.counters.projectedFileCount, pageRevisions, metadataReads,
      optionsUiMs, optionsApiMs, optionsRows, optionsBytes, optionsHasNext,
      optionsMoreUiMs, optionsMoreApiMs, optionsMoreRows,
      planCommitMs, planApiMs, folderPlan, fullFolderPlan, flexiblePlan, automaticPlan,
      updateReads: updateReads.length, health,
      daemonMetrics: daemon.logs().filter(line => line.startsWith('E2E_METRIC ')
        || line.includes('Search View ') && (line.includes('completed with outcome')
          || line.includes(' committed in '))
        || line.includes('Submission commit ') && line.includes(' completed in ')) }));
  });

  test(`keeps ${mode.label} responsive through paced arrivals and refinements @Load`, async ({ page, daemon }, testInfo) => {
    test.setTimeout(45_000);
    await daemon.command('paced-music-search');
    await page.goto('/jobs');
    await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
    await page.getByRole('menuitemradio', { name: mode.label, exact: true }).click();
    await page.locator('#global-search-simple').fill('Fixture');
    let input = page.locator('#global-search-simple');
    if (mode.kind === 'AggregateTracks') {
      await page.getByRole('button', { name: 'split', exact: true }).click();
      input = page.locator('#global-search-artist');
      await input.fill('Fixture Artist');
    }
    type RootPage = { revision: Schema<'SearchViewRevisionDto'>; items: unknown[] };
    const reads: { path: string; milliseconds: number }[] = [];
    const pages: { viewId: string; page: RootPage }[] = [];
    const captured: Promise<void>[] = [];
    const captureErrors: unknown[] = [];
    const definitions = new Map<string, boolean>();
    const requests: { method: string; path: string }[] = [];
    page.on('request', request => requests.push({ method: request.method(),
      path: new URL(request.url()).pathname + new URL(request.url()).search }));
    // Refinement can cancel an obsolete body after its headers arrive. Only
    // completed requests supply page facts; errors reading those still fail.
    const observeFinished = (request: Request) => {
      const path = new URL(request.url()).pathname;
      if (path.startsWith('/api/'))
        reads.push({ path, milliseconds: Math.round(request.timing().responseEnd) });
      if (!path.endsWith(`/${mode.page}`)) return;
      captured.push(request.response().then(async response => {
        if (!response?.ok()) return;
        const body = await response.json() as RootPage;
        pages.push({ viewId: body.revision.viewId, page: body });
      }).catch(error => { captureErrors.push(error); }));
    };
    page.on('requestfinished', observeFinished);
    const waitForRootPage = (accept: (result: RootPage) => boolean) =>
      page.waitForEvent('requestfinished', { predicate: async request => {
        if (!new URL(request.url()).pathname.endsWith(`/${mode.page}`)) return false;
        const response = await request.response();
        return !!response?.ok() && accept(await response.json() as RootPage);
      } });
    const responsiveness = await page.evaluateHandle(() => {
      const inputs: number[] = [];
      const longTasks: number[] = [];
      const input = (event: Event) => {
        if (!(event.target instanceof HTMLInputElement)
          || event.target.getAttribute('aria-label') !== 'Filter search results') return;
        const began = event.timeStamp;
        requestAnimationFrame(() => inputs.push(Math.round(performance.now() - began)));
      };
      document.addEventListener('input', input, true);
      const tasks = new PerformanceObserver(list => {
        for (const entry of list.getEntries()) longTasks.push(Math.round(entry.duration));
      });
      tasks.observe({ entryTypes: ['longtask'] });
      return { finish: () => {
        document.removeEventListener('input', input, true);
        tasks.disconnect();
        return { inputToFrameMs: inputs, longTaskMs: longTasks };
      } };
    });
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Performance.enable');
    const profile = process.env.SOCKSEEK_PROFILE_SEARCH === '1';
    if (profile) {
      await cdp.send('Profiler.enable');
      await cdp.send('Profiler.start');
    }
    const before = await cdp.send('Performance.getMetrics');
    const created = page.waitForResponse(response => response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname));
    await input.press('Enter');
    const initialResponse = await created;
    expect(initialResponse.status()).toBe(202);
    const initial = await initialResponse.json() as Schema<'SearchViewSummaryDto'>;
    definitions.set(initial.viewId, false);
    await expect(page.locator(mode.selector).first()).toBeVisible();
    await expect(page.locator('.search-results-summary')).toContainText('Searching');

    const filter = page.getByRole('textbox', { name: 'Filter search results', exact: true });
    const filteredResponse = page.waitForResponse(response => response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
      && response.request().postDataJSON()?.filterText === 'Album-00');
    // Keystroke cadence is part of the marked Load workload, not a wait for state.
    await filter.pressSequentially('Album-00', { delay: 20 });
    const filtered = await (await filteredResponse).json() as Schema<'SearchViewSummaryDto'>;
    definitions.set(filtered.viewId, true);
    // Old cards remain usable during replacement loading. A 202 response
    // alone does not mean that the new projection has reached the display.
    await expect(page.locator('.result-refinement-status')).toHaveCount(0);
    await expect(page.locator(mode.selector).first()).toBeVisible();
    await expect(page.locator('.search-results-summary')).toContainText('Searching');
    const selected = page.locator(mode.selector).first();
    if (mode.kind === 'AggregateTracks' || mode.kind === 'AggregateAlbums')
      await selected.locator('.aggregate-header-select-button').click();
    else if (mode.kind === 'Files') await selected.getByRole('checkbox').check();
    else await selected.getByRole('checkbox', { name: /Select all files/ }).check();
    await expect(page.getByRole('button', { name: /^Download \d+$/, exact: true })).toBeVisible();

    const densePage = waitForRootPage(result =>
      result.revision.viewId !== initial.viewId && result.revision.viewId !== filtered.viewId
        && !result.revision.isComplete && Number(result.revision.counters.observedPeerCount) >= 30
    );
    const restoredResponse = page.waitForResponse(response => response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
      && !response.request().postDataJSON()?.filterText);
    await filter.clear();
    const restored = await (await restoredResponse).json() as Schema<'SearchViewSummaryDto'>;
    definitions.set(restored.viewId, false);
    // Exercise input again once a substantial page is updating, rather than
    // qualifying responsiveness only against the first small publication.
    await densePage;
    const broadResponse = page.waitForResponse(response => response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
      && response.request().postDataJSON()?.filterText === 'Fixture');
    await filter.pressSequentially('Fixture', { delay: 20 });
    const broad = await (await broadResponse).json() as Schema<'SearchViewSummaryDto'>;
    definitions.set(broad.viewId, false);
    // A 202 only acknowledges creation. Wait until this filter is displayed
    // before clearing it; clearing an in-flight filter can reuse the prior View.
    await expect(page.locator('.result-refinement-status')).toHaveCount(0);
    const canCollapse = mode.kind === 'GenericDirectories' || mode.kind === 'AlbumDirectories';
    const laterPage = canCollapse ? waitForRootPage(result =>
      ![initial.viewId, filtered.viewId, restored.viewId, broad.viewId].includes(result.revision.viewId)
        && !result.revision.isComplete && Number(result.revision.counters.observedPeerCount) >= 30
        && Number(result.revision.counters.observedPeerCount) < 100
    ) : null;
    const finalViewResponse = page.waitForResponse(response => response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)
      && !response.request().postDataJSON()?.filterText);
    await filter.clear();
    const finalView = await (await finalViewResponse).json() as Schema<'SearchViewSummaryDto'>;
    definitions.set(finalView.viewId, false);
    if (laterPage) {
      await laterPage;
      // This peer is still visible in the final first page. New peers reorder
      // surrounding groups; a user's collapse must survive that live update.
      await page.getByRole('button', { name: /^Collapse fixture-peer-000 / }).first().click();
    }
    const completed = waitForCompleteView(page);
    // The timed first half stays live through refinement/collapse. Release the
    // tail explicitly rather than racing a fixed 30–99-peer arrival window.
    await daemon.command('next-search-batch');
    await daemon.command('finish-search');
    const final = await completed;
    expect(final.viewId).toBe(finalView.viewId);
    expect(Number(final.counters.publicFileCount)).toBe(10_000);
    expect(Number(final.counters.projectedFileCount)).toBe(10_000);
    expect(Number(final.counters.topLevelItemCount)).toBe(mode.expectedRoots);
    await expect(page.locator('.search-results-summary')).toContainText('Complete');
    if (laterPage) {
      const collapsed = page.getByRole('button', { name: /^Expand fixture-peer-000 / }).first();
      await expect(collapsed, 'Live insertion must preserve the collapsed peer group.').toBeVisible();
      await collapsed.click();
    }
    await expect(page.locator(mode.selector)).toHaveCount(Math.min(mode.expectedRoots, mode.kind === 'Files' ? 500 : 100));
    await expect(page.getByRole('button', { name: /^Download \d+$/, exact: true })).toBeEnabled();
    page.off('requestfinished', observeFinished);
    await Promise.all(captured);
    expect(captureErrors, 'Every completed result page must have a readable JSON body.').toEqual([]);
    expect(pages.some(entry => !entry.page.revision.isComplete)).toBe(true);
    for (const { viewId, page: result } of pages) {
      expect(definitions.has(viewId), 'Every visible page belongs to a UI-requested View.').toBe(true);
      const peerCount = Number(result.revision.counters.observedPeerCount);
      const filtered = definitions.get(viewId)!;
      const filesPerPeer = filtered ? 10 : 100;
      expect(Number(result.revision.counters.publicFileCount)).toBe(peerCount * 100);
      expect(Number(result.revision.counters.projectedFileCount)).toBe(peerCount * filesPerPeer);
      const roots = mode.kind === 'Files' ? peerCount * filesPerPeer
        : mode.kind === 'AggregateTracks' ? filesPerPeer
        : mode.kind === 'AggregateAlbums' ? (filtered ? 1 : 10)
        : peerCount * (filtered ? 1 : 10);
      expect(Number(result.revision.counters.topLevelItemCount)).toBe(roots);
      expect(result.items).toHaveLength(Math.min(roots, mode.kind === 'Files' ? 500 : 100));
    }
    const rootRequests = requests.filter(request => request.method === 'GET'
      && new URL(request.path, daemon.url).pathname.endsWith(`/${mode.page}`));
    expect(new Set(rootRequests.map(request => request.path)).size,
      'A visible page is fetched once per View revision.').toBe(rootRequests.length);
    expect(requests.filter(request => request.method === 'POST'
      && /^\/api\/jobs\/search(?:\/tracks|\/albums)?$/.test(new URL(request.path, daemon.url).pathname))).toHaveLength(1);
    expect(definitions.size).toBe(5);
    const delivered = daemon.logs().find(line => line.startsWith('E2E_METRIC paced-search '));
    expect(delivered).toMatch(/searches=1 peers=100 receive-ms=\d+ cpu-ms=\d+/);
    const after = await cdp.send('Performance.getMetrics');
    const renderer = Object.fromEntries(['ScriptDuration', 'LayoutDuration', 'RecalcStyleDuration', 'TaskDuration']
      .map(name => [name + 'Ms', Math.round(1_000 * (after.metrics.find(metric => metric.name === name)!.value
        - before.metrics.find(metric => metric.name === name)!.value))]));
    if (profile) {
      const sampled = await cdp.send('Profiler.stop');
      await testInfo.attach('search-cpu-profile', {
        body: JSON.stringify(sampled.profile), contentType: 'application/json',
      });
    }
    await cdp.detach();
    const interaction = await responsiveness.evaluate(observer => observer.finish());
    await responsiveness.dispose();
    expect(interaction.inputToFrameMs.length).toBeGreaterThanOrEqual(17);
    const bytes = await page.evaluate(() => (performance.getEntriesByType('resource') as PerformanceResourceTiming[])
      .filter(entry => new URL(entry.name).pathname.startsWith('/api/'))
      .reduce((total, entry) => total + entry.encodedBodySize, 0));
    await page.screenshot({ path: testInfo.outputPath(`paced-${mode.page}.png`) });
    console.log('PERF_PACED ' + JSON.stringify({ mode: mode.label, interaction, renderer, bytes,
      pages: pages.map(entry => ({ viewId: entry.viewId, revision: entry.page.revision.revision,
        complete: entry.page.revision.isComplete, peers: entry.page.revision.counters.observedPeerCount,
        files: entry.page.revision.counters.projectedFileCount })), requests, reads, delivered }));
  });
}

// Initial construction is distinct from re-sorting an already retained View.
// Opt-in: 100k files across many peers, one peer, 10k songs or 10k copies of one album.
// No downloads compete for storage; the 10k journeys above own that behavior.
for (const { mode, shape } of [
  ...modes.map(mode => ({ mode, shape: 'distributed' as const })),
  ...modes.filter(mode => ['AlbumDirectories', 'AggregateAlbums'].includes(mode.kind))
    .map(mode => ({ mode, shape: 'single-peer' as const })),
  { mode: modes.find(mode => mode.kind === 'AggregateAlbums')!, shape: 'album-options' as const },
  { mode: modes.find(mode => mode.kind === 'AggregateTracks')!, shape: 'track-groups' as const },
]) {
  const singlePeer = shape === 'single-peer';
  const manyOptions = shape === 'album-options';
  const manyGroups = shape === 'track-groups';
  test(`constructs and displays 100k music hits in ${mode.label}${singlePeer ? ' from one peer' : manyOptions ? ' with 10k alternatives' : manyGroups ? ' with 10k song groups' : ''} @Load`, async ({ page, daemon }, testInfo) => {
    test.setTimeout(120_000);
    await daemon.command(singlePeer ? 'large-single-peer-music-search' : manyOptions ? 'large-shared-album-search' : manyGroups ? 'large-varied-music-search' : 'large-music-search-100k');
    const peers = singlePeer ? 1 : manyOptions ? 10_000 : manyGroups ? 10 : 1_000;
    await page.goto('/jobs');
    await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
    await page.getByRole('menuitemradio', { name: mode.label, exact: true }).click();
    if (singlePeer && mode.kind === 'AggregateAlbums') {
      await page.getByRole('button', { name: 'Search configuration', exact: true }).click();
      const panel = page.getByRole('region', { name: 'Search configuration', exact: true });
      await panel.getByLabel('Minimum sharers').fill('1');
      await panel.getByRole('button', { name: 'Close search configuration', exact: true }).click();
    }
    const expectedRoots = manyOptions ? 1 : singlePeer || manyGroups ? 10_000 : mode.kind.startsWith('Aggregate') ? mode.expectedRoots : mode.expectedRoots * 10;
    const pageSize = mode.kind === 'Files' ? 500 : 100;
    const displayed = Math.min(expectedRoots, pageSize);
    type ResultPage = Schema<'SearchViewFilePageDto'> | Schema<'SearchViewDirectoryPageDto'>
      | Schema<'SearchViewAggregateTrackPageDto'> | Schema<'SearchViewAggregateAlbumPageDto'>;
    const rootPath = (url: URL) => new RegExp(`/api/search-views/[^/]+/${mode.page}$`).test(url.pathname);
    const dataRequests: string[] = [];
    page.on('request', request => {
      const url = new URL(request.url());
      if (rootPath(url)) dataRequests.push(url.pathname + url.search);
    });
    const createdResponse = page.waitForResponse(response => response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname));
    const finalResponse = page.waitForResponse(async response => response.ok()
      && rootPath(new URL(response.url())) && (await response.json() as ResultPage).revision.isComplete);
    await page.locator('#global-search-simple').fill('Fixture');
    let input = page.locator('#global-search-simple');
    if (mode.kind === 'AggregateTracks') {
      await page.getByRole('button', { name: 'split', exact: true }).click();
      await page.locator('#global-search-artist').fill('Fixture Artist');
      input = page.locator('#global-search-artist');
    }
    const displayObserver = await observeSearchDisplay(page, mode.selector, displayed);
    await input.press('Enter');
    const created = await createdResponse;
    expect(created.status(), await created.text()).toBe(202);
    const initial = await created.json() as Schema<'SearchViewSummaryDto'>;
    expect(initial.kind).toBe(mode.kind);
    const finished = await finalResponse;
    const first = await finished.json() as ResultPage;
    const final = first.revision;
    expect(final.viewId).toBe(initial.viewId);
    expect(final.retentionState).toBe('Complete');
    expect(final.counters).toMatchObject({ publicFileCount: 100_000, lockedFileCount: 0,
      observedPeerCount: peers, projectedFileCount: 100_000,
      projectedPublicFileCount: 100_000, projectedLockedFileCount: 0, topLevelItemCount: expectedRoots });
    expect(Number(final.counters.preferredFileCount) + Number(final.counters.otherFileCount)).toBe(100_000);
    expect(first.items).toHaveLength(displayed);
    await expect(page.locator('.search-results-summary')).toContainText('Complete');
    await expect(page.locator(mode.selector)).toHaveCount(displayed);
    const display = await displayObserver.evaluate(observer => observer.finish());
    await displayObserver.dispose();
    expect(display.firstResultDomMs).not.toBeNull();
    expect(display.fullPageDomMs).not.toBeNull();
    const ref = (item: ResultPage['items'][number]) => typeof item.ref === 'string' ? item.ref : item.ref.ref;
    expect(new Set(first.items.map(ref)).size).toBe(displayed);
    if (mode.kind === 'AggregateTracks' || mode.kind === 'AggregateAlbums') {
      const groups = first.items as Array<Schema<'SearchViewAggregateTrackGroupDto'> | Schema<'SearchViewAggregateAlbumGroupDto'>>;
      expect(groups.every(group => group.shareCount === peers)).toBe(true);
      expect(groups.every(group => Number(group.selectableOptionCount) === peers)).toBe(true);
      if (manyGroups) {
        const tracks = groups as Schema<'SearchViewAggregateTrackGroupDto'>[];
        expect(new Set(tracks.map(group => group.query.title)).size).toBe(displayed);
      }
    }
    // Continue through the UI where the root page has more rows. No second
    // client-side page is allowed to hide part of a fetched response.
    if (first.nextCursor) {
      const continued = page.waitForResponse(response => response.ok()
        && rootPath(new URL(response.url())) && new URL(response.url()).searchParams.has('cursor'));
      await page.getByRole('button', { name: 'Load more results', exact: true }).click();
      const next = await (await continued).json() as ResultPage;
      expect(next.revision.revision).toBe(final.revision);
      expect(next.items).toHaveLength(Math.min(pageSize, expectedRoots - displayed));
      expect(new Set([...first.items, ...next.items].map(ref)).size).toBe(displayed + next.items.length);
      await expect(page.locator(mode.selector)).toHaveCount(displayed + next.items.length);
    }
    expect(new Set(dataRequests).size, 'Root pages must not be fetched redundantly.').toBe(dataRequests.length);
    const screenshot = testInfo.outputPath(`initial-100k-${mode.page}.png`);
    await page.screenshot({ path: screenshot });
    await testInfo.attach(`${mode.label} initial 100k`, { path: screenshot, contentType: 'image/png' });
    // Separate bounded read cost from construction and DOM timing. These
    // explicit audit reads are additional to the browser-owned request list.
    const reads: { path: string; elapsedMs: number }[] = [];
    for (const path of [`/api/search-views/${final.viewId}/${mode.page}?revision=${final.revision}&limit=${pageSize}`,
      `/api/jobs/${initial.sourceJobId}`]) {
      const response = await measureApi(daemon.url, path);
      reads.push({ path, elapsedMs: response.elapsedMs });
      expect(response.ok, response.body).toBe(true);
    }
    if (manyOptions) {
      const alternatives = `/api/search-views/${final.viewId}/aggregate-albums/${ref(first.items[0]!)}/alternatives`;
      const alternativeRequests: string[] = [];
      const recordAlternative = (request: Request) => {
        const url = new URL(request.url());
        if (url.pathname === alternatives) alternativeRequests.push(url.pathname + url.search);
      };
      page.on('request', recordAlternative);
      const observeOptions = () => page.waitForResponse(response => response.ok()
        && new URL(response.url()).pathname === alternatives);
      const optionResponse = observeOptions();
      await page.locator('.aggregate-options-button').click();
      const options = await (await optionResponse).json() as Schema<'SearchViewAggregateAlbumOptionPageDto'>;
      expect(options.items).toHaveLength(100);
      expect(options.nextCursor).toBeTruthy();
      expect(options.items.every(item => item.initialFiles?.items.length === 10)).toBe(true);
      await expect(page.getByRole('dialog').locator('.aggregate-option')).toHaveCount(100);
      const moreResponse = observeOptions();
      await page.getByRole('button', { name: 'Load more options', exact: true }).click();
      const more = await (await moreResponse).json() as typeof options;
      expect(more.items).toHaveLength(100);
      expect(new Set([...options.items, ...more.items].map(item => item.ref.ref)).size).toBe(200);
      await expect(page.getByRole('dialog').locator('.aggregate-option')).toHaveCount(200);
      page.off('request', recordAlternative);
      expect(alternativeRequests).toHaveLength(2);
      expect(new Set(alternativeRequests).size).toBe(2);
      await page.screenshot({ path: testInfo.outputPath('album-alternatives-10k.png') });
      for (const cursor of [null, options.nextCursor]) {
        expect(cursor === null || typeof cursor === 'string').toBe(true);
        const path = `${alternatives}?revision=${final.revision}&limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`;
        const response = await measureApi(daemon.url, path);
        expect(response.ok, response.body).toBe(true);
        expect((JSON.parse(response.body) as typeof options).items).toHaveLength(100);
        reads.push({ path, elapsedMs: response.elapsedMs });
      }
    }
    if (manyGroups || (singlePeer && mode.kind === 'AggregateAlbums')) {
      // All-except is the normal large selection. It includes unfetched groups,
      // and excluding one displayed group must update both the count and action.
      const filesPerGroup = mode.kind === 'AggregateAlbums' ? 10 : 1;
      const countPath = `/api/search-views/${final.viewId}/selection-counts`;
      const countRequests: string[] = [];
      const recordCount = (request: Request) => {
        if (new URL(request.url()).pathname === countPath) countRequests.push(request.postData() ?? '');
      };
      const countResponse = () => page.waitForResponse(response => response.ok()
        && response.request().method() === 'POST' && new URL(response.url()).pathname === countPath);
      page.on('request', recordCount);
      const selectedAll = countResponse();
      await page.getByRole('button', { name: 'Select all', exact: true }).click();
      expect(await (await selectedAll).json()).toMatchObject({ publicFileCount: 10_000 * filesPerGroup,
        lockedFileCount: 0, selectedItemCount: 10_000 });
      await expect(page.getByRole('button', {
        name: mode.kind === 'AggregateAlbums' ? 'Download 100,000' : 'Download 10,000', exact: true,
      })).toBeEnabled();
      const excludedOne = countResponse();
      await page.locator('.aggregate-result-group').first().getByRole('button', { pressed: true }).click();
      expect(await (await excludedOne).json()).toMatchObject({ publicFileCount: 9_999 * filesPerGroup,
        lockedFileCount: 0, selectedItemCount: 9_999 });
      await expect(page.getByRole('button', {
        name: mode.kind === 'AggregateAlbums' ? 'Download 99,990' : 'Download 9,999', exact: true,
      })).toBeEnabled();
      page.off('request', recordCount);
      expect(countRequests).toHaveLength(2);
      expect(new Set(countRequests).size).toBe(2);
      await page.screenshot({ path: testInfo.outputPath('10k-groups-all-except.png') });
      for (const selection of [{ mode: 'Only', refs: [ref(first.items[0]!)] },
        { mode: 'AllExcept', refs: [ref(first.items[0]!)] }] as const) {
        const response = await measureApi(daemon.url, countPath, { method: 'POST',
          headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ revision: final.revision, selection }) });
        expect(response.ok, response.body).toBe(true);
        const count = selection.mode === 'Only' ? 1 : 9_999;
        expect(JSON.parse(response.body)).toMatchObject({ publicFileCount: count * filesPerGroup,
          lockedFileCount: 0, selectedItemCount: count });
        reads.push({ path: `${countPath} (${selection.mode})`, elapsedMs: response.elapsedMs });
      }
    }
    console.log('PERF_INITIAL_MUSIC_100K ' + JSON.stringify({ mode: mode.label,
      peers, albumsPerPeer: manyOptions ? 1 : singlePeer ? 10_000 : manyGroups ? 1_000 : 10, tracksPerAlbum: 10, ...display,
      firstPageApiMs: Math.round(finished.request().timing().responseEnd),
      rootCount: expectedRoots, displayed, counters: final.counters, dataRequests, reads }));
    for (const read of reads)
      expect(read.elapsedMs, `Bounded read after 100k construction: ${read.path}`).toBeLessThan(100);
    if (manyGroups) {
      // Reuse this large source to qualify the common refinement path. Record
      // its own browser timer; initial-display timings above exclude this phase.
      const filterRequests: string[] = [];
      const filterCreates: string[] = [];
      const repeatedSearches: string[] = [];
      const recordFilter = (request: Request) => {
        const url = new URL(request.url());
        if (request.method() === 'POST' && url.pathname.endsWith('/search-views'))
          filterCreates.push(request.postData() ?? '');
        if (request.method() === 'POST' && url.pathname === '/api/jobs/search/tracks')
          repeatedSearches.push(url.pathname);
        if (request.method() === 'GET' && url.pathname.endsWith('/aggregate-tracks'))
          filterRequests.push(url.pathname + url.search);
      };
      page.on('request', recordFilter);
      const filteredPage = page.waitForResponse(async response => {
        if (!response.ok() || !new URL(response.url()).pathname.endsWith('/aggregate-tracks')) return false;
        const body = await response.json() as Schema<'SearchViewAggregateTrackPageDto'>;
        return body.revision.viewId !== final.viewId && body.revision.isComplete;
      });
      const filteredDisplay = await page.evaluateHandle(() => {
        let began = 0;
        let firstResultDomMs: number | null = null;
        let firstDownloadReadyMs: number | null = null;
        let completeDomMs: number | null = null;
        let completedDownloadReadyMs: number | null = null;
        const input = (event: Event) => {
          if (event.target instanceof HTMLInputElement && event.target.getAttribute('aria-label') === 'Filter search results')
            began = performance.now();
        };
        document.addEventListener('input', input, true);
        const observer = new MutationObserver(() => {
          const groups = document.querySelectorAll('.aggregate-result-group');
          if (began && groups.length === 1 && groups[0]!.textContent?.includes('9999')) {
            const elapsed = performance.now() - began;
            const ready = [...document.querySelectorAll('button')]
              .some(button => button.textContent?.trim() === 'Download 1' && !button.disabled);
            firstResultDomMs ??= elapsed;
            if (ready) firstDownloadReadyMs ??= elapsed;
            if (document.querySelector('.search-results-summary')?.textContent?.includes('Complete')) {
              completeDomMs ??= elapsed;
              if (ready) completedDownloadReadyMs ??= elapsed;
            }
          }
        });
        observer.observe(document.body, { childList: true, subtree: true, characterData: true });
        return { finish: () => {
          observer.disconnect(); document.removeEventListener('input', input, true);
          return { firstResultDomMs, firstDownloadReadyMs, completeDomMs, completedDownloadReadyMs,
            actionEpochMs: performance.timeOrigin + began,
            resources: (performance.getEntriesByType('resource') as PerformanceResourceTiming[])
              .filter(entry => entry.startTime >= began && new URL(entry.name).pathname.startsWith('/api/'))
              .map(entry => ({ path: new URL(entry.name).pathname + new URL(entry.name).search,
                startMs: Math.round(entry.startTime - began), endMs: Math.round(entry.responseEnd - began),
                waitMs: Math.round(entry.responseStart - entry.requestStart),
                downloadMs: Math.round(entry.responseEnd - entry.responseStart) })) };
        } };
      });
      await page.getByRole('textbox', { name: 'Filter search results', exact: true }).fill('9999');
      const filtered = await (await filteredPage).json() as Schema<'SearchViewAggregateTrackPageDto'>;
      expect(filtered.items).toHaveLength(1);
      expect(filtered.revision.counters).toMatchObject({ publicFileCount: 100_000,
        projectedFileCount: 10, projectedPublicFileCount: 10, topLevelItemCount: 1, observedPeerCount: 10 });
      expect(filtered.items[0]!.query.title).toContain('9999');
      expect(filtered.items[0]!.shareCount).toBe(10);
      await expect(page.locator('.aggregate-result-group')).toHaveCount(1);
      await expect(page.locator('.result-refinement-status')).toHaveCount(0);
      await expect(page.locator('.search-results-summary')).toContainText('Complete');
      await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
      const filterTiming = await filteredDisplay.evaluate(observer => observer.finish());
      await filteredDisplay.dispose();
      expect(filterTiming.firstResultDomMs).not.toBeNull();
      expect(filterTiming.firstDownloadReadyMs).not.toBeNull();
      expect(filterTiming.completeDomMs).not.toBeNull();
      expect(filterTiming.completedDownloadReadyMs).not.toBeNull();
      page.off('request', recordFilter);
      expect(filterCreates).toHaveLength(1);
      expect(repeatedSearches).toHaveLength(0);
      expect(new Set(filterRequests).size).toBe(filterRequests.length);
      const alternatives = `/api/search-views/${filtered.revision.viewId}/aggregate-tracks/${filtered.items[0]!.ref}/alternatives?revision=${filtered.revision.revision}&limit=500`;
      const options = await measureApi(daemon.url, alternatives);
      expect(options.ok, options.body).toBe(true);
      expect((JSON.parse(options.body) as Schema<'SearchViewAggregateTrackOptionPageDto'>).items).toHaveLength(10);
      expect(options.elapsedMs).toBeLessThan(100);
      await page.screenshot({ path: testInfo.outputPath('10k-song-groups-filtered.png') });
      console.log('PERF_GROUP_REFINEMENT ' + JSON.stringify({ ...filterTiming,
        sourceFiles: 100_000, sourceGroups: 10_000, visibleGroups: 1, optionFiles: 10,
        filterRequests, alternativeReadMs: options.elapsedMs }));
    }
  });
}

test('projects 100k File Search hits from 20 peers with 25 folders of 200 files each @Load', async ({ page, daemon }, testInfo) => {
  test.setTimeout(120_000);
  await daemon.command('large-distributed-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'File Search', exact: true }).click();
  const reads: { path: string; milliseconds: number }[] = [];
  const pages: Schema<'SearchViewDirectoryPageDto'>[] = [];
  const captures: Promise<void>[] = [];
  const captureErrors: unknown[] = [];
  page.on('requestfinished', request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/')) reads.push({ path: url.pathname + url.search,
      milliseconds: Math.round(request.timing().responseEnd) });
    if (request.method() === 'GET' && /\/api\/search-views\/[^/]+\/directories$/.test(url.pathname))
      captures.push(request.response().then(async response => { if (response?.ok()) pages.push(await response.json()); })
        .catch(error => { captureErrors.push(error); }));
  });
  const display = await observeSearchDisplay(page, '.folder-item-card', 100);
  const initialPage = page.waitForResponse(async response => {
    if (!response.ok() || !/\/api\/search-views\/[^/]+\/directories$/.test(new URL(response.url()).pathname)) return false;
    const body = await response.json() as Schema<'SearchViewDirectoryPageDto'>;
    return Number(body.revision.counters.publicFileCount) === 1_000;
  });
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  expect((await (await initialPage).json() as Schema<'SearchViewDirectoryPageDto'>).revision.isComplete).toBe(false);
  await expect(page.locator('.folder-item-card')).toHaveCount(5);
  await expect(page.locator('.folder-file-row')).toHaveCount(5 * 24);
  await expect(page.locator('.search-results-summary')).toContainText('Searching');

  const finished = waitForCompleteView(page);
  const releasedAt = performance.now();
  await daemon.command('next-search-batch');
  const summary = await finished;
  await expect(page.locator('.search-results-summary')).toContainText('Complete');
  await expect(page.locator('.folder-item-card')).toHaveCount(100);
  await expect(page.locator('.folder-file-row')).toHaveCount(100 * 24);
  const finalBatchToDomMs = Math.round(performance.now() - releasedAt);
  await Promise.all(captures);
  expect(captureErrors).toEqual([]);
  expect(Number(summary.counters.publicFileCount)).toBe(100_000);
  expect(Number(summary.counters.projectedPublicFileCount)).toBe(100_000);
  expect(Number(summary.counters.topLevelItemCount)).toBe(500);
  expect(summary.counters.observedPeerCount).toBe(20);
  const finalPage = pages.filter(item => item.revision.isComplete).at(-1);
  expect(finalPage).toBeTruthy();
  expect(finalPage!.items).toHaveLength(100);
  expect(finalPage!.nextCursor).toBeTruthy();
  expect(reads.filter(read => /\/directories\/[^/]+\/files\?/.test(read.path)),
    'Folder previews come with the root page, without one request per card.').toHaveLength(0);
  const directoryReads = reads.filter(read => /\/api\/search-views\/[^/]+\/directories\?/.test(read.path));
  expect(directoryReads.length).toBeGreaterThan(1);
  expect(Math.max(...directoryReads.map(read => read.milliseconds)),
    'A bounded page with previews must stay responsive during ingestion, not repeatedly scan whole folders.').toBeLessThan(500);
  const timing = await display.evaluate(timing => timing.finish());
  await display.dispose();
  expect(timing.firstResultDomMs).not.toBeNull();
  expect(timing.fullPageDomMs).not.toBeNull();
  await page.screenshot({ path: testInfo.outputPath('100k-files-20-peers.png') });
  console.log('PERF_DISTRIBUTED_SEARCH ' + JSON.stringify({ peers: 20, foldersPerPeer: 25, filesPerFolder: 200,
    totalFiles: 100_000, firstBatchFiles: 1_000, displayedFolders: 100, displayedPreviewFiles: 2_400,
    finalBatchToDomMs, ...timing, reads }));
});

test('keeps an unread projection at its prefix through a 100k-result burst @Load', async ({ page, daemon }) => {
  test.setTimeout(60_000);
  await daemon.command('staged-large-directory-search');
  await page.goto('/jobs');
  const submitted = await page.request.post('/api/jobs/search', {
    data: { queryText: 'Fixture' } satisfies Schema<'SubmitSearchJobRequestDto'>,
  });
  expect(submitted.ok()).toBe(true);
  const source = await submitted.json() as Schema<'JobSummaryDto'>;
  const first = await page.request.post(`/api/jobs/${source.jobId}/search-views`, {
    data: { kind: 'GenericDirectories', includeFullResults: false },
  });
  expect(first.status()).toBe(202);
  async function advance(view: Schema<'SearchViewSummaryDto'>, ready: (value: Schema<'SearchViewSummaryDto'>) => boolean) {
    while (!ready(view)) {
      const response = await page.request.get(`/api/search-views/${view.viewId}/updates?afterRevision=${view.revision}&waitForChange=true`);
      expect(response.ok()).toBe(true);
      view = (await response.json() as Schema<'SearchViewUpdateDto'>).summary;
    }
    return view;
  }
  const unread = await advance(await first.json(), view => Number(view.counters.publicFileCount) === 1000);
  const started = performance.now();
  await daemon.command('next-search-batch');
  await daemon.command('finish-search');
  // A requested, empty filtered View provides a worker barrier through the
  // entire raw source while avoiding another expensive result materialization.
  const filtered = await page.request.post(`/api/jobs/${source.jobId}/search-views`, {
    data: { kind: 'Files', includeFullResults: true, filterText: '__no_matching_result__' },
  });
  expect(filtered.status()).toBe(202);
  const completed = await advance(await filtered.json(), view => Boolean(view.isComplete));
  expect(Number(completed.counters.publicFileCount)).toBe(100_000);
  expect(Number(completed.counters.projectedFileCount)).toBe(0);
  await daemon.command(`report-search-projection:${unread.viewId}`);
  const line = [...daemon.logs()].reverse().find(line => line.startsWith('E2E_PROJECTION_STATE '));
  expect(line).toBeTruthy();
  const held = JSON.parse(line!.slice('E2E_PROJECTION_STATE '.length)) as {
    viewId: string; revision: number; publicFileCount: number; isComplete: boolean;
  };
  expect(held).toBeTruthy();
  // One already-requested batch may finish; raw arrivals must not drain the View.
  expect(held.publicFileCount).toBeLessThanOrEqual(11_000);
  expect(held.isComplete).toBe(false);
  console.log('PERF_UNREAD_SEARCH ' + JSON.stringify({ milliseconds: Math.round(performance.now() - started),
    rawFiles: completed.counters.publicFileCount, unreadFiles: held.publicFileCount,
    unreadRevision: held.revision }));
});

test('reorders one 100k-file Track View without projection writes and downloads its original selection @Load', async ({ page, daemon }, testInfo) => {
  test.setTimeout(120_000);
  await daemon.command('staged-large-directory-search');
  let viewCreates = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(request.url()).pathname)) viewCreates++;
  });
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'Track Search', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.locator('.file-item-card')).toHaveCount(500);
  const completedPage = page.waitForResponse(async response => {
    if (!response.ok() || !/\/api\/search-views\/[^/]+\/files$/.test(new URL(response.url()).pathname)) return false;
    const body = await response.json() as Schema<'SearchViewFilePageDto'>;
    return body.revision.isComplete && Number(body.revision.counters.publicFileCount) === 100_000;
  }, { timeout: 90_000 });
  await daemon.command('next-search-batch');
  await daemon.command('finish-search');
  const complete = await (await completedPage).json() as Schema<'SearchViewFilePageDto'>;
  const chosen = complete.items[0]!;
  const name = (path: string) => path.split(/[\\/]/).at(-1)!;
  await expect(page.locator('.file-item-card').first().getByRole('checkbox'))
    .toHaveAttribute('aria-label', `Select ${name(chosen.remoteFilename)}`);
  await page.locator('.file-item-card').first().getByRole('checkbox').check();
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  expect(viewCreates).toBe(1);
  const initialViewId = complete.revision.viewId;
  const sorts: { order: string; httpMs: number; interactionToDomMs: number }[] = [];
  for (const [value, order] of [
    ['size', 'ItemSizeDescending'], ['reverse', 'ItemSizeAscending'],
    ['speed', 'UploadSpeed'], ['queue', 'QueueDepth'], ['relevance', 'Relevance'],
  ] as const) {
    const response = page.waitForResponse(result => {
      const url = new URL(result.url());
      return result.request().method() === 'GET' && url.pathname === `/api/search-views/${initialViewId}/files`
        && url.searchParams.get('sortOrder') === order;
    });
    const started = performance.now();
    if (value === 'reverse') await page.getByRole('button', { name: 'Reverse item sort', exact: true }).click();
    else await page.getByLabel('Sort', { exact: true }).selectOption(value);
    const result = await response;
    expect(result.ok(), await result.text()).toBe(true);
    const body = await result.json() as Schema<'SearchViewFilePageDto'>;
    expect(body.revision).toEqual(complete.revision);
    expect(body.items).toHaveLength(500);
    expect(viewCreates).toBe(1);
    if (order === 'Relevance') expect(body.items.map(item => item.ref)).toEqual(complete.items.map(item => item.ref));
    else expect(body.items.map(item => item.remoteFilename)).toEqual(Array.from({ length: 500 }, (_, index) =>
      'Fixture\\Album-000\\track-000-' + String(index).padStart(6, '0') + '.txt'));
    await expect(page.locator('.file-item-card').first().getByRole('checkbox'))
      .toHaveAttribute('aria-label', `Select ${name(body.items[0]!.remoteFilename)}`);
    await expect(page.locator('.result-refinement-status')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
    const httpMs = Math.round(result.request().timing().responseEnd);
    sorts.push({ order, httpMs, interactionToDomMs: Math.round(performance.now() - started) });
    expect(httpMs, `A ${order} page must read stored keys instead of replaying 100k observations.`).toBeLessThan(100);
  }
  await expect(page.locator('.file-item-card').first().getByRole('checkbox')).toBeChecked();
  await page.screenshot({ path: testInfo.outputPath('100k-track-sorted-selection.png') });
  const receipt = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/search-views/${initialViewId}/commit`);
  await page.getByRole('button', { name: 'Download 1', exact: true }).click();
  expect(Number((await (await receipt).json() as Schema<'CommitSearchViewSelectionResponseDto'>).submittedCount)).toBe(1);
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Downloads', exact: true }).click();
  await expect(page.locator('.file-item-card .transfer-state')).toHaveText('Complete');
  const history = await page.request.get('/api/transfers?direction=download&limit=500');
  const transfers = (await history.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(transfers.map(transfer => [transfer.identity.username, transfer.identity.remotePath, transfer.status.terminalOutcome]))
    .toEqual([[chosen.peer.username, chosen.remoteFilename, 'Succeeded']]);
  expect((await stat(transfers[0]!.status.localPath!)).size).toBe(128);
  await daemon.command('report-peer-browses');
  expect(daemon.logs().some(line => line === 'E2E_METRIC peer-browses count=0')).toBe(true);
  console.log('PERF_READ_ORDER ' + JSON.stringify({ files: 100_000, viewCreates, sorts }));
});

test('pages a 100k-file search folder and downloads one continued child @Load', async ({ page, daemon }, testInfo) => {
  test.setTimeout(120_000);
  await daemon.command('staged-large-directory-search');
  await page.goto('/jobs');
  await page.getByRole('button', { name: /Search result mode:.*Choose mode/ }).click();
  await page.getByRole('menuitemradio', { name: 'File Search', exact: true }).click();
  await page.locator('#global-search-simple').fill('Fixture');
  const childRequests: Request[] = [];
  const countRequests: Request[] = [];
  const rootPages: { observedAtMs: number; page: Schema<'SearchViewDirectoryPageDto'> }[] = [];
  const captured: Promise<void>[] = [];
  const captureErrors: unknown[] = [];
  const observeFinished = (request: Request) => {
    if (!/\/api\/search-views\/[^/]+\/directories$/.test(new URL(request.url()).pathname)) return;
    const observedAtMs = performance.now();
    captured.push(request.response().then(async response => {
      if (response?.ok()) rootPages.push({ observedAtMs, page: await response.json() });
    }).catch(error => { captureErrors.push(error); }));
  };
  page.on('requestfinished', observeFinished);
  page.on('request', request => {
    if (request.method() === 'GET' && /\/api\/search-views\/[^/]+\/directories\/[^/]+\/files$/.test(new URL(request.url()).pathname))
      childRequests.push(request);
    if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/selection-counts'))
      countRequests.push(request);
  });
  const rendering = await page.evaluateHandle(() => {
    const longTasks: number[] = [];
    const observer = new PerformanceObserver(list => {
      for (const entry of list.getEntries()) longTasks.push(Math.round(entry.duration));
    });
    observer.observe({ entryTypes: ['longtask'] });
    return { finish: () => { observer.disconnect(); return longTasks; } };
  });
  const displayObserver = await observeSearchDisplay(page, '.folder-item-card', 1);
  const discoveryStarted = performance.now();
  console.log('PERF_LARGE_DIRECTORY_PHASE ' + JSON.stringify({ phase: 'search-start', utcMs: Date.now() }));
  const directoryPageAt = (files: number) => page.waitForResponse(async response => {
    if (!response.ok() || !/\/api\/search-views\/[^/]+\/directories$/.test(new URL(response.url()).pathname)) return false;
    const body = await response.json() as Schema<'SearchViewDirectoryPageDto'>;
    return Number(body.revision.counters.publicFileCount) === files;
  });
  const [created, initialPage] = await Promise.all([
    page.waitForResponse(response => response.request().method() === 'POST'
      && /\/api\/jobs\/[^/]+\/search-views$/.test(new URL(response.url()).pathname)),
    directoryPageAt(1000),
    page.locator('#global-search-simple').press('Enter'),
  ]);
  expect(created.status(), await created.text()).toBe(202);
  const initialSummary = (await initialPage.json() as Schema<'SearchViewDirectoryPageDto'>).revision;
  expect(initialSummary.isComplete).toBe(false);
  await expect(page.locator('.search-results-summary')).toContainText('Searching');
  const folder = page.locator('.folder-item-card');
  await expect(folder).toHaveCount(1);
  await expect(folder.locator('.folder-file-row')).toHaveCount(24);
  expect(childRequests).toHaveLength(0);
  const more = page.getByRole('button', { name: 'Load more matching files', exact: true });
  await expect(more).toBeEnabled();
  const continuedStarted = performance.now();
  const [response] = await Promise.all([
    page.waitForResponse(result => result.request().method() === 'GET'
      && /\/api\/search-views\/[^/]+\/directories\/[^/]+\/files$/.test(new URL(result.url()).pathname), { timeout: 10_000 }),
    more.click({ timeout: 5_000 }),
  ]);
  expect(response.ok(), await response.text()).toBe(true);
  const files = await response.json() as Schema<'SearchViewDirectoryFilePageDto'>;
  await expect(folder.locator('.folder-file-row')).toHaveCount(524);
  const continuedUiMs = Math.round(performance.now() - continuedStarted);
  const continuedApiMs = Math.round(response.request().timing().responseEnd);
  expect(childRequests).toHaveLength(1);
  expect(new URL(response.url()).searchParams.get('limit')).toBe('500');
  expect(String(files.revision.revision)).toBe(String(initialSummary.revision));
  expect(files.items).toHaveLength(500);
  expect(files.nextCursor).toBeTruthy();
  expect(files.items.map(item => item.file.remoteFilename)).toEqual(Array.from({ length: 500 }, (_, index) =>
    'Fixture\\Album-000\\track-000-' + String(index + 24).padStart(6, '0') + '.txt'));
  expect(continuedApiMs, 'A 500-file page must not join metadata for all 100k members.').toBeLessThan(100);
  const chosen = files.items.at(-1)!.file;
  const counted = page.waitForResponse(result => result.request().method() === 'POST'
    && new URL(result.url()).pathname.endsWith('/selection-counts'));
  await folder.locator('.folder-file-row').last().getByRole('checkbox').check({ timeout: 5_000 });
  const countResponse = await counted;
  expect(countResponse.ok(), await countResponse.text()).toBe(true);
  const counts = await countResponse.json() as Schema<'CountSearchViewSelectionResponseDto'>;
  expect(Number(counts.publicFileCount)).toBe(1);
  expect(Number(counts.lockedFileCount)).toBe(0);
  const countApiMs = Math.round(countResponse.request().timing().responseEnd);
  expect(countApiMs, 'Counting one selected file must seek its exact membership.').toBeLessThan(100);
  // Arrivals must not collapse the 500 continued rows back to the preview.
  const advanced = directoryPageAt(2000);
  await daemon.command('next-search-batch');
  await advanced;
  await expect(folder.locator('.folder-file-row')).toHaveCount(524);
  await expect(folder.locator('.folder-file-row').last().getByRole('checkbox')).toBeChecked();
  await expect(page.locator('.search-results-summary')).toContainText('Searching');
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  const output = testInfo.outputPath('selected-download');
  await page.getByRole('button', { name: 'Download options', exact: true }).click();
  const options = page.getByRole('region', { name: 'Download options' });
  await options.getByRole('textbox', { name: 'Output directory' }).fill(output);
  await options.getByRole('button', { name: 'Close download options', exact: true }).click();
  const remainingStarted = performance.now();
  const completed = waitForCompleteView(page);
  const activeBacklog = page.waitForResponse(async response => {
    if (!response.ok() || !/\/api\/search-views\/[^/]+\/directories$/.test(new URL(response.url()).pathname)) return false;
    const body = await response.json() as Schema<'SearchViewDirectoryPageDto'>;
    return Number(body.revision.counters.publicFileCount) > 2000;
  });
  await daemon.command('finish-search');
  const backlog = (await (await activeBacklog).json() as Schema<'SearchViewDirectoryPageDto'>).revision;
  await expect(page.getByRole('button', { name: 'Download 1', exact: true })).toBeEnabled();
  const committed = page.waitForResponse(result => result.request().method() === 'POST'
    && new URL(result.url()).pathname.endsWith('/commit'));
  await page.getByRole('button', { name: 'Download 1', exact: true }).click();
  const commit = await committed;
  expect(commit.status(), await commit.text()).toBe(202);
  const receipt = await commit.json() as Schema<'CommitSearchViewSelectionResponseDto'>;
  expect(Number(receipt.submittedCount)).toBe(1);
  expect(Number(receipt.rejectedCount)).toBe(0);
  // The source may have retired while its View still publishes queued results.
  // On fast storage the entire backlog can finish before the next browser poll.
  await expect(page.locator('.search-status-badge')).not.toHaveText('Starting');
  const summary = await completed;
  const remainingDiscoveryObservedMs = Math.round(performance.now() - remainingStarted);
  const discoveryObservedMs = Math.round(performance.now() - discoveryStarted);
  console.log('PERF_LARGE_DIRECTORY_PHASE ' + JSON.stringify({ phase: 'search-complete', utcMs: Date.now(), discoveryObservedMs,
    remainingDiscoveryObservedMs }));
  expect(Number(summary.counters.projectedFileCount)).toBe(100_000);
  expect(Number(summary.counters.publicFileCount)).toBe(100_000);
  expect(Number(summary.counters.observedPeerCount)).toBe(1);
  expect(Number(summary.counters.topLevelItemCount)).toBe(1);
  await expect(page.locator('.search-results-summary')).toContainText('Complete');
  await expect(folder.locator('.folder-file-row')).toHaveCount(524);
  await expect(folder.locator('.folder-file-row').last().getByRole('checkbox')).toBeChecked();
  page.off('requestfinished', observeFinished);
  await Promise.all(captured);
  expect(captureErrors).toEqual([]);
  for (const { page: root } of rootPages) {
    expect(root.items).toHaveLength(Number(root.revision.counters.topLevelItemCount));
    expect(root.items.reduce((total, item) => total + Number(item.publicMatchingFileCount), 0))
      .toBe(Number(root.revision.counters.publicFileCount));
  }
  const livePublications = rootPages.filter(root => root.observedAtMs > remainingStarted);
  const displayTiming = await displayObserver.evaluate(observer => observer.finish());
  await displayObserver.dispose();
  expect(displayTiming.firstResultDomMs).not.toBeNull();
  expect(displayTiming.completeDomMs).not.toBeNull();
  const longTaskMs = await rendering.evaluate(observer => observer.finish());
  await rendering.dispose();
  const continuedResources = displayTiming.resources.filter(resource => /\/directories\/[^/]+\/files\?/.test(resource.path));

  const childPages = childRequests.map(request => new URL(request.url()).pathname + new URL(request.url()).search);
  expect(new Set(childPages).size, 'Continued folders reload once per revision, with fresh cursors.').toBe(childPages.length);
  expect(new Set(countRequests.map(request => String(request.postDataJSON().revision))).size,
    'The same exact selection is counted once per revision.').toBe(countRequests.length);
  expect(daemon.logs().some(line => line === 'E2E_METRIC staged-directory-search searches=1 files=100000')).toBe(true);
  await page.locator('.search-results-summary').scrollIntoViewIfNeeded();
  const screenshot = testInfo.outputPath('100k-folder-selected-continued-file.png');
  await page.screenshot({ path: screenshot, animations: 'disabled' });
  await testInfo.attach('One selected child retained through live growth to 100k files', { path: screenshot, contentType: 'image/png' });
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('button', { name: 'Downloads', exact: true }).click();
  await expect(page.locator('.file-item-card .transfer-state')).toHaveText('Complete');
  const transfers = await page.request.get('/api/transfers?direction=download&limit=500');
  expect(transfers.ok(), await transfers.text()).toBe(true);
  const timeline = await transfers.json() as Schema<'TransferTimelinePageDto'>;
  expect(timeline.items.map(item => [item.identity.username, item.identity.remotePath, item.status.terminalOutcome])).toEqual([
    [chosen.peer.username, chosen.remoteFilename, 'Succeeded'],
  ]);
  const transfer = timeline.items[0]!;
  expect(Number(transfer.progress.bytesTransferred)).toBe(128);
  expect(transfer.status.localPath!.startsWith(output + sep)).toBe(true);
  expect((await stat(transfer.status.localPath!)).size).toBe(128);
  // View completion must not mask a missing retained source tail. This is one
  // test-only history read, independent of the UI's projected folder requests.

  const sourceJobId = (await created.json() as Schema<'SearchViewSummaryDto'>).sourceJobId;
  const rawTailResponse = await measureApi(daemon.url, `/api/jobs/${sourceJobId}/raw?afterSequence=99999&limit=1`);
  expect(rawTailResponse.ok, rawTailResponse.body).toBe(true);
  const rawTail = JSON.parse(rawTailResponse.body) as Schema<'SearchRawResultDto'>[];
  const rawTailApiMs = rawTailResponse.elapsedMs;
  expect(rawTail.map(row => Number(row.sequence))).toEqual([100000]);
  expect(rawTail[0]).toMatchObject({ username: chosen.peer.username,
    filename: 'Fixture\\Album-000\\track-000-099999.txt', size: 128 });
  expect(rawTailApiMs, 'One retained raw result must remain a bounded read at 100k inputs.').toBeLessThan(100);
  await daemon.command('report-peer-browses');
  expect(daemon.logs().some(line => line === 'E2E_METRIC peer-browses count=0')).toBe(true);
  console.log('PERF_LARGE_DIRECTORY ' + JSON.stringify({ discoveryObservedMs, remainingDiscoveryObservedMs,
    rawTailApiMs,
    viewCreateApiMs: Math.round(created.request().timing().responseEnd),
    firstResultDomMs: displayTiming.firstResultDomMs, completeDomMs: displayTiming.completeDomMs,
    continuedApiMs, continuedUiMs,
    continuedApiMaxMs: Math.max(...childRequests.map(request => Math.round(request.timing().responseEnd))),
    continuedEncodedBytes: continuedResources.reduce((sum, resource) => sum + resource.encodedBytes, 0),
    continuedDecodedBytes: continuedResources.reduce((sum, resource) => sum + resource.decodedBytes, 0),
    longTaskMs,
    countApiMs,
    commitApiMs: Math.round(commit.request().timing().responseEnd),
    commitBacklogFiles: Number(backlog.counters.publicFileCount),
    commitBacklogComplete: backlog.isComplete,
    continuedPages: childRequests.map(request => ({ revision: new URL(request.url()).searchParams.get('revision'),
      limit: new URL(request.url()).searchParams.get('limit'), milliseconds: Math.round(request.timing().responseEnd) })),
    countRequests: countRequests.length,
    livePublications: livePublications.map(root => ({
      elapsedMs: Math.round(root.observedAtMs - remainingStarted),
      revision: String(root.page.revision.revision),
      publicFiles: Number(root.page.revision.counters.publicFileCount),
    })),
    daemonMetrics: daemon.logs().filter(line => line.startsWith('E2E_METRIC')) }));
});
