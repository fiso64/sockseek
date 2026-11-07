import { afterEach, expect, it, vi } from 'vitest';
import { browserSession } from '../src/api/session.svelte';
import { DaemonHttpClient } from '../src/api/http';

afterEach(() => {
  browserSession.invalidate(null);
  browserSession.data = null;
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

const session = () => ({ authenticationEnabled: true, authenticated: true, username: 'admin',
  expiresAt: new Date(Date.now() + 60_000).toISOString(), antiforgeryToken: 'csrf' });
const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });

it('does not restore a session from a bootstrap response that arrives after logout', async () => {
  let finish!: (response: Response) => void;
  vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(resolve => { finish = resolve; })));
  const loading = browserSession.refresh();
  browserSession.invalidate(null);
  finish(json(session()));
  await loading;
  expect(browserSession.data?.authenticated).not.toBe(true);
});

it('expires locally, aborts private reads, and ignores an old request after signing in again', async () => {
  vi.useFakeTimers();
  vi.stubGlobal('fetch', vi.fn(async () => json(session())));
  await browserSession.refresh();
  const oldReads = browserSession.requests.signal;
  let finish!: (response: Response) => void;
  vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(resolve => { finish = resolve; })));
  const request = new DaemonHttpClient().get('/server/info');
  const rejected = expect(request).rejects.toMatchObject({ status: 401 });
  await vi.advanceTimersByTimeAsync(60_000);
  expect(oldReads.aborted).toBe(true);
  expect(browserSession.data?.authenticated).toBe(false);
  vi.stubGlobal('fetch', vi.fn(async () => json(session())));
  await browserSession.refresh();
  finish(new Response(null, { status: 401 }));
  await rejected;
  expect(browserSession.data?.authenticated).toBe(true);
  expect(browserSession.headers(undefined, 'POST').get('X-CSRF-Token')).toBe('csrf');
});
