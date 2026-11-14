using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Extractors;

namespace Tests.Core;

[TestClass]
public sealed class LoggingArchitectureTests
{
    [TestMethod]
    public void ConsoleLevel_DoesNotLimitDebugFileLogging()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-logging-" + Guid.NewGuid());
        string path = Path.Combine(directory, "sockseek.log");
        var console = new ConcurrentQueue<CompactLogRecord>();

        try
        {
            using ILoggerFactory factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(new CompactTextLoggerProvider(console.Enqueue, LogLevel.Information));
                builder.AddProvider(new CompactFileLoggerProvider(path, LogLevel.Debug));
            });
            ILogger logger = factory.CreateLogger("Sockseek.Core.TestComponent");

            logger.LogDebug("debug decision");
            logger.LogInformation("visible lifecycle");

            CollectionAssert.AreEqual(
                new[] { "visible lifecycle" },
                console.Select(record => record.Message).ToArray());
            string file = File.ReadAllText(path);
            StringAssert.Contains(file, "debug decision");
            StringAssert.Contains(file, "visible lifecycle");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void CompactFormatter_InfoModeOmitsEmitter()
    {
        var record = new CompactLogRecord(
            new DateTimeOffset(2026, 8, 21, 12, 34, 56, TimeSpan.Zero),
            LogLevel.Warning,
            "Sockseek.Core.Soulseek.SoulseekClientManager",
            new EventId(2005, "connection-lost"),
            "retrying",
            null);

        string line = CompactLogFormatter.Format(
            record,
            includeTimestamp: false,
            includeInformationLevel: true,
            includeSource: false);

        Assert.AreEqual("[warn] [soulseek] retrying", line);
    }

    [TestMethod]
    public void CompactFormatter_DebugModeIncludesEmitter()
    {
        var record = new CompactLogRecord(
            new DateTimeOffset(2026, 8, 21, 12, 34, 56, TimeSpan.Zero),
            LogLevel.Information,
            "Sockseek.Core.Soulseek.SoulseekClientManager",
            new EventId(2012, "login-starting"),
            "Starting Soulseek login",
            null);

        string line = CompactLogFormatter.Format(
            record,
            includeTimestamp: false,
            includeInformationLevel: true,
            includeSource: true);

        Assert.AreEqual("[info] [soulseek:SoulseekClientManager] Starting Soulseek login", line);
    }

    [TestMethod]
    public async Task CompactFileProvider_ConcurrentWritesProduceWholeLines()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-logging-" + Guid.NewGuid());
        string path = Path.Combine(directory, "sockseek.log");

        try
        {
            using var provider = new CompactFileLoggerProvider(path, LogLevel.Debug);
            ILogger logger = provider.CreateLogger("Sockseek.Core.ConcurrentTest");
            const int writerCount = 24;
            await Task.WhenAll(Enumerable.Range(0, writerCount)
                .Select(index => Task.Run(() => logger.LogDebug("message-{Index}", index))));

            string[] lines = File.ReadAllLines(path);
            Assert.AreEqual(writerCount, lines.Length);
            Assert.AreEqual(writerCount, lines.Distinct(StringComparer.Ordinal).Count());
            Assert.IsTrue(lines.All(line => line.Contains("[debug] [core:ConcurrentTest] message-")));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void CompactFileProvider_LockedFileDoesNotAffectCaller()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-logging-" + Guid.NewGuid());
        string path = Path.Combine(directory, "sockseek.log");
        Directory.CreateDirectory(directory);

        try
        {
            using (var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var provider = new CompactFileLoggerProvider(path, LogLevel.Debug))
            {
                ILogger logger = provider.CreateLogger("Sockseek.Core.LockedFileTest");
                logger.LogError("the caller must continue");
            }

            Assert.AreEqual(0, new FileInfo(path).Length);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void CompactFileProvider_WritesCompleteLifecycleSequence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-logging-" + Guid.NewGuid());
        string path = Path.Combine(directory, "sockseek.log");

        try
        {
            using var provider = new CompactFileLoggerProvider(path, LogLevel.Debug);
            ILogger logger = provider.CreateLogger("Sockseek.Server.PeerBrowsing.PeerBrowseService");
            using (OperationLogScope operation = OperationLogScope.Start(
                logger,
                "peer-browse",
                "browse-safe-id"))
            {
                operation.Succeeded("complete", itemCount: 12, byteCount: 34);
            }

            string[] lines = File.ReadAllLines(path);
            Assert.AreEqual(2, lines.Length);
            StringAssert.Contains(lines[0], "peer-browse browse-safe-id started");
            StringAssert.Contains(lines[1], "peer-browse browse-safe-id ended as complete");
            StringAssert.Contains(lines[1], "items=12, bytes=34");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void ProductionLogging_UsesGeneratedUniqueEventIdsAndNoLegacyGlobal()
    {
        string root = FindRepositoryRoot();
        string[] projects = ["Sockseek.Core", "Sockseek.Persistence", "Sockseek.Server", "Sockseek.Cli"];
        var eventIds = new Dictionary<int, string>();
        var violations = new List<string>();
        var expression = new Regex(
            @"\[LoggerMessage\(\s*(?:EventId\s*=\s*)?(?<id>\d+)",
            RegexOptions.Multiline);

        foreach (string project in projects)
        {
            foreach (string file in Directory.EnumerateFiles(
                Path.Combine(root, project), "*.cs", SearchOption.AllDirectories)
                .Where(path => !PathContainsSegment(path, "obj") && !PathContainsSegment(path, "bin")))
            {
                string text = File.ReadAllText(file);
                if (text.Contains("SockseekLog", StringComparison.Ordinal))
                    violations.Add($"legacy logger: {Path.GetRelativePath(root, file)}");
                if (Regex.IsMatch(text, @"\.Log(?:Trace|Debug|Information|Warning|Error|Critical)\s*\("))
                    violations.Add($"non-generated logging call: {Path.GetRelativePath(root, file)}");

                foreach (Match match in expression.Matches(text))
                {
                    int id = int.Parse(match.Groups["id"].Value);
                    string location = Path.GetRelativePath(root, file);
                    if (id is < 1000 or > 5999)
                        violations.Add($"event id {id} outside allocated ranges: {location}");
                    else if (!eventIds.TryAdd(id, location))
                        violations.Add($"duplicate event id {id}: {eventIds[id]} and {location}");
                }
            }
        }

        Assert.IsTrue(eventIds.Count > 0, "Expected generated logging events.");
        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void SensitiveOutput_IsASeparateNonLoggingContract()
    {
        Assert.IsFalse(typeof(ILogger).IsAssignableFrom(typeof(ISensitiveOutput)));
        Assert.IsFalse(typeof(ILoggerProvider).IsAssignableFrom(typeof(ISensitiveOutput)));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sockseek.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Sockseek.sln.");
    }

    private static bool PathContainsSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));
}
