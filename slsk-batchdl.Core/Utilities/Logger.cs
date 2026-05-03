
namespace Sldl.Core;

public static class Logger
{
    public enum LogLevel
    {
        Trace,
        Debug,
        DebugError,
        Info,
        Warn,
        Error,
        Fatal
    }

    public class OutputConfig
    {
        public Action<string> Output = null!;
        public Action<LogLevel, string>? OutputWithLevel;
        public Action<string, ConsoleColor>? ColoredOutput; // only set for console outputs with color
        public LogLevel MinimumLevel;
        public bool PrependDate;
        public bool PrependLogLevel;
        public bool IsFileOutput;
        public bool IsConsoleOutput;
        public bool UseConsoleColors; // Only applicable for console output
    }

    private static readonly List<OutputConfig> OutputConfigs = new();

    private static readonly Lock LockObject = new();

    public static void SetupExceptionHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var exception = (Exception)args.ExceptionObject;
            LogNonConsole(LogLevel.Fatal, $"Unhandled exception. {args.ExceptionObject}: {exception.Message}\n{exception.StackTrace}");
        };
    }

    private static OutputConfig AddOutput(Action<string> output, LogLevel minimumLevel = LogLevel.Info, bool prependDate = true,
        bool prependLogLevel = true, bool isFileOutput = false)
    {
        lock (LockObject)
        {
            var config = new OutputConfig
            {
                Output = output,
                PrependDate = prependDate,
                PrependLogLevel = prependLogLevel,
                UseConsoleColors = false,
                MinimumLevel = minimumLevel,
                IsFileOutput = isFileOutput
            };

            OutputConfigs.Add(config);
            return config;
        }
    }

    public static void AddSink(
        Action<LogLevel, string> output,
        LogLevel minimumLevel = LogLevel.Info,
        bool prependDate = false,
        bool prependLogLevel = false)
    {
        var sinkConfig = AddOutput(message => output(LogLevel.Info, message), minimumLevel, prependDate, prependLogLevel);
        sinkConfig.OutputWithLevel = output;
    }

    public static void AddConsole(LogLevel minimumLevel = LogLevel.Info, bool useColors = true, bool prependDate = false, bool prependLogLevel = false,
        Action<string, ConsoleColor>? writer = null)
    {
        var write = writer ?? ((msg, color) =>
        {
            Console.ForegroundColor = color;
            Console.WriteLine(msg);
            Console.ResetColor();
        });
        var consoleConfig = AddOutput(msg => write(msg, ConsoleColor.Gray), minimumLevel, prependDate, prependLogLevel);
        consoleConfig.UseConsoleColors = useColors;
        consoleConfig.IsConsoleOutput = true;
        consoleConfig.ColoredOutput = write;
    }

    public static void SetConsoleLogLevel(LogLevel logLevel)
    {
        lock (LockObject)
        {
            var consoleConfig = OutputConfigs.FirstOrDefault(x => x.IsConsoleOutput);
            if (consoleConfig != null)
                consoleConfig.MinimumLevel = logLevel;
        }
    }

    public static void RemoveConsoleOutputs()
    {
        lock (LockObject)
            OutputConfigs.RemoveAll(config => config.IsConsoleOutput);
    }

    public static void RemoveNonFileOutputs()
    {
        lock (LockObject)
            OutputConfigs.RemoveAll(config => !config.IsFileOutput);
    }

    public static void AddFile(string filePath, LogLevel minimumLevel = LogLevel.Debug, bool prependDate = true, bool prependLogLevel = true)
    {
        AddOutput(message => File.AppendAllText(filePath, message + '\n'), minimumLevel, prependDate, prependLogLevel, isFileOutput: true);
    }

    public static void AddOrReplaceFile(string filePath, LogLevel minimumLevel = LogLevel.Debug, bool prependDate = true, bool prependLogLevel = true)
    {
        var directoryName = Path.GetDirectoryName(filePath);
        if (directoryName != null && directoryName != String.Empty)
        {
            Directory.CreateDirectory(directoryName);
        }
        OutputConfigs.RemoveAll(config => config.IsFileOutput);
        AddOutput(message => File.AppendAllText(filePath, message + '\n'), minimumLevel, prependDate, prependLogLevel, isFileOutput: true);
    }

    public static void Log(LogLevel level, string message, ConsoleColor? color = null, IEnumerable<OutputConfig>? outputs = null)
    {
        List<OutputConfig> targets;
        lock (LockObject)
        {
            targets = (outputs ?? OutputConfigs).ToList();
        }

        foreach (var config in targets)
        {
            if (level < config.MinimumLevel)
                continue;

            string msg = message;
            if (!config.IsConsoleOutput) msg = msg.TrimStart();
            string logEntry = BuildLogEntry(level, msg, config.PrependDate, config.PrependLogLevel);

            if (config.IsConsoleOutput && config.UseConsoleColors && config.ColoredOutput != null)
            {
                ConsoleColor targetColor = color ?? level switch
                {
                    LogLevel.Error or LogLevel.Fatal => ConsoleColor.Red,
                    LogLevel.Warn or LogLevel.DebugError => ConsoleColor.DarkYellow,
                    _ => ConsoleColor.Gray,
                };
                config.ColoredOutput(logEntry, targetColor);
            }
            else
            {
                if (config.OutputWithLevel != null)
                    config.OutputWithLevel(level, logEntry);
                else
                    config.Output(logEntry);
            }
        }
    }

    public static void LogNonConsole(LogLevel level, string message)
    {
        List<OutputConfig> nonConsole;
        lock (LockObject)
        {
            nonConsole = OutputConfigs.Where(x => !x.IsConsoleOutput).ToList();
        }
        Log(level, message, color: null, outputs: nonConsole);
    }

    public static void LogConsoleOnly(LogLevel level, string message, ConsoleColor? color = null)
    {
        List<OutputConfig> console;
        lock (LockObject)
        {
            console = OutputConfigs.Where(x => x.IsConsoleOutput).ToList();
        }
        Log(level, message, color, console);
    }

    private static string BuildLogEntry(LogLevel level, string message, bool prependDate, bool prependLogLevel)
    {
        var parts = new List<string>();

        if (prependDate)
        {
            parts.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        if (prependLogLevel)
        {
            parts.Add($"[{level}]");
        }

        parts.Add(message);

        return string.Join(" ", parts);
    }

    public static void Trace(string message, ConsoleColor? color = null) => Log(LogLevel.Trace, message, color);
    public static void Debug(string message, ConsoleColor? color = null) => Log(LogLevel.Debug, message, color);
    public static void DebugError(string message, ConsoleColor? color = null) => Log(LogLevel.DebugError, message, color);
    public static void Info(string message, ConsoleColor? color = null) => Log(LogLevel.Info, message, color);
    public static void Warn(string message, ConsoleColor? color = null) => Log(LogLevel.Warn, message, color);
    public static void Error(string message, ConsoleColor? color = null) => Log(LogLevel.Error, message, color);
    public static void Fatal(string message, ConsoleColor? color = null) => Log(LogLevel.Fatal, message, color);
}
