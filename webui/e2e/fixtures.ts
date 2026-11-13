import { test as base, expect, type Page } from '@playwright/test';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { performance } from 'node:perf_hooks';
import { fileURLToPath } from 'node:url';

const probeKeys = new Map<string, string>();

/** Backend probes bypass Playwright's dispatcher; UI timings use browser DOM marks. */
export async function measureApi(baseUrl: string, path: string, init?: RequestInit) {
  const headers = new Headers(init?.headers);
  const key = probeKeys.get(new URL(baseUrl).origin);
  if (key) headers.set('X-API-Key', key);
  if (!headers.has('Accept-Encoding')) headers.set('Accept-Encoding', 'gzip, deflate, br');
  const target = new URL(path, baseUrl);
  const started = performance.now();
  const issuedAt = Date.now();
  const response = await fetch(target, { ...init, headers,
    signal: init?.signal ?? AbortSignal.timeout(30_000) });
  const body = await response.text();
  const elapsedMs = performance.now() - started;
  // Return the consumed body explicitly so callers cannot try to read it twice.
  return { ok: response.ok, status: response.status, headers: response.headers,
    body, bodyBytes: Buffer.byteLength(body), elapsedMs, issuedAt };
}

const assembly = fileURLToPath(new URL('./daemon/bin/Release/Sockseek.WebUi.TestHost.dll', import.meta.url));
const webRoot = fileURLToPath(new URL('../dist/', import.meta.url));

/** Rendering optimizations must preserve the geometry of every fetched row. */
export async function expectFolderLayoutUnchanged(page: Page, scope = '.transfer-peer-list'): Promise<void> {
  const differences = await page.evaluate((scope) => {
    const nodes = [...document.querySelectorAll(`${scope} .folder-file-row, ${scope} .folder-item-card`)];
    const measure = () => nodes.map(node => {
      const box = node.getBoundingClientRect();
      return [box.x, box.y + scrollY, box.width, box.height];
    });
    const optimized = measure();
    const style = document.createElement('style');
    style.textContent = `${scope} .folder-file-row { content-visibility: visible !important; }`;
    document.head.append(style);
    try {
      return measure().flatMap((box, index) => box.some((value, dimension) => Math.abs(value - optimized[index]![dimension]!) > 0.5)
        ? [{ index, optimized: optimized[index], regular: box }] : []);
    } finally { style.remove(); }
  }, scope);
  expect(differences, 'Fetched row/card geometry must match ordinary rendering.').toEqual([]);
}

/** Keep a manual-Retry assertion independent of startup's automatic recovery. */
export async function deferInitialDaemonSnapshot(page: Page): Promise<() => void> {
  let release!: () => void;
  const released = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/daemon/snapshot*', async route => {
    await released;
    await route.continue();
  });
  return release;
}

type FixtureCommand = `configuration:${boolean}:${boolean}:${boolean}` | `webui-settings:${'dark' | 'light' | 'auto'}:${'generic' | 'track' | 'album' | 'song-groups' | 'album-groups'}` | `prune-search-results:${string}` | `wait-job-complete:${string}` | 'hold-download' | 'wait-download-started' | 'wait-search-started' | 'release-download' | 'reject-next-download' | 'report-peer-browses' | 'music-search' | 'ordinary-track-search'
  | 'large-search' | 'large-directory-search' | 'large-distributed-search' | 'staged-large-directory-search' | 'large-music-search' | 'large-music-search-100k' | 'large-single-peer-music-search' | 'large-varied-music-search' | 'large-shared-album-search' | 'paced-music-search' | 'large-peer-shares' | 'large-retained-transfers' | 'retained-imports' | 'expire-search-views' | 'expire-job-previews' | 'forget-job-previews' | 'mixed-visibility-search' | 'staged-rich-group-search'
  | 'empty-search' | 'staged-search' | 'next-search-batch' | 'finish-search' | 'incoming-chat' | 'directory-selection-search' | 'concurrent-retrieval-search' | 'mixed-retrieval-search' | 'hold-directory-browse' | 'release-directory-browse' | 'group-search' | 'rich-group-search' | 'album-group-search' | 'rich-album-group-search' | 'paged-album-group-search'
  | 'fail-next-upload' | 'request-upload' | 'request-queued-upload' | 'release-upload' | 'incoming-room-chat' | 'seed-chat-history' | 'seed-chat-outage'
  | 'album-art-search' | 'large-album-art-search' | 'bare-filename-search' | 'hold-art-download' | 'release-art-download' | 'wait-directory-complete' | 'staged-conditioned-music-search' | 'mixed-album-group-search'
  | 'long-song-fallback' | 'report-fallback-downloads' | `report-search-projection:${string}` | 'terminal-transfer-committed' | 'presence-away';

function startDaemonWorker() {
  const child = spawn('dotnet', [assembly, webRoot], { stdio: ['pipe', 'pipe', 'pipe'] });
  const logs: string[] = [];
  const record = (line: string) => { logs.push(line.startsWith('E2E_READY ') ? 'E2E_READY [credentials omitted]' : line); if (logs.length > 500) logs.shift(); };
  const stdout = createInterface({ input: child.stdout });
  const stderr = createInterface({ input: child.stderr });
  stdout.on('line', record);
  stderr.on('line', record);
  let spawnError: Error | null = null;
  let failed = false;
  child.on('error', error => { spawnError = error; });
  // Request write callbacks handle an EPIPE; keep the stream error from also
  // becoming an unhandled exception in the Playwright worker.
  child.stdin.on('error', () => undefined);
  const exited = new Promise<number | null>(resolve => child.once('close', resolve));

  async function request(command: string, prefix: string, timeoutMs: number): Promise<string> {
    try {
      return await new Promise<string>((resolve, reject) => {
        if (spawnError || child.exitCode !== null || child.signalCode !== null) {
          reject(spawnError ?? new Error(`Daemon worker already exited (${child.exitCode ?? child.signalCode}).`));
          return;
        }
        const timeout = setTimeout(() => finish(new Error(`Daemon fixture timed out waiting for ${prefix}.`)), timeoutMs);
        function cleanup(): void {
          clearTimeout(timeout);
          stdout.off('line', received);
          child.off('close', closed);
          child.off('error', errored);
        }
        function finish(error: Error | null, line = ''): void {
          cleanup();
          if (error) reject(error); else resolve(line);
        }
        function received(line: string): void { if (line.startsWith(prefix)) finish(null, line); }
        function closed(code: number | null): void { finish(new Error(`Daemon worker exited while waiting for ${prefix} (${code}).`)); }
        function errored(error: Error): void { finish(error); }
        stdout.on('line', received);
        child.once('close', closed);
        child.once('error', errored);
        child.stdin.write(command + '\n', error => { if (error) finish(error); });
      });
    } catch (error) {
      failed = true;
      throw error;
    }
  }

  return {
    logs: () => [...logs],
    async start(uploads: boolean, metrics: boolean, applicationLogs: boolean): Promise<string> {
      logs.length = 0;
      const ready = await request(JSON.stringify({ Uploads: uploads, Metrics: metrics, ApplicationLogs: applicationLogs }), 'E2E_READY ', 30_000);
      const { url, apiKey } = JSON.parse(ready.slice('E2E_READY '.length)) as { url: string; apiKey: string };
      probeKeys.set(new URL(url).origin, apiKey);
      return url;
    },
    async command(command: FixtureCommand): Promise<void> {
      await request(command, `E2E_ACK ${command}`, command === 'large-retained-transfers' ? 60_000 : command === 'wait-directory-complete' || command.startsWith('wait-job-complete:') ? 125_000 : command === 'wait-download-started' ? 35_000 : 5_000);
    },
    async stop(): Promise<{ workingSetBytes: number; managedBytes: number }> {
      const stopped = await request('stop', 'E2E_STOPPED ', 15_000);
      return JSON.parse(stopped.slice('E2E_STOPPED '.length)) as { workingSetBytes: number; managedBytes: number };
    },
    async close(): Promise<void> {
      if (!child.stdin.destroyed) child.stdin.end();
      const timeout = setTimeout(() => child.kill('SIGKILL'), 15_000);
      const code = await exited;
      clearTimeout(timeout);
      stdout.close();
      stderr.close();
      if (code !== 0 && !failed) throw new Error(`Daemon worker did not shut down cleanly (${code}).`);
    },
  };
}

type DaemonWorker = ReturnType<typeof startDaemonWorker>;

export const test = base.extend<{
  authenticated: boolean;
  uploadShares: boolean;
  daemonLogMetrics: boolean;
  daemonAssertFastReads: boolean;
  daemonApplicationLogs: boolean;
  daemon: { url: string; command: (command: FixtureCommand) => Promise<void>; logs: () => string[] };
  daemonUrl: string;
}, { daemonWorker: DaemonWorker }>({
  context: async ({ context, daemon, authenticated }, use, testInfo) => {
    if (authenticated) {
      const bootstrap = await context.request.get(`${daemon.url}/api/auth/session`);
      const { antiforgeryToken } = await bootstrap.json() as { antiforgeryToken: string };
      const login = await context.request.post(`${daemon.url}/api/auth/login`, {
        data: { username: 'admin', password: 'test-only-password' },
        headers: { 'X-CSRF-Token': antiforgeryToken },
      });
      expect(login.status()).toBe(204);
      const session = await (await context.request.get(`${daemon.url}/api/auth/session`)).json() as { antiforgeryToken: string };
      await context.setExtraHTTPHeaders({ 'X-CSRF-Token': session.antiforgeryToken });
    }
    // Localhost is a secure context even over HTTP. Exercise the API availability
    // of plain HTTP LAN access in every browser journey, without duplicating them.
    await context.addInitScript(() => {
      Object.defineProperty(crypto, 'randomUUID', { value: undefined, configurable: true });
    });
    const errors: Error[] = [];
    const record = (error: import('@playwright/test').WebError) => { errors.push(error.error()); };
    context.on('weberror', record);
    try { await use(context); }
    finally {
      context.off('weberror', record);
      if (errors.length) {
        await testInfo.attach('uncaught-browser-errors', {
          body: errors.map(error => error.stack ?? String(error)).join('\n\n'), contentType: 'text/plain',
        });
        if (testInfo.status === testInfo.expectedStatus)
          throw new AggregateError(errors, `${errors.length} uncaught browser error(s).`);
      }
    }
  },
  daemonWorker: [async ({}, use) => {
    const worker = startDaemonWorker();
    try { await use(worker); } finally { await worker.close(); }
  }, { scope: 'worker' }],
  authenticated: [true, { option: true }],
  uploadShares: [false, { option: true }],
  daemonLogMetrics: [false, { option: true }],
  // For flows whose measured reads are bounded daemon queries. Peer requests
  // and streamed exports need separate latency expectations.
  daemonAssertFastReads: [false, { option: true }],
  daemonApplicationLogs: [false, { option: true }],
  daemon: async ({ daemonWorker, uploadShares, daemonLogMetrics, daemonAssertFastReads, daemonApplicationLogs }, use, testInfo) => {
    const collectMetrics = daemonLogMetrics || daemonAssertFastReads;
    const fixtureStarted = performance.now();
    let runningUrl: string | null = null;
    let fixtureFailed = false;
    let startupMs = 0;
    try {
      const url = await daemonWorker.start(uploadShares, collectMetrics, daemonApplicationLogs);
      runningUrl = url;
      startupMs = Math.round(performance.now() - fixtureStarted);
      if (testInfo.title.includes('@Load'))
        console.log(`E2E_FIXTURE startup-ms=${startupMs}`);
      await use({ url, logs: daemonWorker.logs, command: daemonWorker.command });
    } catch (error) {
      fixtureFailed = true;
      throw error;
    } finally {
      const teardownStarted = performance.now();
      if (testInfo.status !== testInfo.expectedStatus && runningUrl) {
        try {
          const response = await fetch(`${runningUrl}/api/server/status`, {
            signal: AbortSignal.timeout(2_000),
            headers: { 'X-API-Key': probeKeys.get(new URL(runningUrl).origin)! },
          });
          await testInfo.attach('daemon-status', {
            body: (await response.text()).slice(0, 20_000),
            contentType: response.ok ? 'application/json' : 'text/plain',
          });
        } catch {
          // A failed or stopped daemon cannot provide a status snapshot.
        }
      }
      let stopError: unknown;
      let memory: { workingSetBytes: number; managedBytes: number } | undefined;
      try { memory = await daemonWorker.stop(); } catch (error) { stopError = error; }
      if (runningUrl) probeKeys.delete(new URL(runningUrl).origin);
      if (collectMetrics && !stopError) {
        const summary = daemonWorker.logs().reverse().find(line => line.startsWith('E2E_READ_SUMMARY '))?.slice('E2E_READ_SUMMARY '.length);
        if (summary) {
          await testInfo.attach('daemon-read-metrics', { body: summary, contentType: 'application/json' });
          const counts = JSON.parse(summary) as {
            started: number; completed: number; successful: number; under300: number;
          };
          if (counts.started !== counts.completed)
            stopError = new Error(`Read diagnostics missed completed requests: ${counts.completed}/${counts.started}.`);
          // Include intermediate/background pages, not just the final requests
          // a test explicitly measures, without making extra HTTP requests.
          if (daemonAssertFastReads && counts.under300 !== counts.successful)
            stopError ??= new Error(`${counts.successful - counts.under300} successful daemon read(s) exceeded 300 ms; see daemon-read-metrics.`);
        } else stopError = new Error('The metrics-enabled daemon omitted its final read summary.');
        const storage = daemonWorker.logs().reverse().find(line => line.startsWith('E2E_STORAGE_SUMMARY '))?.slice('E2E_STORAGE_SUMMARY '.length);
        if (storage) {
          await testInfo.attach('daemon-storage-metrics', { body: storage, contentType: 'application/json' });
        } else stopError ??= new Error('The metrics-enabled daemon omitted its final storage summary.');
        const persistence = daemonWorker.logs().reverse()
          .find(line => line.startsWith('E2E_PERSISTENCE_SUMMARY '))?.slice('E2E_PERSISTENCE_SUMMARY '.length);
        if (persistence) {
          await testInfo.attach('daemon-persistence-summary', { body: persistence, contentType: 'application/json' });
          const losses = JSON.parse(persistence) as Record<string, number>;
          if (Object.values(losses).some(count => count !== 0))
            stopError ??= new Error(`Persistence lost work during the scenario: ${persistence}`);
        } else stopError ??= new Error('The metrics-enabled daemon omitted its final persistence summary.');
      }
      const teardownMs = Math.round(performance.now() - teardownStarted);
      if (testInfo.title.includes('@Load')) console.log(`E2E_FIXTURE teardown-ms=${teardownMs}`);
      await testInfo.attach('daemon-fixture-timings', {
        body: JSON.stringify({ startupMs, teardownMs, ...memory }), contentType: 'application/json',
      });
      if (collectMetrics || fixtureFailed || testInfo.status !== testInfo.expectedStatus || stopError)
        await testInfo.attach('daemon.log', { body: daemonWorker.logs().join('\n'), contentType: 'text/plain' });
      // Preserve an existing primary test/setup error, but fail a passing test
      // if its daemon did not fully stop and remove its temporary resources.
      if (stopError && !fixtureFailed && testInfo.status === testInfo.expectedStatus) throw stopError;
    }
  },
  daemonUrl: async ({ daemon }, use) => { await use(daemon.url); },
  baseURL: async ({ daemonUrl }, use) => { await use(daemonUrl); },
});

export { expect };
