import { test, expect } from './fixtures';
import type { Schema } from '../src/api/types';

test('a pre-archive history response cannot resurrect a successfully removed submission', async ({ page }) => {
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Archive history race fixture', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status(), await response.text()).toBe(202);
  const root = await response.json() as Schema<'JobSummaryDto'>;
  await page.goto(`/jobs/${root.jobId}`);
  const remove = page.locator('.job-detail-heading-summary').getByRole('button', { name: 'Remove', exact: true });
  await expect(remove).toBeVisible();

  let oldCaptured!: () => void, freshCaptured!: () => void;
  let releaseOld!: () => void, releaseFresh!: () => void;
  const oldReady = new Promise<void>((resolve) => { oldCaptured = resolve; });
  const freshReady = new Promise<void>((resolve) => { freshCaptured = resolve; });
  const oldReleased = new Promise<void>((resolve) => { releaseOld = resolve; });
  const freshReleased = new Promise<void>((resolve) => { releaseFresh = resolve; });
  let intercepted = 0;
  await page.route('**/api/jobs?**', async (route) => {
    const url = new URL(route.request().url());
    if (url.searchParams.has('parentJobId') || intercepted >= 2) return route.continue();
    const first = intercepted++ === 0;
    const original = await route.fetch();
    if (first) { oldCaptured(); await oldReleased; }
    else { freshCaptured(); await freshReleased; }
    return route.fulfill({ response: original });
  });
  await page.getByRole('button', { name: 'Jobs', exact: true }).click();
  await oldReady;
  const row = page.locator('.search-history-row').filter({ hasText: 'Archive history race fixture' });
  await row.locator('.search-history-open').click();
  await remove.click();
  await expect(page).toHaveURL(/\/jobs$/);
  releaseOld();
  await freshReady;
  // Hold the newer (actually archived) server response too. The old page must
  // not undo successful removal while waiting for current history to arrive.
  await expect(row).toHaveCount(0);
  releaseFresh();
  await expect(page.getByText('No jobs yet', { exact: true })).toBeVisible();
});
