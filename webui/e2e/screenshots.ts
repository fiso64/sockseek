import { test, expect } from './fixtures';
import type { Page } from '@playwright/test';

// Documentation screenshots from the test daemon's invented "showcase" network
// (e2e/daemon/Showcase.cs). Run with: npm run docs:screenshots
const output = new URL('../../docs/pages/screenshots/', import.meta.url).pathname;

test.use({ deviceScaleFactor: 2 });

async function capture(page: Page, name: string): Promise<void> {
  await page.evaluate(() => document.fonts.ready);
  // Let entrance transitions and live updates finish.
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${output}${name}.png` });
}

async function selectFolder(page: Page, peer: string): Promise<void> {
  await page.locator('.peer-item-group', { hasText: peer }).locator('.folder-item-card input[type="checkbox"]').first().check();
}

test('documentation screenshots', async ({ page, daemon }) => {
  test.setTimeout(120_000);
  await daemon.command('showcase');
  await daemon.command('webui-settings:flat:dark:album:relevance');
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/dashboard');
  await page.locator('#global-search-simple').fill('Lake Vesper Static Bloom');
  await page.locator('#global-search-simple').press('Enter');
  await expect(page.getByText('mossgarden', { exact: true }).first()).toBeVisible({ timeout: 30_000 });
  await capture(page, 'search-results');

  await selectFolder(page, 'harbormaster');
  await selectFolder(page, 'vinylwolf');
  await page.locator('.floating-download-action').click();
  await expect(page.locator('.app-notification')).toContainText('requested');
  await page.goto('/downloads');
  await expect(page.getByText('vinylwolf', { exact: true }).first()).toBeVisible();
  await capture(page, 'downloads');

  await page.goto('/shares/mossgarden');
  // The single root folder opens by itself.
  for (const folder of ['Lake Vesper', '2021 - Static Bloom'])
    await page.getByRole('button', { name: `Expand ${folder}`, exact: true }).click();
  await expect(page.getByText('03 - Glass Harbour.flac')).toBeVisible();
  await capture(page, 'shares');

  await page.goto('/dashboard');
  await expect(page.getByRole('img', { name: /Bytes transferred per interval/ })).toBeVisible();
  await capture(page, 'dashboard');
});
