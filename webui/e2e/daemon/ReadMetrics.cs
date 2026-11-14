using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore.Diagnostics;

// Opt-in fixture diagnostics, not daemon services or a production timing policy.
sealed class ReadMetrics : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AsyncLocal<Sample?> current = new();
    private readonly ConcurrentBag<IDisposable> subscriptions = [];
    private readonly IDisposable listeners;
    private readonly ActivityListener storeActivities;
    private long intervalSqlTicks;
    private long intervalCommitTicks;
    private int intervalCommands;
    private long startedReads;
    private readonly object summaryGate = new();
    private readonly Dictionary<string, RouteTotals> routes = [];
    private readonly List<ReadTiming> slowest = [];
    private readonly RouteTotals timelineTotals = new();
    private readonly List<TimelineReadTiming> timelineSlowest = [];
    private readonly ConcurrentDictionary<HistoryWriteShape, long> historyRows = new();

    public ReadMetrics()
    {
        bool profileProjections = Environment.GetEnvironmentVariable("SOCKSEEK_PROFILE_SEARCH_VIEWS") == "1";
        listeners = DiagnosticListener.AllListeners.Subscribe(this);
        storeActivities = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Sockseek.Server.SearchViewCache" or "Sockseek.Server.Transfers"
                or "Sockseek.Server.Submissions" or "Sockseek.Persistence.Write"
                || profileProjections && source.Name == "Sockseek.Server.SearchViews",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => current.Value is not null
                || options.Source.Name is "Sockseek.Server.Transfers" or "Sockseek.Server.Submissions" or "Sockseek.Server.SearchViews" or "Sockseek.Persistence.Write"
                ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = activity =>
            {
                if (activity.Source.Name == "Sockseek.Server.SearchViews")
                {
                    activity.SetCustomProperty("projection-thread", CurrentThreadCounters());
                    activity.SetCustomProperty("projection-cpu", CurrentProcessCpu());
                }
                if (activity.Source.Name == "Sockseek.Persistence.Write" && activity.OperationName == "commit")
                    activity.SetCustomProperty("write-bytes-before", CurrentProcessWriteBytes());
                if (activity.Source.Name != "Sockseek.Server.Transfers"
                    && !(activity.Source.Name == "Sockseek.Persistence.Write" && activity.OperationName is "command" or "batch" or "checkpoint")) return;
                activity.SetCustomProperty("read-parent", current.Value);
                var sample = new Sample(CurrentProcessCpu(), CurrentThreadCounters());
                activity.SetCustomProperty("read-sample", sample);
                current.Value = sample;
            },
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == "Sockseek.Server.SearchViews")
                {
                    Console.WriteLine("E2E_METRIC search-view-phase " + JsonSerializer.Serialize(new
                    {
                        operation = activity.OperationName, traceId = activity.TraceId.ToString(),
                        parentId = activity.ParentSpanId.ToString(), spanId = activity.SpanId.ToString(),
                        viewId = activity.GetTagItem("view.id") ?? activity.Parent?.GetTagItem("view.id"),
                        initial = activity.GetTagItem("initial") ?? activity.Parent?.GetTagItem("initial"),
                        inputCount = activity.GetTagItem("input.count"),
                        milliseconds = activity.Duration.TotalMilliseconds, startedAtUtc = activity.StartTimeUtc,
                        processCpuMilliseconds = activity.GetCustomProperty("projection-cpu") is TimeSpan cpu
                            ? (CurrentProcessCpu() - cpu).TotalMilliseconds : (double?)null,
                        threadAtStart = activity.GetCustomProperty("projection-thread"),
                        threadAtCompletion = CurrentThreadCounters(),
                    }, JsonOptions));
                    return;
                }
                if (activity.Source.Name == "Sockseek.Persistence.Write" && activity.OperationName is "command" or "batch" or "checkpoint")
                {
                    var writerSample = (Sample)activity.GetCustomProperty("read-sample")!;
                    current.Value = (Sample?)activity.GetCustomProperty("read-parent");
                    Console.WriteLine("E2E_METRIC writer-phases " + JsonSerializer.Serialize(new
                    {
                        operation = activity.OperationName, traceId = activity.TraceId.ToString(),
                        parentId = activity.ParentSpanId.ToString(), spanId = activity.SpanId.ToString(),
                        milliseconds = activity.Duration.TotalMilliseconds, startedAtUtc = activity.StartTimeUtc,
                        queueMilliseconds = activity.GetTagItem("queue.duration_ms"),
                        mutationCount = activity.GetTagItem("mutation.count"),
                        readyBackgroundCommands = activity.GetTagItem("ready.background_commands"),
                        readyInteractiveCommands = activity.GetTagItem("ready.interactive_commands"),
                        cacheSpillsBefore = activity.GetTagItem("cache.spills.before"),
                        cacheSpillsAfter = activity.GetTagItem("cache.spills.after"),
                        cacheWritesBefore = activity.GetTagItem("cache.writes.before"),
                        cacheWritesAfter = activity.GetTagItem("cache.writes.after"),
                        cacheBytes = activity.GetTagItem("cache.bytes.after"),
                        checkpointOutcome = activity.GetTagItem("checkpoint.outcome"),
                        checkpointFrames = activity.GetTagItem("checkpoint.frames"),
                        checkpointCopied = activity.GetTagItem("checkpoint.copied"),
                        processCpuMilliseconds = (CurrentProcessCpu() - writerSample.ProcessCpuAtStart).TotalMilliseconds,
                        sqlMilliseconds = TimeSpan.FromTicks(writerSample.SqlTicks).TotalMilliseconds,
                        commands = writerSample.Commands, phases = writerSample.StoreReads.ToArray(),
                        rowBatches = writerSample.RowBatches.ToArray(),
                        observationBatches = writerSample.ObservationBatches.ToArray(),
                    }, JsonOptions));
                    return;
                }
                if (activity.Source.Name == "Sockseek.Server.Submissions")
                {
                    Console.WriteLine("E2E_METRIC submission-phases " + JsonSerializer.Serialize(new
                    {
                        operation = activity.OperationName, jobId = activity.GetTagItem("job.id"),
                        traceId = activity.TraceId.ToString(), spanId = activity.SpanId.ToString(),
                        milliseconds = activity.Duration.TotalMilliseconds, startedAtUtc = activity.StartTimeUtc,
                        phases = activity.Events.Select(item => new { item.Name,
                            milliseconds = (item.Timestamp.UtcDateTime - activity.StartTimeUtc).TotalMilliseconds }),
                    }, JsonOptions));
                    return;
                }
                if (activity.Source.Name != "Sockseek.Server.Transfers")
                {
                    if (current.Value is { } observationSample)
                        foreach (var item in activity.Events.Where(item => item.Name == "search.observations"))
                            observationSample.ObservationBatches.Enqueue(item.Tags.ToDictionary(pair => pair.Key, pair => pair.Value));
                    if (current.Value is { } writeSample)
                        foreach (var item in activity.Events.Where(item => item.Name == "history.rows"))
                        {
                            var tags = item.Tags.ToDictionary(pair => pair.Key, pair => pair.Value);
                            writeSample.RowBatches.Enqueue(tags);
                            var key = new HistoryWriteShape((string)tags["table"]!, (string)tags["operation"]!, (string)tags["columns"]!);
                            long count = Convert.ToInt64(tags["count"], CultureInfo.InvariantCulture);
                            historyRows.AddOrUpdate(key, count, (_, previous) => previous + count);
                        }
                    long? writeBytes = activity.GetCustomProperty("write-bytes-before") is long before
                        ? CurrentProcessWriteBytes() - before : null;
                    current.Value?.StoreReads.Enqueue(new(activity.OperationName, activity.Duration.TotalMilliseconds, activity.StartTimeUtc, writeBytes));
                    return;
                }
                var sample = (Sample)activity.GetCustomProperty("read-sample")!;
                var parent = (Sample?)activity.GetCustomProperty("read-parent");
                current.Value = parent;
                if (parent is not null)
                {
                    // The HTTP snapshot uses the same observed read phases.
                    // Its request totals must still include their nested SQL.
                    Interlocked.Add(ref parent.SqlTicks, sample.SqlTicks);
                    Interlocked.Add(ref parent.ReaderTicks, sample.ReaderTicks);
                    Interlocked.Add(ref parent.ConnectionTicks, sample.ConnectionTicks);
                    Interlocked.Add(ref parent.Commands, sample.Commands);
                    Interlocked.Add(ref parent.QueryCompilationTicks, sample.QueryCompilationTicks);
                    Interlocked.Add(ref parent.QueryCompilations, sample.QueryCompilations);
                    parent.StoreReads.Enqueue(new(activity.OperationName, activity.Duration.TotalMilliseconds, activity.StartTimeUtc));
                    foreach (var command in sample.SlowCommands) parent.SlowCommands.Enqueue(command);
                }
                var duration = activity.Duration.TotalMilliseconds;
                var timing = new TimelineReadTiming(activity.OperationName, duration, TimeSpan.FromTicks(sample.SqlTicks).TotalMilliseconds,
                    sample.Commands, Convert.ToInt32(activity.GetTagItem("transfer.count")), Convert.ToInt32(activity.GetTagItem("group.count")), activity.StartTimeUtc,
                    activity.GetTagItem("read.epoch") as Guid?, activity.GetTagItem("read.sequence") as long?,
                    TimeSpan.FromTicks(sample.QueryCompilationTicks).TotalMilliseconds, sample.QueryCompilations,
                    TimeSpan.FromTicks(sample.ReaderTicks).TotalMilliseconds,
                    TimeSpan.FromTicks(sample.ConnectionTicks).TotalMilliseconds,
                    (CurrentProcessCpu() - sample.ProcessCpuAtStart).TotalMilliseconds);
                lock (summaryGate)
                {
                    timelineTotals.Completed++;
                    if (activity.Status == ActivityStatusCode.Error) timelineTotals.Failed++;
                    else
                    {
                        timelineTotals.Successful++;
                        if (duration < 100) timelineTotals.Under100++;
                        if (duration < 300) timelineTotals.Under300++;
                    }
                    timelineTotals.MaximumMs = Math.Max(timelineTotals.MaximumMs, duration);
                    timelineSlowest.Add(timing);
                    timelineSlowest.Sort((left, right) => right.Milliseconds.CompareTo(left.Milliseconds));
                    if (timelineSlowest.Count > 10) timelineSlowest.RemoveAt(10);
                }
            },
        };
        ActivitySource.AddActivityListener(storeActivities);
    }

    public async Task MeasureAsync(HttpContext context, RequestDelegate next)
    {
        string path = context.Request.Path.Value ?? "";
        if (context.Request.Method == "POST" && path.StartsWith("/api/jobs/", StringComparison.Ordinal)
            && path != "/api/jobs/lookup")
        {
            // Keep mutation latency separate from fixed-size read percentiles.
            // This includes binding and response serialization around owner phases.
            long started = Stopwatch.GetTimestamp();
            double? responseStart = null;
            context.Response.OnStarting(() =>
            {
                responseStart = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return Task.CompletedTask;
            });
            try { await next(context); }
            finally
            {
                Console.WriteLine("E2E_METRIC submission-http " + JsonSerializer.Serialize(new
                {
                    path, milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    responseStartMilliseconds = responseStart, status = context.Response.StatusCode,
                }, JsonOptions));
            }
            return;
        }
        bool readRequest = context.Request.Method == "GET"
            || context.Request.Method == "POST" && (path is "/api/jobs/lookup" or "/api/transfers/lookup" or "/api/transfers/timeline" or "/api/transfers/group-summaries"
                || path.StartsWith("/api/search-views/", StringComparison.Ordinal)
                    && (path.EndsWith("/selection-counts", StringComparison.Ordinal)
                        || path.EndsWith("/selection-settings", StringComparison.Ordinal)
                        || path.EndsWith("/chosen-options/resolve", StringComparison.Ordinal)));
        if (!readRequest || !path.StartsWith("/api/", StringComparison.Ordinal)
            || path.StartsWith("/api/events", StringComparison.Ordinal)
            || path.EndsWith("/updates", StringComparison.Ordinal)
            // Readiness waits are measured by the Review journey's click-to-DOM
            // clock, not treated as slow fixed-size database reads.
            || path.StartsWith("/api/job-previews/", StringComparison.Ordinal)
                && context.Request.Query["waitForCompletion"] == "true")
        {
            await next(context);
            return;
        }
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var sample = new Sample(CurrentProcessCpu(), CurrentThreadCounters());
        long readId = Interlocked.Increment(ref startedReads);
        current.Value = sample;
        context.Response.Headers["X-E2E-Read-Id"] = readId.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-E2E-Read-Started-At"] = startedAtUtc
            .ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        double lastGcPauseMs = 0;
        foreach (var pause in GC.GetGCMemoryInfo().PauseDurations)
            lastGcPauseMs += pause.TotalMilliseconds;
        ThreadPool.GetAvailableThreads(out int availableWorkers, out _);
        ThreadPool.GetMaxThreads(out int maximumWorkers, out _);
        context.Response.Headers["X-E2E-Runtime"] = FormattableString.Invariant(
            $"threads={ThreadPool.ThreadCount};busy={maximumWorkers - availableWorkers};pending={ThreadPool.PendingWorkItemCount};gc2={GC.CollectionCount(2)};last-gc-ms={lastGcPauseMs:F1};total-gc-ms={GC.GetTotalPauseDuration().TotalMilliseconds:F1};server-gc={System.Runtime.GCSettings.IsServerGC}");
        context.Response.OnStarting(() =>
        {
            sample.ResponseStartMs = elapsed.Elapsed.TotalMilliseconds;
            context.Response.Headers["Server-Timing"] = "read;dur="
                + sample.ResponseStartMs.Value.ToString("F1", CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });
        double setupMs = elapsed.Elapsed.TotalMilliseconds;
        context.Response.Headers["X-E2E-Read-Setup-Ms"] = setupMs.ToString("F1", CultureInfo.InvariantCulture);
        bool completedNormally = false;
        try { await next(context); completedNormally = true; }
        finally
        {
            current.Value = null;
            double durationMs = elapsed.Elapsed.TotalMilliseconds;
            string route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            if (path == "/api/jobs") route += context.Request.Query.ContainsKey("parentJobId") ? " [children]" : " [roots/all]";
            ThreadCounters? completionThread = CurrentThreadCounters();
            double processCpuMs = (CurrentProcessCpu() - sample.ProcessCpuAtStart).TotalMilliseconds;
            double completionMetricsMs = elapsed.Elapsed.TotalMilliseconds - durationMs;
            var timing = new ReadTiming(readId, route, context.Response.StatusCode,
                context.RequestAborted.IsCancellationRequested, completedNormally, durationMs, sample.ResponseStartMs,
                TimeSpan.FromTicks(sample.SqlTicks).TotalMilliseconds,
                TimeSpan.FromTicks(sample.ReaderTicks).TotalMilliseconds,
                TimeSpan.FromTicks(sample.ConnectionTicks).TotalMilliseconds,
                (GC.GetTotalPauseDuration() - sample.GcPauseAtStart).TotalMilliseconds,
                processCpuMs, sample.Commands,
                startedAtUtc, durationMs >= 100 ? completionThread?.ThreadId : null,
                sample.ThreadAtStart, completionThread, context.Request.Method, sample.StoreReads.ToArray(), setupMs, completionMetricsMs,
                TimeSpan.FromTicks(sample.QueryCompilationTicks).TotalMilliseconds, sample.QueryCompilations);
            lock (summaryGate)
            {
                if (!routes.TryGetValue(route, out var totals)) routes[route] = totals = new();
                totals.Completed++;
                if (timing.Aborted) totals.Aborted++;
                else if (!completedNormally || timing.StatusCode >= 400) totals.Failed++;
                else
                {
                    totals.Successful++;
                    if (durationMs < 100) totals.Under100++;
                    if (durationMs < 300) totals.Under300++;
                }
                totals.MaximumMs = Math.Max(totals.MaximumMs, durationMs);
                slowest.Add(timing);
                slowest.Sort((left, right) => right.Milliseconds.CompareTo(left.Milliseconds));
                if (slowest.Count > 10) slowest.RemoveAt(10);
            }
            Console.WriteLine($"E2E_METRIC read path={path} duration-ms={durationMs:F1} "
                + $"ef-sql-ms={timing.SqlMilliseconds:F1} ef-commands={sample.Commands} read-id={readId} "
                + $"response-start-ms={sample.ResponseStartMs:F1} ef-reader-ms={timing.ReaderLifetimeMilliseconds:F1} "
                + $"ef-connection-ms={timing.ConnectionMilliseconds:F1} gc-pause-ms={timing.GcPauseMilliseconds:F1} "
                + $"ef-query-compilation-ms={timing.QueryCompilationMilliseconds:F1} ef-query-compilations={timing.QueryCompilations} "
                + $"process-cpu-ms={timing.ProcessCpuMilliseconds:F1} method={context.Request.Method} "
                + $"setup-ms={setupMs:F1} completion-metrics-ms={completionMetricsMs:F1}"
                + (timing.NativeThreadIdAtCompletion is { } threadId
                    ? $" started-at={startedAtUtc:O} completion-native-thread-id={threadId} "
                        + DescribeThreadCounters(sample.ThreadAtStart, completionThread) : ""));
            foreach (string command in sample.SlowCommands)
                Console.WriteLine($"E2E_METRIC slow-read-sql path={path} {command}");
            if (timing.StoreReads.Count > 0)
                Console.WriteLine("E2E_METRIC store-phases " + JsonSerializer.Serialize(new
                    { readId, path, phases = timing.StoreReads }, JsonOptions));
        }
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.EntityFrameworkCore")
            subscriptions.Add(listener.Subscribe(this, name => name == RelationalEventId.CommandExecuting.Name
                || name == RelationalEventId.CommandExecuted.Name
                || name == RelationalEventId.TransactionCommitted.Name
                || name == RelationalEventId.DataReaderDisposing.Name
                || name == RelationalEventId.ConnectionOpened.Name
                || name == CoreEventId.QueryCompilationStarting.Name
                || name == CoreEventId.QueryExecutionPlanned.Name));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        // Pair by context so independent queries within one request can overlap.
        // Do not format expressions or expose parameter values.
        if (value.Key == CoreEventId.QueryCompilationStarting.Name)
        {
            if (current.Value is { } sample && value.Value is QueryExpressionEventData { Context: { } context })
                sample.QueryCompilationStarts[context.ContextId.InstanceId] = Stopwatch.GetTimestamp();
        }
        else if (value.Key == CoreEventId.QueryExecutionPlanned.Name)
        {
            if (current.Value is { } sample && value.Value is QueryExpressionEventData { Context: { } context }
                && sample.QueryCompilationStarts.TryRemove(context.ContextId.InstanceId, out long started))
            {
                Interlocked.Add(ref sample.QueryCompilationTicks, Stopwatch.GetElapsedTime(started).Ticks);
                Interlocked.Increment(ref sample.QueryCompilations);
            }
        }
        else if (value.Key == RelationalEventId.CommandExecuting.Name
            && value.Value is CommandEventData starting)
        {
            if (current.Value is { } sample && CurrentThreadCounters() is { } thread)
                sample.CommandThreads[starting.CommandId] = thread;
        }
        else if (value.Value is CommandExecutedEventData command)
        {
            Interlocked.Increment(ref intervalCommands);
            Interlocked.Add(ref intervalSqlTicks, command.Duration.Ticks);
            if (current.Value is { } sample)
            {
                Interlocked.Increment(ref sample.Commands);
                Interlocked.Add(ref sample.SqlTicks, command.Duration.Ticks);
                sample.CommandThreads.TryRemove(command.CommandId, out ThreadCounters? startThread);
                if (command.Duration.TotalMilliseconds >= 100)
                {
                    ThreadCounters? endThread = CurrentThreadCounters();
                    // SQL templates only: parameter values contain peer/path data.
                    string text = command.Command.CommandText;
                    sample.SlowCommands.Enqueue($"duration-ms={command.Duration.TotalMilliseconds:F1} "
                        + $"started-at={command.StartTime:O} connection={command.ConnectionId} "
                        + $"native-thread-id={endThread?.ThreadId} "
                        + DescribeThreadCounters(startThread, endThread) + " "
                        + $"gc-pause-ms-during-read={(GC.GetTotalPauseDuration() - sample.GcPauseAtStart).TotalMilliseconds:F1} statement="
                        + text[..Math.Min(text.Length, 4_096)].Replace('\r', ' ').Replace('\n', ' '));
                }
            }
        }
        else if (value.Value is TransactionEndEventData committed)
            Interlocked.Add(ref intervalCommitTicks, committed.Duration.Ticks);
        else if (current.Value is { } readSample)
        {
            if (value.Value is DataReaderDisposingEventData reader)
                Interlocked.Add(ref readSample.ReaderTicks, reader.Duration.Ticks);
            else if (value.Value is ConnectionEndEventData connection)
                Interlocked.Add(ref readSample.ConnectionTicks, connection.Duration.Ticks);
        }
    }

    // An interval includes other concurrent database callers too; these are
    // group diagnostic costs, not an exact per-writer attribution.
    public string TakeDatabaseInterval()
        => $"db-interval-ef-sql-ms={TimeSpan.FromTicks(Interlocked.Exchange(ref intervalSqlTicks, 0)).TotalMilliseconds:F1} "
            + $"db-interval-commit-ms={TimeSpan.FromTicks(Interlocked.Exchange(ref intervalCommitTicks, 0)).TotalMilliseconds:F1} "
            + $"db-interval-ef-commands={Interlocked.Exchange(ref intervalCommands, 0)}";

    // Emit after HTTP shutdown: complete counts survive the fixture's bounded
    // log tail. Latency buckets count successful reads; errors/aborts stay explicit.
    public void WriteSummary()
    {
        lock (summaryGate)
        {
            Console.WriteLine("E2E_READ_SUMMARY " + JsonSerializer.Serialize(new
            {
                started = Volatile.Read(ref startedReads),
                completed = routes.Values.Sum(row => row.Completed),
                successful = routes.Values.Sum(row => row.Successful),
                failed = routes.Values.Sum(row => row.Failed),
                aborted = routes.Values.Sum(row => row.Aborted),
                under100 = routes.Values.Sum(row => row.Under100),
                under300 = routes.Values.Sum(row => row.Under300),
                routes = routes.OrderBy(pair => pair.Key).Select(pair => new { route = pair.Key, totals = pair.Value }),
                slowest,
                timeline = new { totals = timelineTotals, slowest = timelineSlowest },
                historyRowWrites = historyRows.Select(pair => new { pair.Key.Table, pair.Key.Operation,
                    pair.Key.Columns, Count = pair.Value }).ToArray(),
            }, JsonOptions));
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void Dispose()
    {
        storeActivities.Dispose();
        listeners.Dispose();
        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    // Coarse whole-process CPU includes concurrent workers and may exceed wall
    // time. It is not exclusive request/SQL CPU; low values can reveal waiting.
    private static long? CurrentProcessWriteBytes()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            string? line = File.ReadLines("/proc/self/io").FirstOrDefault(line => line.StartsWith("write_bytes:", StringComparison.Ordinal));
            return line == null ? null : long.Parse(line.AsSpan("write_bytes:".Length), CultureInfo.InvariantCulture);
        }
        catch (IOException) { return null; }
    }

    private static TimeSpan CurrentProcessCpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    // One procfs read at each boundary, not continuous sampling. Thread CPU is
    // reported in native clock ticks. Async continuations can switch threads;
    // only matching native IDs permit a delta. Request deltas can include other
    // work between awaits even when they return to the same thread. Fault counts
    // do not measure fault duration; minor faults include ordinary allocation.
    private static ThreadCounters? CurrentThreadCounters()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            string stat = File.ReadAllText("/proc/thread-self/stat");
            int endName = stat.LastIndexOf(')');
            string[] fields = stat[(endName + 2)..].Split(' ');
            return new(int.Parse(stat.AsSpan(0, stat.IndexOf(' ')), CultureInfo.InvariantCulture),
                long.Parse(fields[11], CultureInfo.InvariantCulture),
                long.Parse(fields[12], CultureInfo.InvariantCulture),
                long.Parse(fields[7], CultureInfo.InvariantCulture),
                long.Parse(fields[9], CultureInfo.InvariantCulture));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string DescribeThreadCounters(ThreadCounters? start, ThreadCounters? end)
        => start == null || end == null ? "thread-counters=unavailable"
            : start.ThreadId != end.ThreadId ? $"thread-start={start.ThreadId} thread-end={end.ThreadId} thread-delta=unavailable"
            : $"thread-id={end.ThreadId} thread-user-ticks={end.UserTicks - start.UserTicks} "
                + $"thread-system-ticks={end.SystemTicks - start.SystemTicks} "
                + $"thread-minor-faults={end.MinorFaults - start.MinorFaults} "
                + $"thread-major-faults={end.MajorFaults - start.MajorFaults}";

    private sealed record ThreadCounters(int ThreadId, long UserTicks, long SystemTicks, long MinorFaults, long MajorFaults);

    private sealed class Sample(TimeSpan processCpuAtStart, ThreadCounters? threadAtStart)
    {
        public readonly TimeSpan ProcessCpuAtStart = processCpuAtStart;
        public readonly ThreadCounters? ThreadAtStart = threadAtStart;
        public readonly ConcurrentDictionary<Guid, ThreadCounters> CommandThreads = new();
        public int Commands;
        public long SqlTicks;
        public long ReaderTicks;
        public long ConnectionTicks;
        public readonly ConcurrentDictionary<Guid, long> QueryCompilationStarts = new();
        public long QueryCompilationTicks;
        public int QueryCompilations;
        public double? ResponseStartMs;
        public readonly TimeSpan GcPauseAtStart = GC.GetTotalPauseDuration();
        public readonly ConcurrentQueue<string> SlowCommands = new();
        public readonly ConcurrentQueue<StoreReadTiming> StoreReads = new();
        public readonly ConcurrentQueue<Dictionary<string, object?>> RowBatches = new();
        public readonly ConcurrentQueue<Dictionary<string, object?>> ObservationBatches = new();
    }

    private sealed class RouteTotals
    {
        public long Completed { get; set; }
        public long Successful { get; set; }
        public long Failed { get; set; }
        public long Aborted { get; set; }
        public long Under100 { get; set; }
        public long Under300 { get; set; }
        public double MaximumMs { get; set; }
    }

    private sealed record HistoryWriteShape(string Table, string Operation, string Columns);

    // Reader lifetime includes materialization and may overlap command timing;
    // the fields are diagnostic boundaries, not additive exclusive CPU costs.
    private sealed record ReadTiming(
        long ReadId, string Route, int StatusCode, bool Aborted, bool CompletedNormally,
        double Milliseconds, double? ResponseStartMilliseconds, double SqlMilliseconds,
        double ReaderLifetimeMilliseconds, double ConnectionMilliseconds, double GcPauseMilliseconds,
        double ProcessCpuMilliseconds, int Commands,
        DateTimeOffset StartedAtUtc, int? NativeThreadIdAtCompletion,
        ThreadCounters? ThreadAtStart, ThreadCounters? ThreadAtCompletion, string Method,
        IReadOnlyList<StoreReadTiming> StoreReads, double SetupMilliseconds, double CompletionMetricsMilliseconds,
        double QueryCompilationMilliseconds, int QueryCompilations);

    private sealed record StoreReadTiming(string Operation, double Milliseconds, DateTime StartedAtUtc, long? ProcessWriteBytes = null);
    private sealed record TimelineReadTiming(string Operation, double Milliseconds, double SqlMilliseconds, int Commands,
        int TransferCount, int GroupCount, DateTime StartedAtUtc, Guid? ReadEpoch, long? ReadSequence,
        double QueryCompilationMilliseconds, int QueryCompilations, double ReaderLifetimeMilliseconds,
        double ConnectionMilliseconds, double ProcessCpuMilliseconds);
}
