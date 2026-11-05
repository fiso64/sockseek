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

test('cancels a live download and retains its cancelled state after reload', async ({ page, daemon }) => {
  await daemon.command('hold-download');
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Cancellation fixture', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status()).toBe(202);
  await page.goto('/downloads');
  const card = page.locator('.file-item-card').filter({ hasText: 'Fixture song.txt' });
  await expect(card.getByLabel('50% complete', { exact: true })).toBeVisible();
  await expect(card.locator('.transfer-state')).toHaveText('Downloading');
  await expect(card.locator('.transfer-progress-meta').first()).toContainText('64 B · 50%');
  await expect(card.locator('.file-transfer-age')).toHaveText(/^(just now|\d+ .+ ago)$/);
  await expect(card.locator('.file-transfer-age time')).toHaveAttribute('datetime', /T/);
  await page.getByRole('button', { name: 'Actions for fixture-peer', exact: true }).click();
  for (const name of ['Profile', 'Shares', 'Message']) {
    await expect(page.getByRole('menuitem', { name, exact: true })).toBeVisible();
  }
  await page.getByRole('menuitem', { name: 'Message', exact: true }).click();
  await expect(page).toHaveURL(/\/chat\/users\/fixture-peer$/);
  await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toBeVisible();
  await page.goto('/downloads');
  await expect(card.getByLabel('50% complete', { exact: true })).toBeVisible();
  await page.mouse.move(0, 0);
  const cancelAction = card.getByRole('button', { name: 'Cancel Fixture song.txt', exact: true });
  await expect(cancelAction).toHaveCSS('opacity', '0');
  await expect(cancelAction).toHaveCSS('pointer-events', 'none');
  await card.locator('.file-item-main').hover();
  await expect(cancelAction).toHaveCSS('opacity', '1');
  await expect(cancelAction).toHaveCSS('pointer-events', 'auto');
  await cancelAction.click();
  await expect(page.locator('.mutation-status')).toContainText('1 transfer cancelled');
  await expect(card).toContainText('Cancelled');
  await expect(card.getByRole('button', { name: 'Cancel Fixture song.txt', exact: true })).toHaveCount(0);
  await page.reload();
  await expect(card).toContainText('Cancelled');
  await expect(card.getByRole('button', { name: 'Remove Fixture song.txt', exact: true })).toBeVisible();
  await card.locator('.file-item-main').hover();
  await card.getByRole('button', { name: 'Remove Fixture song.txt', exact: true }).click();
  await expect(page.locator('.mutation-status')).toContainText('1 transfer removed');
  await expect(card).toHaveCount(0);
  await page.reload();
  await expect(page.getByText('No downloads', { exact: true })).toBeVisible();
  const archived = await page.request.get('/api/transfers?direction=download&archived=true&limit=100');
  expect(archived.ok()).toBeTruthy();
  expect((await archived.json()).items).toHaveLength(1);
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
  expect((await folder.locator('.folder-transfer-name').allTextContents()).sort()).toEqual(['Fixture', 'Nested']);
  await expect(folder.locator('.folder-transfer-path')).toContainText('Shares\\Fixture');
  await expect(folder).toContainText('Nested/Nested.txt');
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

  await daemon.command('release-download');
  await expect(folder.locator('.folder-file-state.complete')).toHaveCount(2);
  await expect(folder.locator('.folder-transfer-footer')).toHaveCount(0);
  await expect(folder.locator('.folder-file-audio')).toHaveCount(1);
  for (const width of [390, 900, 1280]) {
    await page.setViewportSize({ width, height: 720 });
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
  let firstTimelinePage = true;
  let firstTimelineCursor: string | null = null;
  let coverageOverride: Schema<'TransferRetainedCoverageDto'> | null = null;
  let failTimeline = true;
  let failSummary = false;
  let releaseSummary!: () => void;
  const delayedSummary = new Promise<void>((resolve) => { releaseSummary = resolve; });
  const unavailableSummary = { items: [], retainedCoverage: { state: 'Unavailable', reason: 'Folder summary unavailable.' } } satisfies Schema<'TransferGroupSummariesResponseDto'>;
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    const failedInvocations = new Set<string>();
    const scopes = new Map<string, Schema<'TransferTimelineScopeDto'>>();
    route.onMessage(message => {
      if (typeof message === 'string') for (const frame of message.split('\u001e').filter(Boolean)) {
        const value = JSON.parse(frame);
        if (value.type === 4 && value.target === 'ObserveTransferTimeline') scopes.set(value.invocationId, value.arguments[0]);
      }
      server.send(message);
    });
    server.onMessage(message => {
      if (typeof message !== 'string') { route.send(message); return; }
      for (const frame of message.split('\u001e').filter(Boolean)) {
        const value = JSON.parse(frame);
        if (failedInvocations.has(value.invocationId)) continue;
        const scope = scopes.get(value.invocationId);
        if (scope && value.type === 2) {
          // The real HTTP first page is limited below. Keep its omitted folder
          // member outside the observed prefix until explicitly fetched too.
          const excluded = rows[1]!.transferId;
          const additional = new Set(scope.additionalTransferIds);
          if (!additional.has(excluded)) {
            value.item.items = value.item.items.filter((row: Schema<'TransferStateDto'>) => row.transferId !== excluded);
            if (value.item.windowTransferIds?.includes(excluded)) {
              value.item.windowTransferIds = value.item.windowTransferIds.filter((id: string) => id !== excluded);
              value.item.nextCursor = firstTimelineCursor;
              value.item.boundaryTransferId = rows[0]!.transferId;
            }
          }
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
    if (new URL(route.request().url()).searchParams.has('groupRef')) {
      await route.continue();
      return;
    }
    if (firstTimelinePage) {
      firstTimelinePage = false;
      const url = new URL(route.request().url());
      url.searchParams.set('limit', '1');
      // Exercise the real page boundary/cursor without downloading 100 files
      // in ordinary CI. Do not fabricate the first page or its continuation.
      const response = url.pathname === '/api/transfers/timeline'
        ? await route.fetch({ postData: { ...route.request().postDataJSON(), limit: 1 } })
        : await route.fetch({ url: url.toString() });
      firstTimelineCursor = ((await response.json()) as Schema<'TransferTimelineUpdateDto'>).nextCursor ?? null;
      await route.fulfill({ response });
      return;
    }
    const response = await route.fetch();
    const body = await response.json() as Schema<'TransferTimelineUpdateDto'>;
    if (body.items.some((item) => item.identity.remotePath === 'Other\\Unrelated.txt'))
      resolveUnrelatedHistory();
    if (failSummary) await delayedSummary;
    const additional = new Set<string>(route.request().postDataJSON()?.additionalTransferIds ?? []);
    await route.fulfill({ response, json: {
      ...body,
      groupSummaries: failSummary ? unavailableSummary : body.groupSummaries,
      items: body.items.filter((item) => item.identity.groupRef !== rows[0]!.identity.groupRef || item.transferId === rows[0]!.transferId || additional.has(item.transferId)),
      nextCursor: null,
    } });
  });
  await page.reload();
  const partialFolder = page.locator('.folder-item-card').first();
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(1);
  await expect(partialFolder.locator('.folder-transfer-path')).toContainText('Shares\\Fixture');
  await expect(partialFolder.locator('.folder-transfer-path')).toHaveAttribute('title', /^Remote folders of loaded files:/);
  await expect(partialFolder.locator('.folder-transfer-file-count')).toContainText('1/2');
  await expect(partialFolder).toContainText('Showing loaded files');
  await expect(partialFolder.locator('.folder-file-state.complete')).toHaveCount(1);
  await expect(partialFolder.locator('.folder-transfer-footer')).toHaveCount(0);
  const nextMembers = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname === '/api/transfers'
    && new URL(response.url()).searchParams.has('groupRef'));
  await partialFolder.getByRole('button', { name: 'Load more' }).click();
  const nextMemberPage = await (await nextMembers).json() as Schema<'TransferTimelineUpdateDto'>;
  expect(nextMemberPage.items).toHaveLength(1);
  expect(nextMemberPage.items[0]!.transferId).not.toBe(rows[0]!.transferId);
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
  await expect(partialFolder.locator('.folder-transfer-file-count')).toContainText('2');

  failSummary = true;

  await daemon.command('hold-download');
  const unrelated = await page.request.post('/api/jobs/lists', { data: {
    name: 'Unrelated transfer', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Other\\Unrelated.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(unrelated.status()).toBe(202);
  try {
    const unrelatedCard = page.locator('.file-item-card').filter({ hasText: 'Unrelated.txt' });
    await expect(unrelatedCard.locator('.transfer-state')).toHaveText('Downloading');
    await daemon.command('release-download');
    // A failed timeline observation must preserve loaded rows and make Retry
    // perform authoritative reconciliation. Hold its replacement totals below.
    const failure = page.getByRole('alert').filter({ hasText: 'Transfers unavailable' });
    await expect(failure).toContainText('Transfer updates unavailable.');
    await expect(unrelatedCard).toHaveCount(1);
    await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
    await failure.getByRole('button', { name: 'Retry', exact: true }).click();
    await unrelatedHistory;
    // The replacement snapshot (rows and totals) is withheld. The previous verified folder page
    // must not flash back to its single timeline member during this handoff.
    await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
    await expect(partialFolder.locator('.folder-transfer-file-count')).toContainText('2');
  } finally {
    releaseSummary();
    await daemon.command('release-download');
  }
  await expect(page.locator('.file-item-card').filter({ hasText: 'Unrelated.txt' })).toContainText('Complete');
  await expect(page.getByRole('status').filter({ hasText: 'Folder totals unavailable' })).toBeVisible();
  await expect(partialFolder.locator('.folder-file-row')).toHaveCount(2);
  await expect(partialFolder).toContainText('Size, progress and status cover loaded files only.');
  await expect(partialFolder.locator('.folder-transfer-file-count')).toContainText('2');
  await page.setViewportSize({ width: 390, height: 900 });
  await page.setViewportSize({ width: 1280, height: 720 });
  // Removing an expanded-only member must work even without folder totals.
  const expandedPath = nextMemberPage.items[0]!.identity.groupRelativePath!;
  const expandedFile = partialFolder.locator('.folder-file-row').filter({ hasText: expandedPath });
  await page.route(`**/api/transfers/${nextMemberPage.items[0]!.transferId}/archive`, async route => {
    await route.fulfill({ contentType: 'application/json', json: {
      resolvedCount: 1, succeededCount: 0, noOpCount: 0, rejectedCount: 1, failedCount: 0,
      reasons: [{ reason: 'FixtureArchiveRejected', count: 1 }],
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
  await expect(page.getByRole('status').filter({ hasText: 'Retained history degraded' })).toBeVisible();
  await expect(page.locator('.file-item-card').filter({ hasText: 'Unrelated.txt' })).toContainText('Complete');
  await expect(page.getByText('No downloads', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('alert').filter({ hasText: 'Transfers unavailable' })).toHaveCount(0);
});

test('keeps a loaded folder member page when another member finishes', async ({ page, daemon }) => {
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
  const folder = page.locator('.folder-item-card').filter({ hasText: 'Active.txt' });
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  const retained = await page.request.get('/api/transfers?direction=download&limit=100');
  expect(retained.ok()).toBeTruthy();
  const rows = (await retained.json() as Schema<'TransferTimelinePageDto'>).items;
  const cancelled = rows.find((row) => row.identity.remotePath === 'Fixture\\Cancelled.txt');
  expect(cancelled).toBeTruthy();
  const cancel = await page.request.post(`/api/transfers/${cancelled!.transferId}/cancel`);
  expect(cancel.ok()).toBeTruthy();
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'Cancelled.txt' })).toContainText('Cancelled');

  let failGroupRefresh = false;
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    const scopes = new Map<string, Schema<'TransferTimelineScopeDto'>>();
    route.onMessage(message => {
      if (typeof message === 'string') for (const frame of message.split('\u001e').filter(Boolean)) {
        const value = JSON.parse(frame);
        if (value.type === 4 && value.target === 'ObserveTransferTimeline') scopes.set(value.invocationId, value.arguments[0]);
      }
      server.send(message);
    });
    server.onMessage(message => {
      if (typeof message !== 'string') { route.send(message); return; }
      for (const frame of message.split('\u001e').filter(Boolean)) {
        const value = JSON.parse(frame);
        const scope = scopes.get(value.invocationId);
        if (scope && value.type === 2 && !scope.additionalTransferIds?.includes(cancelled!.transferId)) {
          value.item.items = value.item.items.filter((row: Schema<'TransferStateDto'>) => row.transferId !== cancelled!.transferId);
          if (value.item.windowTransferIds) {
            value.item.windowTransferIds = value.item.windowTransferIds.filter((id: string) => id !== cancelled!.transferId);
            value.item.nextCursor = null;
            value.item.boundaryTransferId = value.item.windowTransferIds.at(-1) ?? null;
          }
        }
        route.send(JSON.stringify(value) + '\u001e');
      }
    });
  });
  await page.route(url => url.pathname === '/api/transfers' || url.pathname === '/api/transfers/timeline', async (route) => {
    if (new URL(route.request().url()).searchParams.has('groupRef')) {
      if (failGroupRefresh) {
        await route.abort('failed');
        return;
      }
      await route.continue();
      return;
    }
    const response = await route.fetch();
    const body = await response.json() as Schema<'TransferTimelinePageDto'>;
    await route.fulfill({ response, json: {
      ...body, items: body.items.filter((row) => row.transferId !== cancelled!.transferId), nextCursor: null,
    } });
  });
  await page.reload();
  await expect(folder.locator('.folder-file-row')).toHaveCount(1);
  await folder.getByRole('button', { name: 'Load more' }).click();
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  failGroupRefresh = true;
  await daemon.command('release-download');
  await expect(folder).toContainText('1 complete · 1 cancelled');
  await expect(folder.locator('.folder-file-row')).toHaveCount(2);
  await expect(folder.getByRole('button', { name: 'Retry loading folder files' })).toBeVisible();
  failGroupRefresh = false;
  await folder.getByRole('button', { name: 'Retry loading folder files' }).click();
  await expect(folder.getByRole('button', { name: 'Retry loading folder files' })).toHaveCount(0);
  await expect(folder.locator('.folder-transfer-file-count')).toContainText('2');
  await expect(folder.locator('.folder-file-row').filter({ hasText: 'Cancelled.txt' })).toContainText('Cancelled');
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
