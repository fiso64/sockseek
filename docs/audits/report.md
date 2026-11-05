# Sockseek v4 Audit Report

- **PR audited:** #205 — `Implement a full webui and update backend`
- **Head commit SHA:** `955aecc544154b7f8fced965db971340748f3617`
- **Previous audited SHA:** `955aecc544154b7f8fced965db971340748f3617`
- **Audit start:** 2026-09-01 18:31:52 UTC
- **Audit end:** 2026-09-01 18:48:14 UTC
- **Substantive audit window:** 16 minutes 22 seconds
- **Baseline selection:** PR #205 remains the only open PR targeting `v4`; its head is unchanged, so this run followed the persisted source-inspection queue instead of repeating the PR diff.
- **Fix notifications:** `sockseek-audit/fixes.md` does not currently exist; no prior finding was closed or dispositioned this run.
- **Execution note:** Exact-head source, generated CLI help, and existing tests were inspected through GitHub. No local .NET runtime/toolchain is available, so no Sockseek build/test/benchmark execution is claimed.

## Summary

This combined report contains **12 correctness findings** and **4 performance findings** that remain unresolved pending explicit confirmation from the project owner.

**New this run:** no new correctness or performance finding was promoted. The run concentrated on finalization/on-complete boundaries, directory-transfer path planning, persistence handoff failure behavior, and adjacent Core transfer paths. A plausible post-processing/transfer-history disagreement was investigated but kept speculative because the source supports an intentional distinction between successful file transfer and later job-level post-processing failure.

| ID | Status | Severity | Confidence | Category |
|---|---|---:|---:|---|
| PERSIST-001 | previously reported | High | Medium | Persistence / shutdown admission |
| PERSIST-002 | previously reported | Medium | High | Persistence / retention consistency |
| PERSIST-003 | previously reported | Medium | Medium-High | Persistence / restart reconciliation |
| PERSIST-004 | previously reported | Medium | High | Persistence / history-read consistency |
| PERSIST-005 | previously reported | Medium-Low | High | Persistence / lifecycle timestamps |
| PERSIST-006 | previously reported | Medium | Medium-High | Persistence / live-to-history transfer handoff |
| PERSIST-007 | previously reported | Medium | High | Persistence / retention policy composition |
| CORE-001 | previously reported | High | High | Search history correctness |
| CORE-002 | previously reported | Medium | High | Generic search semantics |
| CORE-003 | previously reported | Medium | High | Transfer progress/accounting |
| CORE-004 | previously reported | Medium | Medium-High | Fast-search concurrency |
| CORE-005 | previously reported | High | High | Output placement / destructive collisions |
| PERF-001 | previously reported | Medium | High | Historical search projection |
| PERF-002 | previously reported | Medium | High | Transfer-history pagination |
| PERF-003 | previously reported | Medium | High | Workflow-history pagination |
| PERF-004 | previously reported | Medium | High | Retention throughput/backlog |

---

## Correctness and performance findings

### PERSIST-001
- **Status:** previously reported
- **Severity and confidence:** High, Medium
- **Category:** Persistence / shutdown concurrency / accepted-write loss
- **Affected:** `PersistenceInbox` admission/close paths; `PersistenceWriter.RunAsync`; `PersistenceRuntimeHost.StopAsync`.
- **QA / end-user impact:** A late persistence update racing shutdown can be accepted but never reach SQLite, leaving final job/transfer/search state missing after restart.
- **Mechanism:** admission observes completion separately from the independently synchronized stores; a producer can pass the check before close and publish after the writer has concluded drained.
- **Recommendation:** make admission and drained conclusion one atomic lifecycle contract.
- **Follow-up refactor:** **Yes — `REFACTOR-004`**, because the boundary spans all inbox storage classes.

### PERSIST-002
- **Status:** previously reported
- **Severity and confidence:** Medium, High
- **Category:** Persistence / retention consistency
- **Affected:** `RetentionService.RunBatchAsync`; job→search→result cascade configuration.
- **QA / end-user impact:** Search history can disappear before its configured result-retention lifetime when its owning job is pruned first.
- **Mechanism:** terminal job deletion cascades search metadata/results before the independent search-result retention pass, bypassing `SearchResultAge` and prune-state marking.
- **Recommendation:** enforce one explicit lifetime invariant between retained search data and its owning job.
- **Follow-up refactor:** **No**; this is a focused ownership invariant.

### PERSIST-003
- **Status:** previously reported
- **Severity and confidence:** Medium, Medium-High
- **Category:** Persistence / restart reconciliation
- **Affected:** `EnginePersistenceAdapter`; `PersistenceInbox`; `PersistenceRuntimeSession.StartAsync`; search completion/job terminal mutations.
- **QA / end-user impact:** After persistence pressure plus abrupt restart, a terminal search job can retain permanently incomplete search metadata.
- **Mechanism:** search completion and terminal job state can become durable independently; startup repair excludes incomplete searches whose owning job is already terminal.
- **Recommendation:** reconcile terminal-job/incomplete-search combinations explicitly at startup.
- **Follow-up refactor:** **No**; a local reconciliation invariant is sufficient.

### PERSIST-004
- **Status:** previously reported
- **Severity and confidence:** Medium, High
- **Category:** Persistence / historical read consistency / retention concurrency
- **Affected:** multi-statement workflow, search, transfer/attempt, and job-detail history readers; `RetentionService`.
- **QA / end-user impact:** History requests overlapping retention can fail, repeat a workflow on later pages, or return internally inconsistent parent/child detail.
- **Mechanism:** related DTO state is assembled by separate SQL statements without one read snapshot while retention can delete/change the same rows between statements.
- **Recommendation:** give multi-statement historical reads a consistent retained-state view; keep workflow ordering identity retention-stable.
- **Follow-up refactor:** **Yes — `REFACTOR-005`** for workflow history; other manifestations need a consistent read boundary but no additional broad refactor.

### PERSIST-005
- **Status:** previously reported
- **Severity and confidence:** Medium-Low, High
- **Category:** Persistence / lifecycle timestamps / mutation coalescing
- **Affected:** writer batch normalization and missing-row creation for jobs/transfers/attempts.
- **QA / end-user impact:** Fast/backlogged transfers can appear to start and complete at the same instant, producing misleading zero-duration history.
- **Mechanism:** batch normalization keeps only the highest-revision mutation; when the terminal mutation creates a missing row, earlier origin timestamps are lost.
- **Recommendation:** preserve lifecycle-origin timestamps through coalescing.
- **Follow-up refactor:** **No**; this is a focused mutation-data invariant.

### PERSIST-006
- **Status:** previously reported
- **Severity and confidence:** Medium, Medium-High
- **Category:** Persistence / live-to-history transfer handoff
- **Affected:** `PersistenceHandoffTracker`; terminal transfer handlers; workflow retirement; historical transfer facade.
- **QA / end-user impact:** Under queue pressure, live workflow state can retire before the terminal transfer record is durable, creating a temporary or permanent transfer-history hole.
- **Mechanism:** handoff requirements track terminal job/search revisions but not terminal transfer revisions; degraded persistence can break the normal FIFO ordering that otherwise masks this.
- **Recommendation:** include terminal transfer durability in the workflow retirement contract.
- **Follow-up refactor:** **No**; extend the existing handoff invariant.

### PERSIST-007
- **Status:** previously reported
- **Severity and confidence:** Medium, High
- **Category:** Persistence / retention policy composition
- **Affected:** `RetentionService.RunBatchAsync`; age policies; `MaximumRetainedJobs`.
- **QA / end-user impact:** When age and count retention fire together, newer history can be deleted even though age-expired deletions already satisfy part/all of the count deficit.
- **Mechanism:** count excess is calculated from the pre-deletion total and does not subtract age-selected rows, so independent selections can over-delete.
- **Recommendation:** compose age/count selection so age deletions reduce the remaining cap deficit.
- **Follow-up refactor:** **No**; local policy arithmetic is sufficient.

### CORE-001
- **Status:** previously reported
- **Severity and confidence:** High, High
- **Category:** Core/Server / historical search correctness
- **Affected:** live/historical projection, settings resolution, persisted search metadata.
- **QA / end-user impact:** A search can show different results/order after moving from live state to retained history; historical follow-up can fail for an item that was selectable live.
- **Mechanism:** live projection uses effective per-job settings, while historical projection uses daemon defaults because the effective projection definition is not persisted.
- **Recommendation:** persist and reuse a normalized search/projection definition.
- **Follow-up refactor:** **Yes — `REFACTOR-002`**.

### CORE-002
- **Status:** previously reported
- **Severity and confidence:** Medium, High
- **Category:** Core/Server / generic file-search semantics
- **Affected:** generic request mapping; settings resolution; default search conditions.
- **QA / end-user impact:** Generic file search can omit valid non-audio files and rank using music-specific preferences.
- **Mechanism:** generic search inherits the common music-oriented search baseline, including hard audio-format conditions.
- **Recommendation:** use a neutral generic-search baseline before normal composition.
- **Follow-up refactor:** **Yes — `REFACTOR-003`**.

### CORE-003
- **Status:** previously reported
- **Severity and confidence:** Medium, High
- **Category:** Core / transfer progress/accounting
- **Affected:** `ExactPeerFileTransferRunner` progress callback and failure/cancellation terminal paths.
- **QA / end-user impact:** Failure/cancellation history can under-report final transferred bytes; successful transfers later correct the total.
- **Mechanism:** current state stores `PreviousBytesTransferred` rather than the dependency's current `Transfer.BytesTransferred`.
- **Recommendation:** use the current transfer total.
- **Follow-up refactor:** **No**; local external-API mismatch.

### CORE-004
- **Status:** previously reported
- **Severity and confidence:** Medium, Medium-High
- **Category:** Core / fast-search concurrency
- **Affected:** `SongDownloadExecutor.SearchAndDownloadSong`; concurrent search response callback.
- **QA / end-user impact:** Fast search can start more than one provisional transfer for one song when qualifying responses arrive nearly simultaneously.
- **Mechanism:** shared provisional-task/candidate state is checked and assigned without synchronization.
- **Recommendation:** make provisional selection/start an atomic one-winner operation.
- **Follow-up refactor:** **No**; local concurrency invariant.

### CORE-005
- **Status:** previously reported
- **Severity and confidence:** High, High
- **Category:** Core / output placement / destructive collisions
- **Affected:** `Utils.Move`; `FileManager` organization; transfer final rename; `OutputFinalizer`; `DownloadedFileCache`.
- **QA / end-user impact:** Distinct downloads resolving to the same output path can silently replace one another; same-size replacement can leave a stale peer-identity cache entry that later reuses the wrong payload.
- **Mechanism:** move helpers delete an existing destination before replacement; no output-path reservation/collision policy exists, and cache identity is not path ownership.
- **Recommendation:** make final output-path ownership/collision an explicit invariant.
- **Follow-up refactor:** **Yes — `REFACTOR-006`**, because placement/collision semantics span multiple paths and cache publication.

### PERF-001
- **Status:** previously reported
- **Severity and confidence:** Medium, High
- **Category:** Historical search projection / CPU and memory
- **Affected:** historical projection input loading and Core projection.
- **QA / end-user impact:** Large retained searches become increasingly slow/memory-heavy; concurrent users amplify GC/CPU pressure.
- **Mechanism:** each request materializes and fully filters/sorts/groups all retained results even for a small visible page.
- **Recommendation:** expose a bounded, revision-stable retained projection.
- **Follow-up refactor:** **Yes — `REFACTOR-002`**.

### PERF-002
- **Status:** previously reported
- **Severity and confidence:** Medium, High
- **Category:** Persistence / transfer-history pagination
- **Affected:** `TransferHistoryReader.GetTransfersAsync`; transfer indexes.
- **QA / end-user impact:** Default transfer-history browsing gets more expensive as retained history grows.
- **Mechanism:** pagination orders by `(CreatedAtUtc, Id)` without an index beginning with that pair, yielding scan/sort behavior.
- **Recommendation:** align an index with the default cursor/order key.
- **Follow-up refactor:** **No**; focused query/index mismatch.

### PERF-003
- **Status:** previously reported
- **Severity and confidence:** Medium, High
- **Category:** Persistence / workflow-history pagination
- **Affected:** workflow aggregate/list queries and job indexes.
- **QA / end-user impact:** Workflow listing cost grows with total retained jobs rather than page size.
- **Mechanism:** every page groups the retained job population and computes aggregate minima/counts before cursoring and limiting.
- **Recommendation:** serve workflow listing from a bounded/indexable workflow-level summary.
- **Follow-up refactor:** **Yes — `REFACTOR-005`**.

### PERF-004
- **Status:** previously reported
- **Severity and confidence:** Medium, High
- **Category:** Persistence / retention throughput/backlog
- **Affected:** scheduled maintenance and per-category retention batches.
- **QA / end-user impact:** A busy homeserver can remain permanently above retention targets even with retention enabled.
- **Mechanism:** one maintenance invocation removes at most one bounded batch per interval; no catch-up drain converges when eligible history arrives faster.
- **Recommendation:** drain bounded batches toward policy targets within a maintenance work/time budget.
- **Follow-up refactor:** **No**; one maintenance-throughput policy is sufficient.

---

## Speculative observations / product decisions

### On-complete post-processing can disagree with already-terminal transfer success

`SongDownloadExecutor` completes the pending terminal transfer before invoking `OnCompleteExecutor`, and `EnginePersistenceAdapter` persists that as `Completed/Succeeded`. An `update-index` on-complete command may later change the job/index outcome to `Failed`; duplicate-cache publication can also precede post-processing. This was not promoted because transfer success can reasonably represent successful network/file placement independently of later job-level post-processing, and cache reuse can intentionally avoid redownload on a retry. No checked-in contract was found requiring transfer history to inherit post-processing failure.

### Chained on-complete conditions remain anchored to the initial outcome

`OnCompleteExecutor.ExecuteAsync` updates `currentOutcome` after command output, but later `ShouldExecuteCommand` calls receive the original `outcome`, and the command variable context is initially derived from that original outcome. Thus a prior `update-index` state change does not dynamically change later `when=` eligibility. Generated help documents chaining and `update-index` but does not say whether gating should be dynamic or anchored to the original terminal result, so this remains a semantic ambiguity.

### Mutable peer-success ranking context can make search ordering time-dependent

`UserSuccessTracker` is process-local and mutates peer counts used by sorting. Incremental projections can therefore embody reputation values from different moments, with restart changing the context. The intended lifetime remains unclear.

### Failed persistence handoff generations remain registered

`PersistenceHandoffTracker` removes successful generations but retains failed ones, so broad history waits can continue surfacing an old fault. This may intentionally fail closed instead of exposing incomplete history, so it remains a recovery-policy question.

### Upload attempt-start admission gap

`UploadPersistenceAdapter` records a transfer ID in `persistedAttempts` before knowing whether the structural attempt mutation was admitted. Terminal composites usually repair history; only abrupt interruption before terminalization leaves a possible gap, and the desired invariant remains unclear.

### Unknown newer applied migrations

The inspected initializer classifies pending migrations known to the current binary but does not visibly reject migration-history entries unknown to that binary. No current checked-in incompatible downgrade establishes a concrete failure path, so this remains a compatibility observation.

---

## Refactoring opportunities

### REFACTOR-001 — Validated job-state reducer
Centralize legal lifecycle/activity/outcome/cancellation/failure transitions so callers do not imperatively assemble invalid combinations.

### REFACTOR-002 — One semantic owner for search projection/history
**Referenced by:** `CORE-001`, `PERF-001`. Persist durable projection semantics and centralize live/historical projection behavior.

### REFACTOR-003 — Centralize search baseline/settings composition
**Referenced by:** `CORE-002`. Make generic-vs-music baseline and profile/launch/submission precedence one shared semantic boundary.

### REFACTOR-004 — Unify persistence inbox lifecycle/admission
**Referenced by:** `PERSIST-001`. Treat admission/close/drain as one contract across channels and coalescing stores.

### REFACTOR-005 — Retention-stable workflow summary/read model
**Referenced by:** `PERSIST-004`, `PERF-003`. Give workflow identity/order/counts one durable read owner instead of reconstructing them from mutable job rows.

### REFACTOR-006 — Centralize final output-path ownership/collision semantics
**Referenced by:** `CORE-005`. Unify final destination claiming/replacement behavior across transfer rename, organization, incomplete-album movement, and duplicate caching.

---

## Coverage this run

### Baseline and persistent state
- Re-read the repo-hosted prompt and persisted report; fixes file is absent.
- Confirmed PR #205 is still the sole open `v4` PR and head `955aecc544154b7f8fced965db971340748f3617` is unchanged.

### Finalization/on-complete
Inspected `SongDownloadExecutor`, `OnCompleteExecutor`, generated CLI help, `OutputFinalizer`, `DownloadedFileCache`, `ExactPeerFileTransferRunner`, and `EnginePersistenceAdapter`. Traced terminal transfer publication, duplicate-cache timing, on-complete `update-index` outcome mutation, and chained command gating. The strongest candidate was deliberately kept speculative after counterexample analysis showed plausible intentional transfer-vs-post-processing layering.

### Directory transfer/path planning
Inspected `RemoteDirectoryDownloadExecutor`, `DirectoryTransferRunner`, snapshot-to-plan flow, album/incomplete-album placement adjacency, and path helper behavior. No credible peer-directory path escape was established from the inspected executor path; final output construction still needs direct `PlacementPlanner` review.

### Persistence handoff/retention adjacency
Revisited `PersistenceHandoffTracker`, `HistoricalQueryFacade`, and `RetentionService`. Failed-generation lifetime remains potentially intentional fail-closed behavior. No new retention defect beyond `PERSIST-007` was found.

### Output collision adjacency
Revisited remaining move/cache/finalization paths while tracing on-complete. No distinct finding was split from `CORE-005`; destructive replacement and cache aliasing remain one shared output-ownership problem.

---

## Suggested next areas

1. Locate and deeply inspect `PlacementPlanner`/directory plan construction to finish remote-directory confinement/collision review for peer-provided path components.
2. Trace every on-complete caller and index/playlist writer to determine whether source/tests encode a contract tying transfer terminal state to post-processing outcome.
3. Inspect chained on-complete tests/configuration for an `update-index` command followed by success/failure-gated commands to resolve whether `when=` intentionally uses the initial outcome.
4. Continue output-path ownership review through incomplete-album, album-image, non-audio, remote-file, and directory placement; fold shared manifestations into `CORE-005`/`REFACTOR-006`.
5. Continue handoff failed-generation review through broad-history facade and startup/recovery paths, looking for encoded reset/removal/retry semantics rather than inferring product policy.
6. Continue chat persistence query/index review around filtered-room pagination and sparse predicates using source/query inspection only.
7. Compare migration-history handling, model snapshot, and initialization guards for a concrete downgrade incompatibility before promoting the unknown-newer-migration observation.

---

## Audit log

| Run | PR | Head SHA | Areas emphasized | New findings |
|---|---|---|---|---|
| Earlier | #205 | `955aecc544154b7f8fced965db971340748f3617` | Persistence runtime/write/retention; search settings/history | PERSIST-001, PERSIST-002, CORE-001, CORE-002, PERF-001 |
| Previous | #205 | `955aecc544154b7f8fced965db971340748f3617` | SQLite maintenance/recovery, persistence handoff, transfer Core/history, peer-browse, chat, search caches | PERSIST-003, CORE-003, PERF-002 |
| Prior | #205 | `955aecc544154b7f8fced965db971340748f3617` | Workflow history/retention/query plans, chat retention, transfer retry, search sorter semantics, handoff failure lifetime | PERSIST-004, PERF-003 |
| Earlier recent | #205 | `955aecc544154b7f8fced965db971340748f3617` | Writer coalescing/timestamps, handoff transfer coverage, retention throughput/read consistency, fast-search concurrency, finalization/uploads, SQLite maintenance | PERSIST-005, PERSIST-006, CORE-004, PERF-004 |
| Previous run | #205 | `955aecc544154b7f8fced965db971340748f3617` | Mixed retention policies/tests, historical read races, handoff validation, output collisions/cache, chat query shapes, migration compatibility | PERSIST-007, CORE-005 |
| Current | #205 | `955aecc544154b7f8fced965db971340748f3617` | On-complete/finalization terminal boundaries, duplicate-cache timing, directory-transfer path planning, handoff failure semantics, output collision adjacency | none |
