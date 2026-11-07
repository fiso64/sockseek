# Sockseek engineering guidance

Sockseek is a self-hosted homeserver. Remote peers are untrusted; the operator,
configured storage, and ordinary local administration are trusted.

* New features should usually be considered against local CLI, remote CLI and the
  UI. Do not let local vs remote CLI and CLI vs webui implementations diverge.
* Keep plain user searches to raw collection, deduplication and cheap counters.
  Run sorting/grouping for requested views or automatic execution, and stop
  incremental projection work when readers stop requesting it.
* Handle valid work best effort. Do not reject it because of estimated memory,
  aggregate size, queue depth, or other internal implementation limits. Bound resource
  use where it will be clearly desired, do not over-engineer ahead of time.
  Good example: Introducing pagination for daemon uploads and downloads.
  Bad example: Introducing pagination for user chats without an obvious need.
  * Tangentially: Pagination sizes (where pagination is needed at all) should be large
  and err on the side of fewer clicking than minimizing page sizes and making it awkward.
* Isolate failures to the smallest independent entry, request, root, or transfer,
  within reason.
  Good example: When a transfer fails, other transfers are not failed automatically.
  Bad example: Obsessively endowing every single feature with a separate lifecycle
  and developing a million variations of "degraded" daemon states.
* Preserve exact Soulseek identities and wire spelling unless verified protocol
  semantics require otherwise.
* Prefer one clear owner, model, and contract. Centralize and simplify behavior;
  avoid parallel abstractions, speculative configuration, unnecessary compatibility
  layers, and independent persistence lifecycles unless consistency or failure
  isolation genuinely requires them. Moreover, if it turns out that a feature or
  bugfix could be implemented more cleanly with a preliminary refactor, that refactor
  is encouraged (even if large), before rushing to implement the feature or fix the bug.
* Organize by feature and give human readable and natural names. Avoid file sprawl.
* For high-volume persistence, assess SQL round trips and write amplification as
  well as latency. Prefer set-based operations or prepared, batched writes when
  they preserve ordering, replay and per-entry failure behavior.
* Make daemon-owned work observable. At the operation owner, log coarse lifecycle
  and health transitions with a stable correlation ID, outcome, duration, and safe
  bounded counts; log terminal failures with the full exception once. Use
  `Information` for operator-relevant lifecycle, `Debug` for routine decisions and
  rejections, rate-limited `Warning` for recoverable degradation, and `Error` for
  failed operations. Keep detailed timing breakdowns at `Debug`; `Information`
  may include total duration.
* Tests are a maintenance liability as well as an asset. Do not add tests automatically
  for every change. Prefer extending existing tests over new files. Test observable
  behavior and failure isolation rather than implementation details
  or any prose/wording. Avoid polling and fixed waits, optimize setup, mark stress
  tests as `Load`, and remove low-value or obsolete tests while preserving meaningful
  behavioral coverage. Tests should suppress application logging unless they assert it.
  Keep the test suite execution time in check, do not let it bloat. Budget normal
  TypeScript tests below 30 seconds, C# below 2 minutes (below 3 with PR-selected
  Load checks), browser journeys below 3 minutes, and the PR pipeline at 5–7 minutes
  including setup/builds. Run full Load qualification nightly/manually, not on every PR.
* Do not add scattered legacy support junk code. DB migrations are good, but for any
  internal logic, just change it cleanly. For the API, prefer a breaking change and
  document it in BREAKING.md at repo root.
  Normalize old database representations once in a migration. While persistence is
  not yet deployed, database-breaking changes are also acceptable.

Archived designs in `docs/design/archive` are context and rationale, not current requirements.
