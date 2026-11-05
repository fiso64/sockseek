import { readFile, readdir, rm } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import type { Page } from '@playwright/test';
import { test, expect, measureApi } from './fixtures';
import type { Schema } from '../src/api/types';

test.use({ trace: 'off', daemonLogMetrics: true });

// Retirement, rather than a timer or a terminal live delta, establishes that the
// next detail read must survive the engine-to-history handoff.
function observeRetirement(page: Page, jobId: string): Promise<void> {
  return new Promise(resolve => page.on('websocket', socket => socket.on('framereceived', ({ payload }) => {
    for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
      const message = JSON.parse(frame) as { target?: string; arguments?: Schema<'StateUpdateBatchDto'>[] };
      if (message.target?.toLowerCase() === 'stateupdatebatch'
        && message.arguments?.[0]?.state?.removedJobIds.includes(jobId)) resolve();
    }
  })));
}

test('retains 100-candidate Song and aggregate fallback without leaking peer penalties across workflows @Load', async ({ page, daemon }, testInfo) => {
  test.setTimeout(90_000);
  await daemon.command('long-song-fallback');
  const expectedPeers = Array.from({ length: 100 }, (_, index) => `Péér ${String(index).padStart(3, '0')}`);
  const expectedPath = (index: number) => `共有\\Option ${String(index).padStart(3, '0')}\\Artist - Song.mp3`;
  const measurements: { mode: string; acceptanceMs: number; fallbackUiMs: number;
    detailMs: number; timelineMs: number; attemptsMs: number[]; rawApiMs: number[];
    browserDetailReads: number; childPageReads: number }[] = [];
  const workflowIds = new Set<string>();

  for (const aggregate of [false, true]) {
    const mode = aggregate ? 'aggregate' : 'song';
    const output = testInfo.outputPath(`${mode}-output`);
    await daemon.command('hold-download');
    const started = performance.now();
    const response = await measureApi(daemon.url, aggregate ? '/api/jobs/aggregate/tracks' : '/api/jobs/downloads/song', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
        songQuery: { title: 'Song', artist: 'Artist', artistMaybeWrong: false },
        options: { outputParentDir: output, downloadSettings: {
          search: { minSharesAggregate: 1 },
          skip: { skipExisting: false },
          transfer: { maxDownloadRetries: 100, unknownErrorRetries: 0 },
        } },
      } satisfies Schema<'SubmitSongJobRequestDto'>) });
    const acceptanceMs = response.elapsedMs;
    expect(response.status, response.body).toBe(202);
    const root = JSON.parse(response.body) as Schema<'JobSummaryDto'>;
    expect(workflowIds.has(root.workflowId!)).toBe(false);
    workflowIds.add(root.workflowId!);
    const retired = observeRetirement(page, root.jobId!);
    const browserDetailReads: string[] = [];
    const childPages: URLSearchParams[] = [];
    const onRequest = (request: import('@playwright/test').Request) => {
      const url = new URL(request.url()), path = url.pathname;
      if (request.method() === 'GET' && /^\/api\/jobs\/[^/]+$/.test(path)) browserDetailReads.push(path);
      if (request.method() === 'GET' && path === '/api/jobs' && url.searchParams.get('parentJobId') === root.jobId)
        childPages.push(url.searchParams);
    };
    page.on('request', onRequest);
    await page.goto(`/jobs/${root.jobId}`);
    if (aggregate) {
      await expect(page.locator('.job-child-row')).toHaveCount(1);
      await page.locator('.job-child-open').click();
    }
    const songId = new URL(page.url()).pathname.split('/').at(-1)!;
    const card = page.locator('.file-item-card');
    await expect(card.getByLabel('50% complete', { exact: true })).toBeVisible();
    await expect(page.locator('.job-source-summary')).toContainText('Péér 099');
    await expect(card).toContainText('Artist - Song.mp3');
    await expect(card.locator('.file-transfer-audio')).toHaveText('320 kbps');
    await expect(card.locator('.file-transfer-length')).toHaveText('3:00');
    const fallbackUiMs = Math.round(performance.now() - started);

    const activeResponse = await page.request.get(`/api/jobs/${songId}`);
    expect(activeResponse.ok(), await activeResponse.text()).toBe(true);
    const active = await activeResponse.json() as Schema<'JobDetailDto'>;
    expect(active.payload).toMatchObject({ kind: 'song', candidateCount: 100 });
    expect(active.summary.lifecycleState).not.toBe('Terminal');
    const activeTransfersResponse = await page.request.get('/api/transfers?direction=download&limit=500');
    expect(activeTransfersResponse.ok(), await activeTransfersResponse.text()).toBe(true);
    const activeTransfers = (await activeTransfersResponse.json() as Schema<'TransferTimelinePageDto'>).items
      .filter(item => item.identity.workflowId === root.workflowId);
    expect(activeTransfers).toHaveLength(100);
    expect(activeTransfers.filter(item => item.status.terminalOutcome === 'Failed')).toHaveLength(99);
    expect(activeTransfers.find(item => item.identity.username === 'Péér 099')).toMatchObject({
      transferredBytes: 512, totalBytes: 1_024,
    });

    const rawApiMs: number[] = [];
    async function readRawSource() {

      const response = await measureApi(daemon.url, `/api/jobs/${root.jobId}/raw?afterSequence=0&limit=500`);
      expect(response.ok, response.body).toBe(true);
      const rows = JSON.parse(response.body) as Schema<'SearchRawResultDto'>[];
      rawApiMs.push(response.elapsedMs);
      expect(rows).toHaveLength(100);
      expect(response.headers.get('x-next-sequence')).toBeNull();
      expect(rows.map(row => row.username)).toEqual(expectedPeers);
      expect(rows.map(row => row.filename)).toEqual(expectedPeers.map((_, index) => expectedPath(index)));
      expect(rows.every(row => row.visibility === 'Public' && row.size === 1_024
        && row.bitRate === 320 && row.length === 180)).toBe(true);
      // The durable store records instants at millisecond precision.
      return rows.map(row => ({ ...row, observedAtUtc: row.observedAtUtc ? Date.parse(row.observedAtUtc) : null }));
    }
    const liveRaw = await readRawSource();

    await daemon.command('report-fallback-downloads');
    const report = daemon.logs().reverse().find(line => line.startsWith('E2E_FALLBACK_DOWNLOADS '))!;
    expect(JSON.parse(report.slice('E2E_FALLBACK_DOWNLOADS '.length))).toEqual(
      aggregate ? [...expectedPeers, ...expectedPeers] : expectedPeers);

    await expect(page.getByLabel('Connection status').locator('strong')).toContainText('Connected');
    await daemon.command('release-download');
    await retired;
    await expect(page.locator('.job-detail-heading-summary > .search-status-badge')).toHaveCount(0);
    await expect(card.locator('.transfer-state')).toHaveText('Complete');
    await page.reload();
    await expect(page.locator('.job-source-summary')).toContainText('Péér 099');
    await expect(card.locator('.transfer-state')).toHaveText('Complete');
    await expect(card.locator('.file-transfer-audio')).toHaveText('320 kbps');
    await expect(card.locator('.file-transfer-length')).toHaveText('3:00');


    const retainedResponse = await measureApi(daemon.url, `/api/jobs/${songId}`);
    expect(retainedResponse.ok, retainedResponse.body).toBe(true);
    const retained = JSON.parse(retainedResponse.body) as Schema<'JobDetailDto'>;
    const detailMs = retainedResponse.elapsedMs;
    expect(retained.summary).toMatchObject({ lifecycleState: 'Terminal', terminalOutcome: 'Succeeded' });
    expect(retained.payload).toMatchObject({ kind: 'song', candidateCount: 100,
      resolvedUsername: 'Péér 099', resolvedFilename: expectedPath(99), resolvedSize: 1_024 });
    expect(await readRawSource()).toEqual(liveRaw);

    if (aggregate) {
      const aggregateResponse = await page.request.get(`/api/jobs/${root.jobId}`);
      expect(aggregateResponse.ok(), await aggregateResponse.text()).toBe(true);
      expect((await aggregateResponse.json() as Schema<'JobDetailDto'>).payload).toMatchObject({
        kind: 'aggregate', songCount: 1, completedSongCount: 1, succeededSongCount: 1, failedSongCount: 0,
      });
      await page.goto(`/jobs/${root.jobId}`);
      await expect(page.locator('.job-run-progress-copy')).toContainText('1 of 1 complete');
      await expect(page.locator('.job-child-row')).toHaveCount(1);
      await expect(page.locator('.job-child-copy small')).toHaveText(/^Song ·/);
      await expect(page.locator('.job-child-row .search-status-badge')).toHaveText('Complete');
    }

    const timelineResponse = await measureApi(daemon.url, '/api/transfers?direction=download&limit=500');
    expect(timelineResponse.ok, timelineResponse.body).toBe(true);
    const timeline = JSON.parse(timelineResponse.body) as Schema<'TransferTimelinePageDto'>;
    const timelineMs = timelineResponse.elapsedMs;
    const transfers = timeline.items.filter(item => item.identity.workflowId === root.workflowId);
    expect(transfers).toHaveLength(100);
    expect(new Set(transfers.map(item => item.transferId)).size).toBe(100);
    expect(transfers.every(item => item.identity.jobId === songId && item.status.attemptCount === 1)).toBe(true);
    expect(transfers.map(item => item.identity.username).sort()).toEqual(expectedPeers);
    for (let index = 0; index < 100; index++) {
      const transfer = transfers.find(item => item.identity.username === expectedPeers[index])!;
      expect(transfer).toMatchObject({ remotePath: expectedPath(index),
        terminalOutcome: index === 99 ? 'Succeeded' : 'Failed',
        transferredBytes: index === 99 ? 1_024 : 0 });
    }
    const attemptsMs: number[] = [];
    // Exhaustive test-only history reads; the UI requests no per-candidate details.
    for (let index = 0; index < 100; index++) {
      const transfer = transfers.find(item => item.identity.username === expectedPeers[index])!;

      const attemptsResponse = await measureApi(daemon.url, `/api/transfers/${transfer.transferId}/attempts?afterAttemptNumber=0&limit=500`);
      expect(attemptsResponse.ok, attemptsResponse.body).toBe(true);
      const attempts = JSON.parse(attemptsResponse.body) as Schema<'TransferAttemptHistoryDto'>[];
      attemptsMs.push(attemptsResponse.elapsedMs);
      expect(attempts).toHaveLength(1);
      expect(attempts[0]).toMatchObject({ transferId: transfer.transferId, attemptNumber: 1,
        sourceUsername: expectedPeers[index], sourcePath: expectedPath(index),
        state: index === 99 ? 'Completed' : 'Failed', failureReason: index === 99 ? 'None' : 'PeerFailure' });
      expect(attempts[0]!.completedAtUtc).toBeTruthy();
    }
    const successful = transfers.find(item => item.status.terminalOutcome === 'Succeeded')!;
    expect(await readFile(successful.status.localPath!)).toEqual(Buffer.alloc(1_024));
    const files = await readdir(output, { recursive: true, withFileTypes: true });
    expect(files.filter(entry => entry.isFile())).toHaveLength(1);
    // Only semantic jobs need detail reads; the execution wrapper needs none.
    // Count timer-driven reconciliations as measurements, not a wall-clock-sensitive budget.
    expect(new Set(browserDetailReads)).toEqual(new Set([root.jobId, songId].map(id => `/api/jobs/${id}`)));
    if (aggregate) {
      expect(childPages.length).toBeGreaterThan(0);
      expect(childPages.every(query => query.get('kind') === 'song' && query.get('limit') === '100')).toBe(true);
    } else expect(childPages).toHaveLength(0);
    page.off('request', onRequest);
    measurements.push({ mode, acceptanceMs, fallbackUiMs, detailMs, timelineMs, attemptsMs, rawApiMs,
      browserDetailReads: browserDetailReads.length, childPageReads: childPages.length });
    const screenshot = testInfo.outputPath(`${mode}-retained.png`);
    await page.screenshot({ path: screenshot, animations: 'disabled' });
    await testInfo.attach(`${mode} retained job`, { path: screenshot, contentType: 'image/png' });
    await rm(output, { recursive: true });
  }
  console.log('PERF_SONG_FALLBACK ' + JSON.stringify(measurements));
  await testInfo.attach('fallback-measurements', { body: JSON.stringify(measurements), contentType: 'application/json' });
  for (const measurement of measurements) {
    expect(measurement.detailMs, `${measurement.mode} retained detail`).toBeLessThan(100);
    expect(measurement.timelineMs, `${measurement.mode} retained timeline`).toBeLessThan(100);
    expect(Math.max(...measurement.attemptsMs), `${measurement.mode} durable attempts`).toBeLessThan(100);
    expect(Math.max(...measurement.rawApiMs), `${measurement.mode} live/retained raw source`).toBeLessThan(100);
  }
});
