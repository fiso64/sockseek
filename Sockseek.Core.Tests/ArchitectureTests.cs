using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using System.Reflection;

namespace Tests;

[TestClass]
public class ArchitectureTests
{
    private static readonly Lazy<string> RepositoryRoot = new(
        FindRepositoryRootCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<CoreSource>> CoreSources = new(
        LoadCoreSourcesCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    // Temporary source-level tripwires for the JobOutcome refactor. Once job snapshots
    // are immutable and lifecycle changes go through a reducer/state-store boundary,
    // the compiler should enforce these invariants and these tests can go away.
    private static readonly string[] OutcomeProcessors =
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

    private static readonly HashSet<string> LegacyTerminalMutators =
    [
        "SetDone",
        "SetAlreadyExists",
        "SetSkipped",
        "SetCancelled",
        "Fail",
    ];

    private static readonly HashSet<string> TerminalMutatorAllowedFiles =
    [
        Path.Combine("Sockseek.Core", "Jobs", "Job.cs"),
        Path.Combine("Sockseek.Core", "Jobs", "SongJob.cs"),
        Path.Combine("Sockseek.Core", "Jobs", "AlbumJob.cs"),
        Path.Combine("Sockseek.Core", "Jobs", "FileDownloadJob.cs"),
        Path.Combine("Sockseek.Core", "Jobs", "DirectoryDownloadJob.cs"),
        Path.Combine("Sockseek.Core", "Transfers", "Downloads", "JobOrchestration", "JobOutcomeCommitter.cs"),
    ];

    [TestMethod]
    public void OutcomeProcessorTripwires_DoNotDirectlyCallLegacyTerminalMutators()
    {
        var sources = LoadCoreSources();

        foreach (var processor in OutcomeProcessors)
        {
            var methods = sources
                .SelectMany(source => source.Methods.Where(method => method.Identifier.ValueText == processor)
                    .Select(method => (source, method)))
                .ToList();

            Assert.IsTrue(methods.Count > 0, $"Method {processor} was not found in Sockseek.Core source files.");

            foreach (var (source, method) in methods)
                AssertNoLegacyTerminalMutators(source, method, processor);
        }
    }

    [TestMethod]
    public void SearcherTripwire_DoesNotDirectlyCallLegacyTerminalMutators()
    {
        var methods = LoadCoreSources()
            .SelectMany(source => source.Methods
                .Where(method => method.Identifier.ValueText == "Search"
                    && method.Ancestors().OfType<ClassDeclarationSyntax>().Any(type => type.Identifier.ValueText == "Searcher"))
                .Select(method => (source, method)))
            .ToList();

        Assert.IsTrue(methods.Count > 0, "Searcher.Search was not found in Sockseek.Core source files.");

        foreach (var (source, method) in methods)
            AssertNoLegacyTerminalMutators(source, method, "Searcher.Search");
    }

    [TestMethod]
    public void CancellationOutcomeTripwire_DoesNotCommitSourceLessCancellations()
    {
        foreach (var source in LoadCoreSources())
        {
            foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (IsJobOutcomeFailedCancelled(invocation) || IsFailCancelled(invocation))
                {
                    Assert.Fail($"{FormatLocation(source, invocation)} should use JobOutcome.Cancelled(source) / SetCancelled(source) instead of source-less cancelled failure.");
                }
            }
        }
    }

    [TestMethod]
    public void TerminalStateMutatorTripwire_OnlyOutcomeCommitterAndJobTypesCallTerminalMutators()
    {
        foreach (var source in LoadCoreSources())
        {
            if (TerminalMutatorAllowedFiles.Contains(RelativePath(source.Path)))
                continue;

            foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                    continue;

                var invocationName = memberAccess.Name.Identifier.ValueText;
                Assert.IsFalse(
                    LegacyTerminalMutators.Contains(invocationName),
                    $"{FormatLocation(source, invocation)} should commit a JobOutcome instead of directly calling {invocationName}.");
            }
        }
    }

    [TestMethod]
    public void DownloadOrchestration_LivesUnderTransfersDownloads()
    {
        var coreRoot = Path.Combine(FindRepositoryRoot(), "Sockseek.Core");

        Assert.IsFalse(
            Directory.Exists(Path.Combine(coreRoot, "Engine")),
            "Download orchestration should live under Sockseek.Core/Transfers/Downloads, not a top-level Engine folder.");

        Assert.IsTrue(
            File.Exists(Path.Combine(coreRoot, "Transfers", "Downloads", "DownloadEngine.cs")),
            "DownloadEngine.cs should live under Sockseek.Core/Transfers/Downloads.");

        Assert.IsTrue(
            File.Exists(Path.Combine(coreRoot, "Transfers", "Downloads", "JobOrchestration", "JobOrchestrator.cs")),
            "Download job orchestration should live under Sockseek.Core/Transfers/Downloads/JobOrchestration.");
    }

    [TestMethod]
    public void CoreStructure_DoesNotUseCatchAllModelsFolder()
    {
        var coreRoot = Path.Combine(FindRepositoryRoot(), "Sockseek.Core");

        Assert.IsFalse(
            Directory.Exists(Path.Combine(coreRoot, "Models")),
            "Do not add a top-level Models folder; place data shapes beside their owning domain.");
    }

    [TestMethod]
    public void TopLevelCommands_UseConfiguredDispatcherAndCanonicalBinding()
    {
        string cliRoot = Path.Combine(FindRepositoryRoot(), "Sockseek.Cli");
        string dispatcherPath = Path.Combine(
            cliRoot, "Services", "ConfiguredCommandDispatcher.cs");
        string configManagerPath = Path.Combine(
            cliRoot, "Services", "ConfigManager.cs");
        Assert.IsTrue(File.Exists(dispatcherPath),
            "Top-level commands need the configured dispatcher before command-specific parsing.");

        foreach (string path in Directory.EnumerateFiles(cliRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains(
                         $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                         StringComparison.OrdinalIgnoreCase)
                         && !path.Contains(
                             $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                             StringComparison.OrdinalIgnoreCase)))
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
            foreach (InvocationExpressionSyntax invocation in root.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member)
                    continue;

                var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (member.Name.Identifier.ValueText == "RunAsync"
                    && member.Expression.ToString().EndsWith("CommandRunner", StringComparison.Ordinal))
                {
                    if (!Path.GetFullPath(path).Equals(
                            Path.GetFullPath(dispatcherPath), StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.Fail(
                            $"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{line} dispatches a "
                            + "top-level command without ConfiguredCommandDispatcher; this bypasses "
                            + "config/profile resolution.");
                    }
                }

                if (!Path.GetFullPath(path).Equals(
                        Path.GetFullPath(configManagerPath), StringComparison.OrdinalIgnoreCase)
                    && member.Expression.ToString() == "ConfigManager"
                    && member.Name.Identifier.ValueText is "Load" or "BindAll" or "ExtractConfigPath")
                {
                    Assert.Fail(
                        $"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{line} assembles a "
                        + "parallel configuration path; use ConfigManager.LoadAndBindAll.");
                }
            }
        }
    }

    [TestMethod]
    public void DownloadSessionState_UsesPurposeBuiltCollaborators()
    {
        var coreRoot = Path.Combine(FindRepositoryRoot(), "Sockseek.Core");

        Assert.IsFalse(
            Directory.EnumerateFiles(coreRoot, "Registries.cs", SearchOption.AllDirectories).Any(),
            "Mixed session-state registries obscure ownership; use purpose-built collaborators instead.");

        var retiredTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "SessionRegistry",
            "IDownloadRegistry",
            "IUserStats",
        };
        var declarations = LoadCoreSources()
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Select(type => (source, Name: type.Identifier.ValueText)))
            .Where(type => retiredTypes.Contains(type.Name))
            .Select(type => $"{type.Name} at {Path.GetRelativePath(FindRepositoryRoot(), type.source.Path)}")
            .ToList();

        Assert.IsFalse(
            declarations.Count > 0,
            "Retired mixed registry types should not be reintroduced:\n" + string.Join("\n", declarations));
    }

    [TestMethod]
    public void DownloadEvents_PublicContracts_DoNotExposeMutableRuntimeTypes()
    {
        var events = typeof(DownloadEvents).GetEvents(BindingFlags.Instance | BindingFlags.Public);
        Assert.IsTrue(events.Length > 0, "DownloadEvents should expose public event contracts.");

        foreach (var eventInfo in events)
        {
            var invoke = eventInfo.EventHandlerType?.GetMethod("Invoke");
            Assert.IsNotNull(invoke, $"{eventInfo.Name} should have an invokable delegate type.");

            var parameters = invoke.GetParameters();
            Assert.AreEqual(1, parameters.Length, $"{eventInfo.Name} should publish one immutable change record.");
            Assert.IsTrue(
                typeof(CoreChange).IsAssignableFrom(parameters[0].ParameterType),
                $"{eventInfo.Name} should publish a CoreChange-derived immutable contract, not ad hoc runtime arguments.");
        }

        foreach (var eventInfo in typeof(SearchSession).GetEvents(BindingFlags.Instance | BindingFlags.Public))
        {
            var invoke = eventInfo.EventHandlerType?.GetMethod("Invoke");
            Assert.IsNotNull(invoke, $"{eventInfo.Name} should have an invokable delegate type.");

            foreach (var parameter in invoke.GetParameters())
                AssertNoForbiddenPublicContractTypes(parameter.ParameterType, $"SearchSession.{eventInfo.Name}", []);
        }

        var contractTypes = typeof(CoreChange).Assembly
            .GetTypes()
            .Where(type => type.IsPublic
                && type.Namespace is "Sockseek.Core.Events" or "Sockseek.Core.Snapshots")
            .Append(typeof(SearchRawResult))
            .ToList();

        foreach (var contractType in contractTypes)
            AssertNoForbiddenPublicContractTypes(contractType, contractType.Name, []);

        Type[] structuralChanges = [typeof(JobRegisteredChange), typeof(JobStructureChangedChange),
            typeof(JobResultCreatedChange), typeof(TrackListReadyChange)];
        foreach (Type contract in contractTypes.Where(type =>
            (typeof(CoreChange).IsAssignableFrom(type) && !structuralChanges.Contains(type))
            || typeof(JobExecutionPayload).IsAssignableFrom(type)))
            AssertCompact(contract, []);

        static void AssertCompact(Type type, HashSet<Type> visited)
        {
            if (!visited.Add(type)) return;
            Assert.IsFalse(type == typeof(JobSnapshot) || type == typeof(JobSnapshotHeader)
                || type == typeof(DirectoryManifestSnapshot) || type == typeof(DirectoryTransferPlanSnapshot)
                || type == typeof(PeerDirectoryResultSnapshot) || type == typeof(AlbumFolderSnapshot),
                $"Routine execution contracts must not carry {type.Name}; publish structural data separately.");
            if (type.IsArray) AssertCompact(type.GetElementType()!, visited);
            foreach (Type argument in type.GetGenericArguments()) AssertCompact(argument, visited);
            if (type.Assembly != typeof(CoreChange).Assembly) return;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                AssertCompact(property.PropertyType, visited);
        }
    }

    [TestMethod]
    public void RemoteTransferValues_AreSockseekOwnedAndProtocolNeutral()
    {
        Type[] valueTypes =
        [
            typeof(PeerFileIdentity),
            typeof(PeerFileTarget),
            typeof(PeerDirectoryIdentity),
            typeof(PeerDirectorySnapshot),
            typeof(DirectoryTransferEntry),
            typeof(DirectoryTransferPlan),
        ];

        foreach (Type valueType in valueTypes)
        {
            foreach (PropertyInfo property in valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                string typeName = property.PropertyType.FullName ?? property.PropertyType.Name;
                Assert.IsFalse(typeName.Contains("Soulseek.", StringComparison.Ordinal),
                    $"{valueType.Name}.{property.Name} exposes a Soulseek.NET type.");
                Assert.IsFalse(typeName.Contains("Sockseek.Api", StringComparison.Ordinal),
                    $"{valueType.Name}.{property.Name} exposes an API DTO.");
                Assert.IsFalse(typeName.Contains("Sockseek.Server", StringComparison.Ordinal),
                    $"{valueType.Name}.{property.Name} exposes a Server type.");
            }
        }
    }

    [TestMethod]
    public void DownloadLifecycleBases_ContainStateButNoSemanticPolicy()
    {
        CollectionAssert.AreEquivalent(
            new[] { "DownloadPath", "BytesTransferred", "FileSize", "Progress" },
            typeof(FileDownloadJob)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(property => property.Name)
                .ToArray());

        string[] directoryProperties = typeof(DirectoryDownloadJob)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.AreEquivalent(
            new[]
            {
                "DirectoryState", "ActiveAttempt", "FileJobs", "BytesTransferred",
                "TotalKnownBytes", "UnknownFileSizeCount", "Progress", "DownloadPath",
            },
            directoryProperties);

        foreach (MethodInfo method in typeof(FileDownloadJob).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Concat(typeof(DirectoryDownloadJob).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)))
        {
            Assert.IsFalse(method.Name.Contains("Resolve", StringComparison.Ordinal)
                || method.Name.Contains("Plan", StringComparison.Ordinal)
                || method.Name.Contains("Finalize", StringComparison.Ordinal),
                $"Lifecycle base method {method.Name} embeds semantic orchestration.");
        }
    }

    [TestMethod]
    public void SemanticJobs_AreSiblingsAndFolderRetrievalIsNotAlbumShaped()
    {
        Assert.AreEqual(typeof(FileDownloadJob), typeof(SongJob).BaseType);
        Assert.AreEqual(typeof(FileDownloadJob), typeof(RemoteFileJob).BaseType);
        Assert.AreEqual(typeof(DirectoryDownloadJob), typeof(AlbumJob).BaseType);
        Assert.AreEqual(typeof(DirectoryDownloadJob), typeof(RemoteDirectoryJob).BaseType);

        Assert.IsTrue(typeof(RetrieveFolderJob).GetConstructors().All(constructor =>
            constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(AlbumFolder))));
        Assert.IsTrue(typeof(RetrieveFolderJob).GetProperties().All(property =>
            property.PropertyType != typeof(AlbumFolder)));
    }

    [TestMethod]
    public void SharedTransferMechanics_DoNotDependOnConcreteSemanticJobsOrModeFlags()
    {
        string[] relativePaths =
        [
            Path.Combine("Sockseek.Core", "Transfers", "Downloads", "ExactPeerFileTransferRunner.cs"),
            Path.Combine("Sockseek.Core", "Transfers", "Downloads", "DirectoryTransfers", "DirectoryTransferRunner.cs"),
        ];
        string[] forbidden =
        [
            nameof(SongJob),
            nameof(AlbumJob),
            nameof(RemoteFileJob),
            nameof(RemoteDirectoryJob),
            nameof(ExtractionMode),
        ];

        foreach (string relativePath in relativePaths)
        {
            string path = Path.Combine(FindRepositoryRoot(), relativePath);
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
            var identifiers = root.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Select(identifier => identifier.Identifier.ValueText)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string name in forbidden)
                Assert.IsFalse(identifiers.Contains(name), $"{relativePath} depends on semantic type/mode {name}.");
        }
    }

    [TestMethod]
    public void RemoteJobs_HaveRequiredExactSourcesAndNoImplicitThirdDirectoryMode()
    {
        Assert.AreEqual(
            2,
            typeof(RemoteDirectorySource).GetNestedTypes(BindingFlags.Public).Length);
        Assert.ThrowsExactly<ArgumentNullException>(() => new RemoteDirectoryJob(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new RemoteFileJob(null!));

        string providerPath = Path.Combine(
            FindRepositoryRoot(), "Sockseek.Core", "Files", "NameFormatVariableProvider.cs");
        string providerSource = File.ReadAllText(providerPath);
        foreach (string forbidden in new[] { "SongQuery", "TagLib", "AlbumJob", "SongJob" })
            Assert.IsFalse(providerSource.Contains(forbidden, StringComparison.Ordinal),
                $"The shared name-format provider depends on {forbidden}.");
    }

    [TestMethod]
    public void RemoteTransferRefactor_DoesNotRegressToFlagsNormalizationOrGeneralPrefixes()
    {
        string[] forbiddenMembers =
        [
            "AllowBrowseResolvedTarget",
            "SkipResolvedTargetTrackCountVerification",
            "ResolvedTargetNeedsInitialFolderRetrieval",
        ];

        foreach (CoreSource source in LoadCoreSources())
        {
            string text = source.Root.ToFullString();
            foreach (string member in forbiddenMembers)
                Assert.IsFalse(text.Contains(member, StringComparison.Ordinal),
                    $"{RelativePath(source.Path)} reintroduces {member}.");

            foreach (BaseTypeDeclarationSyntax declaration in source.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                Assert.IsFalse(declaration.Identifier.ValueText.StartsWith("General", StringComparison.Ordinal),
                    $"{RelativePath(source.Path)} declares neutral/default type {declaration.Identifier.ValueText} with a General prefix.");
            }

            Assert.IsFalse(text.Contains("PeerUsername.Normalize", StringComparison.Ordinal),
                $"{RelativePath(source.Path)} normalizes exact Soulseek usernames.");
        }
    }

    private static void AssertNoForbiddenPublicContractTypes(Type type, string path, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (IsSimpleContractType(type))
            return;

        if (type.IsArray)
            Assert.Fail($"{path} exposes array type {type.FullName}; use immutable/read-only collection records.");

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            Assert.IsFalse(
                definition == typeof(List<>)
                || definition == typeof(Dictionary<,>)
                || definition == typeof(IList<>)
                || definition == typeof(ICollection<>),
                $"{path} exposes mutable collection type {type.FullName}.");

            foreach (var argument in type.GetGenericArguments())
                AssertNoForbiddenPublicContractTypes(argument, $"{path}<{argument.Name}>", visited);

            if (definition == typeof(IReadOnlyList<>)
                || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(IReadOnlyDictionary<,>))
            {
                return;
            }
        }

        AssertForbiddenRuntimeTypeNotExposed(type, path);

        if (type.Namespace == null || !type.Namespace.StartsWith("Sockseek.Core.", StringComparison.Ordinal))
            return;

        if (!visited.Add(type))
            return;

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            AssertNoForbiddenPublicContractTypes(property.PropertyType, $"{path}.{property.Name}", visited);
    }

    private static void AssertForbiddenRuntimeTypeNotExposed(Type type, string path)
    {
        var forbiddenTypes = new[]
        {
            typeof(Job),
            typeof(SearchSession),
            typeof(FileCandidate),
            typeof(AlbumFolder),
            typeof(AlbumFile),
            typeof(SourceMutation),
            typeof(DownloadSettings),
            typeof(CancellationToken),
            typeof(CancellationTokenSource),
            typeof(Soulseek.SearchResponse),
            typeof(Soulseek.File),
            typeof(Soulseek.TransferStates),
        };

        foreach (var forbiddenType in forbiddenTypes)
        {
            Assert.IsFalse(
                forbiddenType.IsAssignableFrom(type),
                $"{path} exposes mutable/runtime type {type.FullName}; publish a Sockseek-owned immutable snapshot instead.");
        }
    }

    private static bool IsSimpleContractType(Type type)
        => type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(Guid)
            || type == typeof(DateTimeOffset)
            || type == typeof(DateTime)
            || type == typeof(TimeSpan)
            || type == typeof(decimal);

    private static void AssertNoLegacyTerminalMutators(CoreSource source, MethodDeclarationSyntax method, string methodName)
    {
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var invocationName = memberAccess.Name.Identifier.ValueText;
            Assert.IsFalse(
                LegacyTerminalMutators.Contains(invocationName),
                $"{methodName} should return/commit JobOutcome instead of directly calling {invocationName} at {FormatLocation(source, invocation)}");
        }
    }

    private static bool IsJobOutcomeFailedCancelled(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        return memberAccess.Name.Identifier.ValueText == "Failed"
            && memberAccess.Expression.ToString() == "JobOutcome"
            && FirstArgumentContainsCancelled(invocation);
    }

    private static bool IsFailCancelled(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        return memberAccess.Name.Identifier.ValueText == "Fail"
            && FirstArgumentContainsCancelled(invocation);
    }

    private static bool FirstArgumentContainsCancelled(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.FirstOrDefault()?.ToString()
            .Contains("JobFailureReason.Cancelled", StringComparison.Ordinal) == true;

    private static string FormatLocation(CoreSource source, SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        return $"{RelativePath(source.Path)}:{lineSpan.StartLinePosition.Line + 1}";
    }

    private static string RelativePath(string path)
        => Path.GetRelativePath(FindRepositoryRoot(), path);

    private static IReadOnlyList<CoreSource> LoadCoreSources()
        => CoreSources.Value;

    private static IReadOnlyList<CoreSource> LoadCoreSourcesCore()
    {
        var root = FindRepositoryRoot();
        var coreRoot = Path.Combine(root, "Sockseek.Core");

        return Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var text = File.ReadAllText(path);
                var tree = CSharpSyntaxTree.ParseText(text, path: path);
                var rootNode = tree.GetRoot();
                return new CoreSource(path, rootNode, rootNode.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList());
            })
            .ToList();
    }

    private static string FindRepositoryRoot()
        => RepositoryRoot.Value;

    private static string FindRepositoryRootCore()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.EnumerateFiles(dir.FullName, "*.sln")
            .Any(path => Path.GetFileName(path).Equals("Sockseek.sln", StringComparison.OrdinalIgnoreCase)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }

    private sealed record CoreSource(string Path, SyntaxNode Root, IReadOnlyList<MethodDeclarationSyntax> Methods);
}
