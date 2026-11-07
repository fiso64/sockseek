import { test, expect } from './fixtures';
import type { Schema } from '../src/api/types';

test('a cancelled list snapshot cannot resurrect a deleted submission', async ({ page }) => {
  const response = await page.request.post('/api/jobs/lists', { data: {
    name: 'Deletion history race fixture', jobs: [{ kind: 'remote-file', target: {
      username: 'fixture-peer', filename: 'Fixture\\Fixture song.txt', size: 128, extension: '.txt',
    } }],
  } satisfies Schema<'SubmitJobListRequestDto'> });
  expect(response.status(), await response.text()).toBe(202);
  const root = await response.json() as Schema<'JobSummaryDto'>;
  await page.goto(`/jobs/${root.jobId}`);
  const remove = page.locator('.job-detail-heading-summary').getByRole('button', { name: 'Delete', exact: true });
  await expect(remove).toBeVisible();

  let oldCaptured!: () => void, freshCaptured!: () => void;
  let releaseOld = () => {}, releaseFresh = () => {};
  const oldReady = new Promise<void>(resolve => { oldCaptured = resolve; });
  const freshReady = new Promise<void>(resolve => { freshCaptured = resolve; });
  let observations = 0;
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    const scopes = new Map<string, number>();
    route.onMessage(payload => {
      if (typeof payload === 'string') for (const frame of payload.split('\u001e').filter(Boolean)) {
        const message = JSON.parse(frame);
        if (message.type === 4 && message.target === 'ObserveJobList')
          scopes.set(message.invocationId, ++observations);
      }
      server.send(payload);
    });
    server.onMessage(payload => {
      if (typeof payload !== 'string') { route.send(payload); return; }
      for (const frame of payload.split('\u001e').filter(Boolean)) {
        const message = JSON.parse(frame);
        const scope = scopes.get(message.invocationId);
        if (message.type === 2 && message.item.windowJobIds && scope === 2) {
          releaseOld = () => route.send(frame + '\u001e');
          oldCaptured();
        } else if (message.type === 2 && message.item.windowJobIds && scope === 3) {
          releaseFresh = () => route.send(frame + '\u001e');
          freshCaptured();
        } else route.send(frame + '\u001e');
      }
    });
  });
  // Install the interceptor on a fresh connection. Keep the first displayed
  // row while the second scope's pre-deletion snapshot is delayed.
  await page.goto('/jobs?tab=downloads');
  const row = page.locator('.search-history-row').filter({ hasText: 'Deletion history race fixture' });
  await expect(row).toBeVisible();
  await row.locator('.search-history-open').click();
  await page.getByRole('button', { name: 'Jobs', exact: true }).click();
  await oldReady;
  await row.locator('.search-history-open').click();
  await remove.click();
  await expect(page).toHaveURL(/\/jobs\?tab=downloads$/);
  releaseOld();
  await freshReady;
  // The old cancelled scope must not undo successful removal while the
  // replacement observation is still waiting for its initial snapshot.
  await expect(row).toHaveCount(0);
  releaseFresh();
  await expect(page.getByText('No download jobs yet', { exact: true })).toBeVisible();
});
