using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Spectre.Console.Rendering;
using Spectre.Console;

namespace Sockseek.Cli;

internal enum TerminalLogKind
{
    JobSucceeded,
    JobFailed,
    JobPartial,
    JobCancelled,
    JobAlreadyExists,
    SongDownloaded,
    SongAlreadyExists,
    SongSkipped,
    SongFailed,
    AlbumTrackDownloaded,
    AlbumTrackSkipped,
    AlbumTrackFailed,
    ExtractedJobs,
    PlaylistCompleted,
    AggregateCompleted,
    Status,
}

internal sealed record TerminalLogLine(
    TerminalLogKind Kind,
    string JobId,
    int DisplayId,
    string JobType,
    string Message,
    string? Source = null,
    string? Highlight = null,
    bool ShowInLive = true);

internal sealed record JobChildView(
    string Id,
    int DisplayId,
    string State,
    string Name,
    int? Percent = null,
    long? SpeedBytesPerSecond = null,
    TerminalFileMetadata? Metadata = null,
    bool IsMostRecent = false);

internal sealed record TerminalFileMetadata(
    long? SizeBytes,
    int? LengthSeconds,
    int? BitRate,
    int? SampleRate,
    int? BitDepth);

internal sealed record JobView(
    string Id,
    int DisplayId,
    string Kind,
    string Name,
    string State,
    int? Percent = null,
    long? SpeedBytesPerSecond = null,
    int? DoneChildren = null,
    int? TotalChildren = null,
    int? DiscoveryRawResultCount = null,
    IReadOnlyList<JobChildView>? Children = null,
    TerminalFileMetadata? Metadata = null,
    string? ParentId = null,
    bool IsParentSummary = false)
{
    public IReadOnlyList<JobChildView> Children { get; init; } = Children ?? [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record TerminalJobRecord(
    string Id,
    int DisplayId,
    string Kind,
    string State,
    CliJobStatusCategory Category,
    string? ParentId);

internal sealed record TerminalRenderState(
    IReadOnlyList<TerminalJobRecord> JobRecords,
    IReadOnlyList<JobView> JobViews,
    int RetiredCompleted = 0,
    int RetiredFailed = 0);

internal readonly record struct TerminalJobCounts(
    int Active,
    int Queued,
    int Completed,
    int Failed);

internal sealed class TerminalLiveRenderer : IDisposable
{
    private readonly Dictionary<string, JobView> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalJobRecord> _knownJobs = new(StringComparer.Ordinal);
    private int _retiredCompleted;
    private int _retiredFailed;
    private readonly ConcurrentQueue<CliOutputEvent> _pendingLogs = new();
    private readonly List<CliOutputEvent> _printedLogHistory = [];
    private readonly Func<CancellationToken, Task> _runRenderLoop;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromMilliseconds(100);
    private readonly Lock _sync = new();
    private readonly Lock _lifecycleSync = new();

    private CancellationTokenSource? _renderCts;
    private Task? _renderTask;
    private volatile bool _suspended;
    private long _rateLimitResetTicks; // 0 = not rate-limited; otherwise UTC ticks of reset time
    private int _spinFrame;
    private bool _disposed;
    private (int Width, int Height) _lastTerminalSize;
    private bool _terminalWasResized;

    private sealed record LiveRow(IRenderable Renderable);
    private sealed record LiveCell
    {
        public LiveCell(string text, Style? style = null)
        {
            Text = SanitizeLiveText(text);
            Style = style;
        }

        public string Text { get; init; }
        public Style? Style { get; init; }
    }
    private static readonly Style DimIdStyle = new(foreground: Color.Grey);
    private static readonly Style DimStyle = new(foreground: Color.Grey);
    private static readonly Style CyanStyle = new(foreground: Color.Cyan1);
    private static readonly Style YellowStyle = new(foreground: Color.Yellow);
    private static readonly Style BlueStyle = new(foreground: Color.Blue);
    private static readonly Style MagentaStyle = new(foreground: Color.Magenta1);

    private static readonly IReadOnlyList<string> SpinFrames = SupportsUnicodeSpinner()
        ? Spinner.Known.Dots.Frames
        : ["|", "/", "-", "\\"];

    private static readonly IReadOnlyList<string> RateLimitSpinFrames = SupportsUnicodeSpinner()
        ? Spinner.Known.Point.Frames
        : ["·"];

    private static bool SupportsUnicodeSpinner() => AnsiConsole.Profile.Capabilities.Unicode;

    public TerminalLiveRenderer()
        : this(null)
    {
    }

    internal TerminalLiveRenderer(Func<CancellationToken, Task>? runRenderLoop)
    {
        _runRenderLoop = runRenderLoop ?? RenderLoopAsync;
        _lastTerminalSize = GetTerminalSize();
        StartRenderLoop();
    }

    public bool IsPaused
    {
        get => _suspended;
        set
        {
            if (value)
                Suspend();
            else
                Resume();
        }
    }

    public void Publish(CliOutputEvent outputEvent)
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
                return;

            switch (outputEvent)
            {
                case CliOutputEvent.JobLog or CliOutputEvent.ProcessLog or CliOutputEvent.RawLine:
                    _pendingLogs.Enqueue(outputEvent);
                    break;
                case CliOutputEvent.ReplaceRenderState replace:
                    ReplaceRenderState(replace.State);
                    break;
                case CliOutputEvent.RateLimit rateLimit:
                    SetRateLimited(rateLimit.ResetsAt);
                    break;
            }
        }
    }

    private void SetRateLimited(DateTimeOffset? resetsAt)
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _rateLimitResetTicks, resetsAt?.UtcTicks ?? 0L);
    }

    private void ReplaceRenderState(TerminalRenderState state)
    {
        if (_disposed) return;
        lock (_sync)
        {
            _knownJobs.Clear();
            foreach (var record in state.JobRecords)
                _knownJobs[record.Id] = record;

            _jobs.Clear();
            foreach (var view in state.JobViews)
                _jobs[view.Id] = view with { UpdatedAt = DateTimeOffset.UtcNow };

            _retiredCompleted = state.RetiredCompleted;
            _retiredFailed = state.RetiredFailed;
        }
    }

    public void Dispose()
        => Dispose(printSummary: true);

    public void Dispose(bool printSummary)
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
                return;

            _disposed = true;
            StopRenderLoop();
        }

        FlushLogs();
        // When the terminal shrinks, Spectre.Live's repaint can clear the wrong rows and
        // eat into real log scrollback above the live region (see comment in FlushLogs).
        // Replaying the log history at the end recovers those lost lines.
        if (_terminalWasResized)
            ReplayPrintedLogHistory();
        if (printSummary)
            AnsiConsole.MarkupLine(BuildCountsMarkup(CountKnownJobs()));
    }

    private void Suspend()
    {
        lock (_lifecycleSync)
        {
            if (_disposed || _suspended)
                return;

            _suspended = true;
            StopRenderLoop();
        }
    }

    private void Resume()
    {
        lock (_lifecycleSync)
        {
            if (_disposed || !_suspended)
                return;

            _suspended = false;
            StartRenderLoop();
        }
    }

    private void StartRenderLoop()
    {
        _renderCts = new CancellationTokenSource();
        var cancellationToken = _renderCts.Token;
        _renderTask = Task.Run(() => _runRenderLoop(cancellationToken));
    }

    private void StopRenderLoop()
    {
        var cts = _renderCts;
        var task = _renderTask;
        _renderCts = null;
        _renderTask = null;

        if (cts == null || task == null)
            return;

        cts.Cancel();
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AnsiConsole.Live(Render())
                .AutoClear(true)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        RememberTerminalResize();

                        FlushLogs();
                        ctx.UpdateTarget(Render());
                        ctx.Refresh();

                        try { await Task.Delay(_refreshInterval, cancellationToken); }
                        catch (OperationCanceledException) { }
                    }

                    FlushLogs();
                    ctx.UpdateTarget(Render());
                    ctx.Refresh();
                });
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RememberTerminalResize()
    {
        var size = GetTerminalSize();
        if (size == _lastTerminalSize)
            return;

        if (size.Width < _lastTerminalSize.Width || size.Height < _lastTerminalSize.Height)
            _terminalWasResized = true;

        _lastTerminalSize = size;
    }

    private static (int Width, int Height) GetTerminalSize()
    {
        if (Console.IsOutputRedirected)
            return (0, 0);

        try
        {
            return (Console.WindowWidth, Console.WindowHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void FlushLogs()
    {
        // Known bug: Spectre.Live's relative repaint model is not resize-safe when we also
        // print real log lines above the live region. Terminal resize can reflow scrollback,
        // after which Live may clear the wrong rows and delete earlier logs. Buffering logs
        // inside Live avoids real scrollback, which is not an acceptable replacement.
        lock (_sync)
        {
            while (_pendingLogs.TryDequeue(out var line))
            {
                _printedLogHistory.Add(line);
                WriteLogEvent(line);
            }
        }
    }

    private static void WriteLogEvent(CliOutputEvent outputEvent)
    {
        switch (outputEvent)
        {
            case CliOutputEvent.RawLine raw:
                WritePlainLogLines(raw.Text);
                break;
            case CliOutputEvent.ProcessLog process:
                WriteProcessLogLine(process.Line);
                break;
            case CliOutputEvent.JobLog jobLog:
                WriteStructuredLogLine(jobLog.Line);
                break;
        }
    }

    // TODO: Lines wrap incorrectly when terminal size is different
    private void ReplayPrintedLogHistory()
    {
        lock (_sync)
        {
            AnsiConsole.MarkupLine("\n[grey]─── terminal was resized — replaying log ───[/]\n");
            foreach (var line in _printedLogHistory)
            {
                switch (line)
                {
                    case CliOutputEvent.RawLine raw:
                        WritePlainLogLines(raw.Text);
                        break;
                    case CliOutputEvent.JobLog jobLog:
                        WriteStructuredLogLine(jobLog.Line);
                        break;
                    case CliOutputEvent.ProcessLog process:
                        WriteProcessLogLine(process.Line);
                        break;
                }
            }
        }
    }

    private static void WriteStructuredLogLine(TerminalLogLine line)
    {
        line = SanitizeLiveLogLine(line);
        var markup = CliLogStyle.FormatTerminalLogMarkup(line);
        var visualLength = CellCount(Markup.Remove(markup));
        int width = LogLineWidth();
        if (markup.Contains('\n') || (!Console.IsOutputRedirected && visualLength >= width))
        {
            WriteMarkupLogLines(line);
            return;
        }

        AnsiConsole.MarkupLine(markup + PaddingFor(visualLength));
    }

    private static void WritePlainLogLines(string text)
    {
        text = SanitizeLiveText(text);
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            foreach (var visualLine in WrapPlainLogLine(line))
                AnsiConsole.WriteLine(visualLine + PaddingFor(CellCount(visualLine)));
        }
    }

    private static void WriteProcessLogLine(TerminalProcessLogLine line)
    {
        line = line with
        {
            CategoryName = SanitizeLiveText(line.CategoryName),
            Message = SanitizeLiveText(line.Message),
        };
        var normalized = line.Message.Replace("\r\n", "\n").Replace('\r', '\n');
        var messageLines = normalized.Split('\n');
        var prefixText = CliLogStyle.ProcessLogPrefixText(line);
        var prefixMarkup = CliLogStyle.ProcessLogPrefixMarkup(line);

        WriteWrappedProcessLogContent(prefixText, prefixMarkup, messageLines[0], continuation: false);

        foreach (var messageLine in messageLines.Skip(1))
            WriteWrappedProcessLogContent("", "", messageLine, continuation: true);
    }

    private static void WriteWrappedProcessLogContent(
        string prefixText,
        string prefixMarkup,
        string content,
        bool continuation)
    {
        int lineWidth = LogLineWidth() - CellCount(prefixText);
        var chunks = WrapContent(content, lineWidth).ToList();

        foreach (var chunk in chunks)
        {
            var contentMarkup = continuation
                ? $"[grey]{Markup.Escape(chunk)}[/]"
                : Markup.Escape(chunk);
            var markup = prefixMarkup + contentMarkup;
            AnsiConsole.MarkupLine(markup + PaddingFor(CellCount(prefixText) + CellCount(chunk)));
        }
    }

    private static void WriteMarkupLogLines(TerminalLogLine line)
    {
        var normalized = line.Message.Replace("\r\n", "\n").Replace('\r', '\n');
        var messageLines = normalized.Split('\n');
        var displayId = CliLogStyle.FormatTerminalDisplayId(line.DisplayId);
        var prefixText = $"{displayId}{line.JobType}: {CliLogStyle.SourcePrefixText(line.Source)}";
        var prefixMarkup = $"[grey]{Markup.Escape(displayId)}[/]{Markup.Escape(line.JobType)}: {CliLogStyle.SourcePrefixMarkup(line.Source)}";
        var continuationPrefix = new string(' ', displayId.Length);

        WriteWrappedMarkupContent(
            prefixText,
            prefixMarkup,
            messageLines[0],
            first: content => CliLogStyle.FormatMainLogContentMarkup(content, line.Kind, line.Highlight),
            continuation: content => Markup.Escape(content));

        foreach (var messageLine in messageLines.Skip(1))
        {
            // For path lines like "    -> /some/path", align continuation under the path content
            string? pathContPrefix = PathContinuationIndent(messageLine);
            WriteWrappedMarkupContent(
                "",
                "",
                messageLine,
                first: content => $"[grey]{Markup.Escape(content)}[/]",
                continuation: content => $"[grey]{Markup.Escape(content)}[/]",
                continuationPrefixText: pathContPrefix);
        }

        void WriteWrappedMarkupContent(
            string firstPrefixText,
            string firstPrefixMarkup,
            string content,
            Func<string, string> first,
            Func<string, string> continuation,
            string? continuationPrefixText = null)
        {
            string contPrefixText = continuationPrefixText ?? continuationPrefix;
            string contPrefixMarkup = Markup.Escape(contPrefixText);

            int lineWidth = LogLineWidth() - CellCount(firstPrefixText);
            var chunks = WrapContent(content, lineWidth).ToList();

            for (int i = 0; i < chunks.Count; i++)
            {
                bool isFirst = i == 0;
                var chunk = chunks[i];
                var prefixTextForChunk = isFirst ? firstPrefixText : contPrefixText;
                var prefixMarkupForChunk = isFirst ? firstPrefixMarkup : contPrefixMarkup;

                string contentMarkup = (isFirst ? first : continuation)(chunk);
                AnsiConsole.MarkupLine(prefixMarkupForChunk + contentMarkup + PaddingFor(CellCount(prefixTextForChunk) + CellCount(chunk)));
            }
        }
    }

    // Returns the continuation indent string for path lines like "    -> /some/path",
    // aligning wrapped continuation chunks under the path content (after the arrow).
    private static string? PathContinuationIndent(string messageLine)
    {
        var trimmed = messageLine.TrimStart();
        if (!trimmed.StartsWith("-> ") && !trimmed.StartsWith("→ "))
            return null;
        int leadingSpaces = messageLine.Length - trimmed.Length;
        int arrowLen = trimmed.IndexOf(' ') + 1;
        return new string(' ', leadingSpaces + arrowLen);
    }

    private static TerminalLogLine SanitizeLiveLogLine(TerminalLogLine line)
        => line with
        {
            JobType = SanitizeLiveText(line.JobType),
            Message = SanitizeLiveText(line.Message),
            Source = line.Source == null ? null : SanitizeLiveText(line.Source),
            Highlight = line.Highlight == null ? null : SanitizeLiveText(line.Highlight),
        };

    internal static string SanitizeLiveText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        StringBuilder? builder = null;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsBidiControl(c))
            {
                builder ??= new StringBuilder(text.Length).Append(text, 0, i);
                continue;
            }

            builder?.Append(c);
        }

        return builder?.ToString() ?? text;
    }

    private static bool IsBidiControl(char c)
        => c is '\u061C' or '\u200E' or '\u200F'
            || (c >= '\u202A' && c <= '\u202E')
            || (c >= '\u2066' && c <= '\u2069');

    private static IEnumerable<string> WrapContent(string content, int availableWidth)
    {
        if (Console.IsOutputRedirected)
            return [content];

        return WrapContentByCellWidth(content, availableWidth);
    }

    private static IEnumerable<string> WrapContentByCellWidth(string content, int availableWidth)
    {
        int width = Math.Max(1, availableWidth);
        if (CellCount(content) < width)
            return [content];

        var wrapped = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0;
        foreach (var element in TextElements(content))
        {
            var elementWidth = CellCount(element);
            if (current.Length > 0 && currentWidth + elementWidth > width)
            {
                wrapped.Add(current.ToString());
                current.Clear();
                currentWidth = 0;
            }

            current.Append(element);
            currentWidth += elementWidth;
        }

        if (current.Length > 0)
            wrapped.Add(current.ToString());
        return wrapped;
    }

    private static IEnumerable<string> WrapPlainLogLine(string line)
    {
        if (Console.IsOutputRedirected)
            return [line];

        int width = LogLineWidth();
        if (CellCount(line) < width)
            return [line];

        return WrapContent(line, width);
    }

    private static string PaddingFor(int visualLength)
        => Console.IsOutputRedirected ? "" : new string(' ', Math.Max(0, LogLineWidth() - visualLength));

    internal static int CellCount(string text)
        => new Segment(text).CellCount();

    internal static IReadOnlyList<string> WrapContentForWidth(string content, int availableWidth)
        => WrapContentByCellWidth(content, availableWidth).ToList();

    private static IEnumerable<string> TextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            yield return enumerator.GetTextElement();
    }

    private static int LogLineWidth()
    {
        if (Console.IsOutputRedirected)
            return int.MaxValue;

        try
        {
            return Math.Max(1, Console.WindowWidth - 1);
        }
        catch
        {
            return 79;
        }
    }

    private Rows Render()
    {
        var rows = BuildRows();
        var renderables = rows.Select(row => row.Renderable);
        return new Rows(renderables);
    }

    private List<LiveRow> BuildRows()
    {
        int maxRows = MaxLiveRows();
        Dictionary<string, JobView> allJobs;
        TerminalJobCounts counts;
        lock (_sync)
        {
            allJobs = _jobs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            counts = CountKnownJobsUnsafe();
        }
        var visibleIds = allJobs.Values
            .Where(job => IsLiveState(job.State))
            .Select(job => job.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in visibleIds.ToArray())
            AddVisibleAncestors(id, allJobs, visibleIds);

        var jobs = allJobs.Values
            .Where(job => visibleIds.Contains(job.Id))
            .OrderBy(job => job.ParentId ?? job.Id, StringComparer.Ordinal)
            .ThenBy(job => job.ParentId == null ? 0 : 1)
            .ThenBy(job => job.DisplayId)
            .ToList();

        long resetTicks = Interlocked.Read(ref _rateLimitResetTicks);
        bool isRateLimited = resetTicks != 0;
        var statusLine = BuildCountsMarkup(counts);
        if (!IsIdle(counts))
        {
            bool useRateLimitSpinner = isRateLimited
                && !allJobs.Values.Any(j => string.Equals(j.State, "downloading", StringComparison.OrdinalIgnoreCase));
            var frames = useRateLimitSpinner ? RateLimitSpinFrames : SpinFrames;
            var spin = frames[_spinFrame++ % frames.Count];
            statusLine = $"{spin} {statusLine}";
        }

        if (isRateLimited)
        {
            var remaining = new DateTimeOffset(new DateTime(resetTicks, DateTimeKind.Utc)) - DateTimeOffset.UtcNow;
            int secs = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            statusLine += $" | [bold yellow]Search rate limit reached, resuming in {secs}s[/]";
        }
        statusLine += " | [grey][[c]]ancel · [[t]]ry next · [[i]]nfo[/]";

        var rows = new List<LiveRow>();

        var childLimits = AllocateChildLimits(jobs, maxRows - 3 - jobs.Count);
        foreach (var job in jobs)
        {
            var indent = job.ParentId != null ? "  " : "";
            var children = VisibleChildren(job, childLimits.GetValueOrDefault(job.Id));
            var hiddenChildren = job.Children.Count(child => IsLiveState(child.State)) - children.Count;
            rows.Add(JobRow(indent, job, children, hiddenChildren));
            foreach (var child in children)
                rows.Add(ChildRow($"  {indent}  ", child));
        }

        // if (rows.Count == 0)
        //     rows.Add(TextRow("(none)"));

        var finalRows = new List<LiveRow>
        {
            TextRow("") // Newline separating logs and live section
        };

        // TODO: smarter slot selection when rows overflow. Currently trims at the row level (keeps
        // last N rows). Instead, select which jobs to include at the job level before rendering,
        // by priority tier: (1) downloading, (2) searching/extracting/other active, (3) remaining
        // live. Container jobs (AggregateJob, JobList) are pulled in via AddVisibleAncestors and
        // don't compete for slots independently. AlbumJobs use their own state (stable, not
        // derived from children) to avoid flickering during inter-track gaps.
        // Need to test if this approach is too jumpy or not.
        if (rows.Count + 3 <= maxRows)
        {
            finalRows.AddRange(rows);
        }
        else
        {
            int keep = Math.Max(1, maxRows - 4);
            int omitted = rows.Count - keep;
            finalRows.Add(TextRow($"... {omitted} active rows hidden ..."));
            finalRows.AddRange(rows.Skip(rows.Count - keep));
        }

        finalRows.Add(TextRow(""));
        finalRows.Add(MarkupRow(statusLine));

        return finalRows;
    }

    private static LiveRow MarkupRow(string markup)
        => new(new Markup(markup));

    private static LiveRow TextRow(string text)
        => new(new Text(text));

    private static LiveRow JobRow(string indent, JobView job, IReadOnlyList<JobChildView> visibleChildren, int hiddenChildren)
    {
        var prefix = $"{indent}{CliLogStyle.FormatTerminalDisplayId(job.DisplayId)}";
        var leftCells = JobLeftCells(job, visibleChildren, hiddenChildren, out var childMetadata);
        return new LiveRow(new LiveLineRenderable([new LiveCell(prefix, DimIdStyle), ..leftCells],
            childMetadata ?? job.Metadata));
    }

    private static LiveRow ChildRow(string indent, JobChildView child)
        => new(new LiveLineRenderable(
            [new LiveCell(indent), ..ChildLeftCells(child)],
            child.Metadata));

    private static Dictionary<string, int> AllocateChildLimits(IReadOnlyList<JobView> jobs, int availableRows)
    {
        var jobsWithChildren = jobs
            .Select(job => (Job: job, ChildCount: job.Children.Count(child => IsLiveState(child.State))))
            .Where(entry => entry.ChildCount > 0)
            .OrderByDescending(entry => entry.Job.Children.Any(c => string.Equals(c.State, "downloading", StringComparison.OrdinalIgnoreCase)))
            .ThenBy(entry => entry.Job.ParentId == null ? 0 : 1)
            .ToList();

        if (jobsWithChildren.Count == 0 || availableRows <= 0)
            return new Dictionary<string, int>(StringComparer.Ordinal);

        var limits = jobsWithChildren.ToDictionary(
            entry => entry.Job.Id,
            _ => 0,
            StringComparer.Ordinal);

        while (availableRows > 0)
        {
            var changed = false;
            foreach (var (job, childCount) in jobsWithChildren)
            {
                if (availableRows == 0)
                    break;
                if (limits[job.Id] >= childCount)
                    continue;

                limits[job.Id]++;
                availableRows--;
                changed = true;
            }

            if (!changed)
                break;
        }

        return limits;
    }

    private static IReadOnlyList<JobChildView> VisibleChildren(JobView job, int limit)
    {
        var children = job.Children.Where(child => IsLiveState(child.State)).ToList();
        if (children.Count == 0 || limit <= 0)
            return[];
        if (children.Count <= limit)
            return children;

        var visibleIds = children
            .OrderByDescending(c => string.Equals(c.State, "downloading", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.IsMostRecent)
            .Take(limit)
            .Select(child => child.Id)
            .ToHashSet(StringComparer.Ordinal);

        return children.Where(child => visibleIds.Contains(child.Id)).ToList();
    }

    private static void AddVisibleAncestors(
        string id,
        IReadOnlyDictionary<string, JobView> jobs,
        ISet<string> visibleIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (jobs.TryGetValue(id, out var job)
            && job.ParentId is string parentId
            && jobs.ContainsKey(parentId)
            && seen.Add(parentId))
        {
            visibleIds.Add(parentId);
            id = parentId;
        }
    }

    private static bool IsLiveState(string state)
        => !string.Equals(state, "pending", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "succeeded", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "already exists", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
            && !state.StartsWith("failed [", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "cancelled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "skipped", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "not found", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "partial", StringComparison.OrdinalIgnoreCase);

    private TerminalJobCounts CountKnownJobs()
    {
        lock (_sync)
            return CountKnownJobsUnsafe();
    }

    private TerminalJobCounts CountKnownJobsUnsafe()
        => CountRenderState(new TerminalRenderState(
            _knownJobs.Values.ToList(),
            [],
            _retiredCompleted,
            _retiredFailed));

    private static bool IsAlbumKind(string kind)
        => string.Equals(kind, "Album", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "AlbumJob", StringComparison.OrdinalIgnoreCase);

    internal static TerminalJobCounts CountJobRecords(IEnumerable<TerminalJobRecord> records)
    {
        int active = 0;
        int queued = 0;
        int completed = 0;
        int failed = 0;
        foreach (var record in records)
        {
            switch (record.Category)
            {
                case CliJobStatusCategory.Queued:
                    queued++;
                    break;
                case CliJobStatusCategory.Active:
                    active++;
                    break;
                case CliJobStatusCategory.Succeeded:
                    completed++;
                    break;
                case CliJobStatusCategory.Failed:
                    failed++;
                    break;
            }
        }

        return new TerminalJobCounts(active, queued, completed, failed);
    }

    internal static TerminalJobCounts CountRenderState(TerminalRenderState state)
    {
        var counts = CountJobRecords(state.JobRecords);
        return counts with
        {
            Completed = counts.Completed + state.RetiredCompleted,
            Failed = counts.Failed + state.RetiredFailed,
        };
    }

    internal static bool IsIdle(TerminalJobCounts counts)
        => counts.Active == 0 && counts.Queued == 0;

    private static string BuildCountsMarkup(TerminalJobCounts counts)
    {
        var failedPart = counts.Failed > 0 ? $"[red]{counts.Failed}[/]" : $"{counts.Failed}";
        return $"[cyan]{counts.Active}[/] active · {counts.Queued} queued · [green]{counts.Completed}[/] completed · {failedPart} failed";
    }

    private static bool IsQueuedState(string state)
        => string.Equals(state, "pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessfulTerminalState(string state)
        => string.Equals(state, "done", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "succeeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "alreadyexists", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "already exists", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "skipped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "not found", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "notfoundlasttime", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedTerminalState(string state)
        => string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
            || state.StartsWith("failed [", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "partial", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static int MaxLiveRows()
    {
        if (Console.IsOutputRedirected)
            return 0;

        try
        {
            return Math.Clamp(Console.WindowHeight - 8, 4, 60);
        }
        catch
        {
            return 12;
        }
    }

    private static string? StateColor(string state)
    {
        if (string.Equals(state, "downloading", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "downloading tracks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "partial", StringComparison.OrdinalIgnoreCase))
            return "yellow";
        if (string.Equals(state, "searching", StringComparison.OrdinalIgnoreCase))
            return "cyan";
        if (string.Equals(state, "waiting search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "rate limited", StringComparison.OrdinalIgnoreCase))
            return "grey";
        if (string.Equals(state, "on-complete", StringComparison.OrdinalIgnoreCase))
            return "magenta";
        if (string.Equals(state, "retrieving folder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "extracting", StringComparison.OrdinalIgnoreCase))
            return "blue";
        if (string.Equals(state, "queued (r)", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "queued (l)", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "requested", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "initialising", StringComparison.OrdinalIgnoreCase))
            return "grey";
        return null;
    }

    private static Style? StateStyle(string state)
        => StateColor(state) switch
        {
            "yellow" => YellowStyle,
            "cyan" => CyanStyle,
            "magenta" => MagentaStyle,
            "blue" => BlueStyle,
            "grey" => DimStyle,
            _ => (Style?)null,
        };

    internal static string SearchingResultAnnotation(string state, int? rawResultCount)
        => string.Equals(state, "searching", StringComparison.OrdinalIgnoreCase)
            && rawResultCount.HasValue
            ? $" ({rawResultCount.Value})"
            : "";

    internal static string JobProgressAnnotation(JobView job)
    {
        if (IsAlbumKind(job.Kind)
            && job.TotalChildren is int albumTotal
            && albumTotal > 0)
            return $" [{job.DoneChildren ?? 0}/{albumTotal}]";

        if (job.Percent is int percent
            && string.Equals(job.State, "downloading", StringComparison.OrdinalIgnoreCase))
        {
            var speed = job.SpeedBytesPerSecond is long bytesPerSecond
                ? $", {FormatSpeed(bytesPerSecond)}"
                : "";
            return $" ({percent,2}%{speed})";
        }

        return SearchingResultAnnotation(job.State, job.DiscoveryRawResultCount);
    }

    private static IReadOnlyList<LiveCell> JobLeftCells(JobView job, IReadOnlyList<JobChildView> visibleChildren, int hiddenChildren, out TerminalFileMetadata? outMetadata)
    {
        bool isAlbum = IsAlbumKind(job.Kind);
        outMetadata = null;

        var cells = new List<LiveCell>
        {
            new(job.Kind),
            new(": "),
        };

        Style? stateStyle;
        string annotation = JobProgressAnnotation(job);
        string displayName = job.Name;
        List<LiveCell>? extraDisplayCells = null;

        if (isAlbum && job.TotalChildren is > 0)
        {
            stateStyle = null;
        }
        else
        {
            stateStyle = StateStyle(job.State);
        }

        if (isAlbum && hiddenChildren > 0)
        {
            var hiddenDownloadingChild = job.Children
                .FirstOrDefault(c => string.Equals(c.State, "downloading", StringComparison.OrdinalIgnoreCase) 
                                     && !visibleChildren.Any(vc => vc.Id == c.Id));

            if (hiddenDownloadingChild != null)
            {
                stateStyle = YellowStyle;
                outMetadata = hiddenDownloadingChild.Metadata;

                var speedStr = hiddenDownloadingChild.SpeedBytesPerSecond is long spd ? $", {FormatSpeed(spd)}" : "";
                var childPct = hiddenDownloadingChild.Percent ?? 0;
                var progressStr = speedStr.Length > 0 ? $"({childPct}%, {speedStr}) " : $"({childPct}%) ";

                int colonIdx = job.Name.LastIndexOf(": ", StringComparison.Ordinal);
                if (colonIdx >= 0)
                {
                    string albumName = job.Name[..colonIdx];
                    string folderDisplay = job.Name[(colonIdx + 2)..];
                    string sep = folderDisplay.EndsWith('\\') || folderDisplay.EndsWith('/') ? "" : "\\";
                    
                    displayName = $"{albumName}: ";
                    extraDisplayCells =[
                        new LiveCell(progressStr, CyanStyle),
                        new LiveCell($"{folderDisplay}{sep}{hiddenDownloadingChild.Name}")
                    ];
                }
                else
                {
                    displayName = $"{job.Name}: ";
                    extraDisplayCells =[
                        new LiveCell(progressStr, CyanStyle),
                        new LiveCell(hiddenDownloadingChild.Name)
                    ];
                }
            }
        }

        cells.Add(new LiveCell(job.State, stateStyle));
        if (annotation.Length > 0)
            cells.Add(new LiveCell(annotation, CyanStyle));
        cells.Add(new LiveCell(": "));
        
        cells.Add(new LiveCell(displayName));
        if (extraDisplayCells != null)
            cells.AddRange(extraDisplayCells);

        var suffixText = "";
        if (!isAlbum && job.TotalChildren is int total)
            suffixText += $"[{job.DoneChildren ?? 0}/{total}]";
        if (hiddenChildren > 0)
            suffixText += $" (+{hiddenChildren} hidden)";
        if (suffixText.Length > 0)
            cells.Add(new LiveCell(suffixText, DimStyle));

        return cells;
    }

    private static IReadOnlyList<LiveCell> ChildLeftCells(JobChildView child)
    {
        var cells = new List<LiveCell>
        {
            new(child.State, StateStyle(child.State)),
        };
        if (child.Percent is int pct && string.Equals(child.State, "downloading", StringComparison.OrdinalIgnoreCase))
        {
            var speedStr = child.SpeedBytesPerSecond is long spd ? $", {FormatSpeed(spd)}" : "";
            cells.Add(new LiveCell($" ({pct,2}%{speedStr})", CyanStyle));
        }
        cells.Add(new LiveCell(": "));
        cells.Add(new LiveCell(child.Name));
        return cells;
    }

    private static IReadOnlyList<LiveCell> MetadataCells(TerminalFileMetadata? metadata, int level)
    {
        if (metadata == null)
            return [];

        bool compact = level > 1;
        string sep = compact ? "/" : " · ";
        var parts = new List<string>();

        if (metadata.SizeBytes is long sizeBytes && sizeBytes > 0)
            parts.Add(compact ? FormatSizeCompact(sizeBytes) : FormatSize(sizeBytes));
        if (metadata.LengthSeconds is int lengthSeconds && lengthSeconds > 0)
            parts.Add(compact ? FormatDurationCompact(lengthSeconds) : FormatDuration(lengthSeconds));
        if (level <= 3)
        {
            if (metadata.BitRate is int bitRate && bitRate > 0)
                parts.Add(compact ? $"{bitRate}k" : $"{bitRate:D4}k");
            else if (metadata.SampleRate is int sampleRate && sampleRate > 0)
                parts.Add(FormatSampleRate(sampleRate));
        }
        if (level <= 2 && metadata.BitDepth is int bitDepth && bitDepth > 0)
            parts.Add($"{bitDepth}b");

        return parts.Count == 0 ? [] : [new LiveCell($"[{string.Join(sep, parts)}]", DimStyle)];
    }

    private sealed class LiveLineRenderable(
        IReadOnlyList<LiveCell> left,
        TerminalFileMetadata? metadata) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth)
            => new(0, Math.Max(0, maxWidth));

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            if (maxWidth <= 0)
                return [];

            var leftNaturalWidth = Segment.CellCount(ToSegments(left).ToList());
            var spaceForMeta = maxWidth - leftNaturalWidth - 1;

            IReadOnlyList<LiveCell> chosenMetadata = [];
            int metadataWidth = 0;
            for (int level = 1; level <= 4; level++)
            {
                var cells = MetadataCells(metadata, level);
                if (cells.Count == 0) break;
                var w = Segment.CellCount(ToSegments(cells).ToList());
                if (w <= spaceForMeta || level == 4)
                {
                    if (maxWidth - w - 1 >= 12)
                    {
                        chosenMetadata = cells;
                        metadataWidth = w;
                    }
                    break;
                }
            }

            var leftWidth = metadataWidth > 0
                ? Math.Max(0, maxWidth - metadataWidth - 1)
                : maxWidth;

            var leftSegments = ToSegments(FitPathCells(left, leftWidth, leftNaturalWidth)).ToList();
            var rendered = TruncateSegments(leftSegments, leftWidth).ToList();
            if (metadataWidth == 0)
                return rendered;

            var leftRenderedWidth = Segment.CellCount(rendered);
            var padding = Math.Max(1, maxWidth - leftRenderedWidth - metadataWidth);
            rendered.Add(Segment.Padding(padding));
            rendered.AddRange(ToSegments(chosenMetadata));
            return rendered;
        }

        private static IReadOnlyList<LiveCell> FitPathCells(IReadOnlyList<LiveCell> cells, int maxWidth, int knownWidth = -1)
        {
            if (maxWidth <= 0)
                return cells;

            var fitted = cells.ToArray();
            var currentWidth = knownWidth >= 0 ? knownWidth : Segment.CellCount(ToSegments(fitted));
            while (currentWidth > maxWidth)
            {
                var changed = false;
                for (var i = fitted.Length - 1; i >= 0; i--)
                {
                    if (!LooksLikePathText(fitted[i].Text))
                        continue;

                    var cellWidth = CellCount(fitted[i].Text);
                    var targetCellWidth = Math.Max(1, cellWidth - (currentWidth - maxWidth));
                    var shortened = ShortenPathText(fitted[i].Text, targetCellWidth);
                    if (shortened == fitted[i].Text)
                        continue;

                    fitted[i] = fitted[i] with { Text = shortened };
                    changed = true;
                    break;
                }

                if (!changed)
                    break;

                currentWidth = Segment.CellCount(ToSegments(fitted));
            }

            return fitted;
        }

        private static bool LooksLikePathText(string text)
            => text.Contains('\\') || text.Contains('/');

        private static string ShortenPathText(string text, int maxWidth)
        {
            var slashIndex = text.IndexOfAny(['\\', '/']);
            if (slashIndex < 0)
                return text;

            var pathStart = 0;
            var labelIndex = text.LastIndexOf(": ", slashIndex, StringComparison.Ordinal);
            if (labelIndex >= 0)
                pathStart = labelIndex + 2;

            var prefix = text[..pathStart];
            var path = text[pathStart..].Replace('/', '\\').TrimStart('\\');
            var pathWidth = maxWidth - CellCount(prefix);
            if (pathWidth <= 0)
                return text;

            var shortenedPath = ShortenPath(path, pathWidth);
            return prefix + shortenedPath;
        }

        private static string ShortenPath(string path, int maxWidth)
        {
            if (CellCount(path) <= maxWidth)
                return path;

            var firstSeparator = path.IndexOf('\\');
            if (firstSeparator < 0)
                return TruncateEnd(path, maxWidth);

            var head = path[..(firstSeparator + 1)];
            var tail = path[(firstSeparator + 1)..];
            var marker = "(…)";
            var headWithMarker = head + marker;
            var headWithMarkerWidth = CellCount(headWithMarker);

            if (headWithMarkerWidth < maxWidth)
                return headWithMarker + RightFit(tail, maxWidth - headWithMarkerWidth);

            return TruncateEnd(path, maxWidth);
        }

        // TODO: Perf bottleneck. RightFit and TruncateEnd allocate new strings and Segments on every loop iteration,
        // Instead of trial-and-error substrings, iterate through the string once, accumulate the cell width of each
        // character/rune (spectre.console might have utilities for that), find the exact cutoff index, and perform
        // Substring() exactly once.
        private static string RightFit(string text, int maxWidth)
        {
            if (maxWidth <= 0)
                return "";
            if (CellCount(text) <= maxWidth)
                return text;

            for (var start = 1; start < text.Length; start++)
            {
                var candidate = text[start..];
                if (CellCount(candidate) <= maxWidth)
                    return candidate;
            }

            return "";
        }

        private static string TruncateEnd(string text, int maxWidth)
        {
            if (maxWidth <= 0)
                return "";
            if (CellCount(text) <= maxWidth)
                return text;

            var ellipsisWidth = CellCount("…");
            if (maxWidth <= ellipsisWidth)
                return "…";

            for (var length = text.Length - 1; length >= 0; length--)
            {
                var candidate = text[..length] + "…";
                if (CellCount(candidate) <= maxWidth)
                    return candidate;
            }

            return "…";
        }

        private static int CellCount(string text)
            => new Segment(text).CellCount();

        private static IEnumerable<Segment> ToSegments(IEnumerable<LiveCell> cells)
        {
            foreach (var cell in cells)
            {
                if (cell.Text.Length == 0)
                    continue;
                yield return cell.Style is { } style
                    ? new Segment(cell.Text, style)
                    : new Segment(cell.Text);
            }
        }

        private static IEnumerable<Segment> TruncateSegments(IReadOnlyList<Segment> segments, int maxWidth)
        {
            if (maxWidth <= 0)
                return [];

            var result = new List<Segment>();
            var remaining = maxWidth;
            foreach (var segment in segments)
            {
                var width = segment.CellCount();
                if (width <= remaining)
                {
                    result.Add(segment);
                    remaining -= width;
                    continue;
                }

                if (remaining > 0)
                    result.AddRange(Segment.SplitOverflow(segment, Overflow.Ellipsis, remaining));
                break;
            }

            return result;
        }
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0 * 1024.0):F1}GB".PadLeft(6, '0')
            : $"{bytes / (1024.0 * 1024.0):F1}MB".PadLeft(6, '0');

    private static string FormatSizeCompact(long bytes)
        => bytes >= 1024 * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0 * 1024.0):F1}GB"
            : $"{bytes / (1024.0 * 1024.0):F1}MB";

    private static string FormatDuration(int seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string FormatDurationCompact(int seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static string FormatSampleRate(int sampleRate)
        => sampleRate % 1000 == 0
            ? $"{sampleRate / 1000}k"
            : $"{sampleRate / 1000.0:0.#}k";

    private static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000) return $"{bytesPerSecond / 1_000_000.0:F1} MB/s";
        if (bytesPerSecond >= 1_000)     return $"{bytesPerSecond / 1_000.0:F1} KB/s";
        return $"{bytesPerSecond} B/s";
    }

    internal static string FormatLogText(TerminalLogLine line)
        => CliLogStyle.FormatTerminalLogText(line);

}
