using System.Net;
using System.Net.Sockets;
using System.Text;
using Sockseek.Api;
using Sockseek.Cli;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Tests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class UserCommandRunnerTests
{
    [TestMethod]
    public void SelectionCartMaintainsDirectoryAntichainAndRejectsLockedRows()
    {
        var cart = new ShareSelectionCart();
        BrowseDirectoryEntryDto child = DirectoryRow(2, @"Root\Child");
        BrowseDirectoryEntryDto root = DirectoryRow(1, "Root");
        BrowseDirectoryEntryDto locked = DirectoryRow(3, @"Root\Locked", ShareVisibility.Locked);
        var file = new BrowseFileEntryDto(
            10, 2, ShareVisibility.Public,
            new FileMetadataDto("track.flac", 10, "flac", null, null, null, null));

        cart.ToggleDirectory(child);
        cart.ToggleFile(file, child.DisplayPath);
        Assert.AreEqual(1, cart.DirectoryCount);
        Assert.AreEqual(0, cart.FileCount, "The selected child covers its file.");

        cart.ToggleDirectory(root);
        Assert.AreEqual(1, cart.DirectoryCount, "Selecting the ancestor removes its selected descendants.");
        Assert.AreEqual(1, cart.TotalFileCount);
        Assert.AreEqual(10, cart.TotalBytes);
        CollectionAssert.AreEqual(
            new UserShareSelectionDto[] { new UserShareDirectorySelectionDto(1) },
            cart.ToSelections().ToArray());

        StringAssert.Contains(cart.ToggleDirectory(child), "covered");
        StringAssert.Contains(cart.ToggleDirectory(locked), "Locked");
        Assert.AreEqual(1, cart.DirectoryCount);
    }

    [TestMethod]
    public void ScriptableSelectionsPreserveRepeatedFolderAndFileOptions()
    {
        IReadOnlyList<UserShareSelectionDto> selections = UserCommandRunner.ParseSelections(
            ["user", "shares-download", Guid.NewGuid().ToString(), "--folder", "3", "--file=5", "--folder", "8"]);

        CollectionAssert.AreEqual(
            new UserShareSelectionDto[]
            {
                new UserShareDirectorySelectionDto(3),
                new UserShareFileSelectionDto(5),
                new UserShareDirectorySelectionDto(8),
            },
            selections.ToArray());
    }

    [TestMethod]
    public async Task JsonProfileDoesNotFetchPictureBody()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<string> request = RespondProfileAsync(listener, ProfileJson());
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int exitCode = await Sockseek.Cli.Program.Main(
                ["user", "profile", "Peer", "--json", "--remote", $"http://127.0.0.1:{port}", "--no-config"]);

            Assert.AreEqual(0, exitCode, stderr.ToString());
            StringAssert.Contains(await request, "/api/users/Peer/profile?refresh=false");
            StringAssert.Contains(stdout.ToString(), "\"picture\"");
            Assert.IsFalse(stdout.ToString().Contains("\"Picture\"", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task PagedRemoteCommandsShareDefaultsAndAcceptLargeRequestedPages()
    {
        string browseId = Guid.NewGuid().ToString();
        (string[] Args, int DefaultLimit)[] commands =
        [
            (["transfers"], 500),
            (["chat", "conversations"], 500),
            (["user", "shares-page", browseId, "--files", "1"], 500),
            (["user", "shares-page", browseId], 100),
        ];
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            foreach (var command in commands)
            {
                foreach (string? requested in new string?[] { null, "501", "2147483647" })
                {
                    using var listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    Task<string> request = RespondOnceAsync(listener,
                        """{"items":[],"nextCursor":null,"retainedCoverage":{"state":"Available"}}""");
                    string[] limit = requested is null ? [] : ["--limit", requested];
                    int exitCode = await Sockseek.Cli.Program.Main(
                        [.. command.Args, .. limit, "--json", "--remote", $"http://127.0.0.1:{port}", "--no-config"]);
                    Assert.AreEqual(0, exitCode, stderr.ToString());
                    string expected = requested ?? command.DefaultLimit.ToString();
                    StringAssert.Contains(await request.WaitAsync(TimeSpan.FromSeconds(5)), $"limit={expected}");
                }
                foreach (string invalid in new[] { "0", "-1", "2147483648" })
                {
                    int exitCode = await Sockseek.Cli.Program.Main(
                        [.. command.Args, "--limit", invalid, "--remote", "http://127.0.0.1:1", "--no-config"]);
                    Assert.AreEqual(2, exitCode, "Invalid page sizes must fail before any HTTP request.");
                }
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public async Task HumanProfileKeepsStatisticsVisibleWhenUserInfoIsUnavailable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string body = ProfileJson()
            .Replace(
                "\"info\": { \"state\": \"available\", \"reason\": null }",
                "\"info\": { \"state\": \"unavailable\", \"reason\": \"offline\" }")
            .Replace("\"uploadSlots\": 1", "\"uploadSlots\": null")
            .Replace("\"queueLength\": 0", "\"queueLength\": null")
            .Replace("\"hasFreeUploadSlot\": true", "\"hasFreeUploadSlot\": null");
        Task<string> request = RespondProfileAsync(listener, body);
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int exitCode = await Sockseek.Cli.Program.Main(
                ["user", "profile", "Peer", "--picture", "none", "--remote", $"http://127.0.0.1:{port}", "--no-config"]);

            Assert.AreEqual(0, exitCode, stderr.ToString());
            _ = await request;
            StringAssert.Contains(stdout.ToString(), "Shares: 1 files, 1 directories");
            StringAssert.Contains(stdout.ToString(), "Upload count: 1");
            StringAssert.Contains(stdout.ToString(), "Upload capacity: unavailable (offline)");
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            listener.Stop();
        }
    }

    [TestMethod]
    public void PixelRendererEmitsOnlyLocallyGeneratedAnsiAndResetsEachLine()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30));
        string rendered = ProfilePictureRenderer.RenderPixels(image);

        StringAssert.StartsWith(rendered, "\u001b[38;2;10;20;30m\u001b[48;2;10;20;30m▀");
        StringAssert.EndsWith(rendered, "\u001b[0m\n");
        Assert.AreEqual(2, rendered.Count(character => character == '▀'));
    }

    [TestMethod]
    public void PixelRendererUsesTerminalDefaultColorForTransparentPixels()
    {
        using var image = new Image<Rgba32>(1, 2);
        image[0, 0] = new Rgba32(200, 100, 50, 0);
        image[0, 1] = new Rgba32(10, 20, 30);

        string rendered = ProfilePictureRenderer.RenderPixels(image);

        Assert.AreEqual("\u001b[39m\u001b[48;2;10;20;30m▀\u001b[0m\n", rendered);
    }

    [TestMethod]
    public void SixelDetectionParsesPrimaryDeviceAttributesExactly()
    {
        Assert.IsTrue(ProfilePictureRenderer.PrimaryDeviceAttributesAdvertiseSixel(
            "\u001b[?61;4;6;7;21c"));
        Assert.IsTrue(ProfilePictureRenderer.PrimaryDeviceAttributesAdvertiseSixel(
            "noise\u001b[?4c"));
        Assert.IsFalse(ProfilePictureRenderer.PrimaryDeviceAttributesAdvertiseSixel(
            "\u001b[?61;14;40c"));
        Assert.IsFalse(ProfilePictureRenderer.PrimaryDeviceAttributesAdvertiseSixel(
            "\u001b[?61;4;6"));
    }

    [TestMethod]
    public void BrowserTransitionsToFilesWhenFinalDirectoryPageIsExactlyFull()
    {
        Assert.IsTrue(InteractiveShareBrowser.NeedsSeparateFilePage(100, null, 42));
        Assert.IsFalse(InteractiveShareBrowser.NeedsSeparateFilePage(99, null, 42));
        Assert.IsFalse(InteractiveShareBrowser.NeedsSeparateFilePage(100, "next", 42));
        Assert.IsFalse(InteractiveShareBrowser.NeedsSeparateFilePage(100, null, null));
    }

    [TestMethod]
    public void BrowserRowsDiscloseFolderCountsLockedContentAndFileType()
    {
        var directory = new BrowseDirectoryEntryDto(
            1, null, "Root", "Root", ShareVisibility.Mixed,
            false, 0, 0, 12, 2_048, 3, true);
        var file = new BrowseFileEntryDto(
            2, 1, ShareVisibility.Public,
            new FileMetadataDto("track.flac", 1_024, "flac", null, null, null, null));

        string directoryDescription = InteractiveShareBrowser.Describe(
            new InteractiveShareBrowser.BrowserRow.Directory(directory));
        string fileDescription = InteractiveShareBrowser.Describe(
            new InteractiveShareBrowser.BrowserRow.File(file, "Root"));

        StringAssert.Contains(directoryDescription, "12 files");
        StringAssert.Contains(directoryDescription, "3 locked");
        StringAssert.Contains(fileDescription, "flac");
    }

    private static BrowseDirectoryEntryDto DirectoryRow(
        long id,
        string path,
        ShareVisibility visibility = ShareVisibility.Public)
        => new(
            id, null, path[(path.LastIndexOf('\\') + 1)..], path, visibility,
            false, 0, 0, 1, 10, 0, true);

    private static async Task<string> RespondProfileAsync(TcpListener listener, string profile)
    {
        var requests = await Task.WhenAll(
            RespondOnceAsync(listener, profile, includePresence: true),
            RespondOnceAsync(listener, profile, includePresence: true));
        Assert.AreEqual(1, requests.Count(request => request.Contains("/api/users/Peer/presence ")));
        Assert.AreEqual(1, requests.Count(request => request.Contains("/api/users/Peer/profile?")));
        return requests.Single(request => request.Contains("/profile?"));
    }

    private static async Task<string> RespondOnceAsync(TcpListener listener, string body, bool includePresence = false)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[8192];
        int read = await stream.ReadAsync(buffer);
        string request = Encoding.ASCII.GetString(buffer, 0, read);
        if (includePresence && request.Contains("/api/users/Peer/presence "))
            body = """{"username":"Peer","state":"online"}""";
        byte[] payload = Encoding.UTF8.GetBytes(body);
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
            + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response);
        await stream.WriteAsync(payload);
        return request;
    }

    private static string ProfileJson()
        => $$"""
        {
          "username": "Peer",
          "info": { "state": "available", "reason": null },
          "statistics": { "state": "available", "reason": null },
          "pictureSection": { "state": "available", "reason": null },
          "description": "hello",
          "sharedFileCount": 1,
          "sharedDirectoryCount": 1,
          "averageUploadSpeed": 1,
          "uploadCount": 1,
          "uploadSlots": 1,
          "queueLength": 0,
          "hasFreeUploadSlot": true,
          "picture": {
            "url": "/api/users/Peer/picture",
            "mediaType": "image/png",
            "byteLength": 10,
            "eTag": "\"test\""
          },
          "observedAt": "2026-08-12T00:00:00Z"
        }
        """;
}
