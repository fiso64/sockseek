import { test, expect, deferInitialDaemonSnapshot, expectFolderLayoutUnchanged } from './fixtures';
import type { Schema } from '../src/api/types';

test.use({ uploadShares: true });

test('cancels queued uploads within a mixed folder without cancelling active files', async ({ page, daemon }) => {
  await page.clock.install();
  let historyReads = 0, summaryReads = 0;
  let globalTransferRows = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/transfers/timeline') historyReads++;
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/transfers/group-summaries') summaryReads++;
  });
  let backgroundJobId: string | undefined;
  let retired!: () => void;
  const backgroundRetired = new Promise<void>(resolve => { retired = resolve; });
  let committed!: () => void;
  const backgroundCommitted = new Promise<void>(resolve => { committed = resolve; });
  page.on('websocket', socket => socket.on('framereceived', ({ payload }) => {
    if (!backgroundJobId) return;
    for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
      const message = JSON.parse(frame) as { target?: string; arguments?: Schema<'StateUpdateBatchDto'>[] };
      if (message.target?.toLowerCase() !== 'stateupdatebatch') continue;
      const state = message.arguments?.[0]?.state;
      globalTransferRows += state?.transfers.length ?? 0;
      if (state?.removedJobIds.includes(backgroundJobId)) retired();
      if (Number(state?.daemon?.terminalTransferCommitRevision) > 0) committed();
    }
  }));
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto('/uploads');
  await daemon.command('request-upload');
  await expect(page.locator('.file-item-card').filter({ hasText: 'Uploaded.txt' })
    .getByLabel('50% complete', { exact: true })).toBeVisible();
  await daemon.command('request-queued-upload');
  const active = page.locator('.folder-file-row').filter({ hasText: 'Uploaded.txt' });
  await expect(active.getByLabel('50% complete', { exact: true })).toBeVisible();
  const queued = page.locator('.folder-file-row').filter({ hasText: 'Queued.txt' });
  await expect(queued.getByRole('button', { name: 'Queued', exact: true })).toBeVisible();
  const folder = page.locator('.folder-item-card').filter({ hasText: 'Uploaded.txt' });
  await expect(folder.locator('.folder-transfer-file-count')).toHaveText('2 files');
  await expect(folder.locator('.folder-transfer-summary .transfer-state')).toHaveCount(0);
  await expect(folder.locator('.item-data-state')).toHaveCount(0);
  const queuedHistoryReads = historyReads;
  // Advance the reconciliation timer without waiting in real time. An
  // unchanged queued upload is not a retired transfer needing another GET.
  await page.clock.fastForward(2_000);
  expect(historyReads).toBe(queuedHistoryReads);
  // A download can finish while this unchanged upload folder stays open.
  // Its retirement must not refetch upload history or folder totals.
  const beforeDownload = { historyReads, summaryReads };
  await daemon.command('hold-download');
  const background = await page.request.post('/api/jobs/lists', { data: {
    name: 'Background download', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Background.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(background.status()).toBe(202);
  backgroundJobId = (await background.json() as Schema<'JobSummaryDto'>).jobId;
  await daemon.command('wait-download-started');
  const downloads = page.getByRole('button', { name: /^Downloads/ }).locator('.nav-count');
  await expect(downloads).toHaveText('1');
  await daemon.command('release-download');
  await Promise.all([backgroundRetired, backgroundCommitted]);
  const retained = await page.request.get('/api/transfers?direction=download');
  expect(retained.ok()).toBe(true);
  expect((await retained.json() as Schema<'TransferTimelinePageDto'>).items)
    .toMatchObject([{ identity: { remotePath: 'Fixture\\Background.txt' }, status: { terminalOutcome: 'Succeeded' } }]);
  await expect(downloads).toHaveCount(0);
  await page.clock.runFor(100); // Paint after retirement; no real-time sleep.
  expect({ historyReads, summaryReads }, 'Opposite-direction retirement leaves the upload page valid.')
    .toEqual(beforeDownload);
  expect(globalTransferRows, 'Sidebar totals do not require unrelated global transfer rows.').toBe(0);
  await expect(active.getByLabel('50% complete', { exact: true })).toBeVisible();
  await expect(queued.getByRole('button', { name: 'Queued', exact: true })).toBeVisible();
  const activeDetail = active.locator('.folder-file-transfer-detail');
  await expect(activeDetail).toContainText('remaining');
  expect(await activeDetail.evaluate((element) => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  await page.setViewportSize({ width: 320, height: 900 });
  expect(await activeDetail.evaluate((element) => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  await page.setViewportSize({ width: 390, height: 900 });
  await page.getByRole('button', { name: 'Choose cancel scope. Current: All', exact: true }).click();
  await page.getByRole('menuitemradio', { name: 'Queued', exact: true }).click();
  await page.getByRole('button', { name: 'Cancel queued', exact: true }).click();
  await expect(page.locator('.mutation-status')).toContainText('1 transfer cancelled');
  await expect(queued.getByText('Cancelled', { exact: true })).toBeVisible();
  await expect(active.getByLabel('50% complete', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Remove all completed', exact: true }).click();
  await expect(page.locator('.mutation-status')).toContainText('1 transfer removed');
  await expect(queued).toHaveCount(0);
  const remaining = page.locator('.file-item-card').filter({ hasText: 'Uploaded.txt' });
  await expect(remaining.getByLabel('50% complete', { exact: true })).toBeVisible();
  await daemon.command('release-upload');
  await expect(remaining.getByRole('button', { name: 'Complete', exact: true })).toHaveClass(/folder-file-state/);
});

test('serves a peer upload through live progress and retained history', async ({ page, daemon }) => {
  const releaseSnapshot = await deferInitialDaemonSnapshot(page);
  let historyReads = 0;
  page.on('request', request => {
    const url = new URL(request.url());
    if (request.method() === 'POST' && url.pathname === '/api/transfers/timeline') historyReads++;
  });
  const card = page.locator('.file-item-card').filter({ hasText: 'Uploaded.txt' });
  try {
    await page.goto('/uploads');
    await expect(page.getByText('No uploads', { exact: true })).toBeVisible();
    await daemon.command('request-upload');
    releaseSnapshot();
    await expect(card.getByLabel('50% complete', { exact: true })).toBeVisible();
  } finally { releaseSnapshot(); }
  await expect(card.getByLabel('50% complete', { exact: true })).toBeVisible();
  await card.getByRole('button', { name: 'Uploading', exact: true }).click();
  const details = page.getByRole('dialog', { name: 'Transfer details', exact: true });
  await expect(details).toContainText('upload-peer');
  await expect(details).toContainText('Public\\Album\\Uploaded.txt');
  await expect(details.locator('dt').filter({ hasText: /^Transferred$/ }).locator('+ dd')).toHaveText('64 B');
  await expect(details.locator('dt').filter({ hasText: /^Attempts$/ }).locator('+ dd')).toHaveText('1');
  await daemon.command('release-upload');
  await expect(details.locator('dt').filter({ hasText: /^Status$/ }).locator('+ dd')).toHaveText('Complete');
  await expect(details.locator('dt').filter({ hasText: /^Transferred$/ }).locator('+ dd')).toHaveText('128 B');
  await page.keyboard.press('Escape');
  await expect(details).toHaveCount(0);
  await page.getByRole('button', { name: 'Actions for upload-peer', exact: true }).click();
  await expect(page.getByRole('menuitem', { name: 'Shares', exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(card.getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
  await expect(card.locator('.file-transfer-footer')).toHaveCount(0);
  historyReads = 0;
  await page.reload();
  await expect(page.getByLabel('Connection status')).toContainText('Connected');
  await expect(card.getByRole('button', { name: 'Complete', exact: true })).toBeVisible();
  await expect(card.locator('.file-transfer-footer')).toHaveCount(0);
  expect(historyReads).toBe(1);
  await card.getByRole('button', { name: 'Complete', exact: true }).click();
  await expect(details.locator('dt').filter({ hasText: /^Transferred$/ }).locator('+ dd')).toHaveText('128 B');
  await expect(details.locator('dt').filter({ hasText: /^Finished$/ }).locator('+ dd')).not.toBeEmpty();
  expect(historyReads).toBe(1);
  await details.getByRole('button', { name: 'Close transfer details', exact: true }).click();
  await card.locator('.file-item-main').hover();
  await card.getByRole('button', { name: 'Remove Uploaded.txt', exact: true }).click();
  await expect(card).toHaveCount(0);
  await page.reload();
  await expect(page.getByText('No uploads', { exact: true })).toBeVisible();
  const archived = await page.request.get('/api/transfers?direction=upload&archived=true&limit=100');
  expect(archived.ok()).toBeTruthy();
  expect((await archived.json()).items).toHaveLength(1);
});

test('keeps two completed uploads in one retained folder and Content ranking', async ({ page, daemon }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto('/uploads');
  await daemon.command('request-upload');
  const folder = page.locator('.folder-item-card').filter({ hasText: 'Uploaded.txt' });
  await expect(page.locator('.file-item-card').filter({ hasText: 'Uploaded.txt' })
    .getByLabel('50% complete', { exact: true })).toBeVisible();
  await daemon.command('request-queued-upload');
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(folder.locator('.folder-file-row').first()).toContainText('Queued.txt');
  await daemon.command('release-upload');
  await expect(folder.locator('.folder-file-row').getByRole('button', { name: 'Complete', exact: true })).toHaveCount(2);
  await expect(folder.locator('.folder-transfer-file-count')).toHaveText('2 files');
  await expect(folder.locator('.folder-transfer-summary .transfer-state')).toHaveCount(0);
  await folder.locator('.folder-file-row').first().getByRole('button', { name: 'Complete', exact: true }).click();
  const details = page.getByRole('dialog', { name: 'Transfer details', exact: true });
  await expect(details).toContainText('Public\\Album\\Queued.txt');
  await details.getByRole('button', { name: 'Close transfer details', exact: true }).click();
  await expect(folder.locator('.folder-transfer-footer')).toHaveCount(0);

  await expect(folder.locator('.folder-file-row').first()).toHaveCSS('height', '46px');
  await expectFolderLayoutUnchanged(page);
  await expect(folder).not.toContainText('128 B/s');
  const workspace = await page.locator('.workspace').boundingBox();
  const card = await folder.boundingBox();
  expect(workspace).not.toBeNull();
  expect(card).not.toBeNull();
  expect(card!.x + card!.width).toBeLessThanOrEqual(workspace!.x + workspace!.width + 1);

  await page.reload();
  await expect(page.locator('.folder-item-card')).toHaveCount(1);
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(folder.locator('.folder-file-row').getByRole('button', { name: 'Complete', exact: true })).toHaveCount(2);
  await expect(folder.locator('.folder-file-row').first()).toHaveCSS('height', '46px');
  const retained = await page.request.get('/api/transfers?direction=upload&limit=100');
  expect(retained.ok()).toBeTruthy();
  expect((await retained.json()).items).toHaveLength(2);

  await page.goto('/dashboard');
  await expect(page.locator('.dashboard-metric-card').nth(1).locator('strong')).toHaveText('256 B');
  await page.locator('.ranking-panel').getByRole('button', { name: 'Content', exact: true }).click();
  const content = page.locator('.content-table .dashboard-ranking-row').nth(1);
  await expect(content).toContainText('Public');
  await expect(content).toContainText('2');
});


test('explains a failed upload and supersedes it when the peer retries', async ({ page, daemon }) => {
  await page.goto('/uploads');
  await daemon.command('fail-next-upload');
  await daemon.command('request-upload');
  const card = page.locator('.file-item-card').filter({ hasText: 'Uploaded.txt' });
  await expect(card.getByRole('button', { name: 'Failed', exact: true })).toBeVisible();
  await page.reload();
  await card.getByRole('button', { name: 'Failed', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Transfer details', exact: true });
  await expect(dialog).toContainText('The fixture peer disconnected during upload.');
  await page.mouse.click(5, 5);
  await expect(dialog).toHaveCount(0);
  await card.getByRole('button', { name: 'Failed', exact: true }).click();
  await dialog.getByRole('button', { name: 'Close transfer details', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await daemon.command('request-upload');
  await expect(card).toHaveCount(1);
  await expect(card.getByLabel('50% complete', { exact: true })).toBeVisible();
  await expect(page.locator('.folder-item-card')).toHaveCount(0);
  await page.reload();
  await expect(card.getByLabel('50% complete', { exact: true })).toBeVisible();
  await daemon.command('release-upload');
  await expect(card.getByRole('button', { name: 'Complete', exact: true })).toHaveClass(/folder-file-state/);
  const archived = await page.request.get('/api/transfers?direction=upload&archived=true');
  const history = await archived.json() as Schema<'TransferTimelinePageDto'>;
  expect(history.items).toHaveLength(1);
  expect(history.items[0]!.status.failureMessage).toBe('The fixture peer disconnected during upload.');
});
