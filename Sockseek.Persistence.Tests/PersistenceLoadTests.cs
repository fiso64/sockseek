using System.Diagnostics;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Sockseek.Core.Snapshots;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;
using Sockseek.Persistence.Planning;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Persistence.Tests;

[TestClass]
[DoNotParallelize] // Allocation/IO probes must not include another class's workload.
public sealed class PersistenceLoadTests
{
    [TestMethod]
    [TestCategory("Load")]
    public async Task LargeSubmission_MetadataDoesNotRewriteAcceptedCommandOrReceipt()
    {
        await using var database = new LoadDatabase(disableAutomaticCheckpoint: true);
        await database.Initializer.InitializeAsync();
        // Other fixture disposal may clear pooled idle connections. Keep one
        // open so last-connection cleanup cannot checkpoint/remove this WAL.
        await using var heldConnection = await database.Factory.CreateDbContextAsync();
        await heldConnection.Database.OpenConnectionAsync();
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Task writing = new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(CancellationToken.None);
        var store = new SubmissionStore(database.Factory, inbox);
        Guid id = Guid.NewGuid();
        string specification = "{\"retained\":\"" + new string('x', 16 * 1024 * 1024) + "\"}";
        try
        {
            await store.CreateAsync(new(id, DateTimeOffset.UtcNow, 99, specification,
                null, null, null, "fingerprint", "{\"accepted\":true}"));
            long before = new FileInfo(database.DatabasePath + "-wal").Length;
            var stopwatch = Stopwatch.StartNew();
            var retained = await store.GetSubmissionAsync(id);
            Assert.IsNotNull(retained);
            Assert.AreEqual(specification, retained.SpecificationJson);
            Assert.AreEqual(1, retained.Revision);
            Assert.AreEqual("{\"accepted\":true}", retained.CommitReceiptJson);

            Guid runtimeId = Guid.NewGuid();
            await SeedRuntimeAsync(database, runtimeId);
            var root = new JobPersistenceMutation(
                runtimeId, 1, DateTimeOffset.UtcNow, Guid.NewGuid(), 1,
                PersistenceMutationPriority.Structural, Guid.NewGuid(), null, null, null,
                1, "Search", "Terminal", "None", null, "Succeeded", "None", "None", "None",
                null, null, "Accepted root", "Accepted root", 1, new(() => "{}"),
                SubmissionId: id, SemanticRole: "UserRoot", SubmissionSpecificationJson: specification);
            long beforeMetadata = GC.GetTotalAllocatedBytes(precise: true);
            stopwatch.Restart();
            Assert.AreEqual(1, (await store.SetArchivedAsync(id, true)).AffectedSubmissionCount);
            TimeSpan archiveTime = stopwatch.Elapsed;
            Assert.AreEqual(1, (await store.SetArchivedAsync(id, false)).AffectedSubmissionCount);
            TimeSpan unarchiveTime = stopwatch.Elapsed - archiveTime;
            Assert.IsTrue(inbox.TryEnqueue(root));
            inbox.Complete();
            await writing;
            long metadataAllocation = GC.GetTotalAllocatedBytes(precise: true) - beforeMetadata;
            Console.WriteLine($"16-MiB immutable command: archive/unarchive and root registration "
                + $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms (archive {archiveTime.TotalMilliseconds:F1}, "
                + $"unarchive {unarchiveTime.TotalMilliseconds:F1}, root "
                + $"{(stopwatch.Elapsed - archiveTime - unarchiveTime).TotalMilliseconds:F1}), "
                + $"allocated {metadataAllocation / 1048576.0:F2} MiB.");
            var job = await new JobHistoryReader(database.Factory).GetJobAsync(root.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(id, job.SubmissionId);
            retained = await store.GetSubmissionAsync(id);
            Assert.IsNotNull(retained);
            Assert.AreEqual(specification, retained.SpecificationJson);
            Assert.AreEqual(99, retained.SpecificationSchemaVersion);
            Assert.AreEqual(3L, retained.Revision);
            Assert.IsNull(retained.ArchivedAtUtc);
            Assert.AreEqual("{\"accepted\":true}", retained.CommitReceiptJson);
            Assert.IsTrue(metadataAllocation < 8 * 1024 * 1024,
                $"Small metadata/root updates allocated {metadataAllocation} bytes: immutable intent must not be rehydrated.");
            long written = new FileInfo(database.DatabasePath + "-wal").Length - before;
            Assert.IsTrue(written < 1024 * 1024,
                $"Small metadata/root updates appended {written} WAL bytes; accepted command size must not govern metadata writes.");
            Assert.AreEqual(specification, await store.GetSpecificationJsonAsync(id),
                "The independent replay read must preserve accepted bytes after metadata changes.");
            Assert.IsNull(await store.GetSpecificationJsonAsync(Guid.NewGuid()));
        }
        finally
        {
            inbox.Complete();
            await writing;
        }
    }

    [TestMethod]
    [TestCategory("Load")]
    public async Task LargeRetainedSubmissions_HistoryPagesReadOnlySummaryData()
    {
        await using var database = new LoadDatabase();
        await database.Initializer.InitializeAsync();
        Guid runtimeId = Guid.NewGuid();
        await SeedRuntimeAsync(database, runtimeId);
        // About 64 MiB of retained commands. Listing metadata must not read
        // these trees, regardless of whether their retained schema is executable.
        string specification = "{\"retained\":\"" + new string('x', 256 * 1024) + "\"}";
        await ExecuteAsync(database, """
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 250)
            INSERT INTO submissions (
                id, submitted_at_utc, specification_schema_version,
                revision, archived_at_utc, rerun_of_submission_id, preview_id, artifact_id)
            SELECT upper(printf('40000000-0000-0000-0000-%012x', x)), 1000, 99,
                7, CASE WHEN x > 225 THEN 2000 ELSE NULL END,
                CASE WHEN x > 1 THEN '40000000-0000-0000-0000-000000000001' ELSE NULL END,
                upper(printf('70000000-0000-0000-0000-%012x', x)), 'source — 雪'
            FROM n;
            INSERT INTO submission_specifications (submission_id, specification_json)
            SELECT id, $specification FROM submissions;
            """, ("$specification", specification));
        await ExecuteAsync(database, """
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 1000)
            INSERT INTO jobs (
                id, workflow_id, submission_id, semantic_role, last_runtime_id,
                last_sequence, display_id, kind, lifecycle_state, activity_phase,
                terminal_outcome, skip_reason, cancellation_source, failure_reason,
                created_at_utc, updated_at_utc, completed_at_utc, revision,
                payload_schema_version)
            SELECT upper(printf('50000000-0000-0000-0000-%012x', x)),
                upper(printf('40000000-0000-0000-0000-%012x', (x + 3) / 4)),
                upper(printf('40000000-0000-0000-0000-%012x', (x + 3) / 4)),
                CASE WHEN x % 4 = 1 THEN 'UserRoot' ELSE 'ExecutionChild' END,
                $runtime, x, x, 'Song',
                CASE WHEN x % 4 = 2 THEN 'Running' ELSE 'Terminal' END, 'None',
                CASE WHEN x % 4 = 2 THEN 'None' WHEN x % 4 = 3 THEN 'Failed' ELSE 'Succeeded' END,
                'None', 'None', 'None', x, x,
                CASE WHEN x % 4 = 2 THEN NULL ELSE x END, 1, 1
            FROM n;
            """, ("$runtime", SqlGuid(runtimeId)));
        var store = new SubmissionStore(database.Factory, new PersistenceInbox(new(), new()));
        var coldWatch = Stopwatch.StartNew();
        var initial = await store.GetSubmissionsAsync(new(Limit: 1));
        TimeSpan coldTime = coldWatch.Elapsed;
        Assert.AreEqual(1, initial.Items.Count);
        Assert.IsTrue(coldTime < TimeSpan.FromMilliseconds(100),
            $"First summary page took {coldTime.TotalMilliseconds:F1} ms; listing accepted work must be immediately usable.");

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        var first = await store.GetSubmissionsAsync(new(Limit: 100));
        TimeSpan firstTime = stopwatch.Elapsed;
        var second = await store.GetSubmissionsAsync(new(first.NextCursor, 100));
        TimeSpan secondTime = stopwatch.Elapsed - firstTime;
        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - before;
        Console.WriteLine($"250 retained 256-KiB specifications: cold one-row page {coldTime.TotalMilliseconds:F1} ms; summary pages "
            + $"{firstTime.TotalMilliseconds:F1}/{secondTime.TotalMilliseconds:F1} ms, "
            + $"allocated {allocatedBytes / 1048576.0:F2} MiB.");

        var third = await store.GetSubmissionsAsync(new(second.NextCursor, 100));
        var archived = await store.GetSubmissionsAsync(new(Limit: 100, Archived: true));
        var all = first.Items.Concat(second.Items).Concat(third.Items).ToArray();
        Assert.AreEqual(100, first.Items.Count);
        Assert.AreEqual(100, second.Items.Count);
        Assert.AreEqual(25, third.Items.Count);
        Assert.IsNull(third.NextCursor);
        Assert.AreEqual(225, all.Select(item => item.Id).Distinct().Count());
        CollectionAssert.AreEqual(Enumerable.Range(1, 225).Reverse()
            .Select(value => Guid.Parse($"40000000-0000-0000-0000-{value:x12}")).ToArray(),
            all.Select(item => item.Id).ToArray());
        Assert.IsTrue(all.All(item => item.Revision == 7 && item.TotalJobCount == 4
            && item.UserRootJobCount == 1 && item.ActiveJobCount == 1 && item.FailedJobCount == 1));
        Assert.IsTrue(all.All(item => item.ArtifactId == "source — 雪"
            && item.PreviewId == Guid.Parse("70000000" + item.Id.ToString()[8..])));
        Assert.IsTrue(all.SkipLast(1).All(item => item.RerunOfSubmissionId == all[^1].Id));
        Assert.IsNull(all[^1].RerunOfSubmissionId);
        Assert.AreEqual(25, archived.Items.Count);
        Assert.IsTrue(archived.Items.All(item => item.ArchivedAtUtc != null));
        Assert.IsFalse(archived.Items.Any(item => all.Any(active => active.Id == item.Id)));
        var retained = await store.GetSubmissionAsync(first.Items[0].Id);
        Assert.IsNotNull(retained);
        Assert.AreEqual(99, retained.SpecificationSchemaVersion);
        Assert.AreEqual(specification, retained.SpecificationJson,
            "Summary projection must not change the full retained intent used for replay.");
        Assert.IsTrue(allocatedBytes < 8 * 1024 * 1024,
            $"Two metadata pages allocated {allocatedBytes} bytes: retained command size must not govern listing memory.");
    }

    [TestMethod]
    [TestCategory("Load")]
    public async Task HundredThousandJobsAndTransfers_StayCursorBoundedAndUseListingIndexes()
    {
        var readWork = new ReadWorkCounter { MaximumInstructions = 100_000 };
        await using var database = new LoadDatabase(readWork: readWork);
        await database.Initializer.InitializeAsync();
        Guid runtimeId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        await SeedRuntimeAsync(database, runtimeId);

        await ExecuteAsync(database, """
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100000)
            INSERT INTO jobs (
                id, workflow_id, semantic_role, parent_job_id, source_job_id, result_job_id,
                last_runtime_id, last_sequence, display_id, kind, lifecycle_state,
                activity_phase, terminal_outcome, skip_reason, cancellation_source,
                failure_reason, created_at_utc, started_at_utc, updated_at_utc,
                completed_at_utc, revision, payload_schema_version)
            SELECT upper(printf('10000000-0000-0000-0000-%012x', x)), $workflow, CASE WHEN x % 2 = 1 THEN 'UserRoot' ELSE 'SemanticResult' END, NULL,
                CASE WHEN x % 2 = 0 THEN upper(printf('10000000-0000-0000-0000-%012x', x - 1)) END,
                CASE WHEN x % 2 = 1 THEN upper(printf('10000000-0000-0000-0000-%012x', x + 1)) END,
                $runtime, x, x, CASE WHEN x % 2 = 1 THEN 'Extract' ELSE 'Song' END, 'Terminal', 'None', 'Succeeded', 'None', 'None',
                'None', x, x, x, x, 2, 1
            FROM n;
            """, ("$workflow", SqlGuid(workflowId)), ("$runtime", SqlGuid(runtimeId)));

        await ExecuteAsync(database, """
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100000)
            INSERT INTO transfers (
                id, job_id, workflow_id, last_runtime_id, last_sequence, direction,
                source, username, remote_path, local_path, state, terminal_outcome,
                total_bytes, transferred_bytes, attempt_count, created_at_utc,
                started_at_utc, completed_at_utc, failure_reason, cancellation_source, revision)
            SELECT upper(printf('20000000-0000-0000-0000-%012x', x)), NULL, $workflow, $runtime, x,
                CASE WHEN x BETWEEN 2001 AND 2501 THEN 'Upload' ELSE 'Download' END,
                'SoulseekPeer', CASE WHEN x BETWEEN 2001 AND 2501 THEN 'Raré' ELSE 'peer' END,
                'remote', 'local', 'Completed', 'Succeeded',
                100, 100, 1, x, x, x, 'None', 'None', 2
            FROM n;
            """, ("$workflow", SqlGuid(workflowId)), ("$runtime", SqlGuid(runtimeId)));

        var pageTimes = new List<(string Name, double Milliseconds)>();
        async Task<T> ReadPage<T>(string name, Func<Task<T>> read)
        {
            var timer = Stopwatch.StartNew();
            T value = await read();
            pageTimes.Add((name, timer.Elapsed.TotalMilliseconds));
            return value;
        }
        var stopwatch = Stopwatch.StartNew();
        var jobs = new JobHistoryReader(database.Factory);
        var firstJobs = await ReadPage("jobs-workflow", () => jobs.GetJobsAsync(new JobHistoryQuery(Limit: 100, WorkflowId: workflowId, IncludeAll: true)));
        var secondJobs = await ReadPage("jobs-workflow-next", () => jobs.GetJobsAsync(new JobHistoryQuery(firstJobs.NextCursor, 100, WorkflowId: workflowId, IncludeAll: true)));
        var transfers = new TransferHistoryReader(database.Factory);
        var firstTransfers = await ReadPage("transfers-workflow", () => transfers.GetTransfersAsync(new TransferQuery(Limit: 100, WorkflowId: workflowId)));
        var secondTransfers = await ReadPage("transfers-workflow-next", () => transfers.GetTransfersAsync(new TransferQuery(firstTransfers.NextCursor, 100, WorkflowId: workflowId)));
        stopwatch.Stop();
        TimeSpan workflowPages = stopwatch.Elapsed;

        readWork.Enabled = true;
        var sourceRoots = await ReadPage("jobs-import-roots", () => jobs.GetJobsAsync(new JobHistoryQuery(
            Limit: 100, NewestFirst: true, ExcludeExtractResults: true)));
        var nextSourceRoots = await ReadPage("jobs-import-roots-next", () => jobs.GetJobsAsync(new JobHistoryQuery(
            sourceRoots.NextCursor, 100, NewestFirst: true, ExcludeExtractResults: true)));
        CollectionAssert.AreEqual(Enumerable.Range(0, 200).Select(index => 99_999L - index * 2).ToArray(),
            sourceRoots.Items.Concat(nextSourceRoots.Items).Select(job => job.DisplayId).ToArray());
        var linked = await ReadPage("jobs-related-100", () => jobs.GetJobsByIdsAsync(
            sourceRoots.Items.Select(job => job.ResultJobId!.Value).ToArray()));
        readWork.Enabled = false;
        Assert.IsTrue(readWork.Instructions < 100_000,
            $"Import pages and exact linked jobs executed {readWork.Instructions} SQLite instructions; identity lookups must not rescan the workflow.");
        CollectionAssert.AreEquivalent(sourceRoots.Items.Select(job => job.ResultJobId!.Value).ToArray(),
            linked.Select(job => job.Id).ToArray());
        Assert.IsTrue(linked.All(job => job.Kind == "Song"));
        var importTimes = pageTimes.Where(item => item.Name is "jobs-import-roots" or "jobs-import-roots-next" or "jobs-related-100").ToArray();
        Console.WriteLine("100k legacy import jobs (50k sources/results): "
            + string.Join(", ", importTimes.Select(item => $"{item.Name}={item.Milliseconds:F1} ms")));
        Assert.IsTrue(importTimes.All(item => item.Milliseconds < 300),
            "Bounded source pages and linked lookups must use indexes rather than scan retained history.");

        stopwatch.Restart();
        var visibleJobs = await ReadPage("jobs-page", () => jobs.GetJobsAsync(new JobHistoryQuery(Limit: 100, NewestFirst: true)));
        TimeSpan jobsPage = stopwatch.Elapsed;
        stopwatch.Restart();
        var visibleTransfers = await ReadPage("transfers-page", () => transfers.GetTransfersAsync(new TransferQuery(Limit: 100, Direction: "Download")));
        TimeSpan transfersPage = stopwatch.Elapsed;

        var rareTimes = new List<(string Name, double Milliseconds)>();
        async Task<PersistedTransferPage> ReadRare(string name, TransferQuery query)
        {
            stopwatch.Restart();
            var page = await transfers.GetTransfersAsync(query);
            double milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            rareTimes.Add((name, milliseconds));
            pageTimes.Add((name, milliseconds));
            return page;
        }
        var uploads = await ReadRare("rare-direction", new(Limit: 500, Direction: "uPlOaD"));
        var lastUpload = await ReadRare("rare-direction-next", new(uploads.NextCursor, 500, Direction: "upload"));
        var peerUploads = await ReadRare("rare-peer", new(Limit: 500, Direction: "upload", Username: "Raré"));
        var caseDistinctPeer = await ReadRare("case-distinct-peer", new(Limit: 500, Direction: "upload", Username: "raré"));
        Assert.AreEqual(500, uploads.Items.Count);
        Assert.AreEqual(1, lastUpload.Items.Count);
        Assert.IsNull(lastUpload.NextCursor);
        Assert.AreEqual(Guid.Parse("20000000-0000-0000-0000-0000000007d1"), lastUpload.Items.Single().Id);
        CollectionAssert.AreEqual(uploads.Items.Select(item => item.Id).ToArray(),
            peerUploads.Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(0, caseDistinctPeer.Items.Count);
        Console.WriteLine("100k rare-filter pages: " + string.Join(", ", rareTimes.Select(
            item => $"{item.Name}={item.Milliseconds:F1} ms")));
        Assert.IsTrue(rareTimes.All(item => item.Milliseconds < 300),
            "A sparse filter must not make a bounded page take hundreds of milliseconds.");

        Assert.AreEqual(100, visibleJobs.Items.Count);
        Assert.IsNotNull(visibleJobs.NextCursor);
        Assert.AreEqual(100, visibleTransfers.Items.Count);
        Assert.IsNotNull(visibleTransfers.NextCursor);
        Guid[] requestedIds = Enumerable.Range(1, 1000)
            .Select(value => Guid.Parse($"20000000-0000-0000-0000-{value:x12}")).ToArray();
        stopwatch.Restart();
        var exactTransfers = await transfers.GetTransfersAsync(new(
            Limit: 1000, Direction: "download", TransferIds: requestedIds));
        TimeSpan lookupTime = stopwatch.Elapsed;
        CollectionAssert.AreEqual(requestedIds.Reverse().ToArray(),
            exactTransfers.Items.Select(item => item.Id).ToArray());
        Assert.IsNull(exactTransfers.NextCursor);
        Assert.IsTrue(lookupTime < TimeSpan.FromSeconds(1),
            $"A 1,000-ID lookup in retained history took {lookupTime}.");
        Console.WriteLine($"100k jobs/transfers: four workflow pages {workflowPages.TotalMilliseconds:F0} ms; "
            + $"UI-style first jobs page {jobsPage.TotalMilliseconds:F0} ms; "
            + $"first download page {transfersPage.TotalMilliseconds:F0} ms; "
            + $"1,000 exact transfer IDs {lookupTime.TotalMilliseconds:F1} ms.");

        Guid[] excluded = Enumerable.Range(99_001, 1_000)
            .Select(value => Guid.Parse($"20000000-0000-0000-0000-{value:x12}")).ToArray();
        stopwatch.Restart();
        var filtered = await transfers.GetTransfersAsync(new(
            Limit: 500, Direction: "Download", LiveCandidates: excluded.Select(id => new LiveTransferCandidate(id, 2, 0, false)).ToArray()));
        TimeSpan filteredTime = stopwatch.Elapsed;
        stopwatch.Restart();
        var overlapping = await transfers.GetTransfersAsync(new(
            Limit: 500, State: "InProgress",
            LiveCandidates: requestedIds.Select(id => new LiveTransferCandidate(id, 1, 0, true)).ToArray()));
        TimeSpan overlappingTime = stopwatch.Elapsed;
        Assert.AreEqual(0, overlapping.Items.Count);
        Assert.AreEqual(0, overlapping.TransferIds.Count,
            "Newer nonmatching successors must be resolved before hydrating the requested page.");
        Assert.IsTrue(overlappingTime < TimeSpan.FromMilliseconds(100),
            $"A filtered page plus 1,000 newer successors took {overlappingTime}.");
        Assert.AreEqual(500, filtered.Items.Count);
        Assert.AreEqual(Guid.Parse($"20000000-0000-0000-0000-{99_000:x12}"), filtered.Items[0].Id);
        Assert.IsTrue(filtered.Items.All(row => !excluded.Contains(row.Id)));
        Assert.IsNotNull(filtered.NextCursor);
        Assert.IsTrue(filteredTime < TimeSpan.FromMilliseconds(100),
            $"A 500-row page after 1,000 live exclusions took {filteredTime}.");
        Guid[] largeExclusion = Enumerable.Range(60_001, 40_000)
            .Select(value => Guid.Parse($"20000000-0000-0000-0000-{value:x12}")).ToArray();
        stopwatch.Restart();
        var largeFiltered = await transfers.GetTransfersAsync(new(
            Limit: 500, LiveCandidates: largeExclusion.Select(id => new LiveTransferCandidate(id, 2, 0, false)).ToArray()));
        TimeSpan largeFilteredTime = stopwatch.Elapsed;
        Assert.AreEqual(500, largeFiltered.Items.Count);
        Assert.AreEqual(Guid.Parse($"20000000-0000-0000-0000-{60_000:x12}"), largeFiltered.Items[0].Id,
            "A large exclusion set must use one parameter rather than hit SQLite's bind limit.");
        Console.WriteLine($"1,000 filtered successors: {overlappingTime.TotalMilliseconds:F1} ms.");
        Console.WriteLine($"500-row exclusion pages: 1,000 IDs {filteredTime.TotalMilliseconds:F1} ms; "
            + $"40,000 IDs {largeFilteredTime.TotalMilliseconds:F1} ms.");

        // Large live overlap must not deserialize metadata outside the page.
        // These retained predecessors are all older than its newest 500 rows.
        await ExecuteAsync(database, """
            UPDATE transfers SET file_name = 'File.flac', file_size_bytes = 100,
                file_attributes_json = '[{"Type":"BitRate","Value":1000},{"Type":"SampleRate","Value":44100}]'
            WHERE created_at_utc <= 10000;
            """);
        var overlapQuery = new TransferQuery(Limit: 500,
            LiveCandidates: Enumerable.Range(1, 10000).Select(value => new LiveTransferCandidate(
                Guid.Parse($"20000000-0000-0000-0000-{value:x12}"), 3, 0, true)).ToArray());
        await transfers.GetTransfersAsync(overlapQuery); // Warm this query shape outside the guard.
        long beforeOverlap = GC.GetTotalAllocatedBytes(precise: true);
        stopwatch.Restart();
        var boundedOverlap = await transfers.GetTransfersAsync(overlapQuery);
        TimeSpan boundedOverlapTime = stopwatch.Elapsed;
        long overlapAllocation = GC.GetTotalAllocatedBytes(precise: true) - beforeOverlap;
        CollectionAssert.AreEqual(Enumerable.Range(99_501, 500).Reverse()
                .Select(value => Guid.Parse($"20000000-0000-0000-0000-{value:x12}")).ToArray(),
            boundedOverlap.TransferIds.ToArray());
        Assert.AreEqual(500, boundedOverlap.Items.Count);
        Assert.IsTrue(boundedOverlap.Items.All(row => row.File is null));
        Assert.IsTrue(overlapAllocation < 16 * 1024 * 1024,
            $"A 500-row page with 10k live overlaps allocated {overlapAllocation} bytes.");
        Assert.IsTrue(boundedOverlapTime < TimeSpan.FromMilliseconds(100),
            $"A 500-row page with 10k live overlaps took {boundedOverlapTime}.");
        Console.WriteLine($"100k retained transfers/10k live overlaps with off-page attributes: "
            + $"500-row page {boundedOverlapTime.TotalMilliseconds:F1} ms, "
            + $"allocated {overlapAllocation / 1048576.0:F2} MiB.");

        Assert.AreEqual(100, firstJobs.Items.Count);
        Assert.AreEqual(100, secondJobs.Items.Count);
        Assert.AreEqual(200, firstJobs.Items.Concat(secondJobs.Items).Select(job => job.Id).Distinct().Count());
        Assert.AreEqual(100, firstTransfers.Items.Count);
        Assert.AreEqual(100, secondTransfers.Items.Count);
        Assert.AreEqual(200, firstTransfers.Items.Concat(secondTransfers.Items).Select(item => item.Id).Distinct().Count());
        Assert.IsTrue(workflowPages < TimeSpan.FromSeconds(2),
            $"Two bounded pages per resource took {workflowPages}.");

        Console.WriteLine("100k page reads: " + string.Join(", ", pageTimes.Select(
            item => $"{item.Name}={item.Milliseconds:F1} ms")));
        Assert.IsTrue(pageTimes.Count(item => item.Milliseconds < 100) > pageTimes.Count / 2,
            "Most bounded pages must complete below 100 ms, including sparse filters.");
        Assert.IsTrue(pageTimes.All(item => item.Milliseconds < 1000),
            "Even a cold page must not take seconds.");

        StringAssert.Contains(await QueryPlanAsync(database,
            "SELECT id FROM jobs WHERE workflow_id = $workflow ORDER BY display_id, id LIMIT 101",
            ("$workflow", SqlGuid(workflowId))), "IX_jobs_workflow_id_display_id_id");
        StringAssert.Contains(await QueryPlanAsync(database,
            "SELECT id FROM jobs WHERE parent_job_id = $parent ORDER BY display_id, id LIMIT 101",
            ("$parent", SqlGuid(Guid.NewGuid()))), "IX_jobs_parent_job_id_display_id_id");
        StringAssert.Contains(await QueryPlanAsync(database,
            "SELECT id FROM transfers WHERE workflow_id = $workflow ORDER BY created_at_utc, id LIMIT 101",
            ("$workflow", SqlGuid(workflowId))), "IX_transfers_workflow_id_created_at_utc_id");
        StringAssert.Contains(await QueryPlanAsync(database,
            "SELECT id FROM transfers WHERE username = $peer AND archived_at_utc IS NULL "
            + "AND direction COLLATE NOCASE = 'upload' ORDER BY created_at_utc DESC, id DESC LIMIT 501",
            ("$peer", "Raré")), "IX_transfers_username_archived_at_utc_created_at_utc_id");
        StringAssert.Contains(await QueryPlanAsync(database,
            "SELECT id FROM transfers WHERE terminal_outcome = 'Succeeded' "
            + "AND completed_at_utc >= $start AND completed_at_utc < 100001",
            ("$start", 1)), "IX_transfers_terminal_outcome_completed_at_utc");
    }

    [TestMethod]
    [TestCategory("Load")]
    public async Task HundredThousandSearchResults_StaySequenceBoundedAndUsePaginationIndex()
    {
        var readWork = new ReadWorkCounter();
        await using var database = new LoadDatabase(readWork: readWork);
        await database.Initializer.InitializeAsync();
        Guid runtimeId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid searchId = Guid.NewGuid();
        await SeedRuntimeAsync(database, runtimeId);
        await ExecuteAsync(database, """
            INSERT INTO jobs (
                id, workflow_id, semantic_role, last_runtime_id, last_sequence, display_id, kind,
                lifecycle_state, activity_phase, terminal_outcome, skip_reason,
                cancellation_source, failure_reason, created_at_utc, started_at_utc,
                updated_at_utc, completed_at_utc, revision, payload_schema_version)
            VALUES ($search, $workflow, 'UserRoot', $runtime, 100001, 1, 'Search', 'Terminal', 'None',
                'Succeeded', 'None', 'None', 'None', 1, 1, 100001, 100001, 100001, 1);
            INSERT INTO search_jobs (
                job_id, query, revision, result_count, locked_file_count, observed_peer_count, is_complete,
                completed_at_utc, result_persistence_state)
            VALUES ($search, 'large search', 100001, 100000, 0, 100000, 1, 100001, 'Complete');
            """, ("$search", SqlGuid(searchId)), ("$workflow", SqlGuid(workflowId)), ("$runtime", SqlGuid(runtimeId)));
        IReadOnlyList<FileAttributeSnapshot> attributes = [
            new("BitRate", 320, 0), new("Length", 180, 1),
            new("SampleRate", 44100, 4), new("BitDepth", 24, 5),
        ];
        var inputs = Enumerable.Range(1, 100_000).Select(i => new SearchProjectionInput(
            i, i, $"peer-{i}", 1, $@"Music\\file-{i}.mp3", 100, 320, 24, 44100, 180,
            ".mp3", null, null, attributes, DateTimeOffset.FromUnixTimeMilliseconds(i), null)).ToArray();
        long beforePreparation = GC.GetTotalAllocatedBytes(precise: true);
        var prepared = inputs.Chunk(10_000)
            .Select(batch => SearchResultWriter.Prepare(batch)).ToArray();
        long preparationAllocation = GC.GetTotalAllocatedBytes(precise: true) - beforePreparation;
        Console.WriteLine($"Prepare 100k collected files with four attributes: {preparationAllocation / 1048576.0:F1} MiB.");
        Assert.IsTrue(preparationAllocation < 32 * 1024 * 1024,
            $"Raw collection preparation allocated {preparationAllocation} bytes; avoid per-file serialization copies.");
        // Exercise the production batch encoding/constraints, rather than seeding
        // a different representation in SQL. Every chunk commits together here.
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            await context.Database.OpenConnectionAsync();
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await using var transaction = connection.BeginTransaction();
            await using var observations = await SearchResultWriter.ForRawHistoryAsync(
                connection, transaction, searchId, CancellationToken.None);
            int added = 0;
            foreach (var batch in prepared)
            {
                var counts = observations.StoreRawBatch(batch, CancellationToken.None);
                added += counts.Public;
                Assert.AreEqual(0, counts.Locked);
            }
            Assert.AreEqual(100_000, added);
            await transaction.CommitAsync();
        }

        var reader = new SearchHistoryReader(database.Factory);
        var firstRead = Stopwatch.StartNew();
        var first = await reader.GetRawResultsAsync(searchId, 0, 500);
        firstRead.Stop();
        Assert.IsTrue(firstRead.Elapsed < TimeSpan.FromMilliseconds(100),
            $"First 500-row raw-results page took {firstRead.Elapsed.TotalMilliseconds:F1} ms.");
        var tail = await reader.GetRawResultsAsync(searchId, 99_950, 100);
        Assert.IsNotNull(first);
        Assert.AreEqual(500, first.Items.Count);
        Assert.AreEqual(500L, first.NextSequence);
        Assert.IsNotNull(tail);
        Assert.AreEqual(50, tail.Items.Count);
        Assert.IsNull(tail.NextSequence);
        CollectionAssert.AreEqual(
            Enumerable.Range(99_951, 50).Select(value => (long)value).ToArray(),
            tail.Items.Select(item => item.Sequence).ToArray());

        string plan = await QueryPlanAsync(database,
            "SELECT sequence FROM search_results WHERE search_job_id = $search AND sequence > 50000 ORDER BY sequence LIMIT 201",
            ("$search", SqlGuid(searchId)));
        StringAssert.Contains(plan, "source_id=? AND source_sequence>?");
        Assert.IsFalse(plan.Contains("USE TEMP B-TREE", StringComparison.Ordinal), plan);

        // A folder follow-up needs one exact peer, not every retained result.
        // Keep this in the existing scale case: the old unfiltered stream would
        // allocate/visit all 100k facts even though only one peer was requested.
        long beforePeer = GC.GetTotalAllocatedBytes(precise: true);
        readWork.Enabled = true;
        var peerRows = await reader.GetFolderResultsAsync(searchId, new("peer-100000", "Music"), new AlbumQuery(), SearchResultVisibility.Public);
        readWork.Enabled = false;
        long peerAllocation = GC.GetTotalAllocatedBytes(precise: true) - beforePeer;
        Assert.AreEqual(1, peerRows.Count);
        Assert.AreEqual(100_000L, peerRows[0].Sequence);
        Assert.IsTrue(peerAllocation < 256 * 1024,
            $"An exact retained peer read allocated {peerAllocation} bytes; unrelated peers must not be hydrated.");
        Console.WriteLine($"Select one peer from 100k retained peers: {readWork.Instructions} SQLite instructions, {peerAllocation} allocated bytes.");
        Assert.IsTrue(readWork.Instructions < 10_000,
            $"An exact retained peer read executed {readWork.Instructions} SQLite instructions; it must seek the peer before ordering.");

        // Refine the retained scalar facts without expanding every file's wire
        // attributes. Selected metadata must still work after enumeration closes.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        long bytes = 0;
        SearchProjectionInput? selected = null;
        await foreach (SearchProjectionInput input in reader.ReadProjectionInputsAsync(searchId))
        {
            bytes += input.Size;
            selected ??= input;
        }
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Console.WriteLine($"Read 100k retained projection inputs without displaying attributes: {allocated / 1048576.0:F1} MiB.");
        Assert.AreEqual(10_000_000L, bytes);
        Assert.IsTrue(allocated < 96 * 1024 * 1024,
            $"Retained scalar processing allocated {allocated} bytes; avoid unrequested attribute decoding and intermediate per-file copies.");
        CollectionAssert.AreEqual(new[] { "BitRate", "Length", "SampleRate", "BitDepth" },
            selected!.ToFileCandidate().Target.Attributes!.Select(attribute => attribute.Type).ToArray());

        // The opposite shape matters too: one peer can contribute every file.
        // The peer seek must not create a wide, disk-backed SQL ordering pass.
        await ExecuteAsync(database, """
            UPDATE search_observations SET username='single-peer',
                filename='Music\Album-'||source_sequence||'\track.mp3';
            """);
        await ExecuteAsync(database, "PRAGMA wal_checkpoint(TRUNCATE);");
        long? Writes() => File.Exists("/proc/self/io")
            ? long.Parse(File.ReadLines("/proc/self/io").Single(line => line.StartsWith("write_bytes:", StringComparison.Ordinal)).Split(':')[1])
            : null;
        long? beforePeerWrites = Writes();
        var singlePeer = await reader.GetFolderResultsAsync(searchId, new("single-peer", "Music"), new AlbumQuery(), SearchResultVisibility.Public);
        long? peerWrites = Writes() - beforePeerWrites;
        Assert.AreEqual(100_000, singlePeer.Count);
        for (int i = 0; i < singlePeer.Count; i++) Assert.AreEqual(i + 1L, singlePeer[i].Sequence);
        Assert.IsTrue(peerWrites is null or < 1024 * 1024,
            $"Reading one 100k-file peer wrote {peerWrites} bytes; avoid sorting wide SQL rows into temporary files.");

        long beforeFolder = GC.GetTotalAllocatedBytes(precise: true);
        var oneFolder = await reader.GetFolderResultsAsync(searchId, new("single-peer", @"Music\Album-100000"),
            new AlbumQuery(), SearchResultVisibility.Public);
        long folderAllocation = GC.GetTotalAllocatedBytes(precise: true) - beforeFolder;
        Assert.AreEqual(1, oneFolder.Count);
        Assert.AreEqual(100_000L, oneFolder[0].Sequence);
        Assert.IsTrue(folderAllocation < 32 * 1024 * 1024,
            $"One folder from a 100k-file peer allocated {folderAllocation} bytes; hydrate/evaluate only its branch and ancestors.");

    }

    private static async Task SeedRuntimeAsync(LoadDatabase database, Guid runtimeId)
        => await ExecuteAsync(database,
            "INSERT INTO runtime_sessions (id, started_at_utc, version) VALUES ($runtime, 1, 'load-test')",
            ("$runtime", SqlGuid(runtimeId)));

    private static string SqlGuid(Guid value) => value.ToString().ToUpperInvariant();

    private static async Task ExecuteAsync(LoadDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> QueryPlanAsync(LoadDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await using var rows = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await rows.ReadAsync())
            details.Add(rows.GetString(3));
        return string.Join(Environment.NewLine, details);
    }

    private sealed class LoadDatabase : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sockseek-persistence-load", Guid.NewGuid().ToString("N"));
        private readonly SqliteDatabaseOwner owner;

        public LoadDatabase(bool disableAutomaticCheckpoint = false, DbConnectionInterceptor? readWork = null)
        {
            Directory.CreateDirectory(directory);
            var options = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
            global::Tests.TestDatabase.Create(options.DatabasePath);
            owner = SqliteDatabaseOwner.Acquire(options);
            var contextOptions = new DbContextOptionsBuilder<SockseekDbContext>(SockseekDbContextOptions.Create(options));
            if (disableAutomaticCheckpoint)
                contextOptions.AddInterceptors(new ManualCheckpointInterceptor());
            if (readWork != null) contextOptions.AddInterceptors(readWork);
            Factory = new SockseekDbContextFactory(contextOptions.Options);
            Initializer = new SqliteInitializer(Factory, options, owner);
        }

        public SockseekDbContextFactory Factory { get; }
        public SqliteInitializer Initializer { get; }
        public string DatabasePath => Path.Combine(directory, "sockseek.db");

        public ValueTask DisposeAsync()
        {
            owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    // Count SQLite work performed by the real reader, independently of elapsed
    // time/cache state. A peer predicate must not conceal a full-source scan.
    private sealed class ReadWorkCounter : DbConnectionInterceptor
    {
        public volatile bool Enabled;
        private long instructions;
        public long MaximumInstructions { get; init; } = long.MaxValue;
        public long Instructions => Interlocked.Read(ref instructions);
        private void Configure(DbConnection connection)
        {
            var handle = ((SqliteConnection)connection).Handle;
            if (Enabled)
                SQLitePCL.raw.sqlite3_progress_handler(handle, 1_000,
                    _ => Interlocked.Add(ref instructions, 1_000) > MaximumInstructions ? 1 : 0, null);
            else
                SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null!, null);
        }
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData data) => Configure(connection);
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData data,
            CancellationToken cancellationToken = default)
        {
            Configure(connection);
            return Task.CompletedTask;
        }
    }

    // Keep WAL size monotonic for this isolated write-volume probe. This changes
    // checkpoint scheduling only in the fixture, never production durability.
    private sealed class ManualCheckpointInterceptor : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_autocheckpoint=0;";
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_autocheckpoint=0;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
