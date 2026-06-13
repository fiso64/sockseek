using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using Sockseek.Core;

namespace Sockseek.Cli;

internal enum TerminalLogKind
{
    JobSucceeded,
    JobFailed,
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

internal abstract record PrintedLogLine
{
    public sealed record Raw(string Text) : PrintedLogLine;
    public sealed record Structured(TerminalLogLine Line) : PrintedLogLine;
    public sealed record Process(TerminalProcessLogLine Line) : PrintedLogLine;
}

internal sealed record TerminalProcessLogLine(
    LogLevel Level,
    string CategoryName,
    string Message,
    SockseekLog.LogRouting Routing);

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

internal sealed class TerminalLiveRenderer : IDisposable
{
    private readonly ConcurrentDictionary<string, JobView> _jobs = new();
    private readonly Dictionary<string, TerminalJobRecord> _knownJobs = new(StringComparer.Ordinal);
    private int _countQueued, _countActive, _countCompleted, _countFailed;
    private readonly ConcurrentQueue<TerminalLogLine> _logs = new();
    private readonly ConcurrentQueue<TerminalProcessLogLine> _processLogs = new();
    private readonly ConcurrentQueue<string> _rawLogs = new();
    private readonly List<PrintedLogLine> _printedLogHistory = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _renderTask;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromMilliseconds(100);
    private readonly Lock _sync = new();

    private volatile bool _paused;
    private volatile string? _statusMessage;
    private long _rateLimitResetTicks; // 0 = not rate-limited; otherwise UTC ticks of reset time
    private int _spinFrame;
    private bool _disposed;
    private (int Width, int Height) _lastTerminalSize;
    private bool _terminalWasResized;

    private sealed record LiveRow(IRenderable Renderable);
    private sealed record LiveCell(string Text, Style? Style = null);
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
    {
        Printing.LiveWriteLine = (line, _) => EnqueueRawLog(line);
        _lastTerminalSize = GetTerminalSize();
        _renderTask = Task.Run(RenderLoopAsync);
    }

    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public void SetStatusMessage(string? message)
    {
        if (_disposed) return;
        _statusMessage = message;
    }

    public void SetRateLimited(DateTimeOffset? resetsAt)
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _rateLimitResetTicks, resetsAt?.UtcTicks ?? 0L);
    }

    public void Upsert(JobView job)
    {
        if (_disposed) return;
        _jobs[job.Id] = job with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    public void UpsertJob(TerminalJobRecord job)
    {
        if (_disposed) return;
        lock (_sync)
        {
            bool isAlbumChild = job.ParentId != null
                && _knownJobs.TryGetValue(job.ParentId, out var parent)
                && IsAlbumKind(parent.Kind);

            if (_knownJobs.TryGetValue(job.Id, out var old))
                ApplyCountDelta(old.Category, -1, isAlbumChild);

            _knownJobs[job.Id] = job;
            ApplyCountDelta(job.Category, +1, isAlbumChild);
        }
    }

    private void ApplyCountDelta(CliJobStatusCategory category, int delta, bool isAlbumChild)
    {
        if (isAlbumChild) return;
        switch (category)
        {
            case CliJobStatusCategory.Queued:
                _countQueued += delta;
                break;
            case CliJobStatusCategory.Active:
                _countActive += delta;
                break;
            case CliJobStatusCategory.Succeeded:
                _countCompleted += delta;
                break;
            case CliJobStatusCategory.Failed:
                _countFailed += delta;
                break;
        }
    }

    public void Remove(string id)
    {
        if (_disposed) return;
        _jobs.TryRemove(id, out _);
    }

    public void Log(TerminalLogLine line)
    {
        if (_disposed) return;
        _logs.Enqueue(line);
    }

    public void Log(SockseekLog.StructuredLogEntry entry)
    {
        if (_disposed) return;
        _processLogs.Enqueue(new TerminalProcessLogLine(
            entry.Level,
            entry.CategoryName,
            entry.Message,
            entry.Routing));
    }

    public void EnqueueRawLog(string line)
    {
        if (_disposed) return;
        _rawLogs.Enqueue(line);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _renderTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { }
        Printing.LiveWriteLine = null;
        // When the terminal shrinks, Spectre.Live's repaint can clear the wrong rows and
        // eat into real log scrollback above the live region (see comment in FlushLogs).
        // Replaying the log history at the end recovers those lost lines.
        if (_terminalWasResized)
            ReplayPrintedLogHistory();
        AnsiConsole.MarkupLine(BuildCountsMarkup(CountKnownJobs()));
        _cts.Dispose();
    }

    private async Task RenderLoopAsync()
    {
        try
        {
            await AnsiConsole.Live(Render())
                .AutoClear(true)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        RememberTerminalResize();

                        if (!_paused)
                        {
                            FlushLogs();
                            ctx.UpdateTarget(Render());
                            ctx.Refresh();
                        }

                        try { await Task.Delay(_refreshInterval, _cts.Token); }
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
            while (_rawLogs.TryDequeue(out var rawLine))
            {
                _printedLogHistory.Add(new PrintedLogLine.Raw(rawLine));
                WritePlainLogLines(rawLine);
            }

            while (_processLogs.TryDequeue(out var line))
            {
                _printedLogHistory.Add(new PrintedLogLine.Process(line));
                WriteProcessLogLine(line);
            }

            while (_logs.TryDequeue(out var line))
            {
                _printedLogHistory.Add(new PrintedLogLine.Structured(line));
                var markup = FormatLogMarkup(line);
                var visualLength = CellCount(Markup.Remove(markup));
                int width = LogLineWidth();
                if (markup.Contains('\n') || (!Console.IsOutputRedirected && visualLength >= width))
                {
                    WriteMarkupLogLines(line);
                    continue;
                }

                AnsiConsole.MarkupLine(markup + PaddingFor(visualLength));
            }
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
                    case PrintedLogLine.Raw raw:
                        WritePlainLogLines(raw.Text);
                        break;
                    case PrintedLogLine.Structured structured:
                        WriteStructuredLogLine(structured.Line);
                        break;
                    case PrintedLogLine.Process process:
                        WriteProcessLogLine(process.Line);
                        break;
                }
            }
        }
    }

    private static void WriteStructuredLogLine(TerminalLogLine line)
    {
        var markup = FormatLogMarkup(line);
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
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            foreach (var visualLine in WrapPlainLogLine(line))
                AnsiConsole.WriteLine(visualLine + PaddingFor(CellCount(visualLine)));
        }
    }

    private static void WriteProcessLogLine(TerminalProcessLogLine line)
    {
        var normalized = line.Message.Replace("\r\n", "\n").Replace('\r', '\n');
        var messageLines = normalized.Split('\n');
        var prefixText = ProcessLogPrefixText(line);
        var prefixMarkup = ProcessLogPrefixMarkup(line);

        WriteWrappedProcessLogContent(prefixText, prefixMarkup, messageLines[0]);

        foreach (var messageLine in messageLines.Skip(1))
            WriteWrappedProcessLogContent("", "", messageLine);
    }

    internal static string FormatProcessLogMarkup(TerminalProcessLogLine line)
        => $"{ProcessLogPrefixMarkup(line)}{Markup.Escape(line.Message)}";

    private static void WriteWrappedProcessLogContent(
        string prefixText,
        string prefixMarkup,
        string content)
    {
        int lineWidth = LogLineWidth() - CellCount(prefixText);
        var chunks = WrapContent(content, lineWidth).ToList();

        foreach (var chunk in chunks)
        {
            var markup = prefixMarkup + Markup.Escape(chunk);
            AnsiConsole.MarkupLine(markup + PaddingFor(CellCount(prefixText) + CellCount(chunk)));
        }
    }

    private static string ProcessLogPrefixText(TerminalProcessLogLine line)
    {
        if (line.Routing == SockseekLog.LogRouting.ConsoleOnly)
            return "";

        var levelPrefix = line.Level == LogLevel.Information ? "" : $"[{ShortLevel(line.Level)}] ";
        return $"{levelPrefix}[{line.CategoryName}] ";
    }

    private static string ProcessLogPrefixMarkup(TerminalProcessLogLine line)
    {
        if (line.Routing == SockseekLog.LogRouting.ConsoleOnly)
            return "";

        return $"[grey]{Markup.Escape(ProcessLogPrefixText(line))}[/]";
    }

    private static string ShortLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => level.ToString().ToLowerInvariant(),
    };

    private static void WriteMarkupLogLines(TerminalLogLine line)
    {
        var normalized = line.Message.Replace("\r\n", "\n").Replace('\r', '\n');
        var messageLines = normalized.Split('\n');
        var prefixText = $"{FormatDisplayId(line.DisplayId)}{line.JobType}: {SourcePrefixText(line.Source)}";
        var prefixMarkup = $"[grey]{Markup.Escape(FormatDisplayId(line.DisplayId))}[/]{Markup.Escape(line.JobType)}: {SourcePrefixMarkup(line.Source)}";
        var continuationPrefix = new string(' ', FormatDisplayId(line.DisplayId).Length);

        WriteWrappedMarkupContent(
            prefixText,
            prefixMarkup,
            messageLines[0],
            first: content => FormatMainLogContentMarkup(content, line.Kind, line.Highlight),
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
        var allJobs = _jobs.Values.ToDictionary(job => job.Id, StringComparer.Ordinal);
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

        var counts = CountKnownJobs();
        long resetTicks = Interlocked.Read(ref _rateLimitResetTicks);
        bool isRateLimited = resetTicks != 0;
        bool useRateLimitSpinner = isRateLimited
            && !_jobs.Values.Any(j => string.Equals(j.State, "downloading", StringComparison.OrdinalIgnoreCase));
        var frames = useRateLimitSpinner ? RateLimitSpinFrames : SpinFrames;
        var spin = frames[_spinFrame++ % frames.Count];

        var statusLine = $"{spin} {BuildCountsMarkup(counts)}";
        if (isRateLimited)
        {
            var remaining = new DateTimeOffset(new DateTime(resetTicks, DateTimeKind.Utc)) - DateTimeOffset.UtcNow;
            int secs = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            statusLine += $" | [bold yellow]Search rate limit reached, resuming in {secs}s[/]";
        }
        else if (_statusMessage is string msg)
            statusLine += $" | [bold yellow]{Markup.Escape(msg)}[/]";

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
        var prefix = $"{indent}{FormatDisplayId(job.DisplayId)}";
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

    private (int Active, int Queued, int Completed, int Failed) CountKnownJobs()
    {
        lock (_sync)
            return (_countActive, _countQueued, _countCompleted, _countFailed);
    }

    private static bool IsAlbumChild(
        JobView job,
        IReadOnlyDictionary<string, JobView> jobsById)
        => job.ParentId is string parentId
            && jobsById.TryGetValue(parentId, out var parent)
            && IsAlbumKind(parent.Kind);

    private static bool IsAlbumKind(string kind)
        => string.Equals(kind, "Album", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "AlbumJob", StringComparison.OrdinalIgnoreCase);

    private static string BuildCountsMarkup((int Active, int Queued, int Completed, int Failed) counts)
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

    private static string? KindColor(TerminalLogKind kind) => kind switch
    {
        TerminalLogKind.SongDownloaded or TerminalLogKind.AlbumTrackDownloaded
            or TerminalLogKind.JobSucceeded or TerminalLogKind.PlaylistCompleted
            or TerminalLogKind.AggregateCompleted
            => "green",
        TerminalLogKind.SongFailed or TerminalLogKind.AlbumTrackFailed
            or TerminalLogKind.JobFailed
            => "red",
        TerminalLogKind.SongSkipped or TerminalLogKind.AlbumTrackSkipped
            or TerminalLogKind.JobCancelled or TerminalLogKind.SongAlreadyExists
            or TerminalLogKind.JobAlreadyExists
            => "grey",
        _ => null,
    };

    private static string? StateColor(string state)
    {
        if (string.Equals(state, "downloading", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "downloading tracks", StringComparison.OrdinalIgnoreCase))
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
            _ => null,
        };

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
        string annotation;
        string displayName = job.Name;
        List<LiveCell>? extraDisplayCells = null;

        if (isAlbum && job.TotalChildren is int albumTotal)
        {
            annotation = $" [{job.DoneChildren ?? 0}/{albumTotal}]";
            stateStyle = null;
        }
        else if (job.Percent is int pct && string.Equals(job.State, "downloading", StringComparison.OrdinalIgnoreCase))
        {
            var speedStr = job.SpeedBytesPerSecond is long spd ? $", {FormatSpeed(spd)}" : "";
            annotation = $" ({pct,2}%{speedStr})";
            stateStyle = StateStyle(job.State);
        }
        else
        {
            annotation = "";
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
                yield return cell.Style == null
                    ? new Segment(cell.Text)
                    : new Segment(cell.Text, cell.Style);
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

    internal static string FormatLogMarkup(TerminalLogLine line)
    {
        int pathLineIdx = line.Message.IndexOf("\n    ", StringComparison.Ordinal);
        var mainPart = pathLineIdx >= 0 ? line.Message[..pathLineIdx] : line.Message;
        var pathPart = pathLineIdx >= 0 ? line.Message[pathLineIdx..] : null;

        var mainMarkup = SourcePrefixMarkup(line.Source) + FormatMainLogContentMarkup(mainPart, line.Kind, line.Highlight);

        var pathMarkup = pathPart != null ? $"[grey]{Markup.Escape(pathPart)}[/]" : "";
        return $"[grey]{Markup.Escape(FormatDisplayId(line.DisplayId))}[/]{Markup.Escape(line.JobType)}: {mainMarkup}{pathMarkup}";
    }

    private static string FormatMainLogContentMarkup(string content, TerminalLogKind kind, string? highlight)
    {
        var color = KindColor(kind);
        if (color == null)
            return Markup.Escape(content);

        if (!string.IsNullOrEmpty(highlight) && content.StartsWith(highlight, StringComparison.Ordinal))
            return $"[{color}]{Markup.Escape(highlight)}[/]{Markup.Escape(content[highlight.Length..])}";

        return $"[{color}]{Markup.Escape(content)}[/]";
    }

    private static string FormatLogText(TerminalLogLine line)
        => $"{FormatDisplayId(line.DisplayId)}{line.JobType}: {SourcePrefixText(line.Source)}{line.Message}";

    internal static string SourcePrefixText(string? source)
        => string.IsNullOrWhiteSpace(source) ? "" : $"{source}: ";

    private static string SourcePrefixMarkup(string? source)
        => Markup.Escape(SourcePrefixText(source));

    private static string FormatDisplayId(int displayId)
        => $"[{displayId:000}] ";
}
