# Sockseek WebUI

Production Svelte 5 + TypeScript client for the Sockseek daemon. This application
uses real daemon resources; the original prototype remains in Git history.
For a concise review of the frontend and backend changes, tests, CI cadence,
and remaining gaps, see the [current status](../docs/temp/status.md).

```bash
npm ci
npm run api:generate
npm run check
npm run test
npm run dev
```

Set `SOCKSEEK_DAEMON_URL` when the development daemon is not listening on
`http://127.0.0.1:5030`. Production requests use same-origin `/api` routes.

Publish the distributed CLI/daemon from the repository root with
`dotnet publish Sockseek.Cli/Sockseek.Cli.csproj -c Release`. This runs a clean
frontend install, checks the generated API types, builds the SPA, and places
`wwwroot/` beside the published executable. The daemon serves that directory
even when launched from another working directory. Ordinary .NET builds do
not require Node or build the frontend.

## Keyboard shortcuts

| Context | Shortcut | Action |
| --- | --- | --- |
| Anywhere outside text fields and dialogs | 1–8 | Open Dashboard, Jobs, Downloads, Uploads, Users, Shares, Chat, Settings |
| Anywhere | / | Focus the global search |
| Search results, Jobs, Shares | ↑ / ↓ | Move between visible items; Down loads the next page at its boundary |
| Search results, Shares | Space | Select/deselect the current item |
| Search results, Shares | Tab | Focus controls within the current item |
| Search results | R | Retrieve full contents of fully selected, loaded folder results |
| Search results, Shares | Ctrl+Enter | Download the selection |
| Jobs | ← / → | Switch Searches / Download jobs tabs |
| Jobs | Enter | Open the focused job |
| Shares | → | Expand/collapse the current folder (outside filtering) |
| New Job dialog | Ctrl+Enter | Start the job or accept the reviewed selection |

Search navigation visits top-level files, folders or aggregate groups, not nested
files inside a result. The keyboard cursor is distinct from download selection.
Text editing, native control activation and open dialogs/popovers take priority;
New Job explicitly accepts Ctrl+Enter inside its fields. Disabled actions remain
disabled. Plain Enter does not act as a global download shortcut; focused buttons retain
their normal Enter activation.

## Browser tests

Use Node.js (see `engines` in `package.json`), npm, and the repository's .NET SDK.
From this directory:

```bash
npm ci
npx playwright install chromium
npm run test:e2e
```

On supported Linux distributions, `npx playwright install --with-deps chromium`
also installs browser system dependencies and may require administrator access.
On NixOS, use packaged Chromium instead of Playwright's downloaded Linux binary:

```bash
nix shell nixpkgs#chromium nixpkgs#nodejs_24 -c sh -c \
  'PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH="$(command -v chromium)" npm run test:e2e'
```

This needs neither a NixOS rebuild nor hard-coded Nix store paths. Run Playwright
under Node, not Bun: Bun's Playwright test-loader behavior differs. The executable
override is optional; CI uses Playwright's matching downloaded Chromium.

The command builds the production frontend and test daemon before testing.
Each test starts a real daemon on an assigned loopback port with fresh temporary
storage, fixture state and a copy of the built SPA in the published `wwwroot/`
layout. A worker-scoped driver reuses the .NET process to
amortize runtime/JIT startup; it does not share the application, database, peer
or transfer controls between tests. The next test starts only after the previous
application is disposed and its temporary tree is removed. Browser contexts also
remain test-scoped. Only the
external Soulseek client is replaced with deterministic fixture
callbacks; browser HTTP, SignalR, planning, and persistence remain real. No running
operator daemon, credentials, or public Soulseek connection are required.
Process-global runtime caches and the monotonic display-ID allocator stay warm;
these tests do not assert that every application starts its display IDs at one.
Cold-process packaging/startup and restart qualifications remain separate checks.

Upload journeys opt into `test.use({ uploadShares: true })`. That fixture scans a
temporary shared file, admits a peer request through the real sharing adapter,
and gates the fake external upload at 50%. Other tests do not configure local
shares or incur scan startup. No transfer-history rows are seeded directly.
The fake external download writes the requested file size in fixed-size chunks,
so real daemon progress remains consistent even for richer search fixtures.

Tests live in `e2e/` and run with one worker. Synchronize on visible state or
explicit fixture gates, not fixed sleeps. `npm run test:e2e:ui` opens the interactive
runner. Failed tests retain screenshots, assertion context, and daemon logs under
`test-results/`; failures also attach a bounded daemon status snapshot when the
test daemon remains reachable. CI uploads the HTML report and these artifacts.
Tracing is opt-in: use `npx playwright test --grep "journey name" --trace retain-on-failure`,
then `npx playwright show-trace <path-to-trace.zip>`. Avoid tracing timing probes;
copying the DOM at every action distorts UI latency.
Metrics-enabled journeys also attach `daemon-storage-metrics`: Linux process-attributed
storage writes (including the final WAL backfill), cancelled writes, retained data,
downloaded payload bytes and fixture sizes. Counters are `null` where unavailable. Compare write volume
with these payloads as well as latency; process attribution is not whole-device I/O
or SQLite logical WAL/journal traffic. Investigate amplification separately when needed.
The metric fixture keeps an idle pager attached through shutdown, then materializes
the remaining WAL before sampling. Its final checkpoint time and writes are reported
separately. Closing pooled connections alone can leave a WAL; reopening it for the
measurement can introduce recovery copying rather than measure the remaining work.
If a test times out or its setup fails, teardown preserves that primary error
and attaches the daemon log; an abnormal daemon exit fails an otherwise
passing test.

Keep fast logic tests in `tests/` (`npm test`). Completed E2E checks are recorded in the [verification table](../docs/temp/verification-log.md);
remaining coverage and parity gaps are in [current status](../docs/temp/status.md).

### Test cadence

Every PR runs TypeScript units, normal C# tests, selected bulk regression
cases (`Load` + `CI`), production packaging and the ordinary browser journeys.
Backend and frontend jobs run independently; .NET test hosts remain sequential
and Playwright keeps one worker with no retries. Full C# and browser Load suites
run nightly and through CI's manual **full_load** option.

From the repository root, `dotnet test --maxcpucount:1` excludes Load by default.
Use `--filter 'TestCategory!=Load|TestCategory=CI'` to match PR CI's combined
selection in one invocation, `--filter 'TestCategory=Load&TestCategory=CI'` for
just its bulk subset, or
`--filter 'TestCategory=Load'` for full C# qualification. From `webui/`, run
`SOCKSEEK_RUN_LOAD=1 npx playwright test --grep '@Load'` after building the test
daemon/SPA to run browser Load cases. `Sockseek.Benchmarks` remains explicitly
invoked with `dotnet run`, outside test discovery and CI.

Keep the summed PR test commands below 3m on the maintainer machine, without
raising worker counts. This excludes setup/builds; keep the complete hosted PR
pipeline within 5–7m. Full Load qualification remains nightly/manual.

### What belongs in this suite

Prefer complete user journeys: search with incrementally arriving results, select
and download, review and commit, observe progress, and recover after reload or
reconnection. Add failure cases when they exercise browser/daemon integration or
protect a meaningful user-visible regression. Keep settings permutations, mapping
rules, and backend-only behavior in unit or integration tests rather than repeating
them through the browser.

Use the real daemon for ordinary successful journeys. Tests may interrupt the
transport or hold an actual response to exercise failures and races. A narrowly
scoped browser-boundary case may alter specific fields of a fetched daemon
response to exercise missing or otherwise unreachable wire states; it verifies
the browser's handling of that state, not the daemon's production of it. Do not
replace whole successful workflows with canned frontend fixtures. Prefer
accessible role/name locators and assertions on observable outcomes over
component internals.

Functional tests do not prove visual parity with the prototype. Check representative
layouts and interaction states separately; any screenshot baselines should use a
consistent browser, viewport, and rendering environment. Chromium is the initial
functional gate, not a claim of cross-browser coverage.
