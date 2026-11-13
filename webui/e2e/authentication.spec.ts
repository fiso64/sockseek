import { test, expect } from './fixtures';

test.use({ authenticated: false });

test('signs in, manages a CLI key in System, and revokes the browser session on logout', async ({ page, daemon }) => {
  const applicationChunk = /\/assets\/App-[^/]+\.js$/;
  let applicationRequests = 0;
  let documentRequests = 0;
  page.on('request', request => {
    if (applicationChunk.test(request.url())) applicationRequests++;
    if (request.isNavigationRequest()) documentRequests++;
  });
  await page.route('**/api/auth/session', route => route.abort('failed'));
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Retry connection' })).toBeVisible();
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await page.unroute('**/api/auth/session');
  await page.getByRole('button', { name: 'Retry connection' }).click();
  await expect(page.getByRole('form', { name: 'Log in to Sockseek' })).toBeVisible();
  await page.getByLabel('Username').fill('admin');
  await page.getByLabel('Password').fill('wrong');
  await page.getByRole('button', { name: 'Log in', exact: true }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  expect(applicationRequests).toBe(0);
  await page.route(applicationChunk, route => route.abort('failed'), { times: 1 });
  await page.getByLabel('Password').fill('test-only-password');
  await page.getByRole('button', { name: 'Log in', exact: true }).click();
  // A missing chunk must leave the authenticated session intact. Reloading is
  // explicit because doing so automatically could discard unsaved work.
  await expect(page.getByRole('alert').getByRole('button', { name: 'Reload page' })).toBeVisible();
  await expect(page.getByRole('form', { name: 'Log in to Sockseek' })).toHaveCount(0);
  expect(documentRequests).toBe(1);
  await page.getByRole('button', { name: 'Reload page' }).click();
  await page.getByRole('navigation', { name: 'Secondary navigation' }).getByRole('button', { name: 'System' }).click();
  await page.getByRole('tablist', { name: 'System sections' }).getByRole('tab', { name: 'API', exact: true }).click();
  await page.getByRole('button', { name: 'Create API key', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Name').fill('Laptop CLI');
  await dialog.getByRole('button', { name: 'Create key', exact: true }).click();
  const secret = await dialog.getByLabel('API key', { exact: true }).inputValue();
  expect(secret).toMatch(/^ssk_[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}$/);
  const remote = await page.request.get(`${daemon.url}/api/server/info`, { headers: { 'X-API-Key': secret } });
  expect(remote.status()).toBe(200);
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await page.getByRole('button', { name: 'Revoke Laptop CLI' }).click();
  await dialog.getByRole('button', { name: 'Revoke key' }).click();
  await expect(dialog).toHaveCount(0);
  expect((await page.request.get(`${daemon.url}/api/server/info`, { headers: { 'X-API-Key': secret } })).status()).toBe(401);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('form', { name: 'Log in to Sockseek' })).toBeVisible();
  expect((await page.request.get(`${daemon.url}/api/server/info`)).status()).toBe(401);
});

test.describe('authenticated startup', () => {
  test.use({ authenticated: true });

  test('keeps login hidden throughout a delayed session check on reload', async ({ page }) => {
    await page.goto('/system');
    await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible();
    await page.addInitScript(() => {
      const state = { loginFlashed: false };
      Object.assign(window, { authViewCheck: state });
      new MutationObserver(() => {
        if (document.querySelector('.login-screen')) state.loginFlashed = true;
      }).observe(document, { childList: true, subtree: true });
    });
    let release!: () => void;
    let requested!: () => void;
    const held = new Promise<void>(resolve => { release = resolve; });
    const started = new Promise<void>(resolve => { requested = resolve; });
    await page.route('**/api/auth/session', async route => {
      requested();
      await held;
      await route.continue();
    });
    try {
      await page.reload();
      await started;
      await expect(page.getByRole('status', { name: 'Checking session' })).toBeVisible();
      await expect(page.getByRole('main')).toHaveCSS('background-image', 'none');
      await expect(page.getByRole('main')).toHaveCSS('background-color',
        await page.evaluate(() => getComputedStyle(document.body).backgroundColor));
      await expect(page.getByRole('form', { name: 'Log in to Sockseek' })).toHaveCount(0);
      await expect(page.getByRole('navigation')).toHaveCount(0);
    } finally { release(); }
    await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible();
    expect(await page.evaluate(() => (window as unknown as { authViewCheck: { loginFlashed: boolean } }).authViewCheck.loginFlashed)).toBe(false);
    await expect(page).toHaveURL(/\/system$/);
  });
});
