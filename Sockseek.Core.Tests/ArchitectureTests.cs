using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests;

[TestClass]
public class ArchitectureTests
{
    // Temporary source-level tripwires for the JobOutcome refactor. Once job snapshots
    // are immutable and lifecycle changes go through a reducer/state-store boundary,
    // the compiler should enforce these invariants and these tests can go away.
    [TestMethod]
    public void OutcomeProcessorTripwires_DoNotDirectlyCallLegacyTerminalMutators()
    {
        var root = FindRepositoryRoot();
        var engineSource = File.ReadAllText(Path.Combine(root, "Sockseek.Core", "DownloadEngine.cs"));

        string[] outcomeProcessors =
        [
            "ProcessExtractJob",
            "ProcessSearchJob",
            "ProcessRetrieveFolderJob",
            "ProcessSongDiscovery",
            "ProcessAlbumDiscovery",
            "ProcessAggregateDiscovery",
            "ProcessAlbumAggregateDiscovery",
            "ProcessLeafDownload",
            "ProcessSongDownload",
            "ProcessAggregateDownload",
            "ProcessAlbumDownload",
            "DownloadSong",
            "DownloadEmbeddedSong",
            "SearchAndDownloadSong",
        ];

        string[] directTerminalMutations =
        [
            ".SetDone(",
            ".SetAlreadyExists(",
            ".SetSkipped(",
            ".Fail(",
        ];

        foreach (var processor in outcomeProcessors)
        {
            var body = GetMethodBody(engineSource, processor);
            foreach (var mutation in directTerminalMutations)
            {
                Assert.IsFalse(
                    body.Contains(mutation, StringComparison.Ordinal),
                    $"{processor} should return/commit JobOutcome instead of directly calling {mutation}");
            }
        }
    }

    [TestMethod]
    public void SearcherTripwire_DoesNotDirectlyCallLegacyTerminalMutators()
    {
        var root = FindRepositoryRoot();
        var searcherSource = File.ReadAllText(Path.Combine(root, "Sockseek.Core", "Services", "Searcher.cs"));

        var searchBody = GetMethodBody(searcherSource, "Search");
        string[] directTerminalMutations =
        [
            ".SetDone(",
            ".SetAlreadyExists(",
            ".SetSkipped(",
            ".Fail(",
        ];

        foreach (var mutation in directTerminalMutations)
        {
            Assert.IsFalse(
                searchBody.Contains(mutation, StringComparison.Ordinal),
                $"Searcher.Search should return JobOutcome instead of directly calling {mutation}");
        }
    }

    [TestMethod]
    public void CancellationOutcomeTripwire_DoesNotCommitSourceLessCancellations()
    {
        var root = FindRepositoryRoot();
        var sources = new[]
        {
            Path.Combine(root, "Sockseek.Core", "DownloadEngine.cs"),
            Path.Combine(root, "Sockseek.Core", "Services", "Searcher.cs"),
        };

        string[] sourceLessCancellationPatterns =
        [
            "JobOutcome.Failed(JobFailureReason.Cancelled",
            ".Fail(JobFailureReason.Cancelled",
        ];

        foreach (var sourcePath in sources)
        {
            var source = File.ReadAllText(sourcePath);
            foreach (var pattern in sourceLessCancellationPatterns)
            {
                Assert.IsFalse(
                    source.Contains(pattern, StringComparison.Ordinal),
                    $"{Path.GetFileName(sourcePath)} should use JobOutcome.Cancelled(source) / SetCancelled(source) instead of {pattern}");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.EnumerateFiles(dir.FullName, "*.sln")
            .Any(path => Path.GetFileName(path).Equals("Sockseek.sln", StringComparison.OrdinalIgnoreCase)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }

    private static string GetMethodBody(string source, string methodName)
    {
        var nameIndex = FindMethodDeclaration(source, methodName);

        var openBrace = source.IndexOf('{', nameIndex);
        Assert.IsTrue(openBrace >= 0, $"Method {methodName} has no opening brace.");

        var depth = 0;
        for (var i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(openBrace, i - openBrace + 1);
            }
        }

        throw new InvalidOperationException($"Method {methodName} has no closing brace.");
    }

    private static int FindMethodDeclaration(string source, string methodName)
    {
        var index = -1;
        while ((index = source.IndexOf(methodName, index + 1, StringComparison.Ordinal)) >= 0)
        {
            var lineStart = source.LastIndexOf('\n', index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var linePrefix = source.Substring(lineStart, index - lineStart);

            if (linePrefix.Contains("Task<", StringComparison.Ordinal)
                || linePrefix.Contains("Task ", StringComparison.Ordinal)
                || linePrefix.Contains("Job ", StringComparison.Ordinal)
                || linePrefix.Contains("void ", StringComparison.Ordinal)
                || linePrefix.Contains("static ", StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Method {methodName} was not found.");
    }
}
