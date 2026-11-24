import { test, expect, deferInitialDaemonSnapshot, expectFolderLayoutUnchanged } from './fixtures';
import type { Schema } from '../src/api/types';

test('retries unavailable transfer history without presenting it as empty', async ({ page }) => {
  const direction = 'download';
  let interrupted = true;
  // Exercise explicit Retry independently of startup's automatic live recovery.
  // Otherwise recovery may correctly remove the alert between finding it and
  // clicking its Retry button.
  const releaseSnapshot = await deferInitialDaemonSnapshot(page);
  await page.route(url => url.pathname === '/api/transfers' || url.pathname === '/api/transfers/timeline', (route) => interrupted ? route.abort('failed') : route.continue());
  try {
    await page.goto(`/${direction}s`);
    const alert = page.getByRole('alert').filter({ hasText: 'Transfers unavailable' });
    await expect(alert).toBeVisible();
    await expect(page.getByText(`No ${direction}s`, { exact: true })).toHaveCount(0);
    interrupted = false;
    const loaded = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === '/api/transfers/timeline' && response.request().postDataJSON().direction.toLowerCase() === direction && response.ok();
    });
    await alert.getByRole('button', { name: 'Retry', exact: true }).click();
    await loaded;
    await expect(alert).toHaveCount(0);
    await expect(page.getByText(`No ${direction}s`, { exact: true })).toBeVisible();

  } finally { releaseSnapshot(); }
  await expect(page.getByLabel('Connection status')).toContainText('Connected');
});

test('retries one refused file from Downloads and from its job', async ({ page, daemon }) => {
  const retryLookups: string[] = [];
  const detailLookups: string[] = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname.endsWith('/failure-detail')) detailLookups.push(url.pathname);
    if (url.pathname.endsWith('/repeat-options') || url.pathname === '/api/transfers' && url.searchParams.has('jobId'))
      retryLookups.push(url.pathname);
  });
  const { readFile } = await import('node:fs/promises');
  for (const surface of ['downloads', 'jobs']) {
    await daemon.command('reject-next-download');
    const filename = `Fixture\\${surface}.txt`;
    const submission = await page.request.post('/api/jobs/lists', { data: {
      name: 'Retry individual files', jobs: [{ kind: 'remote-file', target: {
        username: 'fixture-peer', filename, size: 128, extension: '.txt',
      } }],
    } satisfies Schema<'SubmitJobListRequestDto'> });
    expect(submission.status()).toBe(202);
    await page.goto('/downloads');
    const retry = page.getByRole('button', { name: `Retry ${surface}.txt`, exact: true });
    const history = async () => (await (await page.request.get('/api/transfers?direction=download')).json()).items as Schema<'TransferStateDto'>[];
    await expect(page.getByRole('article').filter({ hasText: `${surface}.txt` }).getByRole('button', { name: 'Peer refused', exact: true })).toBeVisible();
    await expect(retry).toBeVisible();
    const failed = (await history()).find(row => row.identity.remotePath === filename)!;
    if (surface === 'jobs') await page.goto(`/jobs/${failed.identity.jobId}`);
    expect(failed.status.failureReason).toBe('Denied');
    const card = page.getByRole('article').filter({ hasText: `${surface}.txt` });
    const failureButton = card.getByRole('button', { name: surface === 'downloads' ? 'Peer refused' : 'Failed', exact: true });
    await expect(failureButton).toBeVisible();
    await expect(card.locator('.progress-track')).toHaveCount(0);
    await failureButton.click();
    const dialog = page.getByRole('dialog', { name: 'Transfer details' });
    const failure = dialog.locator('.transfer-details-message');
    await expect(failure.locator(':scope > p')).toContainText('fixture-peer');
    await expect(failure.locator(':scope > p')).toContainText('Fixture rejects the first attempt only.');
    if (surface === 'jobs') {
      const jobPath = `/api/jobs/${failed.identity.jobId}`;
      const job = await (await page.request.get(jobPath)).json();
      expect(job.summary.lifecycle.hasFailureDetail).toBe(true);
      expect(job.summary).not.toHaveProperty('failureDetail');
      expect(detailLookups).toEqual([]);
      const details = failure.locator('details');
      await expect(details).toHaveJSProperty('open', false);
      await details.locator('summary').click();
      await expect(details.locator('p')).toContainText('TransferRejectedException');
      expect(detailLookups).toEqual([`${jobPath}/failure-detail`]);
    }
    await dialog.getByRole('button', { name: 'Close transfer details' }).click();
    const accepted = page.waitForResponse(response => response.url().endsWith(`/transfers/${failed.transferId}/retry`));
    await retry.click();
    expect((await accepted).status()).toBe(200);
    await page.goto('/downloads');
    await expect(page.getByRole('article').filter({ hasText: `${surface}.txt` }).getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
    const rows = (await history()).filter(row => row.identity.remotePath === filename);
    expect(rows).toHaveLength(2);
    const completed = rows.find(row => row.status.terminalOutcome === 'Succeeded')!;
    expect(completed.identity.username).toBe('fixture-peer');
    expect(completed.status.localPath).toBe(failed.status.localPath);
    expect(await readFile(completed.status.localPath!)).toHaveLength(128);
    if (surface === 'downloads') {
      await expect(retry).toHaveCount(0);
      const completedCard = page.getByRole('article').filter({ hasText: 'downloads.txt' })
        .filter({ has: page.getByRole('button', { name: 'Complete', exact: true }) });
      await completedCard.getByRole('button', { name: 'Remove Fixture' }).click();
      await expect(retry).toBeVisible();
      const nextRequest = page.waitForResponse(response => response.url().endsWith(`/transfers/${failed.transferId}/retry`));
      await retry.click();
      const nextRetry = await nextRequest;
      expect(nextRetry.status()).toBe(200);
      expect((await nextRetry.json()).jobId).not.toBe(completed.identity.jobId);
    }
  }
  expect(retryLookups).toEqual([]);
});

test('records local output failures before the first peer attempt and can retry them', async ({ page, daemon }, info) => {
  const { writeFile, unlink } = await import('node:fs/promises');
  const destination = info.outputPath('blocked-output');
  await writeFile(destination, 'existing file');
  const submission = await page.request.post('/api/jobs/lists', { data: {
    name: 'Local output failure', options: { outputParentDir: destination },
    jobs: [{ kind: 'remote-file', target: { username: 'fixture-peer', filename: 'local.txt', size: 128 } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(submission.status()).toBe(202);
  await daemon.command(`wait-job-complete:${(await submission.json()).jobId}`);
  const history = await (await page.request.get('/api/transfers?direction=download')).json();
  expect(history.items).toHaveLength(1);
  expect(history.items[0].status).toMatchObject({ failureReason: 'LocalStorageError', attemptCount: 0 });
  await page.goto('/downloads');
  const card = page.getByRole('article').filter({ hasText: 'local.txt' });
  await card.getByRole('button', { name: 'Local file error' }).click();
  const dialog = page.getByRole('dialog', { name: 'Transfer details' });
  await expect(dialog.locator('.transfer-details-message')).toContainText(destination);
  await dialog.getByRole('button', { name: 'Close transfer details' }).click();
  await unlink(destination);
  await card.getByRole('button', { name: 'Retry local.txt' }).click();
  await expect(card.getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
});

for (const button of ['file', 'folder']) test(`finishes the selected folder through its ${button} retry`, async ({ page, daemon }, testInfo) => {
  const { readFile, writeFile } = await import('node:fs/promises');
  await daemon.command('reject-next-download');
  const result = await page.request.post('/api/jobs/lists', { data: {
    name: 'Batch', options: { outputParentDir: testInfo.outputPath('music'),
      downloadSettings: { skip: { skipExisting: false }, output: { nameFormat: '{default-folder}/{foldername}/{filename}' } } },
    jobs: [{ kind: 'remote-directory', plan: { displayRoot: 'Album', totalKnownBytes: 256,
      entries: ['one.txt', 'two.txt'].map(name => ({ target: {
        username: 'fixture-peer', filename: `Fixture\\Album\\Disc 2\\${name}`, size: 128, extension: '.txt',
      }, relativeDirectoryComponents: ['Disc 2'] })) } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(result.status(), await result.text()).toBe(202);
  await daemon.command(`wait-job-complete:${(await result.json()).jobId}`);
  const history = async () => (await (await page.request.get('/api/transfers?direction=download')).json()).items as Schema<'TransferStateDto'>[];
  const initial = await history();
  const kept = initial.find(row => row.status.terminalOutcome === 'Succeeded')!;
  const failed = initial.find(row => row.status.terminalOutcome === 'Failed')!;
  const retryLookups: string[] = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/repeat-options')) retryLookups.push(request.url()); });
  await writeFile(kept.status.localPath!, 'already exported');
  if (button === 'folder') {
    await page.goto(`/jobs/${failed.identity.groupRef}`);
    await expect(page.getByTestId('retry-directory')).toHaveAccessibleName('Retry 1 missing file');
  }
  await page.goto('/downloads');
  const pending = page.waitForResponse(response => new URL(response.url()).pathname.endsWith(button === 'folder' ? '/retry-directory' : '/retry') && response.request().method() === 'POST');
  if (button === 'folder') await page.getByTestId('retry-directory').click();
  else {
    await page.locator('.folder-file-row').getByRole('button', { name: /^Retry / }).click();
  }
  const next = await pending;
  expect(next.ok(), await next.text()).toBe(true);
  await daemon.command(`wait-job-complete:${(await next.json()).jobId}`);
  const rows = await history();
  expect(rows).toHaveLength(3);
  const retried = rows.find(row => row.transferId !== failed.transferId && row.identity.remotePath === failed.identity.remotePath)!;
  expect(retried.status.terminalOutcome).toBe('Succeeded');
  expect(retried.status.localPath).toBe(failed.status.localPath);
  expect(await readFile(retried.status.localPath!)).toHaveLength(128);
  expect(await readFile(kept.status.localPath!, 'utf8')).toBe('already exported');
  await expect(page.getByTestId('retry-directory')).toHaveCount(0);
  await page.reload();
  await expect(page.getByText('Completed on retry', { exact: true })).toBeVisible();
  const duplicate = await page.request.post(`/api/jobs/${failed.identity.groupRef}/retry-directory?memberJobId=${failed.identity.jobId}`);
  expect(duplicate.status()).toBe(409);
  expect(retryLookups).toEqual([]);
});

test('cancels a live download and retains its cancelled state after reload', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Cancellation fixture', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  await page.goto('/downloads');
  const card = page.locator('.folder-item-card').filter({ hasText: 'Fixture song.txt' });
  const file = card.locator('.folder-file-row');
  await expect(card.getByLabel('50% complete', { exact: true }).first()).toBeVisible();
  await expect(card.locator('.folder-transfer-age')).toHaveText(/^(just now|\d+ .+ ago)$/);
  await expect(card.locator('.folder-transfer-age time')).toHaveAttribute('datetime', /T/);
  await page.getByRole('button', { name: 'Actions for fixture-peer', exact: true }).click();
  for (const name of ['Profile', 'Shares', 'Message', 'Copy']) {
    await expect(page.getByRole('menuitem', { name, exact: true })).toBeVisible();
  }
  await page.getByRole('menuitem', { name: 'Message', exact: true }).click();
  await expect(page).toHaveURL(/\/chat\/users\/fixture-peer$/);
  await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toBeVisible();
  await page.goto('/downloads');
  await expect(card.getByLabel('50% complete', { exact: true }).first()).toBeVisible();
  await page.mouse.move(0, 0);
  const cancelAction = file.getByRole('button', { name: 'Cancel Fixture song.txt', exact: true });
  await expect(cancelAction).toHaveCSS('opacity', '0');
  await expect(cancelAction).toHaveCSS('pointer-events', 'none');
  await file.hover();
  await expect(cancelAction).toHaveCSS('opacity', '1');
  await expect(cancelAction).toHaveCSS('pointer-events', 'auto');
  await cancelAction.click();
  await expect(page.locator('.mutation-status')).toContainText('1 transfer cancelled');
  await expect(card).toContainText('Cancelled');
  await expect(card.getByRole('button', { name: 'Cancel Fixture song.txt', exact: true })).toHaveCount(0);
  await page.reload();
  await expect(card).toContainText('Cancelled');
  await page.mouse.move(0, 0);
  await expect(file.getByRole('button', { name: 'Remove Fixture song.txt', exact: true })).toBeVisible();
  await expect(card.locator('.folder-transfer-path')).toHaveText('Fixture');
  await file.hover();
  await file.getByRole('button', { name: 'Remove Fixture song.txt', exact: true }).click();
  await expect(page.locator('.mutation-status')).toContainText('1 transfer removed');
  await expect(card).toHaveCount(0);
  await page.reload();
  await expect(page.getByText('No downloads', { exact: true })).toBeVisible();
  const archived = await page.request.get('/api/transfers?direction=download&includeRemoved=true&limit=100');
  expect(archived.ok()).toBeTruthy();
  expect((await archived.json()).items).toHaveLength(1);
});

test('groups independent downloads by submission and keeps group actions isolated', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  const submit = async (names: string[]) => {
    const response = await page.request.post('/api/jobs/lists', { data: {
      name: 'Independent files', jobs: names.map(name => ({ kind: 'remote-file' as const, target: {
        username: 'fixture-peer', filename: `Fixture\\${name}.txt`, size: 128, extension: '.txt',
      } })),
    } satisfies Schema<'SubmitJobListRequestDto'> });
    expect(response.status()).toBe(202);
  };
  await submit(['One', 'Two']);
  await page.goto('/downloads');
  const folder = page.locator('.folder-item-card').filter({ hasText: 'One.txt' });
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await submit(['Separate']);
  const separate = page.locator('.folder-item-card').filter({ hasText: 'Separate.txt' });
  await expect(separate.getByLabel('50% complete', { exact: true }).first()).toBeVisible();
  const response = await page.request.get('/api/transfers?direction=download&limit=100');
  const rows = (await response.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(rows).toHaveLength(3);
  expect(rows.every(row => row.identity.groupKind === 'Files')).toBeTruthy();
  const one = rows.find(row => row.identity.remotePath?.endsWith('One.txt'))!;
  const two = rows.find(row => row.identity.remotePath?.endsWith('Two.txt'))!;
  const other = rows.find(row => row.identity.remotePath?.endsWith('Separate.txt'))!;
  expect(one.identity.groupRef).toBe(two.identity.groupRef);
  expect(one.identity.groupRef).not.toBe(other.identity.groupRef);
  expect(one.status.localPath?.replaceAll('\\', '/')).toMatch(/\/One.txt$/);
  expect(one.status.localPath?.replaceAll('\\', '/')).not.toMatch(/\/Fixture\/One.txt$/);

  await folder.locator('.folder-item-summary').hover();
  await folder.getByRole('button', { name: 'Cancel Fixture', exact: true }).click();
  await expect(page.locator('.mutation-status')).toContainText('2 transfers cancelled');
  await expect(folder.locator('.folder-file-row').getByRole('button', { name: 'Cancelled', exact: true })).toHaveCount(2);
  await expect(separate.getByLabel('50% complete', { exact: true }).first()).toBeVisible();
  await page.reload();
  await expect(folder.locator('.folder-file-row').getByRole('button', { name: 'Cancelled', exact: true })).toHaveCount(2);
  await folder.locator('.folder-item-summary').hover();
  await folder.getByRole('button', { name: 'Remove Fixture', exact: true }).click();
  await expect(folder).toHaveCount(0);
  await expect(separate).toBeVisible();
  await daemon.command('release-download');
  await expect(separate.getByRole('button', { name: 'Complete', exact: true })).toHaveClass(/folder-file-state/);
  await page.reload();
  await expect(folder).toHaveCount(0);
  await expect(separate.getByRole('button', { name: 'Complete', exact: true })).toHaveClass(/folder-file-state/);
});

test('groups directory downloads in live and retained transfer views', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Directory transfer fixture', jobs: [{ kind: 'remote-directory', plan: {
      displayRoot: 'Fixture', totalKnownBytes: 256, entries: [
        { target: { username: 'fixture-peer', filename: 'Shares\\Fixture\\Selected.txt', size: 128, extension: '.txt' }, relativeDirectoryComponents: [] },
        { target: { username: 'fixture-peer', filename: 'Shares\\Fixture\\Nested\\Nested.txt', size: 128, extension: '.txt',
          bitRate: 320, bitDepth: 24, sampleRate: 96000, length: 123 }, relativeDirectoryComponents: ['Nested'] },
      ],
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  await page.goto('/downloads');
  const folder = page.locator('.folder-item-card').filter({ hasText: 'Selected.txt' });
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(folder.locator('.folder-transfer-path')).toHaveText('Fixture');
  await expect(folder.locator('.folder-subfolder-path')).toHaveText('Nested');
  await expect(folder.locator('.folder-file-path')).toHaveText(['Selected.txt', 'Nested.txt']);
  await expect(folder.locator('.folder-transfer-file-count')).toContainText('2');
  await expect(folder.locator('.item-data-state')).toHaveCount(0);
  await expect(folder.locator('.folder-transfer-summary .transfer-state')).toHaveCount(0);
  await expect(folder.locator('.folder-transfer-footer .transfer-progress-track')).toBeVisible();
  await expect(folder.locator('.folder-transfer-age')).toHaveText(/^(just now|\d+ .+ ago)$/);
  await expect(folder.locator('.folder-transfer-age time')).toHaveAttribute('datetime', /T/);
  await expect(folder.locator('.folder-heading-primary .folder-transfer-path')).toBeVisible();
  await expect(folder.locator('.folder-heading-meta')).toContainText('2 files');
  await expect(folder.locator('.folder-heading-meta .folder-transfer-age')).toBeVisible();
  await page.setViewportSize({ width: 390, height: 900 });
  await expect(folder.locator('.folder-heading-meta .folder-file-count')).toBeVisible();
  await expectFolderLayoutUnchanged(page);
  for (const filename of await folder.locator('.folder-file-path').all()) {
    await expect(filename).toBeVisible();
    expect((await filename.boundingBox())?.width).toBeGreaterThan(40);
  }
  const peerCountBounds = await page.locator('.peer-result-count').evaluateAll(nodes =>
    nodes.map(node => ({ right: node.getBoundingClientRect().right, width: window.innerWidth })));
  for (const bounds of peerCountBounds) expect(bounds.right).toBeLessThanOrEqual(bounds.width);
  await page.setViewportSize({ width: 1280, height: 720 });
  await expectFolderLayoutUnchanged(page);

  const widths = [390, 900, 1280];
  const columns = () => folder.locator('.folder-file-row').filter({ hasText: 'Nested.txt' })
    .locator('.folder-file-path, .folder-file-audio, .folder-file-length, .folder-file-size, .folder-file-state, .folder-file-action')
    .evaluateAll(cells => cells.map(cell => {
      const { x, width } = cell.getBoundingClientRect();
      return [x, width];
    }));
  const activeColumns = new Map<number, number[][]>();
  for (const width of widths) {
    await page.setViewportSize({ width, height: 720 });
    activeColumns.set(width, await columns());
  }
  await daemon.command('release-download');
  await expect(folder.locator('.folder-file-state.complete')).toHaveCount(2);
  await expect(folder.locator('.folder-transfer-footer')).toHaveCount(0);
  await expect(folder.locator('.folder-file-audio')).toHaveCount(1);
  for (const width of widths) {
    await page.setViewportSize({ width, height: 720 });
    expect(await columns()).toEqual(activeColumns.get(width));
    await expectFolderLayoutUnchanged(page);
  }
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(folder.locator('.folder-transfer-footer')).toHaveCount(0);
  const retained = await page.request.get('/api/transfers?direction=download&limit=100');
  expect(retained.ok()).toBeTruthy();
  const rows = (await retained.json() as Schema<'TransferTimelinePageDto'>).items;
  expect(rows).toHaveLength(2);
  expect(rows[0]?.identity.groupRef).toBeTruthy();
  expect(rows[0]?.identity.groupRef).toBe(rows[1]?.identity.groupRef);
  expect(rows.every(row => row.identity.groupDisplayPath === 'Fixture')).toBeTruthy();
  const totals = await page.request.post('/api/transfers/group-summaries', { data: {
    groups: [{ direction: 'Download', username: 'fixture-peer', groupRef: rows[0]!.identity.groupRef! }],
  } satisfies Schema<'TransferGroupSummariesRequestDto'> });
  expect(totals.ok()).toBeTruthy();
  const summary = (await totals.json() as Schema<'TransferGroupSummariesResponseDto'>).items[0]!;
  expect(Number(summary.fileCount)).toBe(2);
  expect(Number(summary.knownTotalBytes)).toBe(256);
  let resolveUnrelatedHistory!: () => void;
  const unrelatedHistory = new Promise<void>((resolve) => { resolveUnrelatedHistory = resolve; });
  // Ask the real daemon for an extent of one transfer and no rows to complete
  // folders with, so a two-file folder is partial without downloading hundreds.
  let smallExtent = true;
  const small = (scope: Schema<'TransferTimelineScopeDto'>) => smallExtent ? { ...scope, limit: 1, folderCompletionLimit: 0 } : scope;
  let coverageOverride: Schema<'TransferRetainedCoverageDto'> | null = null;
  let failTimeline = true;
  let failSummary = false;
  let releaseSummary!: () => void;
  const delayedSummary = new Promise<void>((resolve) => { releaseSummary = resolve; });
  const unavailableSummary = { items: [], retainedCoverage: { state: 'Unavailable', reason: 'Folder summary unavailable.' } } satisfies Schema<'TransferGroupSummariesResponseDto'>;
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    const failedInvocations = new Set<string>();
    const scopes = new Set<string>();
    route.onMessage(message => {
      if (typeof message !== 'string') { server.send(message); return; }
      for (const frame of message.split('\u001e').filter(Boolean)) {
        const value = JSON.parse(frame);
        if (value.type === 4 && value.target === 'ObserveTransferTimeline') {
          scopes.add(value.invocationId);
          value.arguments[0] = small(value.arguments[0]);
        }
        server.send(JSON.stringify(value) + '\u001e');
      }
    });
    server.onMessage(message => {
      if (typeof message !== 'string') { route.send(message); return; }
      for (const frame of message.split('\u001e').filter(Boolean)) {
        const value = JSON.parse(frame);
        if (failedInvocations.has(value.invocationId)) continue;
        if (scopes.has(value.invocationId) && value.type === 2) {
          if (coverageOverride) value.item.retainedCoverage = coverageOverride;
          if (failSummary && value.item.groupSummaries) value.item.groupSummaries = unavailableSummary;
        }
        if (failTimeline && value.type === 2 && value.item?.items?.some((row: Schema<'TransferStateDto'>) =>
          row.identity.remotePath === 'Other\\Unrelated.txt' && row.status.isTerminal)) {
          failTimeline = false;
          failedInvocations.add(value.invocationId);
          // Fail only the observation, leaving the daemon connection and real
          // successful reads intact. Retry must reconcile without dropping rows.
          route.send(JSON.stringify({ type: 3, invocationId: value.invocationId,
            error: 'Transfer updates unavailable.' }) + '\u001e');
          server.send(JSON.stringify({ type: 5, invocationId: value.invocationId }) + '\u001e');
        } else route.send(JSON.stringify(value) + '\u001e');
      }
    });
  });
  await page.route(url => url.pathname === '/api/transfers' || url.pathname === '/api/transfers/timeline', async (route) => {
    const response = route.request().method() === 'POST'
      ? await route.fetch({ postData: small(route.request().postDataJSON()) }) : await route.fetch();
    const body = await response.json() as Schema<'TransferTimelineUpdateDto'>;
    if (body.items.some((item) => item.identity.remotePath === 'Other\\Unrelated.txt'))
      resolveUnrelatedHistory();
    if (failSummary) await delayedSummary;
    await route.fulfill({ response, json: {
      ...body, groupSummaries: failSummary ? unavailableSummary : body.groupSummaries,
    } });
  });
  await page.reload();
  const partialFolder = page.locator('.folder-item-card').filter({ has: page.locator('.folder-transfer-path', { hasText: /^Fixture$/ }) });
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(1);
  await expect(partialFolder.locator('.folder-transfer-path')).toHaveText('Fixture');
  await expect(partialFolder.locator('.folder-transfer-path')).toHaveAttribute('title', 'Fixture');
  await expect(partialFolder.locator('.folder-transfer-file-count')).toContainText('1/2');
  await expect(partialFolder.locator('.item-data-state')).toHaveCount(0);
  await expect(partialFolder.locator('.folder-file-state.complete')).toHaveCount(1);
  await expect(partialFolder.locator('.folder-transfer-footer')).toHaveCount(0);
  // More of the folder arrives through the same extent, not a member request.
  const memberReads: string[] = [];
  const recordMemberRead = (request: import('@playwright/test').Request) => {
    if (new URL(request.url()).searchParams.has('groupRef')) memberReads.push(request.url());
  };
  page.on('request', recordMemberRead);
  await partialFolder.getByRole('button', { name: 'Load more' }).click();
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
  await expect(partialFolder.locator('.folder-transfer-file-count')).toContainText('2');
  await expect(partialFolder.getByRole('button', { name: 'Load more' })).toHaveCount(0);
  page.off('request', recordMemberRead);
  expect(memberReads).toEqual([]);
  // An ordinary extent holds the whole folder without being asked.
  smallExtent = false;
  await page.reload();
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
  await expect(partialFolder.getByRole('button', { name: 'Load more' })).toHaveCount(0);

  failSummary = true;

  await daemon.command('hold-download');
  const unrelated = await page.request.post('/api/jobs/lists', { data: {
    name: 'Unrelated transfer', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Other\\Unrelated.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(unrelated.status()).toBe(202);
  try {
    const unrelatedCard = page.locator('.folder-item-card').filter({ hasText: 'Unrelated.txt' });
    await expect(unrelatedCard.getByRole('button', { name: 'Downloading', exact: true }).first()).toBeVisible();
    await daemon.command('release-download');
    // A failed timeline observation must preserve loaded rows and make Retry
    // perform authoritative reconciliation. Hold its replacement totals below.
    const failure = page.getByRole('alert').filter({ hasText: 'Transfers unavailable' });
    await expect(failure).toContainText('Transfer updates unavailable.');
    await expect(unrelatedCard).toHaveCount(1);
    await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
    await failure.getByRole('button', { name: 'Retry', exact: true }).click();
    await unrelatedHistory;
    // The replacement snapshot (rows and totals) is withheld. The previous verified folder
    // must stay as it was during this handoff.
    await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
    await expect(partialFolder.locator('.folder-transfer-file-count')).toContainText('2');
  } finally {
    releaseSummary();
    await daemon.command('release-download');
  }
  await expect(page.locator('.folder-item-card').filter({ hasText: 'Unrelated.txt' }).getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Folder totals unavailable' })).toBeVisible();
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
  await expect(partialFolder.locator('.folder-transfer-file-count')).toContainText('2');
  await page.setViewportSize({ width: 390, height: 900 });
  await page.setViewportSize({ width: 1280, height: 720 });
  // Removing a folder's older member must work even without folder totals.
  const olderMember = rows[1]!;
  const expandedPath = olderMember.identity.groupRelativePath!;
  const expandedFile = partialFolder.locator('.folder-file-row').filter({ has: page.getByLabel(`Remove ${expandedPath}`, { exact: true }) });
  await page.route(`**/api/transfers/${olderMember.transferId}`, async route => {
    await route.fulfill({ contentType: 'application/json', json: {
      resolvedCount: 1, succeededCount: 0, noOpCount: 0, rejectedCount: 1, failedCount: 0,
      reasons: [{ reason: 'FixtureRemovalRejected', count: 1 }],
    } satisfies Schema<'TransferCommandReceiptDto'> });
  }, { times: 1 });
  await expandedFile.hover();
  await expandedFile.getByRole('button', { name: `Remove ${expandedPath}`, exact: true }).click();
  await expect(page.locator('.mutation-status')).toContainText('Remove rejected');
  await expect(page.getByRole('status').filter({ hasText: 'Folder totals unavailable' })).toBeVisible();
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
  await expandedFile.hover();
  await expandedFile.getByRole('button', { name: `Remove ${expandedPath}`, exact: true }).click();
  await expect(page.locator('.mutation-status')).toContainText('1 transfer removed');
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(1);
  await expect(expandedFile).toHaveCount(0);
  failSummary = false;
  const retryRequests: string[] = [];
  const recordRetry = (request: import('@playwright/test').Request) => {
    const url = new URL(request.url());
    if (url.pathname === '/api/transfers/timeline') retryRequests.push('timeline');
    if (url.pathname === '/api/transfers/group-summaries') retryRequests.push('totals');
  };
  page.on('request', recordRetry);
  await page.getByRole('status').filter({ hasText: 'Folder totals unavailable' }).getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Folder totals unavailable' })).toHaveCount(0);
  await expect(partialFolder.locator('.item-data-state')).toHaveCount(0);
  page.off('request', recordRetry);
  expect(retryRequests).toEqual([]);
  await page.reload();
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(1);
  await partialFolder.locator('.folder-item-summary').hover();
  await partialFolder.getByRole('button', { name: 'Remove Fixture' }).click();
  await expect(partialFolder).toHaveCount(0);
  const remaining = await page.request.get('/api/transfers?direction=download&limit=100');
  expect((await remaining.json() as Schema<'TransferTimelinePageDto'>).items.map((item) => item.identity.remotePath)).toEqual(['Other\\Unrelated.txt']);

  // The daemon integration test exercises actual isolated writer loss. At
  // this browser boundary, keep real retained rows and vary only coverage:
  // degraded history must remain visible with an explicit warning.
  coverageOverride = { state: 'Degraded', reason: 'PersistenceDegraded' };
  await page.route(url => url.pathname === '/api/transfers' || url.pathname === '/api/transfers/timeline', async route => {
    const response = await route.fetch();
    const body = await response.json() as Schema<'TransferTimelinePageDto'>;
    await route.fulfill({ response, json: { ...body,
      retainedCoverage: coverageOverride,
    } });
  });
  await page.reload();
  await expect(page.getByRole('status').filter({ hasText: 'History degraded' })).toBeVisible();
  await expect(page.locator('.folder-item-card').filter({ hasText: 'Unrelated.txt' }).getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
  await expect(page.getByText('No downloads', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('alert').filter({ hasText: 'Transfers unavailable' })).toHaveCount(0);
});

test('keeps the members of an expanded folder when another member finishes', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  const submitted = await page.request.post('/api/jobs/lists', { data: {
    name: 'Member transition fixture', jobs: [{ kind: 'remote-directory', plan: {
      displayRoot: 'Fixture', totalKnownBytes: 256, entries: [
        { target: { username: 'fixture-peer', filename: 'Fixture\\Cancelled.txt', size: 128, extension: '.txt' }, relativeDirectoryComponents: [] },
        { target: { username: 'fixture-peer', filename: 'Fixture\\Active.txt', size: 128, extension: '.txt' }, relativeDirectoryComponents: [] },
      ],
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(submitted.status()).toBe(202);
  await page.goto('/downloads');
  const folder = page.locator('.folder-item-card').filter({ has: page.locator('.folder-transfer-path', { hasText: /^Fixture$/ }) });
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  const retained = await page.request.get('/api/transfers?direction=download&limit=100');
  expect(retained.ok()).toBeTruthy();
  const rows = (await retained.json() as Schema<'TransferTimelinePageDto'>).items;
  const cancelled = rows.find((row) => row.identity.remotePath === 'Fixture\\Cancelled.txt');
  expect(cancelled).toBeTruthy();
  const cancel = await page.request.post(`/api/transfers/${cancelled!.transferId}/cancel`);
  expect(cancel.ok()).toBeTruthy();
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'Cancelled.txt' })).toContainText('Cancelled');

  // Ask the real daemon for one transfer and no rows to complete folders with,
  // so the two-file folder is partial until it is expanded.
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    route.onMessage(message => {
      if (typeof message !== 'string') { server.send(message); return; }
      for (const frame of message.split('\u001e').filter(Boolean)) {
        const value = JSON.parse(frame);
        if (value.type === 4 && value.target === 'ObserveTransferTimeline')
          value.arguments[0] = { ...value.arguments[0], limit: 1, folderCompletionLimit: 0 };
        server.send(JSON.stringify(value) + '\u001e');
      }
    });
    server.onMessage(message => route.send(message));
  });
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(1);
  await expect(folder.locator('.folder-transfer-file-count')).toContainText('1/2');
  await folder.getByRole('button', { name: 'Load more' }).click();
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await daemon.command('release-download');
  const members = folder.locator('.folder-file-row');
  await expect(members.filter({ hasText: 'Active.txt' }).getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
  await expect(members.filter({ hasText: 'Cancelled.txt' }).getByRole('button', { name: 'Cancelled', exact: true })).toBeVisible();
  await expect(folder.locator('.folder-transfer-file-count')).toContainText('2');
  await expect(folder.getByRole('button', { name: 'Load more' })).toHaveCount(0);
});

test('cancels every member of a live directory from its folder action', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Grouped cancellation fixture', jobs: [{ kind: 'remote-directory', plan: {
      displayRoot: 'Fixture', totalKnownBytes: 256, entries: [
        { target: { username: 'fixture-peer', filename: 'Fixture\\One.txt', size: 128, extension: '.txt' }, relativeDirectoryComponents: [] },
        { target: { username: 'fixture-peer', filename: 'Fixture\\Two.txt', size: 128, extension: '.txt' }, relativeDirectoryComponents: [] },
      ],
    } }, { kind: 'remote-directory', plan: {
      displayRoot: 'Other', totalKnownBytes: 128, entries: [
        { target: { username: 'fixture-peer', filename: 'Other\\Unrelated.txt', size: 128, extension: '.txt' }, relativeDirectoryComponents: [] },
      ],
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  await page.goto('/downloads');
  const folder = page.locator('.folder-item-card').filter({ hasText: 'One.txt' });
  const otherFolder = page.locator('.folder-item-card').filter({ hasText: 'Unrelated.txt' });
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(otherFolder.locator('.folder-file-row')).toHaveCount(1);
  await folder.locator('.folder-item-summary').hover();
  await folder.getByRole('button', { name: 'Cancel Fixture' }).click();
  await expect(page.locator('.mutation-status')).toContainText('2 transfers cancelled');
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'One.txt' })).toContainText('Cancelled');
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'Two.txt' })).toContainText('Cancelled');
  await expect(otherFolder.getByRole('button', { name: 'Cancel Other' })).toBeVisible();
});

test('retries a failed automatic album track from its original peer and folder', async ({ page, daemon }, testInfo) => {
  await daemon.command('album-art-search');
  await daemon.command('reject-next-download');
  const output = testInfo.outputPath('album-retry');
  const response = await page.request.post('/api/jobs/downloads/album', { data: {
    albumQuery: { album: 'Fixture Album', artistMaybeWrong: false },
    options: { outputParentDir: output, downloadSettings: {
      output: { nameFormat: 'Organized/{foldername}/{filename}' },
      search: { noBrowseFolder: true }, transfer: { maxDownloadRetries: 1 }, skip: { skipExisting: false },
    } },
  } satisfies Schema<'SubmitAlbumJobRequestDto'> });
  expect(response.status(), await response.text()).toBe(202);
  const original = await response.json() as Schema<'JobSummaryDto'>;
  await daemon.command(`wait-job-complete:${original.jobId}`);
  const history = async () => (await (await page.request.get('/api/transfers?direction=download')).json()).items as Schema<'TransferStateDto'>[];
  const failed = (await history()).find(row => row.status.terminalOutcome === 'Failed')!;
  expect(failed.identity.groupRef).toBe(original.jobId);
  await page.goto('/downloads');
  const receipt = page.waitForResponse(response => response.url().endsWith(`/${failed.transferId}/retry`));
  await page.locator('.folder-file-row').getByRole('button', { name: /^Retry / }).first().click();
  const retry = await receipt;
  expect(retry.ok(), await retry.text()).toBe(true);
  await daemon.command(`wait-job-complete:${(await retry.json()).jobId}`);
  const members = (await history()).filter(row => row.identity.remotePath === failed.identity.remotePath
    && row.identity.username === failed.identity.username && row.status.terminalOutcome === 'Succeeded');
  expect(members).toHaveLength(1);
  const filename = failed.identity.remotePath!.split('\\').at(-1);
  const path = `${output}/Organized/Fixture Album/${filename}`;
  expect(members[0]?.status.localPath).toBe(path);
  const { readFile } = await import('node:fs/promises');
  expect(await readFile(path)).toHaveLength(128);
});
