using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Persistence.Write;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class PersistenceArchitectureTests
{
    [TestMethod]
    public void CoreAndApiProjects_DoNotReferencePersistenceEfOrSqlite()
    {
        string root = FindRepositoryRoot();
        AssertProjectOmits(Path.Combine(root, "Sockseek.Core", "Sockseek.Core.csproj"));
        AssertProjectOmits(Path.Combine(root, "Sockseek.Api", "Sockseek.Api.csproj"));
    }

    [TestMethod]
    public void ServerPersistenceBoundary_DoesNotUseOrmOrRawSqliteObjects()
    {
        string serverRoot = Path.Combine(FindRepositoryRoot(), "Sockseek.Server");
        string[] forbidden =
        [
            "SockseekDbContext",
            "DbContext",
            "DbSet<",
            "Microsoft.Data.Sqlite",
            "SqliteConnection",
        ];

        foreach (string file in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            foreach (string token in forbidden)
                Assert.IsFalse(source.Contains(token, StringComparison.Ordinal),
                    $"{Path.GetRelativePath(serverRoot, file)} crosses the persistence use-case boundary with '{token}'.");
        }
    }

    [TestMethod]
    public void PublicPersistenceMutations_DoNotExposeMutableRuntimeOrProviderTypes()
    {
        var mutationTypes = typeof(PersistenceMutation).Assembly.GetTypes()
            .Where(type => type.IsPublic && typeof(PersistenceMutation).IsAssignableFrom(type))
            .ToList();
        Assert.IsTrue(mutationTypes.Count > 1);

        foreach (var mutationType in mutationTypes)
        {
            foreach (var property in mutationType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                AssertSafe(property.PropertyType, $"{mutationType.Name}.{property.Name}", []);
        }
    }

    private static void AssertSafe(Type type, string path, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        Type[] forbidden =
        [
            typeof(Job),
            typeof(SearchSession),
            typeof(FileCandidate),
            typeof(Soulseek.SearchResponse),
            typeof(Soulseek.File),
            typeof(CancellationTokenSource),
            typeof(Stream),
        ];
        foreach (Type forbiddenType in forbidden)
            Assert.IsFalse(forbiddenType.IsAssignableFrom(type), $"{path} exposes {type.FullName}.");

        Assert.IsFalse(type.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true,
            $"{path} exposes EF type {type.FullName}.");
        Assert.IsFalse(type.Namespace?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true,
            $"{path} exposes SQLite type {type.FullName}.");

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid)
            || type == typeof(DateTimeOffset) || type == typeof(decimal))
            return;
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
                AssertSafe(argument, $"{path}<{argument.Name}>", visited);
        }
        if (type.Namespace?.StartsWith("Sockseek.Persistence.Write", StringComparison.Ordinal) != true
            || !visited.Add(type))
            return;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            AssertSafe(property.PropertyType, $"{path}.{property.Name}", visited);
    }

    private static void AssertProjectOmits(string projectPath)
    {
        string project = File.ReadAllText(projectPath);
        string[] forbidden = ["EntityFrameworkCore", "SQLite", "Sqlite", "Sockseek.Persistence"];
        foreach (string token in forbidden)
            Assert.IsFalse(project.Contains(token, StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(projectPath)} must not reference {token}.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Sockseek.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Sockseek repository root.");
    }
}
