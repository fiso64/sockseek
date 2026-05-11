using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;
using Sldl.Core.Settings;
using Sldl.Api;
using Sldl.Server;

namespace Tests.Server;

[TestClass]
public class OpenApiContractTests
{
    [TestMethod]
    public async Task OpenApiDocument_ContainsCoreServerContractSchemas()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-openapi-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "sldl-openapi-out-" + Guid.NewGuid());
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

            Assert.AreEqual("3.0.0-dev.10", version);

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

    [TestMethod]
    public void SldlApiJsonContext_CoversApiDtoContracts()
    {
        var dtoTypes = typeof(SldlApiJsonContext).Assembly
            .GetTypes()
            .Where(type =>
                type.IsPublic
                && type.Namespace == typeof(SldlApiJsonContext).Namespace
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
            "Missing SldlApiJsonContext metadata for:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    private static bool HasApiJsonTypeInfo(Type type)
    {
        try
        {
            return SldlApiJsonContext.Default.GetTypeInfo(type) != null;
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
