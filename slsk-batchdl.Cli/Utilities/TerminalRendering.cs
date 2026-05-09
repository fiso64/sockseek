using System.Collections.Concurrent;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Sldl.Cli;

internal enum TerminalLogKind
{
    JobSucceeded,
    JobFailed,
    JobCancelled,
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
    string Message);

internal sealed record JobChildView(
    string Id,
    int DisplayId,
    string State,
    string Name,
    int? Percent = null,
    long? SpeedBytesPerSecond = null,
    bool IsMostRecent = false);

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
    string? ParentId);

internal sealed class TerminalLiveRenderer : IDisposable
{
    private readonly ConcurrentDictionary<string, JobView> _jobs = new();
    private readonly Dictionary<string, TerminalJobRecord> _knownJobs = new(StringComparer.Ordinal);
    private int _countQueued, _countActive, _countCompleted, _countFailed;
    private readonly ConcurrentQueue<TerminalLogLine> _logs = new();
    private readonly ConcurrentQueue<string> _rawLogs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _renderTask;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromMilliseconds(100);
    private readonly Lock _sync = new();

    private volatile bool _paused;
    private volatile string? _statusMessage;
    private long _rateLimitResetTicks; // 0 = not rate-limited; otherwise UTC ticks of reset time
    private int _spinFrame;
    private bool _disposed;

    private sealed record LiveRow(IRenderable Renderable);
    private static readonly Style DimIdStyle = new(foreground: Color.Grey);

    private static readonly IReadOnlyList<string> SpinFrames = SupportsUnicodeSpinner()
        ? Spinner.Known.Dots.Frames
        : ["|", "/", "-", "\\"];

    private static readonly IReadOnlyList<string> RateLimitSpinFrames = SupportsUnicodeSpinner()
        ? Spinner.Known.Point.Frames
        : ["·"];

    private static bool SupportsUnicodeSpinner()
    {
        if (!AnsiConsole.Profile.Capabilities.Unicode)
            return false;
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("WT_SESSION") is null)
            return false;
        return true;
    }

    public TerminalLiveRenderer()
    {
        Printing.LiveWriteLine = (line, _) => EnqueueRawLog(line);
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
                ApplyCountDelta(old.State, -1, isAlbumChild);

            _knownJobs[job.Id] = job;
            ApplyCountDelta(job.State, +1, isAlbumChild);
        }
    }

    private void ApplyCountDelta(string state, int delta, bool isAlbumChild)
    {
        if (isAlbumChild) return;
        if (IsQueuedState(state))                  _countQueued    += delta;
        else if (IsSuccessfulTerminalState(state)) _countCompleted += delta;
        else if (IsFailedTerminalState(state))     _countFailed    += delta;
        else if (IsLiveState(state))               _countActive    += delta;
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

    private void FlushLogs()
    {
        lock (_sync)
        {
            while (_rawLogs.TryDequeue(out var rawLine))
            {
                WritePlainLogLines(rawLine);
            }

            while (_logs.TryDequeue(out var line))
            {
                var markup = FormatLogMarkup(line);
                var visualLength = Markup.Remove(markup).Length;
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

    private static void WritePlainLogLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            foreach (var visualLine in WrapPlainLogLine(line))
                AnsiConsole.WriteLine(visualLine + PaddingFor(visualLine.Length));
        }
    }

    private static void WriteMarkupLogLines(TerminalLogLine line)
    {
        var normalized = line.Message.Replace("\r\n", "\n").Replace('\r', '\n');
        var messageLines = normalized.Split('\n');
        var prefixText = $"{FormatDisplayId(line.DisplayId)}{line.JobType}: ";
        var prefixMarkup = $"[dim]{Markup.Escape(FormatDisplayId(line.DisplayId))}[/]{Markup.Escape(line.JobType)}: ";
        var continuationPrefix = new string(' ', prefixText.Length);

        WriteWrappedMarkupContent(
            prefixText,
            prefixMarkup,
            messageLines[0],
            content => FormatMainLogContentMarkup(content, line.Kind));

        foreach (var messageLine in messageLines.Skip(1))
        {
            WriteWrappedMarkupContent(
                "",
                "",
                messageLine,
                content => $"[dim]{Markup.Escape(content)}[/]");
        }

        void WriteWrappedMarkupContent(
            string firstPrefixText,
            string firstPrefixMarkup,
            string content,
            Func<string, string> formatContent)
        {
            var prefixTextForChunk = firstPrefixText;
            var prefixMarkupForChunk = firstPrefixMarkup;
            foreach (var chunk in WrapContent(content, LogLineWidth() - firstPrefixText.Length))
            {
                var markup = prefixMarkupForChunk + formatContent(chunk);
                AnsiConsole.MarkupLine(markup + PaddingFor(prefixTextForChunk.Length + chunk.Length));
                prefixTextForChunk = continuationPrefix;
                prefixMarkupForChunk = Markup.Escape(continuationPrefix);
            }
        }
    }

    private static IEnumerable<string> WrapContent(string content, int availableWidth)
    {
        if (Console.IsOutputRedirected)
            return [content];

        int width = Math.Max(1, availableWidth);
        if (content.Length < width)
            return [content];

        var wrapped = new List<string>();
        for (int offset = 0; offset < content.Length; offset += width)
            wrapped.Add(content.Substring(offset, Math.Min(width, content.Length - offset)));
        return wrapped;
    }

    private static IEnumerable<string> WrapPlainLogLine(string line)
    {
        if (Console.IsOutputRedirected)
            return [line];

        int width = LogLineWidth();
        if (line.Length < width)
            return [line];

        var wrapped = new List<string>();
        for (int offset = 0; offset < line.Length; offset += width)
            wrapped.Add(line.Substring(offset, Math.Min(width, line.Length - offset)));
        return wrapped;
    }

    private static string PaddingFor(int visualLength)
        => Console.IsOutputRedirected ? "" : new string(' ', Math.Max(0, LogLineWidth() - visualLength));

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

        var rows = new List<LiveRow>
        {
            MarkupRow(statusLine),
            TextRow(""),
        };

        var childLimits = AllocateChildLimits(jobs, maxRows - rows.Count - jobs.Count);
        foreach (var job in jobs)
        {
            var indent = job.ParentId != null ? "  " : "";
            var children = VisibleChildren(job, childLimits.GetValueOrDefault(job.Id));
            var hiddenChildren = job.Children.Count(child => IsLiveState(child.State)) - children.Count;
            rows.Add(JobRow(indent, job, hiddenChildren));
            foreach (var child in children)
                rows.Add(ChildRow($"  {indent}  ", child));
        }

        if (rows.Count == 2)
            rows.Add(TextRow("(none)"));

        if (rows.Count <= maxRows)
            return rows;

        int keep = Math.Max(3, maxRows - 1);
        int omitted = rows.Count - keep;
        return [
            ..rows.Take(2),
            TextRow($"... {omitted} active rows hidden ..."),
            ..rows.Skip(rows.Count - Math.Max(1, keep - 2)).Take(maxRows - 3),
        ];
    }

    private static LiveRow MarkupRow(string markup)
        => new(new Markup(markup));

    private static LiveRow TextRow(string text)
        => new(new Text(text));

    private static LiveRow JobRow(string indent, JobView job, int hiddenChildren)
        => HangingTextRow($"{indent}{FormatDisplayId(job.DisplayId)}", FormatJobBodyMarkup(job, hiddenChildren), dimPrefix: true);

    private static LiveRow ChildRow(string indent, JobChildView child)
        => HangingTextRow(indent, FormatChildMarkup(child));

    private static Dictionary<string, int> AllocateChildLimits(IReadOnlyList<JobView> jobs, int availableRows)
    {
        var jobsWithChildren = jobs
            .Select(job => (Job: job, ChildCount: job.Children.Count(child => IsLiveState(child.State))))
            .Where(entry => entry.ChildCount > 0)
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
            return [];
        if (children.Count <= limit)
            return children;

        var visibleIds = children.Take(limit).Select(child => child.Id).ToHashSet(StringComparer.Ordinal);
        var recent = children.LastOrDefault(child => child.IsMostRecent);
        if (recent != null && !visibleIds.Contains(recent.Id))
        {
            visibleIds.Remove(children.Take(limit).Last().Id);
            visibleIds.Add(recent.Id);
        }

        return children.Where(child => visibleIds.Contains(child.Id)).ToList();
    }

    private static LiveRow HangingTextRow(string prefix, string bodyMarkup, bool dimPrefix = false)
    {
        var grid = new Grid
        {
            Expand = true,
        };

        grid.AddColumn(new GridColumn
        {
            Width = prefix.Length,
            NoWrap = true,
            Padding = new Padding(0, 0, 0, 0),
        });
        grid.AddColumn(new GridColumn
        {
            Padding = new Padding(0, 0, 0, 0),
        });
        grid.AddRow(dimPrefix ? new Text(prefix, DimIdStyle) : new Text(prefix), new Markup(bodyMarkup));

        return new LiveRow(grid);
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
            && !string.Equals(state, "cancelled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "skipped", StringComparison.OrdinalIgnoreCase);

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
            || string.Equals(state, "notfoundlasttime", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedTerminalState(string state)
        => string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static int MaxLiveRows()
    {
        if (Console.IsOutputRedirected)
            return 0;

        try
        {
            return Math.Clamp(Console.WindowHeight - 8, 4, 30);
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
            or TerminalLogKind.AggregateCompleted or TerminalLogKind.SongAlreadyExists
            => "green",
        TerminalLogKind.SongFailed or TerminalLogKind.AlbumTrackFailed
            or TerminalLogKind.JobFailed
            => "red",
        TerminalLogKind.SongSkipped or TerminalLogKind.AlbumTrackSkipped
            or TerminalLogKind.JobCancelled
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

    private static string FormatJobBodyMarkup(JobView job, int hiddenChildren)
    {
        bool isAlbum = string.Equals(job.Kind, "Album", StringComparison.OrdinalIgnoreCase);

        string suffix = "";
        string? stateColor;

        string annotation;
        if (isAlbum && job.TotalChildren is int albumTotal)
        {
            annotation = $" [cyan]{Markup.Escape($"[{job.DoneChildren ?? 0}/{albumTotal}]")}[/]";
            stateColor = null;
        }
        else if (job.Percent is int pct && string.Equals(job.State, "downloading", StringComparison.OrdinalIgnoreCase))
        {
            var speedStr = job.SpeedBytesPerSecond is long spd ? $", {FormatSpeed(spd)}" : "";
            annotation = $" [cyan]{Markup.Escape($"({pct,2}%{speedStr})")}[/]";
            stateColor = StateColor(job.State);
            if (job.TotalChildren is int total)
                suffix += $" [dim]{Markup.Escape($"[{job.DoneChildren ?? 0}/{total}]")}[/]";
        }
        else
        {
            annotation = "";
            stateColor = StateColor(job.State);
            if (job.TotalChildren is int total)
                suffix += $" [dim]{Markup.Escape($"[{job.DoneChildren ?? 0}/{total}]")}[/]";
        }

        if (hiddenChildren > 0)
            suffix += $" [dim]{Markup.Escape($"(+{hiddenChildren} hidden)")}[/]";

        var stateMarkup = stateColor != null
            ? $"[{stateColor}]{Markup.Escape(job.State)}[/]"
            : Markup.Escape(job.State);

        return $"{Markup.Escape(job.Kind)}: {stateMarkup}{annotation}: {Markup.Escape(job.Name)}{suffix}";
    }

    private static string FormatChildMarkup(JobChildView child)
    {
        var stateColor = StateColor(child.State);
        var stateMarkup = stateColor != null
            ? $"[{stateColor}]{Markup.Escape(child.State)}[/]"
            : Markup.Escape(child.State);
        string annotation = "";
        if (child.Percent is int pct && string.Equals(child.State, "downloading", StringComparison.OrdinalIgnoreCase))
        {
            var speedStr = child.SpeedBytesPerSecond is long spd ? $", {FormatSpeed(spd)}" : "";
            annotation = $" [cyan]{Markup.Escape($"({pct,2}%{speedStr})")}[/]";
        }
        return $"{stateMarkup}{annotation}: {Markup.Escape(child.Name)}";
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000) return $"{bytesPerSecond / 1_000_000.0:F1} MB/s";
        if (bytesPerSecond >= 1_000)     return $"{bytesPerSecond / 1_000.0:F1} KB/s";
        return $"{bytesPerSecond} B/s";
    }

    private static string FormatLogMarkup(TerminalLogLine line)
    {
        int pathLineIdx = line.Message.IndexOf("\n    ", StringComparison.Ordinal);
        var mainPart = pathLineIdx >= 0 ? line.Message[..pathLineIdx] : line.Message;
        var pathPart = pathLineIdx >= 0 ? line.Message[pathLineIdx..] : null;

        var color = KindColor(line.Kind);
        string mainMarkup;
        if (color != null)
        {
            int colonIdx = mainPart.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx >= 0)
                mainMarkup = $"[{color}]{Markup.Escape(mainPart[..colonIdx])}[/]: {Markup.Escape(mainPart[(colonIdx + 2)..])}";
            else
                mainMarkup = $"[{color}]{Markup.Escape(mainPart)}[/]";
        }
        else
        {
            mainMarkup = Markup.Escape(mainPart);
        }

        var pathMarkup = pathPart != null ? $"[dim]{Markup.Escape(pathPart)}[/]" : "";
        return $"[dim]{Markup.Escape(FormatDisplayId(line.DisplayId))}[/]{Markup.Escape(line.JobType)}: {mainMarkup}{pathMarkup}";
    }

    private static string FormatMainLogContentMarkup(string content, TerminalLogKind kind)
    {
        var color = KindColor(kind);
        if (color == null)
            return Markup.Escape(content);

        int colonIdx = content.IndexOf(": ", StringComparison.Ordinal);
        if (colonIdx >= 0)
            return $"[{color}]{Markup.Escape(content[..colonIdx])}[/]: {Markup.Escape(content[(colonIdx + 2)..])}";

        return $"[{color}]{Markup.Escape(content)}[/]";
    }

    private static string FormatLogText(TerminalLogLine line)
        => $"{FormatDisplayId(line.DisplayId)}{line.JobType}: {line.Message}";

    private static string FormatDisplayId(int displayId)
        => $"[{displayId:000}] ";
}
