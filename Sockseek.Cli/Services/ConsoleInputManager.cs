using System.Threading.Channels;

namespace Sockseek.Cli;

public static class ConsoleInputManager
{
    private static readonly Channel<ConsoleKeyInfo> _keyChannel = Channel.CreateUnbounded<ConsoleKeyInfo>();
    private static readonly SemaphoreSlim _consoleInteractionLock = new(1, 1);
    private static volatile bool _directPromptActive;
    private static int _consoleOutputPauseDepth;
    private static Func<Task>? _onCancelRequested;
    private static Func<Task>? _onNextCandidateRequested;
    private static Func<Task>? _onInfoRequested;

    public enum CancelPromptAction
    {
        Abort,
        CancelAll,
        CancelJob,
        Invalid,
        Refresh,
    }

    public readonly record struct CancelPromptResult(CancelPromptAction Action, int? JobId = null, string? Input = null);

    public static bool GlobalCancelEnabled { get; set; } = true;
    public static Func<Task>? OnCancelRequested
    {
        get => _onCancelRequested;
        set
        {
            _onCancelRequested = value;
            HandlersChanged?.Invoke();
        }
    }
    public static Func<Task>? OnNextCandidateRequested
    {
        get => _onNextCandidateRequested;
        set
        {
            _onNextCandidateRequested = value;
            HandlersChanged?.Invoke();
        }
    }
    public static Func<Task>? OnInfoRequested
    {
        get => _onInfoRequested;
        set
        {
            _onInfoRequested = value;
            HandlersChanged?.Invoke();
        }
    }
    public static CliProgressReporter? Reporter { get; set; }

    internal static event Action? HandlersChanged;

    public static async Task RunLoopAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_directPromptActive)
                {
                    await Task.Delay(50, ct);
                    continue;
                }

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (GlobalCancelEnabled && char.ToLower(key.KeyChar) == 'c')
                    {
                        if (OnCancelRequested != null)
                        {
                            using var interaction = await AcquireConsoleInteractionAsync(ct);
                            using var pause = PauseConsoleOutput();
                            await OnCancelRequested();
                        }
                    }
                    else if (GlobalCancelEnabled && char.ToLower(key.KeyChar) == 't')
                    {
                        if (OnNextCandidateRequested != null)
                        {
                            using var interaction = await AcquireConsoleInteractionAsync(ct);
                            using var pause = PauseConsoleOutput();
                            await OnNextCandidateRequested();
                        }
                    }
                    else if (GlobalCancelEnabled && char.ToLower(key.KeyChar) == 'i')
                    {
                        if (OnInfoRequested != null)
                        {
                            using var interaction = await AcquireConsoleInteractionAsync(ct);
                            using var pause = PauseConsoleOutput();
                            await OnInfoRequested();
                        }
                    }
                    else
                    {
                        _keyChannel.Writer.TryWrite(key);
                    }
                }
                else
                {
                    await Task.Delay(50, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public static async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct = default)
    {
        return await _keyChannel.Reader.ReadAsync(ct);
    }

    public static int? ReadJobIdInput()
    {
        var result = ReadCancelPromptResult();
        return result.Action == CancelPromptAction.CancelJob ? result.JobId : null;
    }

    public static CancelPromptResult ReadJobIdOrRefreshResult()
    {
        var input = ReadPromptInput();

        if (input == null)
            return new(CancelPromptAction.Abort);

        input = input.Trim();

        if (input.Equals("r", StringComparison.OrdinalIgnoreCase))
            return new(CancelPromptAction.Refresh);

        return int.TryParse(input, out int id)
            ? new(CancelPromptAction.CancelJob, id)
            : new(CancelPromptAction.Abort);
    }

    public static CancelPromptResult ReadCancelPromptResult()
    {
        var input = ReadPromptInput();

        if (input == null)
            return new(CancelPromptAction.Abort);

        input = input.Trim();

        if (input.Length == 0 ||
            input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("a", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return new(CancelPromptAction.CancelAll);
        }

        if (input.Equals("n", StringComparison.OrdinalIgnoreCase))
            return new(CancelPromptAction.Abort);

        return int.TryParse(input, out int id)
            ? new(CancelPromptAction.CancelJob, id, input)
            : new(CancelPromptAction.Invalid, Input: input);
    }

    public static string? ReadPromptInput(string? prompt = null)
    {
        _directPromptActive = true;
        bool restoreWindowsCursorVisible = false;
        bool previousWindowsCursorVisible = true;
        bool restoreAnsiCursorHidden = false;
        try
        {
            if (!Console.IsOutputRedirected)
            {
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        previousWindowsCursorVisible = Console.CursorVisible;
                        Console.CursorVisible = true;
                        restoreWindowsCursorVisible = true;
                    }
                    catch
                    {
                        restoreWindowsCursorVisible = false;
                    }
                }
                else
                {
                    Console.Write("\u001b[?25h");
                    restoreAnsiCursorHidden = true;
                }
            }

            if (prompt != null)
                Console.Write(prompt);

            return Console.ReadLine();
        }
        finally
        {
            if (restoreWindowsCursorVisible)
            {
                try
                {
                    Console.CursorVisible = previousWindowsCursorVisible;
                }
                catch { }
            }
            else if (restoreAnsiCursorHidden && !Console.IsOutputRedirected)
            {
                Console.Write("\u001b[?25l");
            }

            _directPromptActive = false;
        }
    }

    public static void PrepareDirectPromptInput()
    {
        _directPromptActive = true;
    }

    public static async Task<IDisposable> AcquireConsoleInteractionAsync(CancellationToken ct = default)
    {
        await _consoleInteractionLock.WaitAsync(ct);
        return new ActionDisposable(() => _consoleInteractionLock.Release());
    }

    public static IDisposable PauseConsoleOutput()
    {
        if (Interlocked.Increment(ref _consoleOutputPauseDepth) == 1)
        {
            Printing.SetBuffering(true);
            if (Reporter != null)
                Reporter.IsPaused = true;
        }

        return new ActionDisposable(() =>
        {
            if (Interlocked.Decrement(ref _consoleOutputPauseDepth) == 0)
            {
                if (Reporter != null)
                    Reporter.IsPaused = false;
                Printing.SetBuffering(false);
            }
        });
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                dispose();
        }
    }
}
