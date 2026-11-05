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

}
