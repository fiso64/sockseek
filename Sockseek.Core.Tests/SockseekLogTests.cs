using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using Sockseek.Core;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;

namespace Tests.Core;

[TestClass]
public class SockseekLogTests
{
    [TestCleanup]
    public void Cleanup()
    {
        SockseekLog.RemoveNonFileOutputs();
        SockseekLog.RemoveFileOutputs();
    }

    [TestMethod]
    public void LogConsoleOnly_DoesNotWriteToNonConsoleSinks()
    {
        SockseekLog.RemoveNonFileOutputs();
        var consoleMessages = new List<string>();
        var sinkMessages = new List<string>();

        SockseekLog.AddConsole(writer: (message, _) => consoleMessages.Add(message));
        SockseekLog.AddSink((_, message) => sinkMessages.Add(message));

        SockseekLog.LogConsoleOnly(LogLevel.Information, "spotify-token=secret");

        CollectionAssert.Contains(consoleMessages, "spotify-token=secret");
        Assert.AreEqual(0, sinkMessages.Count);
    }

    [TestMethod]
    public void StructuredSink_ReceivesLogMetadataAndFormattedMessage()
    {
        var entries = new List<(SockseekLog.StructuredLogEntry Entry, string Message)>();
        SockseekLog.AddStructuredSink((entry, message) => entries.Add((entry, message)), LogLevel.Debug, prependLogLevel: true);

        SockseekLog.Soulseek.Debug("looking for file");
        SockseekLog.Warn("cli warning", categoryName: SockseekLog.Categories.Cli);
        SockseekLog.LogConsoleOnly(LogLevel.Information, "spotify-token=secret");

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual(LogLevel.Debug, entries[0].Entry.Level);
        Assert.AreEqual(SockseekLog.Categories.Soulseek, entries[0].Entry.CategoryName);
        Assert.AreEqual("looking for file", entries[0].Entry.Message);
        Assert.AreEqual(SockseekLog.LogRouting.All, entries[0].Entry.Routing);
        Assert.AreEqual("[debug] [soulseek] looking for file", entries[0].Message);

        Assert.AreEqual(LogLevel.Warning, entries[1].Entry.Level);
        Assert.AreEqual(SockseekLog.Categories.Cli, entries[1].Entry.CategoryName);
        Assert.AreEqual("cli warning", entries[1].Entry.Message);
        Assert.AreEqual("[warn] [cli] cli warning", entries[1].Message);
    }

    [TestMethod]
    public void NonConsoleLogs_IncludeExplicitCategoryAndLevel()
    {
        var sinkMessages = new List<string>();
        SockseekLog.AddSink((_, message) => sinkMessages.Add(message), LogLevel.Debug, prependLogLevel: true);

        SockseekLog.Info("cli message", categoryName: SockseekLog.Categories.Cli);
        SockseekLog.Debug("core message", callerFilePath: "/repo/Sockseek.Core/DownloadEngine.cs");
        SockseekLog.Warn("daemon message", callerFilePath: "/repo/Sockseek.Server/ServerHost.cs");
        SockseekLog.Jobs.Info("job message");
        SockseekLog.Soulseek.Debug("soulseek message");

        CollectionAssert.AreEqual(new[]
        {
            "[info] [cli] cli message",
            "[debug] [core] core message",
            "[warn] [daemon] daemon message",
            "[info] [jobs] job message",
            "[debug] [soulseek] soulseek message",
        }, sinkMessages);
    }

    [TestMethod]
    public void JobLog_EmitsStructuredJobContext()
    {
        var entries = new List<SockseekLog.StructuredLogEntry>();
        SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry));
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });

        SockseekLog.Jobs.Info(song, "running fallback: Artist - Track");

        Assert.AreEqual(1, entries.Count);
        var entry = entries[0];
        Assert.AreEqual(LogLevel.Information, entry.Level);
        Assert.AreEqual(SockseekLog.Categories.Jobs, entry.CategoryName);
        Assert.AreEqual($"[{song.DisplayId}] SongJob: running fallback: Artist - Track", entry.Message);

        var context = entry.Context as SockseekLog.JobLogContext;
        Assert.IsNotNull(context);
        Assert.AreEqual(song.DisplayId, context.DisplayId);
        Assert.AreEqual("SongJob", context.JobType);
        Assert.AreEqual("running fallback: Artist - Track", context.Message);
        Assert.IsTrue(context.ShowInLive);
    }

    [TestMethod]
    public void UserFacingJobLogs_DoNotUseRawJobShapedSockseekLogStrings()
    {
        var repositoryRoot = FindRepositoryRoot();
        var coreRoot = Path.Combine(repositoryRoot, "Sockseek.Core");
        var forbiddenPatterns = new[]
        {
            new Regex("SockseekLog\\.Jobs\\.(Info|Warn|Error)\\s*\\(\\s*(?:\\$@?|@\\$)\"\\s*\\[", RegexOptions.Singleline),
            new Regex("SockseekLog\\.Jobs\\.(Info|Warn|Error)\\s*\\(\\s*(?:\\$@?|@\\$)\"\\s*\\{(?:OnCompleteLogPrefix|logPrefix)\\b", RegexOptions.Singleline),
        };

        var offenders = Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !PathContainsSegment(path, "bin") && !PathContainsSegment(path, "obj"))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return forbiddenPatterns
                    .SelectMany(pattern => pattern.Matches(text).Cast<Match>())
                    .Select(match => $"{Path.GetRelativePath(repositoryRoot, path)}:{LineNumber(text, match.Index)}");
            })
            .Distinct()
            .OrderBy(line => line)
            .ToList();

        if (offenders.Count > 0)
            Assert.Fail("Use SockseekLog.Jobs.Info/Warn/Error(job, message) for job-shaped user-facing logs:\n" + string.Join("\n", offenders));
    }

    [TestMethod]
    public void ConsoleLogs_IncludeCategoryAndOnlyNonInfoLevelByDefault()
    {
        var consoleMessages = new List<string>();
        SockseekLog.AddConsole(LogLevel.Debug, writer: (message, _) => consoleMessages.Add(message));

        SockseekLog.Info("plain output", categoryName: SockseekLog.Categories.Cli);
        SockseekLog.Debug("debug output", categoryName: SockseekLog.Categories.Soulseek);
        SockseekLog.Warn("warn output", categoryName: SockseekLog.Categories.Core);

        CollectionAssert.AreEqual(new[]
        {
            "[cli] plain output",
            "[debug] [soulseek] debug output",
            "[warn] [core] warn output",
        }, consoleMessages);
    }

    [TestMethod]
    public void ConsoleOnlyLogs_RemainRawUserOutput()
    {
        var consoleMessages = new List<string>();
        SockseekLog.AddConsole(writer: (message, _) => consoleMessages.Add(message));

        SockseekLog.LogConsoleOnly(LogLevel.Information, "spotify-token=secret", categoryName: SockseekLog.Categories.Cli);

        CollectionAssert.AreEqual(new[]
        {
            "spotify-token=secret",
        }, consoleMessages);
    }

    [TestMethod]
    public void ExtractorContext_LogEmitsExtractJobMessageEvent()
    {
        var events = new DownloadEvents();
        var job = new ExtractJob("spotify://playlist");
        var messages = new List<(int DisplayId, LogLevel Level, string? Source, string Message)>();
        events.JobMessage += (eventJob, level, source, message) => messages.Add((eventJob.DisplayId, level, source, message));

        var context = ExtractorContext.ForExtractJob(job, events, "Spotify");

        context.Log.Info("Loading Spotify playlist");

        CollectionAssert.AreEqual(new[]
        {
            (job.DisplayId, LogLevel.Information, "Spotify", "Loading Spotify playlist"),
        }, messages);
    }

    [TestMethod]
    public void ExceptionFormatting_SeparatesSummaryAndDetail()
    {
        var inner = new InvalidOperationException("inner broke");
        var exception = new ApplicationException("outer wrapper", inner);

        Assert.AreEqual("inner broke", SockseekLog.ExceptionSummary(exception));
        StringAssert.Contains(SockseekLog.ExceptionDetail(exception), nameof(ApplicationException));
        StringAssert.Contains(SockseekLog.ExceptionDetail(exception), "inner broke");
        StringAssert.Contains(SockseekLog.FormatException("Operation failed", exception), "Operation failed:");
        StringAssert.Contains(SockseekLog.FormatException("Operation failed", exception), nameof(ApplicationException));
    }

    [TestMethod]
    public void ExpectedSoulseekPeerNetworkNoise_ClassifiesTimeoutAndSocketAbort()
    {
        var exception = new AggregateException(
            new TimeoutException("Inactivity timeout of 15000 milliseconds was reached"),
            new IOException(
                "Unable to read data from the transport connection",
                new SocketException((int)SocketError.OperationAborted)));

        Assert.IsTrue(IsExpectedSoulseekPeerNetworkNoise(exception));
    }

    [TestMethod]
    public void ExpectedSoulseekPeerNetworkNoise_ClassifiesPeerConnectionRefusedAggregate()
    {
        var exception = new AggregateException(
            new SocketException((int)SocketError.ConnectionRefused));

        Assert.IsTrue(IsExpectedSoulseekPeerNetworkNoise(exception));
    }

    [TestMethod]
    public void ExpectedSoulseekPeerNetworkNoise_ClassifiesSoulseekNetworkStackFaults()
    {
        var exception = new AggregateException(Soulseek.Network.Tcp.UnobservedFaultFactory.NullReferenceFromConnectionLoop());

        Assert.IsTrue(IsExpectedSoulseekPeerNetworkNoise(exception));
    }

    [TestMethod]
    public void ExpectedSoulseekPeerNetworkNoise_ClassifiesDisposedConnectionDisconnectTask()
    {
        var exception = new AggregateException(
            new ObjectDisposedException("Connection"),
            new ObjectDisposedException("MessageConnection"));

        Assert.IsTrue(IsExpectedSoulseekPeerNetworkNoise(exception));
    }

    [TestMethod]
    public void ExpectedSoulseekPeerNetworkNoise_DoesNotClassifyOtherDisposedObjects()
    {
        Assert.IsFalse(IsExpectedSoulseekPeerNetworkNoise(new AggregateException(new ObjectDisposedException("FileStream"))));

        try
        {
            throw new ObjectDisposedException("Connection");
        }
        catch (ObjectDisposedException thrown)
        {
            Assert.IsFalse(IsExpectedSoulseekPeerNetworkNoise(new AggregateException(thrown)));
        }
    }

    [TestMethod]
    public void ExpectedSoulseekPeerNetworkNoise_DoesNotClassifyUnknownApplicationFailure()
    {
        var exception = new AggregateException(new InvalidOperationException("engine invariant broke"));

        Assert.IsFalse(IsExpectedSoulseekPeerNetworkNoise(exception));
    }

    [TestMethod]
    public void UnobservedTaskExceptionHandler_MarksExpectedPeerNetworkNoiseObservedAndLogsTrace()
    {
        var entries = new List<SockseekLog.StructuredLogEntry>();
        SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry), LogLevel.Trace);
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(
            new SocketException((int)SocketError.ConnectionRefused)));

        HandleUnobservedTaskException(args);

        Assert.IsTrue(args.Observed);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(LogLevel.Trace, entries[0].Level);
        Assert.AreEqual(SockseekLog.Categories.Core, entries[0].CategoryName);
        StringAssert.Contains(entries[0].Message, "Ignored unobserved Soulseek peer-network task exception");
    }

    [TestMethod]
    public void UnobservedTaskExceptionHandler_MarksUnknownFailuresObservedAndLogsError()
    {
        var entries = new List<SockseekLog.StructuredLogEntry>();
        SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry), LogLevel.Trace);
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(
            new InvalidOperationException("engine invariant broke")));

        HandleUnobservedTaskException(args);

        Assert.IsTrue(args.Observed);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(LogLevel.Error, entries[0].Level);
        Assert.AreEqual(SockseekLog.Categories.Core, entries[0].CategoryName);
        StringAssert.Contains(entries[0].Message, "Unobserved task exception");
        StringAssert.Contains(entries[0].Message, "engine invariant broke");
    }

    [TestMethod]
    public async Task FileLogging_AllowsConcurrentWritesToSameLogFile()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "Sockseek-logger-concurrent-" + Guid.NewGuid() + ".log");

        try
        {
            SockseekLog.AddOrReplaceFile(logPath, LogLevel.Debug, prependDate: false, prependLogLevel: false);

            await Task.WhenAll(Enumerable.Range(0, 100)
                .Select(i => Task.Run(() => SockseekLog.Debug($"message-{i}"))));

            var lines = File.ReadAllLines(logPath);
            Assert.AreEqual(100, lines.Length);
            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 100).Select(i => $"[tests.core] message-{i}").ToArray(),
                lines);
        }
        finally
        {
            SockseekLog.RemoveFileOutputs();
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }

    [TestMethod]
    public void FileLogging_DoesNotThrowWhenLogFileIsLockedByAnotherProcess()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "Sockseek-logger-locked-" + Guid.NewGuid() + ".log");
        File.WriteAllText(logPath, "");

        try
        {
            SockseekLog.AddOrReplaceFile(logPath, LogLevel.Debug, prependDate: false, prependLogLevel: false);

            using var locked = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            SockseekLog.Debug("this should not crash");
        }
        finally
        {
            SockseekLog.RemoveFileOutputs();
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }

    private static bool IsExpectedSoulseekPeerNetworkNoise(Exception exception)
        => (bool)typeof(SockseekLog)
            .GetMethod("IsExpectedSoulseekPeerNetworkNoise", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [exception])!;

    private static void HandleUnobservedTaskException(UnobservedTaskExceptionEventArgs args)
        => typeof(SockseekLog)
            .GetMethod("HandleUnobservedTaskException", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [args]);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sockseek.Core", "Sockseek.Core.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static bool PathContainsSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));

    private static int LineNumber(string text, int index)
        => text[..index].Count(ch => ch == '\n') + 1;
}
