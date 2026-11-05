# Response to the Sockseek v4 audit report

> Implementation follow-up: [fixes.md](fixes.md) records the final disposition
> and changes made after this source review.

## Scope and overall assessment

The imported report audited commit `955aecc544154b7f8fced965db971340748f3617`.
This response checks its claims against current commit
`f573e4271973e950bbe95085c4a2663407f88e51`. The intervening commit is large and
implements much of the daemon-audit plan, so the report must not be treated as a
current issue list without revalidation.

No benchmark is claimed here. Performance verdicts distinguish a demonstrably
unbounded query shape from a demonstrated user-visible performance problem.

| Disposition | Findings |
|---|---|
| Confirmed current issue | PERSIST-002, PERSIST-003, PERSIST-004, PERSIST-006, PERSIST-007, CORE-003, CORE-004, CORE-005, PERF-004 |
| Partly confirmed or overstated | PERSIST-001, PERSIST-005, PERF-003 |
| Resolved by current code | CORE-001, CORE-002, PERF-001, PERF-002 |

The report is useful, but its headline count of 12 correctness and 4 performance
findings is no longer current. It also sometimes jumps from a valid mechanism to
a heavier read model or abstraction before showing that the simpler correction
is insufficient.

## Numbered findings

### PERSIST-001 — shutdown admission race

**Verdict: partly confirmed; impact and refactor scope are overstated.**

`PersistenceInbox.TryEnqueue` checks `IsCompleted` before entering the separately
locked progress/degraded stores (`PersistenceInbox.cs:62-93,225-257`). The writer
declares the inbox drained by independently sampling the counters and stores
(`PersistenceWriter.cs:100-106`). A progress producer can therefore pass the
completion check, the writer can observe a drained inbox and exit, and the
producer can then add to `progress` and return `true`. That is a real
accepted-but-never-written race.

The report makes this sound uniform across all storage classes, but it is not:

- ordinary channel writes fail and return `false` after closure;
- search writes get `ChannelClosedException` and return `false`;
- critical writes fall into `degraded` but return `false`;
- progress writes are the clear path that can return `true` after the writer has
  made its final drain decision.

Normal hosted-service ordering lowers the likelihood: persistence is registered
first and therefore stopped after the engine and Search View services
(`ServerHost.cs:104-129`). It does not repair the inbox's own contract, and an
in-flight callback can still expose the race.

The right fix is small: make close, admission, and the final drain decision share
one lifecycle gate or an in-flight-admission count. `REFACTOR-004` should not
become a new generalized queue framework.

### PERSIST-002 — job retention can pre-empt search-result retention

**Verdict: confirmed, subject to an explicit retention-precedence decision.**

Jobs are deleted first (`RetentionService.cs:51-91`). The `jobs -> search_jobs`
and `search_jobs -> search_results` foreign keys cascade
(`EntityConfigurations.cs:110-160`), so deleting a job can remove the search and
all raw results before the later search-retention pass can mark them `Pruned`.
The count cap can trigger the same behavior regardless of search age.

This is not merely an exotic interpretation of the options. The daemon guide
says each history type is configurable independently (`docs/daemon.md:448-468`).
Under that public wording, `search-result-retention-days = 90` and successful-job
retention of 30 days should not silently produce 30-day raw-result retention.

The report's recommendation is directionally correct but underspecified. The
product must define which policy wins when the job cap conflicts with raw-result
retention. A focused implementation could protect search-owning jobs until their
raw results are eligible, or prune results explicitly before deleting their
owner. No new persistence subsystem is warranted.

### PERSIST-003 — incomplete search metadata after restart

**Verdict: confirmed; the report misses an additional retry-ordering problem.**

Startup reconciliation only marks an incomplete search interrupted when its job
belongs to an unfinished runtime *and is nonterminal*
(`PersistenceRuntimeSession.cs:84-98`). A terminal job can be written from the
critical lane before the search lane is drained. An abrupt stop at that point
leaves the terminal-job/incomplete-search combination permanently unreconciled.

Normal result and completion mutations share the ordered, backpressured search
channel, so queue pressure alone does not reorder them. Writer recovery does:
failed batches are put into `degraded`, which drains terminal priority before
ordinary priority (`PersistenceInbox.cs:137-147,186-190`). If a batch boundary
then separates completion from some result batches, completion may commit first.
Late result application increments counts that completion already set to the
final totals (`PersistenceWriter.cs:544-617,620-638`), so recovery can also
overcount metadata even without a restart.

`SearchTerminalPersistenceMutation` already expresses an atomic result-batches
plus completion write (`PersistenceMutations.cs:179-188`), and the writer handles
it (`PersistenceWriter.cs:662-679`), but production code never constructs it;
only tests do. The production adapter still enqueues a plain completion
(`EnginePersistenceAdapter.cs:199-221`).

Startup reconciliation should handle terminal-job/incomplete-search rows, as the
report says. It should be paired with using or removing the currently dormant
terminal composite so retry ordering cannot corrupt counts. Startup repair alone
cannot reconstruct result rows that were never made durable; it can only label
the retained state honestly.

### PERSIST-004 — multi-statement history reads race retention

**Verdict: confirmed, but two separate concerns are bundled together.**

The readers use one `DbContext` but no read transaction. For example, raw search
history reads metadata and then rows (`SearchHistoryReader.cs:89-108`), transfer
detail reads a transfer and then its latest attempt
(`TransferHistoryReader.cs:128-146`), and workflow summaries perform several
queries (`JobHistoryReader.cs:150-176,226-280`). Retention runs in another
transaction and can delete between those statements.

The most direct failure is in workflow summary construction:
`firstJobs[aggregate.WorkflowId]` assumes rows found by the first aggregate query
still exist during the later query (`JobHistoryReader.cs:234-266`). Concurrent
retention can violate that assumption. Search and transfer endpoints can return
less obvious parent/child inconsistencies instead.

A read transaction/snapshot is the simple fix for consistency within one HTTP
response. Cross-page stability is a different issue: retention can delete the
earliest job in a workflow, changing `FirstDisplayId` and allowing that workflow
to move relative to a cursor. Solve that by defining the retention unit and an
immutable workflow ordering identity. A fully materialized durable workflow
summary is only one option, not a necessary consequence of this finding.

### PERSIST-005 — lifecycle timestamps lost through coalescing

**Verdict: partly confirmed; current code has already fixed the transfer-level
part.**

Current mutations preserve job registration time (`RegisteredAtUtc`) and
transfer request/start times (`RequestedAtUtc`, `StartedAtUtc`) through a
terminal mutation (`PersistenceMutations.cs:25-90`). The writer uses those values
when creating missing job and transfer rows (`PersistenceWriter.cs:266-303,
334-386`). A coalesced terminal transfer no longer necessarily appears to start
at completion.

Two narrower holes remain:

- a terminal job mutation carries registration time but not its actual start
  time, so a missing job row receives terminal `OccurredAtUtc` for both
  `StartedAtUtc` and `CompletedAtUtc`;
- `TransferAttemptPersistenceMutation` carries no explicit start time, and a
  terminal mutation creating a missing attempt uses its terminal occurrence for
  both start and completion (`PersistenceMutations.cs:103-123` and
  `PersistenceWriter.cs:411-449`).

This needs fields on existing mutation records, not a refactor. The report should
no longer list transfer rows generally as affected.

### PERSIST-006 — download transfer live-to-history handoff

**Verdict: confirmed, and the proposed workflow-only fix is too narrow.**

Download terminal mutations are enqueued without registering a transfer handoff
(`EnginePersistenceAdapter.cs:246-266`). Workflow retirement waits for terminal
job and search revisions only (`EnginePersistenceAdapter.cs:584-607` and
`PersistenceHandoffTracker.cs:45-100`).

More importantly, `EngineStateStore` removes a download from live transfer state
as soon as it sees the terminal event (`EngineStateStore.cs:1344-1370`), before
SQLite commit. The transfer-history list does not wait for a known download
handoff (`HistoricalQueryFacade.cs:80-162`). During ordinary writer latency it
can therefore show a stale nonterminal persisted row, or no row if the start was
not yet durable. Degraded ordering can make that permanent.

Uploads already have the better shape: register the exact terminal transfer
revision, enqueue it, wait for that revision, then retire presentation state
(`UploadPersistenceAdapter.cs:70-83` and `EngineSupervisor.cs:416-444`). Downloads
should use the same semantic path. Merely adding transfer revisions to workflow
retirement would leave the earlier terminalization window intact.

### PERSIST-007 — age and count retention over-delete

**Verdict: confirmed, straightforward arithmetic bug.**

`excess` is calculated from the pre-deletion total. The count selection excludes
already age-selected IDs but still takes the full `excess`
(`RetentionService.cs:60-87`). With 110 rows, a cap of 100, and 10 already chosen
by age, the code selects 10 additional rows and deletes 20; only the aged 10 were
needed to reach the cap.

Subtract `agedJobIds.Count` from the remaining count deficit, clamped at zero.
The existing count-only test does not combine the policies
(`SqliteInitializationTests.cs:628-672`), which is why it does not expose this.
No broader refactor is useful.

### CORE-001 — historical projection uses different semantics

**Verdict: resolved in current code.**

`SearchDefinition` now retains the baseline, default projection, network query,
typed query, and projection settings (`SearchDefinition.cs:178-224`). Directly
accepted searches retain it in the submission; derived search children retain
it in their job snapshot. Historical mapping verifies the two sources if both
exist and refuses to invent current defaults when neither exists
(`HistoricalJobDtoMapper.cs:13-71`).

Both live and retained Search Views feed the same `SearchViewKernel`; historical
sources page retained raw inputs and use the retained definition
(`SearchViewCoordinator.cs:1101-1128,1448-1475`). That is the intended fix, not
two similar projection implementations.

### CORE-002 — generic search inherits music defaults

**Verdict: resolved in current code.**

The settings owner now has exactly the useful distinction: `Generic` versus
`Music`. Generic clears file and folder conditions, while track and album jobs
both use the music baseline (`JobSettingsComposer.cs:10-14,55-81`). Settings
precedence is centralized in `JobSettingsComposer`, shared by local and daemon
submission paths. There is no need for separate track and album “intent” merely
to choose defaults; the retained projection kind carries the later behavioral
distinction.

### CORE-003 — progress stores `PreviousBytesTransferred`

**Verdict: confirmed.**

The progress callback assigns and publishes
`progress.PreviousBytesTransferred` (`ExactPeerFileTransferRunner.cs:136-145`).
State callbacks use the current `state.Transfer.BytesTransferred`, which makes
the inconsistency especially clear. Failure and cancellation terminal events
reuse `owner.BytesTransferred`; success later corrects it from target/file size.
Failed and cancelled transfers can therefore under-report the final observed
bytes, including the input to cumulative accounting.

Use `progress.Transfer.BytesTransferred`. This is a local fix. It should have a
test whose final progress increment is followed immediately by failure or
cancellation.

### CORE-004 — fast-search provisional transfer race

**Verdict: confirmed, with another fast-search coordination bug nearby.**

The callback checks and assigns `fastDownloadTask` and `fastCandidate` without
synchronization (`SongDownloadExecutor.cs:266-302`). `Searcher.RunSearches` can
run the normal and de-diacritized searches concurrently with `Task.WhenAll`
(`Searcher.cs:670-689`). Nothing in this owner establishes serialized callbacks,
so two qualifying responses can both observe `null` and start downloads.

There is also a missed wake-up: when `fastDownloadTask` is null at
`Task.WhenAny(fastDownloadTask ?? searchTask, searchTask)`, the code awaits the
search task twice (`SongDownloadExecutor.cs:305-310`). Assigning a provisional
task later does not change the already-created `WhenAny`, so fast search may wait
for full search completion instead of reacting to the download. The existing
success test has the mock publish its first response synchronously enough to
avoid that production timing (`FastSearchTests.cs:52-90`).

Use a one-winner atomic claim plus a stable completion signal/TCS that exists
before search callbacks begin. A lock around only the assignment fixes the
duplicate-start race but not the missed wake-up.

### CORE-005 — destructive output collisions and stale cache aliases

**Verdict: confirmed, but the report fails to distinguish intentional overwrite
from accidental collision.**

`Utils.Move` deliberately deletes an existing destination
(`Utils.cs:235-251`), and cached reuse copies with overwrite enabled
(`ExactPeerFileTransferRunner.cs:95-107`). Explicit replacement is a supported
feature: a server test sets `SkipExisting=false` and expects an existing file to
be overwritten (`UserBrowseApiTests.cs:169-183`). A blanket “never replace” fix
would therefore be wrong.

The actual bugs are:

- no process-wide claim prevents concurrent independent jobs from choosing the
  same final path after both have passed skip-existing checks;
- `PlacementPlanner` resolves collisions only among entries in one directory
  plan, not against other jobs or existing ownership
  (`PlacementPlanner.cs:54-97`);
- `DownloadedFileCache` maps peer identity to a path and validates only existence
  and expected length (`DownloadedFileCache.cs:25-53`). If another same-size
  payload intentionally or accidentally replaces that path, later reuse of the
  old peer identity copies the wrong bytes.

Final-path ownership/collision policy should be centralized enough to coordinate
concurrent finalization and invalidate every cache identity aliased to a replaced
path. It must retain explicit overwrite behavior and existing skip semantics.
This is a real silent-content-corruption risk; `REFACTOR-006` is justified if it
stays focused on that invariant.

### PERF-001 — full historical search materialization per request

**Verdict: resolved in current code.**

Search View construction now consumes live or retained raw results in bounded
batches of 200, incrementally applies `SearchViewKernel`, and publishes projected
revisions (`SearchViewCoordinator.cs:795-838`). Historical reads page raw inputs
rather than materializing the whole retained search per visible-page request
(`SearchViewCoordinator.cs:1457-1475`). Visible pages then query the stored
revision directly (`SearchViewCoordinator.cs:180-214` and adjacent methods).

Building a new projection still necessarily visits its source results once. The
reported pathological behavior—rebuilding/filtering/sorting everything for each
small page—is gone.

### PERF-002 — transfer pagination lacks its order index

**Verdict: resolved in current code.**

The default query filters on `ArchivedAtUtc` and orders by
`CreatedAtUtc, Id` (`TransferHistoryReader.cs:100-139`). Current configuration
and migration add the matching
`(ArchivedAtUtc, CreatedAtUtc, Id)` index
(`EntityConfigurations.cs:216-224` and
`20260831100000_AddTransferTimelineProjection.cs:26-29`). SQLite can scan that
index in reverse for the descending order.

Arbitrary combinations of optional filters will not all have perfect covering
indexes, but that is a separate measurement-driven question.

### PERF-003 — workflow pagination groups all retained jobs

**Verdict: mechanism confirmed; severity and prescribed solution are not
established.**

Every workflow page starts from `Jobs.GroupBy(WorkflowId)` and computes minima
and counts before applying the cursor and limit (`JobHistoryReader.cs:150-170,
210-224`). Its work therefore grows with retained job count, not page size.

That does not by itself establish a medium user-visible performance problem. The
default cap is 100,000 jobs, SQLite can aggregate that scale reasonably, and
workflow-history pages are not the high-frequency live-update path. No query
plan or timing in the report supports the severity claim.

Do not introduce a materialized workflow summary solely from this audit. First
fix the correctness semantics in PERSIST-004 (immutable ordering/retention unit),
then inspect the query plan or measure at the supported scale. A durable summary
is appropriate only if the corrected indexed query remains too slow or if it is
the simplest owner of the chosen immutable workflow identity.

### PERF-004 — retention cannot catch up

**Verdict: confirmed and plausible on a busy homeserver.**

The hosted service runs once per interval (`PersistenceMaintenanceHostedService.cs:19-32`),
and `RetentionService.RunBatchAsync` removes at most one configured batch for
each category. Defaults are a 24-hour interval and a batch size of 500. More than
500 newly eligible transfers or jobs per day is plausible, so backlog can grow
forever even though every run succeeds.

The recommendation is sound: loop bounded batches until no category hits its
batch ceiling, subject to a time/work budget and cancellation, then resume on the
next interval if necessary. This is not an invitation to an unbounded startup
purge or a load test hidden in the ordinary suite.

## Speculative observations and product decisions

### On-complete versus terminal transfer success

The separation is defensible and should not be promoted as a bug. A successful
network transfer/file placement and a later failed command are different facts.
The report is also correct that cache publication can precede post-processing:
for an unstaged destination, `ExactPeerFileTransferRunner` publishes before it
returns (`ExactPeerFileTransferRunner.cs:403-405`); the finalizer publishes again
after on-complete (`SongDownloadExecutor.cs:184-191`). That early entry allows a
retry to reuse successfully transferred bytes even if a later external command
fails, which is a reasonable policy rather than evidence of corruption by
itself.

Document the layer boundary; do not rewrite transfer history to “failed” because
an external post-processing command failed. If a command transforms the file,
the job outcome/path and cache publication remain the post-processing owner's
responsibility.

### Chained on-complete conditions use the initial outcome

The observation is correct. `currentOutcome` changes, but command eligibility
continues to receive the original `outcome`, and variables are initialized from
that original outcome (`OnCompleteExecutor.cs:136-155,203-223`). Existing tests
cover a single `update-index` command and ordinary chaining, not an outcome
change followed by a gated command (`OnCompleteExecutorTests.cs:722-825`).

This is an undocumented semantic choice, not yet a correctness finding. Dynamic
semantics are arguably more useful for a chain: after `update-index` changes a
failure to success, a following `when=success` command and its variables would
see the new state. Whichever behavior is chosen needs one explicit help sentence
and a behavioral test. If initial-outcome gating is intentional, add only the
documentation/test; no abstraction is needed.

### Mutable peer-success ranking

Correctly left as a product decision. Current code contains the requested
`TODO [V4]` and explicitly preserves only the existing in-memory/workflow effect
(`UserSuccessTracker.cs:10-16`). Live Search View creation snapshots those counts;
historical creation uses none and does not persist reputation
(`SearchViewCoordinator.cs:95-121`). Persisted projected sort keys keep an
existing view stable, but creating a new view after restart can rank differently.

Do not expand current scope until lifetime, sharing, decay, and restart semantics
are decided. Preserve within-workflow behavior in the meantime.

### Failed handoff generations remain registered

This is not merely an accidental possibility: the tests explicitly require an
affected workflow and broad `WaitForAllAsync` to keep throwing after permanent
loss (`PersistenceHandoffTrackerTests.cs:111-143`). Successful generations are
removed, while failed ones are not (`PersistenceHandoffTracker.cs:337-381`).

Failing closed avoids silently presenting incomplete authoritative history, but
one failed workflow consequently poisons every broad submission/job/workflow
listing until restart. Restart then forgets the in-memory failure and can expose
the incomplete database, so the policy is not durable either. This deserves a
product fix consistent with failure isolation: retain an explicit durable or
health-backed coverage failure, fail targeted reads for the affected workflow,
and give broad pages an explicit unavailable/degraded contract rather than
silently omitting data. Simply deleting the failed generation would be wrong.

### Upload attempt-start admission gap

The observation is real but narrow. `persistedAttempts.Add` happens before the
attempt mutation's enqueue result is known (`UploadPersistenceAdapter.cs:87-95`).
A later terminal composite normally repairs the row. Abrupt interruption before
terminalization can leave no attempt row, and subsequent nonterminal updates do
not retry because the ID is already marked.

The immediate code should only mark the start represented once the inbox has
accepted/retained it. The inbox's current boolean is ambiguous for critical
overflow—it stores into degraded but returns `false`—so this should be corrected
alongside PERSIST-001's admission contract rather than with upload-specific
polling.

### Unknown newer applied migrations

Confirmed as a compatibility gap, conditional on supporting rollback to an older
binary. The initializer checks only migrations the current binary considers
pending against its safe list, then calls `MigrateAsync`
(`SqliteInitializer.cs:41-59`). It does not reject IDs present in
`__EFMigrationsHistory` but absent from the current assembly.

Because current migrations are additive, there is no demonstrated current data
loss. A future incompatible migration could make downgrade fail later and less
clearly. A small explicit “database schema is newer than this binary” guard is
reasonable; a general migration compatibility framework is not.

## Refactoring opportunities

### REFACTOR-001 — validated job-state reducer

Reasonable long-term work, but unsupported as a finding from this report. The
code already has an atomic `ApplyStateTransition` boundary and a TODO describing
the remaining validation work (`Job.cs:37-57,267-289`), while production terminal
commits are centralized through `JobOutcomeCommitter`. The report gives no
concrete illegal transition. Keep the focused validated reducer on the roadmap;
do not bundle the much larger immutable-record/unidirectional-flow rewrite into
fixes for these findings.

### REFACTOR-002 — one search projection/history owner

Implemented by `SearchDefinition`, `SearchViewKernel`, bounded raw sources, and
the Search View store. Remove this from the outstanding list.

### REFACTOR-003 — centralized search settings composition

Implemented by `SearchSettingsBaselines` and `JobSettingsComposer`, with generic
versus music semantics shared across frontends. Remove this from the outstanding
list.

### REFACTOR-004 — persistence inbox lifecycle/admission

The invariant is real, but “across all storage classes” should mean one small
lifecycle contract inside the existing inbox. It does not justify new public
types, queues, or persistence ownership.

### REFACTOR-005 — durable workflow summary/read model

Premature as written. PERSIST-004 first needs a read snapshot plus a decision on
whether retention deletes jobs or whole workflows and what immutable field owns
workflow order. PERF-003 should then be measured. A summary table may ultimately
be clean, but it should follow those semantics rather than predetermine them.

### REFACTOR-006 — output-path ownership/collision semantics

Justified, with a tighter contract: coordinate concurrent final path claims,
make overwrite versus skip versus collision-renaming explicit, and invalidate
cache aliases when replacement occurs. Do not erase the tested
`SkipExisting=false` overwrite behavior. `PlacementPlanner` already owns safe
directory layout and within-plan collision suffixing; reuse it where applicable
rather than introducing a parallel placement model.

## Suggested next areas from the report

1. **PlacementPlanner review — completed here.** Peer-provided components are
   sanitized portably, empty/dot components are replaced, the final path is
   proven below the configured parent, and collisions within one directory plan
   get deterministic suffixes (`PlacementPlanner.cs:54-150`). No directory
   traversal issue is evident. Cross-job/existing-output ownership remains part
   of CORE-005.

2. **Trace on-complete callers — low remaining value for this audit.** Current
   code supports the intentional transfer-success/job-postprocess-failure split.
   Early cache publication still exists for unstaged destinations, but has a
   coherent retry purpose. A short contract note is more useful than further
   architectural work unless product semantics change.

3. **Chained on-complete test — worthwhile and small.** Decide initial versus
   dynamic outcome semantics, document it, and add exactly one behavioral test
   covering `update-index` followed by an outcome-gated command.

4. **Continue output ownership review — completed enough to act.** Directory
   within-plan collision handling exists, but music organization, direct files,
   incomplete-album movement, concurrent jobs, and cache aliases do not share a
   final-path ownership contract. Keep these as one CORE-005 fix.

5. **Failed-generation recovery — still worthwhile.** Tests establish the
   current fail-closed behavior. The next work is a product/API coverage decision,
   not more source archaeology.

6. **Filtered chat-room pagination — low priority, no current finding.**
   `GetFilteredRoomsAsync` pages through all subscribed rooms and evaluates a
   runtime state predicate in memory (`ChatPersistenceStore.cs:657-690`). That is
   asymptotically unbounded, but subscribed room counts are normally small and
   the base query has the correct account/order index. Do not add another index
   or read model without evidence that this endpoint is slow at a supported
   scale.

7. **Unknown migrations — now concrete enough for a small guard.** The missing
   comparison is visible in the initializer. Decide whether binary downgrade is
   supported; if yes, reject unknown applied migration IDs with a clear startup
   error and test it.

## Recommended priority

1. Fix PERSIST-007 and CORE-003 first; both are small, unambiguous correctness
   changes.
2. Repair fast-search coordination as one change covering CORE-004 and the
   missed wake-up.
3. Fix search terminal/recovery ordering and startup labeling (PERSIST-003).
4. Give downloads the same exact terminal handoff semantics as uploads
   (PERSIST-006).
5. Define and implement output collision/cache ownership (CORE-005).
6. Add read snapshots and settle retention precedence/order identity
   (PERSIST-002/PERSIST-004).
7. Close the remaining timestamp and admission gaps (PERSIST-001/PERSIST-005).
8. Add retention catch-up with a work budget (PERF-004).

PERF-003, dynamic on-complete chaining, failed-handoff presentation, and binary
downgrade behavior need a small semantic decision before implementation. The
four resolved findings should be removed from any active audit backlog.
