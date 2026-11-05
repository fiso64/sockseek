# Browser tests

The suite exercises real daemon planning, uploaded CSV parsing, settings
overrides, search projections, reopening a search URL, and an individually
selected file download, and recovering across a real SignalR reconnect. Search cases also verify intermediate result/count updates before job
completion. New Job cases exercise provisional settings before upload, paged
preview paging, and reviewed/direct submission.

Reconnect tests use Playwright WebSocket routing to close and reconnect the
actual daemon connection; messages are not fabricated. Chromium offline
emulation alone did not close an already-established WebSocket in this setup.
The `incoming-room-chat` fixture command raises a deterministic Soulseek room
event through the same injected peer seam; the browser still reads it through
the real daemon's Chat state and live subscription.

## Runner conventions

- Run `npm ci`, then `npm run test:e2e` from `webui/`.
- Both `test:e2e` and `test:e2e:ui` build the frontend and Release .NET test host first.
  Install the .NET 10 SDK as well as Node 24. Direct `npx playwright test` is
  only appropriate after building those prerequisites.
- Browser workflows and performance probes use the same Release daemon build.
  Earlier Debug-host measurements in the audit log retain their original scope.
- Chromium only, one worker, no automatic retries masking intermittent failures.
- Use locator assertions and observable API/lifecycle transitions, not sleeps.
- Keep browser tests here and controller/unit tests in `tests/`.
- Failure artifacts go to ignored `test-results/` and `playwright-report/`.
- Screenshots, assertion context and daemon logs are retained on failure. Tracing
  is opt-in (`npx playwright test --grep "journey name" --trace retain-on-failure`)
  so successful CI actions do not all capture DOM snapshots. Do not trace Load
  timing probes.
- Backend and browser CI jobs run independently on separate runners. Within each
  job, test hosts remain sequential. Failure artifacts are uploaded for seven days.
- CI uses the Playwright-managed browser installed by
  `npx playwright install --with-deps chromium` on its disposable Linux runner.
  Do not run that dependency-install command on a maintainer's NixOS machine.

## Coverage boundaries

Use the browser for representative complete journeys and UI-specific failures,
not another copy of every controller/backend permutation. In particular:

- `tests/search-controller.test.ts` owns selection revisions, hidden readers,
  stale responses, aggregate choices and retry identity; server Search View tests
  verify actual counting/commit of unseen descendants and failure isolation.
- `tests/share-selection.test.ts`, `tests/preview-selection.test.ts` and backend
  planning tests own nested selection combinations. Browser cases still exercise
  actual checkboxes, paging, Review and downloading selected files.
- Keep the five search modes, uploads/downloads, chat, offline/reconnect, imports,
  job tabs, retention and representative failure/retry flows in the browser suite.
- Seed history with domain batches when transport is not under test; advance the
  browser clock for retry deadlines and await responses before dependent edits.
  Focus the tab being exercised: a background tab throttles animation frames.

## NixOS

Use packaged Chromium, not Playwright's downloaded Ubuntu executable. The
latter requires libraries outside its bundle and does not run directly on
NixOS. The supported executable override is
`PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH`.

From `webui/`, provide real Node/npm together with Chromium. A Bun-backed Node
shim does not support Playwright's TypeScript loader:

```sh
nix shell nixpkgs#chromium nixpkgs#nodejs_24 -c sh -c 'PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH="$(command -v chromium)" npm run test:e2e'
```

This does not require sudo, a system rebuild, manual library paths, or a
hardcoded `/nix/store` path. CI's managed Chromium remains the canonical browser
for visual baselines, since local nixpkgs may provide a different version.

## Opt-in performance probes

After building the frontend and test host, run
`SOCKSEEK_RUN_LOAD=1 npx playwright test --grep '@Load'` to include the marked
large-data cases. They cover all five 10k-result search modes, a 100k-file peer
share, request/rendering costs, 10k-row CSV Review with either one exact
choice or all-except one, and a 100k-row retained-transfer timeline. They are
excluded from the default browser CI run. The latter seeds only history in the
fresh migrated database, then uses real endpoints/projections and one actual
download; it does not execute 100k transfers or claim accounting ingestion coverage.
Bulk plan tests measure durable acceptance and then cancel held work; they do
not claim execution of every selected download. Keep these runs separate from
other test hosts when collecting timings.

Probes using `daemonLogMetrics` retain their bounded daemon log on success as
well as failure, including fixture-only persistence commit durations/batch sizes
and existing operation-owner phase timings. Ordinary tests keep failure-only
logs. Compare request/payload counts, UI milestones and backend phases separately;
one fast HTTP call or passing scenario does not establish whole-stack latency.

Metrics-enabled probes attach `daemon-read-metrics` separately from the bounded
log tail: complete API GET counts/latency buckets and the ten slowest samples.
Event transport and `/updates` long polls are excluded. SQL, reader lifetime,
connection and GC boundaries are not additive exclusive costs. Process CPU is a
coarse whole-process span including concurrent workers; it can exceed request
wall time and must not be reported as exclusive request/SQL CPU. Keep cold
first-use samples visible alongside warmed samples.
Read summaries retain UTC start timestamps; slow reads also include the native
thread at completion on Linux, including non-EF readers. That thread is a
correlation hint, not the identity of every earlier async continuation.

The five-mode probe also observes result DOM availability from the actual Enter
event, retains browser resource timings, and samples cumulative renderer costs.
Locator assertions are watchdogs; their retry intervals are not rendering time.
DOM availability is not a paint measurement, and renderer counters include
browser/test activity rather than exclusive application CPU.
The timeline probe reports DOM milestones to the real test clock through a
browser binding, preserving its virtual-clock refresh checks. Notification
transport is included; a later retained HTTP response is a separate milestone.

The automatic aggregate Load journey writes about 1 GB across 110 files and
indexes large peer shares. Its 60-second terminal-state allowance is separate
from the 100-ms metadata read guards; it is an observable condition wait, not a
fixed delay or an ordinary-CI workload.

## Daemon isolation contract

The fixture must start the real server against a fresh temporary data/output
directory and an injected deterministic Soulseek client. Use the existing
`ServerOptions.ClientFactory` seam; ordinary end-to-end journeys must not mock
browser HTTP/SignalR calls. A separately identified browser-boundary case may
alter specific fields of a fetched real HTTP response to simulate a legacy or
otherwise unreachable wire state; do not count it as daemon behavior evidence.
Never fabricate SignalR messages. The server must not use operator configuration,
credentials, shared files, or a daemon already listening on a guessed port.
The worker driver owns one warm .NET process; each test starts and disposes a
new application, service container, peer and freshly migrated temporary
data/output tree. The worker lazily prepares an empty database with the real
migrations once, checkpoints it, and copies it into each test's own data tree.
Each copy renews the time-sensitive accounting seed and still runs normal
initialization, integrity checks and runtime reconciliation. No application
runtime, jobs, chats, configuration or artifacts are taken from another test.
Native migration tests continue to run migrations directly; this fixture does
not replace that coverage. The one-time template cost is included in the first
requested case, and separately logged when metrics are enabled.
Stop acknowledgement follows disposal and deletion. Runtime
caches and monotonic display-ID allocation can remain warm; a cold-process
packaging/restart check is a separate qualification. Bind an ephemeral loopback
port and expose it to the browser fixture. Do not share application services or
test data between cases to save setup time.

Required first workflows: search/live results/selected download, New Job review
and commit, and reopening/reconnecting. Rendered prototype comparisons are a
separate fidelity check, not a substitute for daemon workflow tests.
