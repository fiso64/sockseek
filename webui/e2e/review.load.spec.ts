import { performance } from 'node:perf_hooks';
import { stat } from 'node:fs/promises';
import { test, expect, measureApi } from './fixtures';
import type { Schema } from '../src/api/types';

test.use({ trace: 'off', daemonLogMetrics: true, daemonApplicationLogs: true });

for (const allExcept of [false, true]) {
test(`reviews 10k CSV rows and durably accepts ${allExcept ? 'all except one' : 'one explicit choice'} @Load`, async ({ page, daemon }, testInfo) => {
  test.setTimeout(180_000);
  await daemon.command('music-search');
  if (allExcept) await daemon.command('staged-search');
  const requests: { method: string; path: string; elapsedMs: number }[] = [];
  const jobReads: { path: string; startedAt: number; elapsedMs: number }[] = [];
  const childPages: { query: string; count: number; first?: number | string; last?: number | string; nextCursor?: string; error?: string }[] = [];
  page.on('response', response => {
    const url = new URL(response.url());
    if (url.pathname !== '/api/jobs' || !url.searchParams.has('parentJobId')) return;
    void response.json().then((items: Schema<'JobSummaryDto'>[]) => {
      childPages.push({ query: url.search, count: items.length, first: items[0]?.displayId,
        last: items.at(-1)?.displayId, nextCursor: response.headers()['x-next-cursor'] });
    }).catch(error => childPages.push({ query: url.search, count: 0, error: String(error) }));
  });
  page.on('requestfinished', request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/job-previews'))
      requests.push({ method: request.method(), path: url.pathname + url.search,
        elapsedMs: Math.round(request.timing().responseEnd) });
    if (request.method() === 'GET' && (url.pathname === '/api/jobs' || url.pathname.startsWith('/api/jobs/')))
      jobReads.push({ path: url.pathname + url.search, startedAt: request.timing().startTime,
        elapsedMs: Math.round(request.timing().responseEnd) });
  });
  await page.goto('/jobs');
  await page.getByRole('button', { name: 'New job', exact: false }).click();
  await page.getByRole('button', { name: 'CSV file', exact: true }).click();
  await page.locator('input[type=file]').setInputFiles({
    name: 'large-review.csv', mimeType: 'text/csv',
    buffer: Buffer.from(['artist,title', 'Fixture,Fixture song',
      ...Array.from({ length: 9_999 }, (_, index) => `Fixture,Review Item ${String(index + 1).padStart(5, '0')}`), ''].join('\n')),
  });
  // Measure display from the click itself; locator assertions remain watchdogs.
  const previewTiming = await page.evaluateHandle(() => {
    let clickedAt: number | null = null;
    const timing = { readyDomMs: null as number | null, treeDomMs: null as number | null };
    let displayed!: () => void;
    const displayReady = new Promise<void>(resolve => { displayed = resolve; });
    function clicked(event: MouseEvent) {
      if ((event.target as Element)?.closest('button')?.textContent?.trim() === 'Review')
        clickedAt ??= performance.now();
    }
    document.addEventListener('click', clicked, true);
    const observer = new MutationObserver(() => {
      if (clickedAt === null) return;
      const start = document.querySelector<HTMLButtonElement>('.new-job-start-button');
      if (start && !start.disabled && start.textContent?.trim() === 'Start 10000 jobs')
        timing.readyDomMs ??= Math.round(performance.now() - clickedAt);
      if (document.querySelectorAll('.job-preview-row').length === 102)
        timing.treeDomMs ??= Math.round(performance.now() - clickedAt);
      if (timing.readyDomMs !== null && timing.treeDomMs !== null) displayed();
    });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['disabled'] });
    return { waitUntilDisplayed: () => displayReady, finish: () => {
      observer.disconnect();
      document.removeEventListener('click', clicked, true);
      return { ...timing, resources: (performance.getEntriesByType('resource') as PerformanceResourceTiming[])
        .filter(entry => entry.startTime >= (clickedAt ?? Infinity) && new URL(entry.name).pathname.startsWith('/api/job-previews'))
        .map(entry => ({ path: new URL(entry.name).pathname + new URL(entry.name).search,
          startMs: Math.round(entry.startTime - clickedAt!), endMs: Math.round(entry.responseEnd - clickedAt!) })) };
    } };
  });
  const previewStart = performance.now();
  const createdResponse = page.waitForResponse(response => response.ok()
    && response.request().method() === 'POST'
    && new URL(response.url()).pathname === '/api/job-previews');
  await page.getByRole('button', { name: 'Review', exact: true }).click();
  const created = await (await createdResponse).json() as Schema<'CreateJobPreviewResponseDto'>;
  await previewTiming.evaluate(timing => timing.waitUntilDisplayed());
  await expect(page.getByRole('button', { name: 'Start 10000 jobs', exact: true })).toBeEnabled({ timeout: 120_000 });
  await expect(page.locator('.job-preview-row')).toHaveCount(102);
  // Includes Playwright actionability/assertion waiting. The DOM markers below
  // measure the user's wait and belong in the canonical performance report.
  const previewObservedMs = Math.round(performance.now() - previewStart);
  const previewDisplay = await previewTiming.evaluate(timing => timing.finish());
  await previewTiming.dispose();
  expect(previewDisplay.readyDomMs).not.toBeNull();
  expect(previewDisplay.treeDomMs).not.toBeNull();
  console.log('PERF_REVIEW_DISPLAY ' + JSON.stringify({ allExcept, ...previewDisplay }));
  const readinessReads = requests.filter(request => request.method === 'GET' && !request.path.includes('/nodes?'));
  expect(readinessReads).toHaveLength(1);
  expect(readinessReads[0]!.path).toContain('waitForCompletion=true');
  const nodeReads = requests.filter(request => request.method === 'GET' && request.path.includes('/nodes?'));
  expect(nodeReads).toHaveLength(3); // Extract -> List -> first 100 leaves.
  expect(new Set(nodeReads.map(request => request.path)).size).toBe(3);
  const screenshot = testInfo.outputPath('large-review-first-page.png');
  await page.screenshot({ path: screenshot });
  await testInfo.attach('paged 10k CSV Review', { path: screenshot, contentType: 'image/png' });

  const firstSong = page.locator('.job-preview-row').filter({ hasText: 'Fixture song' });
  await firstSong.scrollIntoViewIfNeeded();
  const treeScreenshot = testInfo.outputPath('large-review-leaves.png');
  await page.screenshot({ path: treeScreenshot });
  await testInfo.attach('10k Review first leaf page', { path: treeScreenshot, contentType: 'image/png' });
  if (allExcept) await firstSong.locator('input[type=checkbox]').uncheck();
  else {
    await page.getByRole('button', { name: 'Deselect all', exact: true }).click();
    await firstSong.locator('input[type=checkbox]').check();
  }
  const expectedCount = allExcept ? 9_999 : 1;
  await expect(page.getByRole('button', { name: `Start ${expectedCount} ${allExcept ? 'jobs' : 'job'}`, exact: true })).toBeEnabled();
  const acceptedResponse = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/job-previews/${created.preview.previewId}/commit`);
  const commitStart = performance.now();
  await page.locator('.new-job-start-button').click();
  const accepted = await acceptedResponse;
  expect(accepted.ok(), await accepted.text()).toBe(true);
  const commitUiMs = Math.round(performance.now() - commitStart);
  const commitApiMs = Math.round(accepted.request().timing().responseEnd);
  const body = accepted.request().postDataJSON() as Schema<'CommitJobPreviewRequestDto'>;
  expect(body.selection).toEqual({ mode: allExcept ? 'AllExcept' : 'Only', refs: [] });
  expect(body.subtreeOverrides).toHaveLength(1);
  expect(body.subtreeOverrides![0]!.selected).toBe(!allExcept);
  expect(Buffer.byteLength(JSON.stringify(body))).toBeLessThan(1_000);
  const receipt = await accepted.json() as Schema<'CommitJobPreviewResponseDto'>;
  console.log('PERF_REVIEW_ACCEPTED ' + JSON.stringify({ allExcept, previewObservedMs,
    previewReadyDomMs: previewDisplay.readyDomMs, previewTreeDomMs: previewDisplay.treeDomMs, commitUiMs, commitApiMs,
    receipt, requests, daemonMetrics: daemon.logs().filter(line => line.includes('Job Preview ') || line.includes('Submission commit ')) }));
  if (allExcept) expect(commitApiMs, 'Accepting 10k frozen commands must not expand identical settings into tens of MiB.')
    .toBeLessThan(1_000);
  expect(receipt.requestedCount).toBe(expectedCount);
  expect(receipt.resolvedCount).toBe(expectedCount);
  expect(receipt.submittedCount).toBe(expectedCount);
  expect(receipt.rejectedCount).toBe(0);
  expect(receipt.rejectionReasons).toEqual([]);
  expect(receipt.submissionId).toBeTruthy();
  let acceptedRootJobId: string | null = null;
  try {
    // The preview is no longer public; retry must use the durable receipt,
    // independently of when maintenance reclaims the temporary spool rows.
    expect((await page.request.get(`/api/job-previews/${created.preview.previewId}`)).status()).toBe(404);
    const retry = await measureApi(daemon.url, `/api/job-previews/${created.preview.previewId}/commit`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    });
    const retryMs = retry.elapsedMs;
    expect(retry.ok, retry.body).toBe(true);
    expect(retryMs, 'A retained idempotent receipt is a bounded read, not another plan commit.').toBeLessThan(100);
    expect(JSON.parse(retry.body)).toEqual(receipt);
    const conflict = await page.request.post(`/api/job-previews/${created.preview.previewId}/commit`, {
      data: { ...body, subtreeOverrides: [] },
    });
    expect(conflict.status()).toBe(409);
    console.log('PERF_REVIEW_PHASE ' + JSON.stringify({ allExcept, phase: 'opening accepted detail' }));
    const root = page.locator('.search-history-open').filter({ hasText: 'large-review.csv' }).first();
    await root.evaluate(element => element.addEventListener('click',
      () => performance.mark('review-detail-start'), { once: true }));
    const detailReady = page.waitForFunction(count => {
      const start = performance.getEntriesByName('review-detail-start').at(-1);
      if (start && document.querySelector('.automatic-job-detail h1')?.textContent === 'large-review'
        && document.querySelectorAll('.job-child-row').length === count)
        return { elapsedMs: performance.now() - start.startTime };
    }, Math.min(expectedCount, 100));
    await expect(root).toBeVisible();
    await root.click();
    const detailTiming = await detailReady;
    const detailUiMs = (await detailTiming.jsonValue())!.elapsedMs;
    await detailTiming.dispose();
    await expect(page.locator('.automatic-job-detail h1')).toHaveText('large-review');
    const selectedJobId = new URL(page.url()).pathname.split('/').at(-1);
    acceptedRootJobId = selectedJobId ?? null;
    const detailResponse = await measureApi(daemon.url, `/api/jobs/${selectedJobId}`);
    const detailApiMs = detailResponse.elapsedMs;
    expect(detailResponse.ok, detailResponse.body).toBe(true);
    expect(detailApiMs, 'Accepted root metadata must not scan its selected descendants.').toBeLessThan(100);
    const detail = JSON.parse(detailResponse.body) as Schema<'JobDetailDto'>;
    expect(detail.payload).toMatchObject({ kind: 'job-list', count: expectedCount });
    await expect(page.locator('.job-child-row')).toHaveCount(Math.min(expectedCount, 100));
    console.log('PERF_REVIEW_PHASE ' + JSON.stringify({ allExcept, phase: 'accepted detail visible', elapsedMs: detailUiMs, jobReads }));
    if (allExcept) expect(detailUiMs, 'The accepted plan must appear without waiting for a one-second detail polling tick.')
      .toBeLessThan(1_000);
    const submissionsResponse = await measureApi(daemon.url, '/api/submissions?archived=false&limit=100');
    expect(submissionsResponse.ok, submissionsResponse.body).toBe(true);
    const submissionsBody = submissionsResponse.body;
    const submissions = JSON.parse(submissionsBody) as Schema<'SubmissionSummaryDto'>[];
    const submissionsApiMs = submissionsResponse.elapsedMs;
    expect(submissionsApiMs, 'Submission summaries must not hydrate complete accepted plans.').toBeLessThan(100);
    expect(Array.isArray(submissions)).toBe(true);
    expect(submissions).toHaveLength(1);
    expect(submissions[0]).toMatchObject({ submissionId: receipt.submissionId,
      previewId: created.preview.previewId });
    expect(Buffer.byteLength(submissionsBody)).toBeLessThan(2_000);
    const status = await measureApi(daemon.url, '/api/server/status');
    expect(status.ok, status.body).toBe(true);
    const { persistence } = JSON.parse(status.body) as { persistence: {
      state: string; permanentlyFailedMutationCount: number;
    } };
    expect(persistence.state).toBe('Healthy');
    expect(persistence.permanentlyFailedMutationCount).toBe(0);
    const statusApiMs = status.elapsedMs;
    expect(statusApiMs, 'Health is a bounded snapshot even with 10k accepted jobs.').toBeLessThan(100);
    if (!allExcept) {
      await page.goto('/downloads');
      await expect(page.locator('.file-item-card')).toHaveCount(1);
      await expect(page.locator('.file-item-card').getByRole('button', { name: 'Complete', exact: true })).toHaveClass(/folder-file-state/);
      const response = await page.request.get('/api/transfers?direction=download&archived=false&limit=100');
      const transfers = (await response.json() as Schema<'TransferTimelinePageDto'>).items;
      expect(transfers).toHaveLength(1);
      expect(transfers[0]!.identity.username).toBe('fixture-peer');
      expect(transfers[0]!.identity.remotePath).toBe('Fixture\\Fixture song.flac');
      expect(transfers[0]!.status.terminalOutcome).toBe('Succeeded');
      expect((await stat(transfers[0]!.status.localPath!)).size).toBe(128);
    }
    const daemonMetrics = daemon.logs().filter(line => line.includes('Job Preview ') || line.includes('Submission commit ')
      || line.startsWith('E2E_METRIC persistence-commit '));
    expect(daemonMetrics.some(line => line.includes(`Job Preview ${created.preview.previewId} completed with outcome Ready`))).toBe(true);
    expect(daemonMetrics.some(line => line.includes(`Submission commit ${body.idempotencyKey} completed in`))).toBe(true);
    console.log('PERF_REVIEW ' + JSON.stringify({ allExcept, previewObservedMs,
      previewReadyDomMs: previewDisplay.readyDomMs, previewTreeDomMs: previewDisplay.treeDomMs, commitUiMs, commitApiMs,
      retryMs, detailUiMs, detailApiMs, submissionsApiMs, submissionsBytes: Buffer.byteLength(submissionsBody),
      statusApiMs, expectedCount, commitBytes: Buffer.byteLength(JSON.stringify(body)), persistence, requests,
      daemonMetrics }));
  } finally {
    if (allExcept) {
      // Cancellation after acceptance alone can run before any child starts.
      // Wait for the actual held leaf search, never for a guessed delay.
      await daemon.command('wait-search-started');
      const queuedRoot = await measureApi(daemon.url, `/api/jobs/${acceptedRootJobId}`);
      expect(queuedRoot.ok, queuedRoot.body).toBe(true);
      const queuedDetail = JSON.parse(queuedRoot.body) as Schema<'JobDetailDto'>;
      expect(queuedDetail.childCount, 'Known members must be visible while the first execution window is held.').toBe(expectedCount);
      const queuedRootApiMs = queuedRoot.elapsedMs;
      expect(queuedRootApiMs).toBeLessThan(100);
      const queuedPage = await measureApi(daemon.url, `/api/jobs?parentJobId=${acceptedRootJobId}&archived=false&includeAll=false&limit=100`);
      expect(queuedPage.ok, queuedPage.body).toBe(true);
      const queuedJobs = JSON.parse(queuedPage.body) as Schema<'JobSummaryDto'>[];
      const queuedPageApiMs = queuedPage.elapsedMs;
      expect(queuedPageApiMs).toBeLessThan(100);
      expect(queuedJobs).toHaveLength(100);
      expect(queuedPage.headers.get('x-next-cursor')).toBeTruthy();
      const queuedChild = queuedJobs[31]!;
      expect(queuedChild.lifecycleState).toBe('Pending');
      console.log('PERF_REVIEW_QUEUED_READ ' + JSON.stringify({ queuedRootApiMs, queuedPageApiMs,
        childCount: queuedDetail.childCount }));
      try {
        await page.getByRole('button', { name: 'Load more', exact: true }).click();
        await expect(page.locator('.job-child-row')).toHaveCount(200);
      } finally {
        console.log('PERF_REVIEW_PAGING ' + JSON.stringify({ jobReads, childPages }));
      }
      const queuedRow = page.locator('.job-child-row').nth(31);
      await queuedRow.scrollIntoViewIfNeeded();
      const queuedScreenshot = testInfo.outputPath('queued-members.png');
      await page.screenshot({ path: queuedScreenshot, animations: 'disabled' });
      await testInfo.attach('members beyond the active execution window', { path: queuedScreenshot, contentType: 'image/png' });
      const queuedCancellation = page.waitForResponse(response => response.request().method() === 'POST'
        && new URL(response.url()).pathname === `/api/jobs/${queuedChild.jobId}/cancel`);
      await queuedRow.hover();
      await queuedRow.getByRole('button', { name: /^Cancel / }).click();
      expect((await queuedCancellation).status()).toBe(202);
      console.log('PERF_REVIEW_QUEUED ' + JSON.stringify({ declaredChildren: expectedCount,
        visibleChildren: queuedDetail.childCount, queuedRootApiMs, queuedPageApiMs,
        displayedRows: 200, cancelledQueuedJobId: queuedChild.jobId }));
      const running = await page.request.get(`/api/jobs?workflowId=${receipt.workflowId}&includeAll=true&archived=false&limit=100`);
      expect(running.ok(), await running.text()).toBe(true);
      const jobs = await running.json() as Schema<'JobSummaryDto'>[];
      const runningSongs = jobs.filter(job => job.kind === 'song' && job.lifecycleState === 'Running');
      expect(runningSongs.length).toBeGreaterThan(0);
      expect(runningSongs.some(job => job.activityPhase === 'Searching')).toBe(true);
      console.log('PERF_REVIEW_PHASE ' + JSON.stringify({ allExcept, phase: 'cancelling accepted workflow',
        expectedCount, runningSongCount: runningSongs.length }));
      // Install before the request. Locator assertions retry at up to one-
      // second intervals: useful watchdogs, but not a precise display clock.
      const terminalTiming = await page.evaluateHandle(() => {
        const started = performance.now();
        const timing = { elapsedMs: null as number | null, stop: () => observer.disconnect() };
        const observer = new MutationObserver(() => {
          if (document.querySelector('.job-detail-heading-summary > .search-status-badge')?.textContent?.trim() === 'Cancelled') {
            timing.elapsedMs = performance.now() - started;
            observer.disconnect();
          }
        });
        observer.observe(document.body, { childList: true, subtree: true, characterData: true });
        return timing;
      });
      const cancellationStart = performance.now();
      const cancelled = await measureApi(daemon.url, `/api/workflows/${receipt.workflowId}/cancel`, { method: 'POST' });
      const cancellationMs = cancelled.elapsedMs;
      console.log('PERF_REVIEW_CANCEL ' + JSON.stringify({
        cancellationMs,
        response: JSON.parse(cancelled.body),
        daemonMetrics: daemon.logs().filter(line => line.includes(`Workflow cancellation ${receipt.workflowId}`)),
      }));
      expect(cancelled.status).toBe(202);
      // Coarse opt-in regression guard, not an acceptable normal-operation target.
      expect(cancellationMs, 'Cancelling a held 10k-job plan must not block for minutes.').toBeLessThan(5_000);
      // A fast receipt alone can hide a slow synchronous drain on another
      // continuation. Await the real root's terminal display and exact outcome
      // counts too; container completion follows child/output finalization.
      expect(acceptedRootJobId).toBeTruthy();
      let finalDetail!: Schema<'JobDetailDto'>;
      let retainedRootApiMs = 0;
      let rootTerminalPublishedMs: number | null = null;
      try {
        await expect(page.locator('.job-detail-heading-summary > .search-status-badge'))
          .toHaveText('Cancelled', { timeout: 15_000 });
      } finally {
        rootTerminalPublishedMs = await terminalTiming.evaluate(timing => {
          timing.stop();
          return timing.elapsedMs;
        });
        await terminalTiming.dispose();
        // Diagnose backend completion independently even when the UI is stale.
        const finalDetailResponse = await measureApi(daemon.url, `/api/jobs/${acceptedRootJobId}`);
        expect(finalDetailResponse.ok, finalDetailResponse.body).toBe(true);
        finalDetail = JSON.parse(finalDetailResponse.body) as Schema<'JobDetailDto'>;
        retainedRootApiMs = finalDetailResponse.elapsedMs;
        expect(retainedRootApiMs, 'Retained root metadata must remain a bounded read after 10k cancellations.').toBeLessThan(100);
        console.log('PERF_REVIEW_CANCEL_ROOT ' + JSON.stringify({ retainedRootApiMs,
          lifecycle: finalDetail.summary.lifecycleState, outcome: finalDetail.summary.terminalOutcome,
          stats: finalDetail.summary.stats }));
      }
      const rootTerminalObservedMs = Math.round(performance.now() - cancellationStart);
      expect(rootTerminalPublishedMs, 'Observe the actual terminal DOM transition, not a later assertion poll.').not.toBeNull();
      const rootTerminalUiMs = Math.round(rootTerminalPublishedMs!);
      expect(finalDetail.summary).toMatchObject({ lifecycleState: 'Terminal', terminalOutcome: 'Cancelled' });
      expect(finalDetail.payload).toMatchObject({ kind: 'job-list', count: expectedCount,
        activeJobCount: 0, completedJobCount: expectedCount, succeededJobCount: 0, failedJobCount: expectedCount });
      expect(finalDetail.childCount).toBe(expectedCount);
      const retainedQueued = await page.request.get(`/api/jobs/${queuedChild.jobId}`);
      expect(retainedQueued.ok(), await retainedQueued.text()).toBe(true);
      expect((await retainedQueued.json() as Schema<'JobDetailDto'>).summary).toMatchObject({
        lifecycleState: 'Terminal', terminalOutcome: 'Cancelled', cancellationSource: 'UserRequestedJob',
      });

      const finalHealthResponse = await measureApi(daemon.url, '/api/server/status');
      expect(finalHealthResponse.ok, finalHealthResponse.body).toBe(true);
      const finalHealth = (JSON.parse(finalHealthResponse.body) as { persistence: {
        state: string; retryMutationCount: number; permanentlyFailedMutationCount: number;
      } }).persistence;
      expect(finalHealth.state).toBe('Healthy');
      expect(finalHealth.retryMutationCount).toBe(0);
      expect(finalHealth.permanentlyFailedMutationCount).toBe(0);
      console.log('PERF_REVIEW_CANCEL_DRAIN ' + JSON.stringify({ rootTerminalUiMs, rootTerminalObservedMs,
        retainedRootApiMs, healthApiMs: finalHealthResponse.elapsedMs,
        cancelledChildren: expectedCount, health: finalHealth.state,
        retryMutationCount: finalHealth.retryMutationCount,
        permanentlyFailedMutationCount: finalHealth.permanentlyFailedMutationCount }));
      expect(rootTerminalUiMs, 'The cancelled 10k-job root must drain and reach the UI promptly, not just acknowledge quickly.')
        .toBeLessThan(5_000);
    }
  }
});
}
