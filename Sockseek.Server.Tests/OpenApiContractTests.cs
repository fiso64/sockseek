using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class OpenApiContractTests
{
    [TestMethod]
    public async Task OpenApiDocument_ContainsCoreServerContractSchemas()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-openapi-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-openapi-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(outputDir);

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(url) };
            using var response = await http.GetAsync("/api/openapi.json");

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var json = document.RootElement.GetRawText();

            var version = document.RootElement
                .GetProperty("info")
                .GetProperty("version")
                .GetString();

            Assert.AreEqual(ExpectedOpenApiVersion(), version);

            StringAssert.Contains(json, nameof(JobSummaryDto));
            StringAssert.Contains(json, nameof(SubmitAlbumJobRequestDto));
            StringAssert.Contains(json, nameof(AlbumJobPayloadDto));
            StringAssert.Contains(json, nameof(FileCandidateDto));
            StringAssert.Contains(json, nameof(WorkflowTreeDto));
            StringAssert.Contains(json, nameof(ApiErrorDto));
            StringAssert.Contains(json, "discriminator");
            StringAssert.Contains(json, "kind");
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, recursive: true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    private static string ExpectedOpenApiVersion()
    {
        var assemblyVersion = typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ServerHost).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        var metadataIndex = assemblyVersion.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex >= 0 ? assemblyVersion[..metadataIndex] : assemblyVersion;
    }

    [TestMethod]
    public void SockseekApiJsonContext_CoversApiDtoContracts()
    {
        var dtoTypes = typeof(SockseekApiJsonContext).Assembly
            .GetTypes()
            .Where(type =>
                type.IsPublic
                && type.Namespace == typeof(SockseekApiJsonContext).Namespace
                && type.Name.EndsWith("Dto", StringComparison.Ordinal)
                && !type.ContainsGenericParameters)
            .OrderBy(type => type.FullName)
            .ToList();

        var closedGenericDtoTypes = new[]
        {
            typeof(SearchResultSnapshotDto<FileCandidateDto>),
            typeof(SearchResultSnapshotDto<AlbumFolderDto>),
            typeof(SearchResultSnapshotDto<AggregateTrackCandidateDto>),
            typeof(SearchResultSnapshotDto<AggregateAlbumCandidateDto>),
            typeof(CollectionPatchDto<string>),
            typeof(CollectionPatchDto<RegexRuleDto>),
        };

        var missing = dtoTypes
            .Concat(closedGenericDtoTypes)
            .Distinct()
            .Where(type => !HasApiJsonTypeInfo(type))
            .Select(type => type.FullName)
            .ToList();

        Assert.AreEqual(
            0,
            missing.Count,
            "Missing SockseekApiJsonContext metadata for:" + "\n" + string.Join("\n", missing));
    }

    private static bool HasApiJsonTypeInfo(Type type)
    {
        try
        {
            return SockseekApiJsonContext.Default.GetTypeInfo(type) != null;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
