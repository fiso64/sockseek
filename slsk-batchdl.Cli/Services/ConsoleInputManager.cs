using System.Threading.Channels;

namespace Sldl.Cli;

public static class ConsoleInputManager
{
    private static readonly Channel<ConsoleKeyInfo> _keyChannel = Channel.CreateUnbounded<ConsoleKeyInfo>();

    public enum CancelPromptAction
    {
        Abort,
        CancelAll,
        CancelJob,
        Invalid,
    }

    public readonly record struct CancelPromptResult(CancelPromptAction Action, int? JobId = null, string? Input = null);

    public static bool GlobalCancelEnabled { get; set; } = true;
    public static Func<Task>? OnCancelRequested { get; set; }
    public static Func<Task>? OnNextCandidateRequested { get; set; }
    public static Func<Task>? OnInfoRequested { get; set; }
    public static CliProgressReporter? Reporter { get; set; }

    public static async Task RunLoopAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (GlobalCancelEnabled && key.KeyChar == 'c')
                    {
                        if (OnCancelRequested != null)
                        {
                            if (Reporter != null) Reporter.IsPaused = true;
                            Printing.SetBuffering(true);
                            await OnCancelRequested();
                            Printing.SetBuffering(false);
                            Printing.Flush();
                            if (Reporter != null) Reporter.IsPaused = false;
                        }
                    }
                    else if (GlobalCancelEnabled && key.KeyChar == 't')
                    {
                        if (OnNextCandidateRequested != null)
                        {
                            if (Reporter != null) Reporter.IsPaused = true;
                            Printing.SetBuffering(true);
                            await OnNextCandidateRequested();
                            Printing.SetBuffering(false);
                            Printing.Flush();
                            if (Reporter != null) Reporter.IsPaused = false;
                        }
                    }
                    else if (key.KeyChar == 'i')
                    {
                        if (OnInfoRequested != null)
                        {
                            if (Reporter != null) Reporter.IsPaused = true;
                            Printing.SetBuffering(true);
                            await OnInfoRequested();
                            Printing.SetBuffering(false);
                            Printing.Flush();
                            if (Reporter != null) Reporter.IsPaused = false;
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

    public static CancelPromptResult ReadCancelPromptResult()
    {
        var input = ReadCancelPromptInput();

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

    private static string? ReadCancelPromptInput()
    {
        var input = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return input.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length == 0)
                    continue;

                input.Length--;
                Console.Write("\b \b");
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                input.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }
}
