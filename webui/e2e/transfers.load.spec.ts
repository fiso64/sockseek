import { performance } from 'node:perf_hooks';
import { EventEmitter, once } from 'node:events';
import { readdir, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { test, expect, measureApi, expectTransferLayoutUnchanged } from './fixtures';
import type { Schema } from '../src/api/types';

test.use({ trace: 'off', daemonLogMetrics: true });

test('pages a 100k-transfer retained timeline and preserves expanded folders through live arrivals @Load', async ({ page, daemon }, testInfo) => {
  test.setTimeout(120_000);
  await daemon.command('large-retained-transfers');
  await page.clock.install();
  // The virtual clock controls refresh-timer assertions later in the case.
  // Report DOM milestones to the real test clock, independently of locator retries.
  const domMarks: Record<string, number> = {};
  const domEvents = new EventEmitter();
  async function waitForDomMark(name: string): Promise<void> {
    // Reuse the observer's readiness signal before validating the settled
    // extent; locator assertions need not drive another readiness check.
    if (domMarks[name] === undefined)
      await once(domEvents, name, { signal: AbortSignal.timeout(5_000) });
  }
  await page.exposeFunction('__recordTransferDom', (name: string) => {
    domMarks[name] ??= performance.now();
    domEvents.emit(name);
  });
  await page.addInitScript(() => {
    const reported = new Set<string>();
    function report(name: string) {
      if (reported.has(name)) return;
      reported.add(name);
      void (window as typeof window & { __recordTransferDom: (name: string) => Promise<void> })
        .__recordTransferDom(name).catch(() => {});
    }
    function clicked(event: MouseEvent) {
      const button = event.target instanceof Element ? event.target.closest('button') : null;
      if (button?.textContent?.trim() === 'Load more folder files'
        && button.closest('.folder-item-card')?.querySelector('.folder-transfer-name')?.textContent === 'Folder049') report('moreStarted');
    }
    document.addEventListener('click', clicked, true);
    const observer = new MutationObserver(() => {
      const folder = [...document.querySelectorAll('.folder-item-card')]
        .find(card => card.querySelector('.folder-transfer-name')?.textContent === 'Folder049');
      if (!folder) return;
      const rows = folder.querySelectorAll('.folder-file-row').length;
      const count = folder.querySelector('.folder-transfer-file-count')?.textContent ?? '';
      if (rows === 500 && count.includes('500/2000')
        && folder.querySelector('.transfer-state')?.textContent?.trim() === 'Complete') report('initial');
      if (reported.has('moreStarted') && rows === 1000 && count.includes('1000/2000')) report('more');
      const arrival = [...document.querySelectorAll('.file-item-card')]
        .find(card => card.textContent?.includes('New arrival.txt'));
      if (rows === 1000 && count.includes('1000/2000')
        && arrival?.querySelector('.transfer-state')?.textContent?.trim() === 'Complete') report('arrival');
      if (['initial', 'more', 'arrival'].every(name => reported.has(name))) {
        observer.disconnect();
        document.removeEventListener('click', clicked, true);
      }
    });
    observer.observe(document, { childList: true, subtree: true, characterData: true });
  });
  const summaryRequests: { elapsedMs: number; groups: unknown }[] = [];
  const observedAt = performance.now();
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/transfers/group-summaries')
      summaryRequests.push({ elapsedMs: Math.round(performance.now() - observedAt), groups: request.postDataJSON() });
  });
  const requests: { method: string; path: string; elapsedMs: number }[] = [];
  page.on('requestfinished', request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/transfers')) requests.push({
      method: request.method(), path: url.pathname + url.search,
      elapsedMs: Math.round(request.timing().responseEnd),
    });
  });
  const timelineEvents = new EventEmitter();
  page.on('websocket', socket => {
    const scopes = new Map<string, { limit: number; started: number }>();
    socket.on('framesent', frame => {
      for (const encoded of frame.payload.toString().split('\u001e').filter(Boolean)) {
        const message = JSON.parse(encoded);
        if (message.type === 4 && message.target === 'ObserveTransferTimeline')
          scopes.set(message.invocationId, { limit: message.arguments[0].limit, started: performance.now() });
      }
    });
    socket.on('framereceived', frame => {
      for (const encoded of frame.payload.toString().split('\u001e').filter(Boolean)) {
        const message = JSON.parse(encoded);
        if (message.type !== 2 || !message.item?.items) continue;
        const scope = scopes.get(message.invocationId);
        if (scope && message.item.windowTransferIds)
          timelineEvents.emit(`scope-${scope.limit}`, { milliseconds: Math.round(performance.now() - scope.started),
            boundaryId: message.item.boundaryTransferId });
        for (const row of message.item.items as Schema<'TransferHistoryDto'>[])
          if (row.terminalOutcome === 'Succeeded')
            timelineEvents.emit(row.remotePath!, { row, receivedAt: Date.now() });
      }
    });
  });
  const expandTimeline = async (limit: number): Promise<{ milliseconds: number; boundaryId: string }> => {
    const ready = once(timelineEvents, `scope-${limit}`, { signal: AbortSignal.timeout(5_000) });
    await page.getByRole('button', { name: 'Load earlier downloads', exact: true }).click();
    const [result] = await ready;
    await expect(page.getByRole('button', { name: 'Load earlier downloads', exact: true })).toBeEnabled();
    return result;
  };
  const completedRow = async (path: string): Promise<{ row: Schema<'TransferHistoryDto'>; receivedAt: number }> => {
    const [value] = await once(timelineEvents, path, { signal: AbortSignal.timeout(5_000) });
    return value;
  };
  const started = performance.now();
  const firstPage = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/transfers/timeline');
  await page.goto('/downloads');
  await waitForDomMark('initial');
  const folder = page.locator('.folder-item-card').filter({
    has: page.locator('.folder-transfer-name').filter({ hasText: /^Folder049$/ }),
  });
  await expect(folder.locator('.folder-file-row')).toHaveCount(500);
  await expect(folder.locator('.folder-transfer-file-count')).toContainText('500/2000');
  await expect(folder.locator('.transfer-state').first()).toHaveText('Complete');
  await expect(folder.locator('.folder-transfer-size')).toHaveText('10.00 GB');
  const initialUiMs = Math.round(performance.now() - started);
  const summary = (await (await firstPage).json() as Schema<'TransferTimelineUpdateDto'>).groupSummaries!.items;
  expect(summary).toHaveLength(1);
  expect(summary[0]).toMatchObject({ fileCount: 2_000, knownTotalBytes: 10_000_000_000,
    transferredBytes: 10_000_000_000, succeededCount: 2_000 });
  expect(requests.filter(item => item.method === 'GET')).toHaveLength(0);
  expect(requests.filter(item => item.path === '/api/transfers/timeline')).toHaveLength(1);
  expect(requests.filter(item => item.method === 'POST' && item.path.endsWith('/group-summaries'))).toHaveLength(0);

  const moreStarted = performance.now();
  const membersResponse = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname === '/api/transfers'
    && new URL(response.url()).searchParams.has('groupRef'));
  await folder.getByRole('button', { name: 'Load more folder files', exact: true }).click();
  const members = await (await membersResponse).json() as Schema<'TransferTimelinePageDto'>;
  const bulkLookupIds = new Set(members.items.map(item => item.transferId));
  await waitForDomMark('more');
  await expect(folder.locator('.folder-file-row')).toHaveCount(1000);
  await expect(folder.locator('.folder-transfer-file-count')).toContainText('1000/2000');
  await expect(folder.locator('.folder-transfer-size')).toHaveText('10.00 GB');
  const moreUiMs = Math.round(performance.now() - moreStarted);
  expect(members.items).toHaveLength(500);
  expect(members.items[0]!.remotePath).toBe('Library\\Folder049\\Track099500.flac');
  expect(new Set(await folder.locator('.folder-file-path').allTextContents()).size).toBe(1000);
  expect(requests.filter(item => item.method === 'GET')).toHaveLength(1);
  expect(requests.filter(item => item.method === 'POST' && item.path.endsWith('/group-summaries'))).toHaveLength(0);

  // A continuation wholly inside an already-known retained folder adds no
  // summary identity or invalidation. Its exact totals need no second POST.
  await expandTimeline(1000);
  await expect(folder.locator('.folder-file-row')).toHaveCount(1000);
  expect(requests.filter(item => item.method === 'POST' && item.path.endsWith('/group-summaries'))).toHaveLength(0);

  // A real unrelated completion shifts the global page. Its reconciliation
  // must not discard the already-expanded group prefix or its exact totals.
  const arrivalStarted = performance.now();
  const retainedArrival = completedRow('Fixture\\New arrival.txt');
  const accepted = await measureApi(daemon.url, '/api/jobs/lists', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    name: 'Unrelated live arrival', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\New arrival.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'>) });
  expect(accepted.status, accepted.body).toBe(202);
  await waitForDomMark('arrival');
  await expect(page.locator('.file-item-card').filter({ hasText: 'New arrival.txt' })
    .locator('.transfer-state')).toHaveText('Complete');
  await retainedArrival;
  await expect(folder.getByRole('button', { name: 'Load more folder files', exact: true })).toBeEnabled();
  await expect(folder.locator('.folder-file-row')).toHaveCount(1000);
  await expect(folder.locator('.folder-transfer-file-count')).toContainText('1000/2000');
  await expect(folder.locator('.folder-file-path').filter({ hasText: 'Track099001.flac' })).toHaveCount(1);
  const arrivalUiMs = Math.round(performance.now() - arrivalStarted);
  for (const name of ['initial', 'moreStarted', 'more', 'arrival']) expect(domMarks[name]).toBeDefined();
  const domUiMs = {
    initialFromNavigation: Math.round(domMarks.initial! - started),
    moreFromClick: Math.round(domMarks.more! - domMarks.moreStarted!),
    arrivalFromSubmission: Math.round(domMarks.arrival! - arrivalStarted),
  };
  // Reconciliation already fetched authoritative totals. A previously scheduled
  // live-progress refresh must not reread the same groups one second later.
  const settledSummaries = summaryRequests.length;
  await page.clock.fastForward(1_000);
  expect(summaryRequests, JSON.stringify(summaryRequests)).toHaveLength(settledSummaries);
  // Global and folder cursors cover overlapping slices of the same moving
  // timeline. Alternate them after displacement by the unrelated completion;
  // loading more globally must not restart the folder's already-loaded prefix.
  let folderContinuations = 2;
  const interleavedPaging: { globalObservationMs: number; folderApiMs: number; uiMs: number; files: number }[] = [];
  {
    const pagingStarted = performance.now();
    const globalResponse = await expandTimeline(1500);
    // The moving prefix now contains the unrelated file plus 1499 members.
    await expect(folder.locator('.folder-file-row')).toHaveCount(1499);
    const nextMembers = page.waitForResponse(response => response.ok()
      && new URL(response.url()).pathname === '/api/transfers'
      && new URL(response.url()).searchParams.has('groupRef'));
    await folder.getByRole('button', { name: 'Load more folder files', exact: true }).click();
    const memberResponse = await nextMembers;
    const memberPage = await memberResponse.json() as Schema<'TransferTimelinePageDto'>;
    for (const item of memberPage.items) bulkLookupIds.add(item.transferId);
    await expect(folder.locator('.folder-file-row')).toHaveCount(1999);
    expect(memberPage.items).toHaveLength(500);
    expect(memberPage.items[0]!.remotePath).toBe('Library\\Folder049\\Track098501.flac');
    // Displacement leaves one final member; continue from its real cursor.
    const finalMember = page.waitForResponse(response => response.ok()
      && new URL(response.url()).pathname === '/api/transfers'
      && new URL(response.url()).searchParams.has('groupRef'));
    await folder.getByRole('button', { name: 'Load more folder files', exact: true }).click();
    const finalPage = await (await finalMember).json() as Schema<'TransferTimelinePageDto'>;
    expect(finalPage.items).toHaveLength(1);
    folderContinuations++;
    const expectedFiles = 2000;
    await expect(folder.locator('.folder-file-row')).toHaveCount(expectedFiles);
    await expect(folder.locator('.folder-transfer-file-count')).toHaveText(/2000\s*files/);
    const paths = await folder.locator('.folder-file-path').allTextContents();
    expect(new Set(paths).size).toBe(expectedFiles);
    await expect(folder.locator('.folder-transfer-size')).toHaveText('10.00 GB');
    interleavedPaging.push({
      globalObservationMs: globalResponse.milliseconds,
      folderApiMs: Math.round(memberResponse.request().timing().responseEnd),
      uiMs: Math.round(performance.now() - pagingStarted), files: expectedFiles,
    });
  }
  // Catch the global cursor up to the folder page just fetched. This reveals
  // no new group and must neither reread members nor ask for unchanged totals.
  await expandTimeline(2000);
  await expect(folder.locator('.folder-file-row')).toHaveCount(2000);
  // Cross the expanded folder's cached extent, then the next group boundary.
  // Older global rows are newly revealed data, not evidence that the cached
  // prefix changed. Folder048 arrives with its totals in the same observation.
  const summaryCallsBeforeBoundary = requests.filter(item => item.method === 'POST' && item.path.endsWith('/group-summaries')).length;
  await expandTimeline(2500);
  await expect(folder.locator('.folder-file-row')).toHaveCount(2000);
  const olderFolder = page.locator('.folder-item-card').filter({ hasText: 'Folder048' });
  // The new group and its exact totals share one frame and need no totals POST.
  await expect(olderFolder).toHaveCount(1);
  await expect(olderFolder.locator('.folder-transfer-file-count')).toContainText('499/2000');
  await expect(olderFolder.locator('.folder-transfer-size')).toHaveText('10.00 GB');
  expect(requests.filter(item => item.method === 'POST' && item.path.endsWith('/group-summaries'))).toHaveLength(summaryCallsBeforeBoundary);
  await expect(folder.locator('.folder-transfer-file-count')).toHaveText(/2000\s*files/);
  await expect(folder.getByRole('button', { name: 'Load more folder files', exact: true })).toHaveCount(0);
  await folder.locator('.folder-transfer-name').scrollIntoViewIfNeeded();
  const screenshot = testInfo.outputPath('large-retained-folder.png');
  await page.screenshot({ path: screenshot });
  await testInfo.attach('100k retained timeline with expanded folder', { path: screenshot, contentType: 'image/png' });
  await page.setViewportSize({ width: 390, height: 900 });
  await folder.locator('.folder-transfer-name').scrollIntoViewIfNeeded();
  const peerCountBounds = await page.locator('.peer-result-count').evaluateAll(nodes =>
    nodes.map(node => ({ right: node.getBoundingClientRect().right, width: window.innerWidth })));
  for (const bounds of peerCountBounds) expect(bounds.right).toBeLessThanOrEqual(bounds.width);
  const narrowScreenshot = testInfo.outputPath('large-retained-folder-narrow.png');
  await page.screenshot({ path: narrowScreenshot });
  await testInfo.attach('100k retained timeline at narrow width', { path: narrowScreenshot, contentType: 'image/png' });
  expect(requests.filter(item => item.method === 'GET'
    && new URL(item.path, daemon.url).searchParams.has('groupRef')),
  'Only explicit folder continuations should read members.').toHaveLength(folderContinuations);
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.evaluate(() => {
    const reported = new Set<string>();
    const observer = new MutationObserver(() => {
      for (const card of document.querySelectorAll('.file-item-card')) {
        const match = card.textContent?.match(/Burst ([123])\.txt/);
        if (!match || card.querySelector('.transfer-state')?.textContent?.trim() !== 'Complete') continue;
        const name = `burst-${match[1]}`;
        if (reported.has(name)) continue;
        reported.add(name);
        void (window as typeof window & { __recordTransferDom: (name: string) => Promise<void> })
          .__recordTransferDom(name).catch(() => {});
      }
      if (reported.size === 3) observer.disconnect();
    });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true });
  });
  const profiling = await page.context().newCDPSession(page);
  await profiling.send('Performance.enable');
  const captureCpu = process.env.SOCKSEEK_PROFILE_TRANSFERS === '1';
  if (captureCpu) {
    await profiling.send('Profiler.enable');
    await profiling.send('Profiler.start');
  }
  const beforeRenderer = await profiling.send('Performance.getMetrics');
  const burstRequestStart = requests.length;
  const burstStarted = performance.now();
  const burstPhases: { acceptanceMs: number; completionMs: number | null; notificationMs: number | null; domMs: number }[] = [];
  // Separate quick completions exercise sustained same-direction invalidation,
  // rather than one coalesced terminal batch. Observe authoritative timeline updates.
  for (let index = 1; index <= 3; index++) {
    const submittedAt = Date.now();
    const submitted = performance.now();
    const filename = `Burst ${index}.txt`;
    const remotePath = `Fixture\\${filename}`;
    const retained = completedRow(remotePath);
    const response = await measureApi(daemon.url, '/api/jobs/lists', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
      name: filename, jobs: [{ kind: 'remote-file', target: {
        username: 'fixture-peer', filename: remotePath, size: 128, extension: '.txt',
      } }],
    } satisfies Schema<'SubmitJobListRequestDto'>) });
    expect(response.status, response.body).toBe(202);
    const acceptanceMs = response.elapsedMs;
    const { row: completed, receivedAt } = await retained;
    // Wait on the observed DOM event; repeated accessible-role queries across
    // 2,500 file rows otherwise dominate the renderer timings being measured.
    await waitForDomMark(`burst-${index}`);
    expect(domMarks[`burst-${index}`]).toBeDefined();
    // The canonical live overlay can know the outcome before its retained
    // completion timestamp. Preserve that missing fact in the diagnostics.
    const completedAt = completed.completedAtUtc ? Date.parse(completed.completedAtUtc) : null;
    burstPhases.push({ acceptanceMs, completionMs: completedAt === null ? null : completedAt - submittedAt,
      notificationMs: completedAt === null ? null : receivedAt - completedAt,
      domMs: Math.round(domMarks[`burst-${index}`]! - submitted) });
  }
  const burstObservedMs = Math.round(performance.now() - burstStarted);
  if (captureCpu) {
    const { profile } = await profiling.send('Profiler.stop');
    await testInfo.attach('transfer-burst-cpu', { body: JSON.stringify(profile), contentType: 'application/json' });
  }
  const afterRenderer = await profiling.send('Performance.getMetrics');
  const burstRenderer = Object.fromEntries(['ScriptDuration', 'LayoutDuration', 'RecalcStyleDuration', 'TaskDuration']
    .map(name => [name + 'Ms', Math.round(1_000 * (afterRenderer.metrics.find(metric => metric.name === name)!.value
      - beforeRenderer.metrics.find(metric => metric.name === name)!.value))]));
  await profiling.detach();
  for (let index = 1; index <= 3; index++)
    await expect(page.locator('.file-item-card').filter({ hasText: `Burst ${index}.txt` })
      .locator('.transfer-state')).toHaveText('Complete');
  await expect(page.getByRole('button', { name: 'Load earlier downloads', exact: true })).toBeEnabled();
  await expect(folder.locator('.folder-file-row')).toHaveCount(2000);
  await expect(folder.locator('.folder-transfer-size')).toHaveText('10.00 GB');
  await expect(olderFolder.locator('.folder-transfer-file-count')).toContainText('496/2000');
  expect(new Set(await folder.locator('.folder-file-path').allTextContents()).size).toBe(2000);
  expect(requests.filter(item => item.method === 'GET'
    && new URL(item.path, daemon.url).searchParams.has('groupRef')),
  'Unrelated same-direction completions must retain the expanded folder prefix.').toHaveLength(folderContinuations);
  const burstUiMs = Math.round(performance.now() - burstStarted);
  const burstRequests = requests.slice(burstRequestStart);
  expect(burstRequests.filter(item => item.method === 'GET')).toHaveLength(0);
  expect(burstRequests.filter(item => item.path.endsWith('/group-summaries'))).toHaveLength(0);
  expect(burstRequests.filter(item => item.path.endsWith('/lookup'))).toHaveLength(0);
  expect(bulkLookupIds.size).toBe(1000);

  const bulkResponse = await measureApi(daemon.url, '/api/transfers/lookup', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    transferIds: [...bulkLookupIds], direction: 'Download',
  } satisfies Schema<'TransferLookupRequestDto'>) });
  const bulkLookupApiMs = bulkResponse.elapsedMs;
  expect(bulkResponse.ok, bulkResponse.body).toBe(true);
  const bulk = JSON.parse(bulkResponse.body) as Schema<'TransferTimelinePageDto'>;
  expect(new Set(bulk.items.map(item => item.transferId))).toEqual(bulkLookupIds);
  expect(bulk.items).toHaveLength(1000);
  expect(bulk.nextCursor ?? null).toBeNull();
  expect(bulk.retainedCoverage.state).toBe('Available');
  expect(bulk.items.every(item => item.terminalOutcome === 'Succeeded'
    && Number(item.totalBytes) === 5_000_000 && Number(item.transferredBytes) === 5_000_000)).toBe(true);
  expect(bulkLookupApiMs, 'A batch lookup must use the requested identities instead of scanning or paging the timeline.').toBeLessThan(100);
  console.log('PERF_TRANSFERS ' + JSON.stringify({ initialUiMs, moreUiMs, arrivalUiMs, domUiMs, interleavedPaging, burstUiMs, burstObservedMs, burstRequests, burstPhases, burstRenderer, bulkLookupApiMs, requests, summaryRequests,
    daemonMetrics: daemon.logs().filter(line => line.startsWith('E2E_METRIC')) }));
});


for (const requestedRows of [500, 1000]) test(`downloads 50 folders of 200 tiny files with a ${requestedRows}-row Downloads extent @Load`, async ({ page, daemon }, testInfo) => {
  let globalTransferRows = 0, globalJobRows = 0, scopedLiveRows = 0, socketBytes = 0;
  let unrequestedLiveRows = 0;
  const globalJobIds = new Set<string>();
  let scopedSummaryUpdates = 0, scopedSummaryGroups = 0;
  const summaryReads: { groups: number; milliseconds: number }[] = [];
  page.on('requestfinished', request => {
    if (new URL(request.url()).pathname === '/api/transfers/group-summaries')
      summaryReads.push({ groups: request.postDataJSON().groups.length, milliseconds: Math.round(request.timing().responseEnd) });
  });
  test.setTimeout(120_000);
  const observations = new EventEmitter();
  page.on('websocket', socket => {
    const windows = new Map<string, Set<string>>();
    socket.on('framereceived', frame => {
      const payload = typeof frame.payload === 'string' ? frame.payload : frame.payload.toString();
      socketBytes += Buffer.byteLength(payload);
      for (const encoded of payload.split('\u001e').filter(Boolean)) {
        const update = JSON.parse(encoded);
        if (update.target?.toLowerCase() === 'stateupdatebatch') {
          globalTransferRows += update.arguments?.[0]?.state?.transfers.length ?? 0;
          for (const job of update.arguments?.[0]?.state?.jobs ?? []) { globalJobRows++; globalJobIds.add(job.jobId); }
        }
        if (update.type === 3) windows.delete(update.invocationId);
        if (update.type !== 2 || !update.item?.items) continue;
        scopedLiveRows += update.item.liveTransfers?.length ?? 0;
        if (update.item.groupSummaries) {
          scopedSummaryUpdates++;
          scopedSummaryGroups += update.item.groupSummaries.items.length;
        }
        const ids = update.item.windowTransferIds ? new Set<string>(update.item.windowTransferIds)
          : windows.get(update.invocationId) ?? new Set<string>();
        for (const row of update.item.items) ids.add(row.transferId);
        for (const id of update.item.removedTransferIds) ids.delete(id);
        for (const row of update.item.liveTransfers ?? []) if (!ids.has(row.transferId)) unrequestedLiveRows++;
        windows.set(update.invocationId, ids);
        observations.emit(`extent-${ids.size}`);
      }
    });
  });
  await daemon.command('hold-download');
  const folderCount = 50, filesPerFolder = 200;
  const outputDirectory = testInfo.outputPath('outputs');
  const expectedFiles: string[] = [];
  const jobs = Array.from({ length: folderCount }, (_, folderIndex) => {
    const folderName = `Batch${String(folderIndex).padStart(2, '0')}`;
    const entries = Array.from({ length: filesPerFolder }, (_, index) => {
      const filename = `File${String(folderIndex * filesPerFolder + index).padStart(5, '0')}.bin`;
      expectedFiles.push(join(folderName, filename));
      return { target: { username: 'fixture-peer', filename: `Fixture\\${folderName}\\${filename}`,
        size: 128, extension: '.bin' }, relativeDirectoryComponents: [] };
    });
    return { kind: 'remote-directory', plan: {
      displayRoot: folderName, entries, totalKnownBytes: filesPerFolder * 128,
    } } satisfies Schema<'JobDraftDto'>;
  });
  const accepted = await page.request.post('/api/jobs/lists', { data: {
    name: null, jobs, options: { outputParentDir: outputDirectory },
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(accepted.status(), await accepted.text()).toBe(202);
  const root = await accepted.json() as Schema<'JobSummaryDto'>;
  await daemon.command('wait-download-started');
  await page.goto('/downloads');
  const folder = page.locator('.folder-item-card');
  await expect(folder.first()).toBeVisible();
  await expect(folder.locator('.folder-file-row').first()).toBeVisible();
  let startedEpoch = 0;
  let completedRowsDomMs = 0, displayedDomMs = 0;
  let recordDisplay!: () => void;
  const displayRecorded = new Promise<void>(resolve => { recordDisplay = resolve; });
  await page.exposeFunction('__recordDownloadDisplay', (marks: { rows: number; display: number }) => {
    completedRowsDomMs = Math.round(marks.rows - startedEpoch);
    displayedDomMs = Math.round(marks.display - startedEpoch);
    recordDisplay();
  });
  await page.evaluate(({ requestedRows, filesPerFolder }) => {
    const state = window as typeof window & {
      __maximumTransferRows?: number;
      __downloadBackendCompleted?: () => void;
      __recordDownloadDisplay: (marks: { rows: number; display: number }) => Promise<void>;
    };
    let backendDone = false, reported = false;
    let rowsAt: number | null = null;
    let lastDomUpdate = performance.timeOrigin + performance.now();
    const countPattern = new RegExp(`^\\s*(?:\\d+\\/)?${filesPerFolder}\\s*$`);
    const observe = () => {
      const rows = document.querySelectorAll('.folder-file-row').length;
      state.__maximumTransferRows = Math.max(state.__maximumTransferRows ?? 0, rows);
      // Verify final state after backend completion, but timestamp the DOM
      // mutation itself. Dispatching this verification can be delayed by the
      // test driver; that delay must not become apparent rendering latency.
      // Unrelated DOM changes make this a conservative readiness bound.
      if (!backendDone || reported || rows !== requestedRows
        || document.querySelectorAll('.folder-file-state.complete').length !== requestedRows) return;
      rowsAt ??= lastDomUpdate;
      const folders = [...document.querySelectorAll('.folder-item-card')];
      if (!folders.length || folders.some(card =>
        card.querySelector('.folder-transfer-file-count')?.textContent?.includes('?')
        || card.querySelector('.item-data-state')?.textContent?.includes('cover loaded files only')
        || ![...card.querySelectorAll('.folder-transfer-file-count strong')].every(count =>
          countPattern.test(count.textContent ?? ''))
        || ![...card.querySelectorAll('.transfer-state')].every(status => status.textContent?.trim() === 'Complete'))) return;
      reported = true;
      void state.__recordDownloadDisplay({ rows: rowsAt, display: lastDomUpdate });
    };
    state.__downloadBackendCompleted = () => { backendDone = true; observe(); };
    observe();
    new MutationObserver(() => {
      lastDomUpdate = performance.timeOrigin + performance.now();
      observe();
    }).observe(document.body, {
      childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class'],
    });
  }, { requestedRows, filesPerFolder });
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Performance.enable');
  const before = await cdp.send('Performance.getMetrics');
  const started = performance.now();
  startedEpoch = performance.timeOrigin + started;
  await daemon.command('release-download');
  const completed = daemon.command(`wait-job-complete:${root.jobId}`).then(async () => {
    const elapsedMs = Math.round(performance.now() - started);
    await page.evaluate(() => (window as typeof window & { __downloadBackendCompleted: () => void }).__downloadBackendCompleted());
    return elapsedMs;
  });
  if (requestedRows > 500) {
    await expect(page.getByRole('button', { name: 'Load earlier downloads', exact: true })).toBeEnabled();
    const expanded = once(observations, `extent-${requestedRows}`, { signal: AbortSignal.timeout(10_000) });
    await page.getByRole('button', { name: 'Load earlier downloads', exact: true }).click();
    await expanded;
  }
  const backendCompletionMs = await completed;
  await expect(folder.locator('.folder-file-row')).toHaveCount(requestedRows);
  await expect(folder.locator('.folder-file-state.complete')).toHaveCount(requestedRows);
  const completedRowsMs = Math.round(performance.now() - started);
  // Completed rows alone are not a complete screen: every visible directory
  // must also have exact totals, even if it entered during an earlier read.
  await expect(folder.locator('.folder-transfer-file-count').filter({ hasText: '?' })).toHaveCount(0);
  await expect(folder.locator('.item-data-state').filter({ hasText: 'cover loaded files only' })).toHaveCount(0);
  // The moving prefix can change how many directories contain those rows.
  // Check every current label without freezing a separately sampled card count.
  await expect(folder.locator('.folder-transfer-file-count strong')
    .filter({ hasNotText: /^\s*(?:\d+\/)?200\s*$/ })).toHaveCount(0);
  await expect(folder.locator('.transfer-state').filter({ hasNotText: /^\s*Complete\s*$/ })).toHaveCount(0);
  const folderNames = await folder.locator('.folder-transfer-name').allTextContents();
  expect(folderNames.length).toBeGreaterThan(0);
  expect(new Set(folderNames).size, 'A directory run must appear only once, even while totals refresh.').toBe(folderNames.length);
  expect(folderNames.length).toBeLessThanOrEqual(folderCount);
  expect(folderNames.every(name => /^Batch\d{2}$/.test(name.trim()))).toBe(true);
  const displayedMs = Math.round(performance.now() - started);
  await displayRecorded;
  const maximumRows = await page.evaluate(() => (window as typeof window & { __maximumTransferRows: number }).__maximumTransferRows);
  expect(maximumRows, 'Live arrivals must preserve the requested extent.').toBeLessThanOrEqual(requestedRows);
  expect(globalTransferRows, 'Downloads progress comes from the fetched scope.').toBe(0);
  expect(scopedLiveRows).toBeGreaterThan(0);
  expect(unrequestedLiveRows, 'Progress must stay within the fetched observation membership.').toBe(0);
  const after = await cdp.send('Performance.getMetrics');
  const renderer = Object.fromEntries(['ScriptDuration', 'TaskDuration', 'LayoutDuration', 'JSHeapUsedSize']
    .map(name => [name, after.metrics.find(metric => metric.name === name)!.value
      - before.metrics.find(metric => metric.name === name)!.value]));
  for (const width of [390, 900, 1280]) {
    await page.setViewportSize({ width, height: 720 });
    await expectTransferLayoutUnchanged(page);
  }
  const lastFile = folder.locator('.folder-file-row').last();
  await lastFile.scrollIntoViewIfNeeded();
  const lastAction = lastFile.getByRole('button');
  await lastAction.focus();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Shift+Tab');
  await expect(lastAction).toBeFocused();
  await expect(lastAction).toHaveCSS('opacity', '1');
  await expect(lastAction).toHaveCSS('pointer-events', 'auto');
  await page.screenshot({ path: testInfo.outputPath('paged-active-download.png') });
  const expanded = once(observations, `extent-${requestedRows + 500}`, { signal: AbortSignal.timeout(5_000) });
  await page.getByRole('button', { name: 'Load earlier downloads', exact: true }).click();
  await expect(folder.locator('.folder-file-row')).toHaveCount(requestedRows + 500);
  expect(await folder.count()).toBeLessThanOrEqual(folderCount);
  expect(new Set(await folder.locator('.folder-file-path').allTextContents()).size).toBe(requestedRows + 500);
  await expanded;
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(500);
  expect(await folder.count()).toBeLessThanOrEqual(folderCount);
  await expect(folder.locator('.folder-file-state.complete')).toHaveCount(500);
  await cdp.detach();
  const rootResponse = await page.request.get(`/api/jobs/${root.jobId}`);
  expect(rootResponse.ok(), await rootResponse.text()).toBe(true);
  const completedRoot = await rootResponse.json() as Schema<'JobDetailDto'>;
  expect(completedRoot.summary.terminalOutcome).toBe('Succeeded');
  expect([...globalJobIds], 'A Downloads overview must receive roots, never all downloaded child jobs.').toEqual([root.jobId]);
  const outputFiles = (await readdir(outputDirectory, { recursive: true })).filter(name => name.endsWith('.bin')).sort();
  expect(outputFiles).toEqual(expectedFiles.sort());
  const sizes = await Promise.all(outputFiles.map(name => stat(join(outputDirectory, name))));
  expect(sizes.every(file => file.size === 128)).toBe(true);
  expect(summaryReads, 'Observed rows and totals must not need a separate HTTP reconciliation.').toHaveLength(0);
  console.log('PERF_ACTIVE_DOWNLOADS ' + JSON.stringify({ files: outputFiles.length, folderCount, filesPerFolder, requestedRows, maximumRows,
    backendCompletionMs, completedRowsMs, displayedMs, completedRowsDomMs, displayedDomMs,
    globalTransferRows, globalJobRows, scopedLiveRows, socketBytes, summaryReads, scopedSummaryUpdates, scopedSummaryGroups, renderer }));
});
