# Fixes for the Sockseek v4 audit report

This is the implementation follow-up to [report.md](report.md) and
[response.md](response.md). The imported report audited an older revision. Each
finding was first rechecked against the current tree; this file records the
result after the accepted fixes in this worktree.

The changes stay within the existing Core, persistence, and daemon owners. They
do not wire the web UI to the backend, introduce a new persistence lifecycle, or
persist the still-undefined user-success/reputation state.

## Correctness findings

### PERSIST-001 — shutdown admission race

**Fixed.** `PersistenceInbox` now has one lifecycle gate for starting an
admission and closing the inbox, plus an active-admission count included in the
writer's drained predicate. Once completion wins the gate, later mutations are
rejected; once an admission wins it, shutdown cannot report the inbox drained
until that admission has published or failed.

Critical overflow also now returns `true` when the mutation was actually
retained in the degraded store and `false` only when it was lost. This gives
callers, including terminal handoff owners, an accurate admission contract.
The correction remains local to the existing inbox rather than creating the
generalized queue framework proposed by `REFACTOR-004`.

### PERSIST-002 — job retention pre-empts search-result retention

**Fixed with an explicit precedence rule.** Raw search-result retention wins
over job age and count retention. A retention transaction first prunes raw rows
that have reached their own cutoff, then excludes every job that still owns raw
rows from job deletion. This also ensures pruning is recorded on search
metadata instead of being hidden inside a foreign-key cascade.

The practical consequence is intentional: `MaximumRetainedJobs` may remain
temporarily exceeded while protected raw search results are still within their
configured lifetime. Once those results become eligible and are pruned, their
jobs can be removed by the normal age/count pass.

### PERSIST-003 — incomplete search metadata after restart

**Fixed.** Degraded mutations are replayed in their original sequence rather
than by priority, so a search completion cannot leapfrog its earlier result
batches during writer recovery. Startup reconciliation now marks incomplete
searches interrupted when they belong to an unfinished runtime even if the job
row is already terminal.

The unused `SearchTerminalPersistenceMutation` composite and its parallel
writer/tracker branches were removed. Search results and completion already
share one ordered, backpressured lane; preserving that lane's sequence is the
simpler single contract.

### PERSIST-004 — history reads race retention

**Fixed.** Every reader that assembles one response from multiple SQL statements
now does so in a read transaction: job detail/list/workflow queries, raw search
result/detail queries, and transfer/attempt detail queries.

Workflow cursors no longer depend on the mutable minimum retained job display
ID. Both live and retained workflow pages use the immutable workflow ID as the
ordering and cursor identity, including when the two sources are merged. A test
deletes the former first job of an unseen workflow between pages and verifies
that the workflow is still returned exactly once.

This deliberately avoids a materialized workflow-summary table. The tradeoff is
that workflow enumeration has stable identity order rather than inferred
creation order. If chronological workflow ordering becomes a product
requirement, it should get one explicit immutable workflow sequence; it should
not again be inferred from whichever child jobs retention happens to leave.

### PERSIST-005 — lifecycle timestamps lost through coalescing

**Fixed.** Terminal job mutations now retain the first observed running
timestamp, and terminal attempt mutations retain the attempt's actual start
timestamp. Missing rows created from a coalesced terminal mutation therefore no
longer use completion time as their start time. The adapter drops its temporary
per-workflow timestamp state when the workflow retires.

### PERSIST-006 — download live-to-history handoff

**Fixed.** Downloads now register the exact terminal transfer revision before
enqueueing it, just as uploads do. A terminal download remains in live state
until that revision commits; workflow retirement leaves such transfers alone;
and the supervisor removes the live entry only after the handoff completes.
Admission failure is reported to the handoff tracker, and a failed handoff is
logged once by the operation owner before live presentation is retired.

Archive operations use the same generic terminal-transfer removal path for both
directions. This closes the ordinary and degraded-writer window in which a
download could disappear from live state before authoritative history existed.

The broader policy for a permanently failed handoff generation remains the
separate product/API decision described below; this fix does not silently claim
durability after a failed write.

### PERSIST-007 — age and count retention over-delete

**Fixed.** Rows already selected by age now reduce the remaining count deficit
before the count policy selects more rows. A combined-policy regression test
covers the reported 110-row/100-row-cap shape.

### CORE-001 — historical projection semantics differ from live semantics

**Already resolved; no additional change.** Current code retains a normalized
`SearchDefinition`, builds both live and retained views through
`SearchViewKernel`, and pages retained raw inputs in bounded batches. The local
CLI, remote CLI, and daemon therefore consume shared projection semantics rather
than reconstructing historical results from current defaults.

### CORE-002 — generic search inherits music defaults

**Already resolved; no additional change.** Settings composition has the exact
distinction needed here: generic versus music. Track and album do not need
separate intent merely to choose defaults; their retained projection kind still
captures the later behavioral distinction.

### CORE-003 — progress stores the previous byte count

**Fixed.** Progress, owner state, events, failure/cancellation terminal state,
and persistence accounting now use the dependency's current
`Transfer.BytesTransferred` value. A regression test reports progress and then
fails the transfer without another state callback, verifying that the terminal
byte count is the current value.

### CORE-004 — fast-search provisional transfer race

**Fixed.** Candidate selection is an atomic one-winner operation, and a stable
completion source exists before search callbacks begin. Concurrent callbacks
cannot start duplicate provisional transfers, while a candidate that arrives
after the caller starts waiting still wakes it immediately. Tests cover both
timings.

### CORE-005 — output collision and stale cache aliases

**Fixed while preserving explicit overwrite behavior.** Final output paths have
a runtime-wide, reference-counted claim with no arbitrary capacity limit.
Different concurrent targets cannot replace a path already
published by another target when skip-existing protection applies. Explicit
`SkipExisting=false` replacement remains supported, and an existing file that
failed the configured song/aggregate length tolerance may still be replaced.

The duplicate cache now owns a reverse path-to-identity index. Publishing a
replacement invalidates every old identity alias for that path, and all
finalization, move, and delete paths invalidate affected aliases. Music
finalization is serialized through the same cache owner; staged payloads are
either moved safely, converted to `AlreadyExists` where appropriate, or retained
with an explicit finalization failure.

Tests cover alias invalidation, two distinct targets racing for one output,
preservation of established tolerance-based redownload behavior, and a final
path appearing after the earlier skip check.

## Performance findings

### PERF-001 — repeated full historical search materialization

**Already resolved; no additional change.** Retained source rows are consumed in
bounded batches into a revisioned Search View projection. Visible pages read the
stored projection instead of rebuilding the entire result set per request.

### PERF-002 — transfer history lacks the cursor-order index

**Already resolved; no additional change.** The current schema contains the
`(ArchivedAtUtc, CreatedAtUtc, Id)` index used by the default timeline query.

### PERF-003 — workflow pages aggregate all retained jobs

**Not promoted to a demonstrated performance bug.** The query still aggregates
retained jobs and is therefore proportional to retained job count. At the
supported maximum of roughly 100,000 jobs, this is likely acceptable for a
low-frequency history page, but no benchmark claim is made. The correctness
issue was fixed without a second durable read model. A materialized summary
should be added only if query-plan or real-scale evidence shows this query
missing the sub-300 ms browsing target.

### PERF-004 — retention cannot catch up

**Fixed.** On each scheduled tick, maintenance keeps taking bounded retention
batches while any independently counted category reaches the batch ceiling. It
yields between batches, observes cancellation, and stops after a five-second
continuous-work budget so retention cannot monopolize the service. Remaining
work resumes on the next configured tick. This is normal catch-up behavior, not
an unmarked load test or an unbounded startup purge.

## Speculative observations and decisions

### Transfer success versus on-complete failure

**Documented, not changed.** Network/file-placement success and later
post-processing failure are intentionally separate facts. README/help now state
that transfer history can show a successful transfer while the owning job shows
a post-processing failure. This preserves useful cache reuse after bytes were
successfully obtained.

### Chained on-complete conditions

**Resolved as dynamic semantics.** Each command now evaluates its condition and
variables against the outcome produced by the preceding command. Thus an
`update-index` action that changes failure to success makes a subsequent
`when=success` command eligible and exposes the updated outcome variables.
README/help and a behavioral test describe the rule. The implementation remains
in the shared Core executor used by local and daemon workflows.

### Mutable user-success ranking

**Intentionally deferred to V4 design work.** No reputation snapshot was added
to Search Views or persistence. The existing in-memory tracker continues to
down-rank a peer after a failure within the current workflow, and the existing
`TODO [V4]` calls out the unresolved owner, lifetime, sharing, decay, and restart
semantics. This audit does not turn it into a daemon-wide shared resource.

### Permanently failed handoff generations

**Still a product/API decision, not silently “fixed.”** The current tracker
continues to fail closed rather than pretending missing history is complete.
PERSIST-006 now closes successful transfer handoffs and records admission
failure accurately, but scoped durable coverage errors and degraded broad-page
contracts require an API decision. They should be handled with the same
smallest-failure-domain rule as the rest of the daemon, not by forgetting the
failure or poisoning unrelated workflows indefinitely.

### Upload attempt-start admission gap

**Fixed.** The upload adapter removes its provisional “attempt persisted” marker
when the inbox rejects the attempt mutation, so the next nonterminal snapshot
retries it. The corrected inbox boolean distinguishes retained degraded writes
from actual loss. A behavioral test rejects the first attempt admission and
verifies the next transfer update retries and succeeds.

### Unknown newer migrations

**Fixed.** Startup compares applied migration IDs with the current assembly's
known migrations and throws a clear schema-compatibility exception before
attempting migration when the database is newer than the binary. A test injects
an unknown future migration and verifies the rejection.

## Refactoring recommendations

- **REFACTOR-001:** Deferred. No illegal state transition was demonstrated, and
  the existing reducer TODO remains the right place for future work.
- **REFACTOR-002:** Already implemented by the shared `SearchDefinition`,
  `SearchViewKernel`, and bounded Search View source/projection path.
- **REFACTOR-003:** Already implemented by centralized generic-versus-music
  settings composition shared by local CLI, remote CLI, and daemon execution.
- **REFACTOR-004:** Addressed as the small inbox lifecycle contract described in
  PERSIST-001; no new queue abstraction was introduced.
- **REFACTOR-005:** Not implemented. Read snapshots and immutable workflow cursor
  identity solve the correctness issue without another durable model. Revisit
  only with performance evidence or a chronological-order requirement.
- **REFACTOR-006:** Implemented narrowly as output claims, centralized
  finalization guards, and reverse cache ownership. Existing placement planning
  and overwrite semantics remain authoritative.

## Suggested follow-up areas

1. `PlacementPlanner` needs no traversal fix from this audit; its within-plan
   sanitization and collision suffixing remain in place. Cross-job ownership is
   handled by CORE-005.
2. On-complete ownership is now documented and its chained semantics tested.
3. Output ownership now covers exact transfers, organized song/album outputs,
   incomplete-album moves/deletes, and cache aliases without a parallel
   placement model.
4. Failed-generation recovery still needs the scoped API/coverage decision
   described above.
5. Filtered chat-room pagination remains a measurement-driven low-priority
   candidate; no new index or read model was added without evidence.
6. Unknown migration handling now has the focused compatibility guard.

## Verification

The implementation is covered by focused regressions for each changed
correctness boundary and by the existing project suites. Generated CLI help is
kept in sync with README through the repository's help-generation check. No
worker counts, fixed waits, ordinary-suite load tests, or arbitrary production
limits were added.

`dotnet test Sockseek.sln --no-restore` passes:

- Core: 824 passed
- Persistence: 115 passed
- Server: 206 passed
- CLI: 299 passed
