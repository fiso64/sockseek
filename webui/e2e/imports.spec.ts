import { test, expect } from './fixtures';
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
  let summaryReads = 0, observations = 0;
  let observeResult!: () => void;
  const resultObserved = new Promise<void>(resolve => { observeResult = resolve; });
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === 'GET' && /^\/api\/jobs\/[^/]+$/.test(path)) detailReads.push(path);
    if (path === '/api/jobs/lookup') summaryReads++;
  });
  page.on('websocket', socket => socket.on('framesent', ({ payload }) => {
    for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
      if (JSON.parse(frame).target === 'ObserveJobs') { observations++; observeResult(); }
    }
  }));
  // The canonical list must retain its imported result through persistence retirement.
  let observeRetired!: () => void;
  const retired = new Promise<void>(resolve => { observeRetired = resolve; });
  page.on('websocket', socket => socket.on('framereceived', ({ payload }) => {
    for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
      const message = JSON.parse(frame) as { target?: string; arguments?: Schema<'StateUpdateBatchDto'>[] };
      if (message.target?.toLowerCase() === 'stateupdatebatch'
        && message.arguments?.[0]?.state?.removedWorkflowIds.includes(submitted.workflowId!)) observeRetired();
    }
  }));
  await page.goto('/jobs?tab=downloads');
  const row = page.getByTestId('job-row').filter({ hasText: 'Fixture song' });
  await expect(page.locator('.search-history-row')).toHaveCount(1);
  await expect(row).toHaveAttribute('data-job-kind', 'song');
  await expect(row.locator('.search-status-badge')).toHaveClass(/\breceiving\b/);
  await expect(row.getByRole('img', { name: 'Text import' })).toHaveAttribute('title', 'Text import');
  await expect(row.getByTestId('job-cancel')).toBeVisible();
  await expect(row.getByTestId('job-remove')).toHaveCount(0);
  expect(detailReads.filter(path => path === `/api/jobs/${submitted.jobId}`),
    'Import context must use the job summary rather than fetch its source payload.').toHaveLength(0);
  const response = await page.request.get(`/api/jobs?submissionId=${submitted.submissionId}&includeAll=false&limit=100`);
  const roots = await response.json() as Schema<'JobSummaryDto'>[];
  const imported = roots.find((job) => job.kind === 'extract')!;
  expect(imported.resultJobId).toBeTruthy();
  await resultObserved;
  await daemon.command('release-download');
  await retired;
  await expect(row).toBeVisible();
  await expect(row.locator('.search-status-badge')).toHaveClass(/\bcomplete\b/);
  await expect(row.getByTestId('job-cancel')).toHaveCount(0);
  await expect(row.getByTestId('job-remove')).toBeVisible();
  expect(observations).toBe(1);
  expect(summaryReads, 'Progress and retirement must not refetch the linked summary.').toBe(1);
  await row.getByTestId('job-open').click();
  await expect(page).toHaveURL(new RegExp(`/jobs/${imported.resultJobId}$`));
  await expect(page.locator('.automatic-job-detail h1')).toHaveText('Fixture song');
  await page.reload();
  await expect(page.locator('.automatic-job-detail h1')).toHaveText('Fixture song');
  // An old import URL is a presentation entry point to its retained result too.
  await page.goto(`/jobs/${imported.jobId}`);
  await expect(page).toHaveURL(new RegExp(`/jobs/${imported.resultJobId}$`));
  await expect(page.locator('.automatic-job-detail h1')).toHaveText('Fixture song');
});
