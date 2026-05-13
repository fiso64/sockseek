# Logging

sldl uses `SldlLog` as the single application logging entry point over
`Microsoft.Extensions.Logging` levels. The old handmade `Logger` class was
removed; new code should not add ad-hoc log sinks or write backend/server logs
directly to `Console`.

## Ownership

- Core owns engine and backend diagnostics. Core code logs through `SldlLog`
  at `Trace`, `Debug`, `Information`, `Warning`, `Error`, or `Critical`.
- CLI owns user-facing rendering. Help text, JSON output, prompts, interactive
  key handling, job-info views, and live progress rendering may use
  `Console`, `Printing`, or `AnsiConsole` because they are UI output, not logs.
- CLI event/progress summaries that should also be captured in log files use
  `SldlLog`. In live progress mode, event log lines go to non-console sinks so
  they do not corrupt the live display; in no-progress/plain mode they also go
  to the console.
- Server/daemon owns HTTP hosting logs and systemd-friendly stdout output.
  `CoreLoggerBridge` routes core/server log events to daemon stdout with
  timestamp, level, and category.

## Categories

Non-console and file logs include an explicit category so mixed CLI, core, and
daemon output is easy to identify:

- `sldl.cli`
- `sldl.core`
- `sldl.daemon`
- `sldl.tests.*` for test-only logs

Console UI output intentionally omits these prefixes to preserve the existing
CLI experience.

## Audit Notes

The logging cleanup classified existing output call sites as follows:

- Backend/core status, error, debug, and trace messages were migrated to
  `SldlLog`.
- Server supervisor, progress reporter, bad-request, and daemon startup output
  were migrated or bridged through `SldlLog`.
- CLI progress/event logs were migrated to `SldlLog`, with explicit
  non-console routing for live progress.
- Remaining direct `Console`, `Printing`, and `AnsiConsole` calls in the CLI are
  intentional UI/rendering paths: help/version text, profile listing, prompts,
  keyboard echoing, JSON command output, job-info views, and live terminal
  rendering.

When adding a new message, first decide whether it is a log or UI. Logs go
through `SldlLog`; UI output stays in the CLI rendering layer.
