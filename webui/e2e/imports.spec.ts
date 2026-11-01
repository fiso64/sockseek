import { test, expect, deferInitialDaemonSnapshot } from './fixtures';
import type { Schema } from '../src/api/types';

test('folds a running text extraction into one source-labelled root and opens its retained result', async ({ page, daemon }) => {
  await daemon.command('music-search');
  await daemon.command('hold-download');
  // Seed an ordinary public extraction submission, as a remote CLI can.
  // New Job preview commit exercises the same import presentation separately.
  const accepted = await page.request.post('/api/jobs/extract', { data: {
    input: 'Fixture song', inputType: 'string', options: {
      downloadSettings: { extraction: { requestedMode: 0 /* ServerExtractionMode.Song */ } },
    },
  } satisfies Schema<'SubmitExtractJobRequestDto'> });
  expect(accepted.status()).toBe(202);
  const submitted = await accepted.json() as Schema<'JobSummaryDto'>;
  const detailReads: string[] = [];
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === 'GET' && /^\/api\/jobs\/[^/]+$/.test(path)) detailReads.push(path);
  });
  // The HTTP list/lookup must show the active result before live hydration.
  // Retiring it while that snapshot is held then exercises cache reconciliation.
  const releaseSnapshot = await deferInitialDaemonSnapshot(page);
  let observeRetired!: () => void;
  const retired = new Promise<void>(resolve => { observeRetired = resolve; });
  page.on('websocket', socket => socket.on('framereceived', ({ payload }) => {
    for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
      const message = JSON.parse(frame) as { target?: string; arguments?: Schema<'StateUpdateBatchDto'>[] };
      if (message.target?.toLowerCase() === 'stateupdatebatch'
        && message.arguments?.[0]?.state?.removedWorkflowIds.includes(submitted.workflowId!)) observeRetired();
    }
  }));
  try {
    await page.goto('/jobs');
    const row = page.locator('.search-history-row').filter({ hasText: 'Fixture song' });
    await expect(page.locator('.search-history-row')).toHaveCount(1);
    await expect(row.locator('.search-status-badge')).toHaveText('Running');
    await expect(row.locator('.job-type-badge')).toHaveText('Text import');
    await expect(row.getByRole('button', { name: 'Cancel Fixture song', exact: true })).toBeVisible();
    await expect(row.getByRole('button', { name: 'Remove Fixture song', exact: true })).toHaveCount(0);
    expect(detailReads.filter(path => path === `/api/jobs/${submitted.jobId}`),
      'Import context must use the job summary rather than fetch its source payload.').toHaveLength(0);
    const response = await page.request.get(`/api/jobs?submissionId=${submitted.submissionId}&includeAll=false&archived=false&limit=100`);
    const roots = await response.json() as Schema<'JobSummaryDto'>[];
    const imported = roots.find((job) => job.kind === 'extract')!;
    expect(imported.resultJobId).toBeTruthy();
    await daemon.command('release-download');
    await retired;
    releaseSnapshot();
    await expect(row.locator('.search-status-badge')).toHaveText('Complete');
    await row.locator('.search-history-open').click();
    await expect(page).toHaveURL(new RegExp(`/jobs/${imported.resultJobId}$`));
    await expect(page.locator('.automatic-job-detail h1')).toHaveText('Fixture song');
    await expect(page.locator('.job-detail-kicker')).not.toContainText('Extract');
    await page.reload();
    await expect(page.locator('.automatic-job-detail h1')).toHaveText('Fixture song');
    // An old import URL is a presentation entry point to its retained result too.
    await page.goto(`/jobs/${imported.jobId}`);
    await expect(page).toHaveURL(new RegExp(`/jobs/${imported.resultJobId}$`));
    await expect(page.locator('.automatic-job-detail h1')).toHaveText('Fixture song');
  } finally { releaseSnapshot(); }
});
