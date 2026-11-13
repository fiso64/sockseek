using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Cli;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Server;
using Sockseek.Server.PeerRestrictions;
using Sockseek.Server.Persistence;
using Tests.ClientTests;
using UserInfo = Soulseek.UserInfo;
using System.Net;
using SixLabors.ImageSharp;

namespace Tests;

[TestClass]
public sealed class DaemonConfigWatcherTests
{
    [TestMethod]
    public async Task WebUiPreferencesReloadWithValidationAndCliPrecedence()
    {
        await using var fixture = new Fixture("web-theme = light\nweb-default-search-mode = track",
            ["--web-default-search-mode", "song-groups"]);
        await fixture.StartRuntimeAsync();
        Assert.AreEqual("light", fixture.ServerOptions.WebUi.Theme);
        Assert.AreEqual("song-groups", fixture.ServerOptions.WebUi.DefaultSearchMode);
        fixture.Write("web-theme = auto\nweb-default-search-mode = album-groups");
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        Assert.AreEqual("auto", fixture.ServerOptions.WebUi.Theme);
        Assert.AreEqual("song-groups", fixture.ServerOptions.WebUi.DefaultSearchMode);
        Assert.IsFalse(fixture.Supervisor.GetStatus().Configuration!.RestartRequired);
        fixture.Write("web-theme = invalid");
        Assert.IsFalse(await fixture.Watcher.ReloadAsync());
        Assert.AreEqual(1, fixture.Supervisor.GetStatus().Configuration!.ErrorLine);
        Assert.IsFalse(string.IsNullOrWhiteSpace(fixture.Supervisor.GetStatus().Configuration!.Error));
        Assert.AreEqual("auto", fixture.ServerOptions.WebUi.Theme);
        fixture.Write("web-default-search-mode = invalid");
        Assert.IsFalse(await fixture.Watcher.ReloadAsync());
        fixture.Write("");
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        Assert.AreEqual("dark", fixture.ServerOptions.WebUi.Theme);
        Assert.IsNull(fixture.Supervisor.GetStatus().Configuration!.Error);
        Assert.IsNull(fixture.Supervisor.GetStatus().Configuration!.ErrorLine);
    }

    [TestMethod]
    public async Task RuntimeReloadUpdatesProfileAndUploadLimitsAndUsesNewRootsOnRescan()
    {
        await using var fixture = new Fixture("", []);
        await fixture.StartRuntimeAsync();
        string root = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "library");
        Directory.CreateDirectory(root);
        File.WriteAllText(System.IO.Path.Combine(root, "shared.txt"), "testing");
        string picture = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "profile.png");
        using (var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(2, 2))
            await image.SaveAsPngAsync(picture);
        string runtimeConfig = $"upload-slots = 3\nupload-speed-limit-kib = 64\nshare = {root}\nuser-picture = {picture}\n";
        fixture.Write(runtimeConfig + "user-description = updated");
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        Assert.IsFalse(fixture.Supervisor.GetStatus().Configuration!.RestartRequired);
        SharingRuntime sharing = fixture.Supervisor.Runtime.Sharing!;
        Assert.AreEqual(3, sharing.Uploads.GetQueueSnapshot().TotalSlots);
        Assert.AreEqual(64, fixture.Client.LastOptionsPatch!.MaximumUploadSpeed);
        UserInfo info = await sharing.Adapter.ResolveUserInfoAsync("peer", new IPEndPoint(IPAddress.Loopback, 1234));
        Assert.AreEqual("updated", info.Description);
        Assert.IsTrue(info.HasPicture);
        Assert.IsNull(sharing.Catalogs.CurrentMetadata);
        await sharing.ScanNowAsync();
        Assert.AreEqual(1, sharing.Catalogs.CurrentMetadata!.FileCount);

        // Description-only changes retain the loaded image even if its source
        // file is no longer readable, and do not republish unrelated state.
        File.Delete(picture);
        var appliedLimits = fixture.Client.LastOptionsPatch;
        int runtimeUpdates = 0;
        sharing.StateChanged += (_, _) => runtimeUpdates++;
        fixture.Write(runtimeConfig + "user-description = changed");
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        UserInfo changed = await sharing.Adapter.ResolveUserInfoAsync("peer", new IPEndPoint(IPAddress.Loopback, 1234));
        Assert.AreEqual("changed", changed.Description);
        CollectionAssert.AreEqual(info.Picture, changed.Picture);
        Assert.AreSame(appliedLimits, fixture.Client.LastOptionsPatch);
        Assert.AreEqual(0, runtimeUpdates);

        fixture.Write(""); // Remove roots and restore the default unlimited speed.
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        Assert.AreEqual(int.MaxValue, fixture.Client.LastOptionsPatch!.MaximumUploadSpeed);
        info = await sharing.Adapter.ResolveUserInfoAsync("peer", new IPEndPoint(IPAddress.Loopback, 1234));
        Assert.IsFalse(info.HasPicture);
        Assert.AreEqual("RescanRequired", sharing.GetSharingState().Reason);
        Assert.AreEqual(1, sharing.Catalogs.CurrentMetadata.FileCount);
        await sharing.ScanNowAsync();
        Assert.AreEqual(0, sharing.Catalogs.CurrentMetadata!.FileCount);
        Assert.AreEqual(DaemonFeatureState.Disabled, sharing.GetSharingState().State);

        fixture.Write("share-rescan-interval = 1h");
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        Assert.IsTrue(fixture.Supervisor.GetStatus().Configuration!.RestartRequired);
        fixture.Write("username = different\nverbose = true");
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        Assert.IsTrue(fixture.Supervisor.GetStatus().Configuration!.RestartRequired);
    }

    [TestMethod]
    public async Task ReloadPreservesOverridesRejectsInvalidEditsAndReportsRestart()
    {
        const string initial = "pref-format = mp3\nserver-port = 5030\nupload-blocked-user = blocked";
        await using var fixture = new Fixture(initial, ["--pref-min-bitrate", "192"]);
        await fixture.StartRuntimeAsync();
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        fixture.Restrictions.Policy.SetUsernameOverride(PeerRestrictionKind.UploadAccess,
            "override", PeerUsernameRestrictionOverride.Blocked);
        fixture.Write("pref-format = flac\npref-min-bitrate = 320\nserver-port = 5031\n" +
            "[lossless]\npref-format = wav\n[interactive]\nprofile-cond = interactive\npref-format = flac");
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        var settings = fixture.Settings();
        CollectionAssert.AreEqual(new[] { "flac" }, settings.Search!.PreferredCond!.Formats!.Replace!.ToArray());
        Assert.AreEqual(192, settings.Search.PreferredCond.MinBitrate);
        Assert.IsTrue(fixture.Supervisor.GetStatus().Configuration!.RestartRequired);
        Assert.AreEqual(2, fixture.Supervisor.Submissions.GetProfiles().Count);
        Assert.IsFalse(fixture.Restrictions.Policy.IsUploadUsernameBlocked("blocked"));
        Assert.IsTrue(fixture.Restrictions.Policy.IsUploadUsernameBlocked("override"));

        foreach (string bad in new[] { "pref-format = ogg\nserver-port = 70000", "[broken]\nprofile-cond = (album &&", "upload-blocked-ip = nonsense" })
        {
            fixture.Write(bad);
            Assert.IsFalse(await fixture.Watcher.ReloadAsync());
            Assert.IsTrue(fixture.Supervisor.GetStatus().Configuration!.ReloadFailed);
            Assert.IsFalse(string.IsNullOrWhiteSpace(fixture.Supervisor.GetStatus().Configuration!.Error));
            CollectionAssert.AreEqual(new[] { "flac" }, fixture.Settings().Search!.PreferredCond!.Formats!.Replace!.ToArray());
        }
        const string invalidValue = "blah";
        fixture.Write($"pref-format = ogg\nserver-port = {invalidValue}");
        Assert.IsFalse(await fixture.Watcher.ReloadAsync());
        var failure = fixture.Supervisor.GetStatus().Configuration!;
        Assert.AreEqual(2, failure.ErrorLine);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Error));
        Assert.IsTrue(failure.Error!.Contains(invalidValue, StringComparison.Ordinal));
        File.Delete(fixture.Path);
        Assert.AreEqual(System.IO.Path.GetDirectoryName(fixture.Path), ConfigManager.Load(fixture.Path).ConfigDir);
        Assert.IsFalse(await fixture.Watcher.ReloadAsync());
        Assert.IsNull(fixture.Supervisor.GetStatus().Configuration!.ErrorLine);
        fixture.Write(initial);
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        Assert.IsFalse(fixture.Supervisor.GetStatus().Configuration!.RestartRequired);
        Assert.IsFalse(fixture.Supervisor.GetStatus().Configuration!.ReloadFailed);
        CollectionAssert.AreEqual(new[] { "mp3" }, fixture.Settings().Search!.PreferredCond!.Formats!.Replace!.ToArray());
        fixture.Write("pref-format = bad\nbroken");
        Assert.IsFalse(await fixture.Watcher.ReloadAsync());
        fixture.Write(initial); // Reverting to the last applied text clears failure too.
        Assert.IsTrue(await fixture.Watcher.ReloadAsync());
        Assert.IsFalse(fixture.Supervisor.GetStatus().Configuration!.ReloadFailed);
    }

    [TestMethod]
    public async Task WatchesAtomicReplacementAndCanBeDisabledAtStartup()
    {
        await using (var fixture = new Fixture("pref-format = mp3", []))
        {
            var first = fixture.Log.NextReload();
            await fixture.Watcher.StartAsync(CancellationToken.None);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(fixture.Supervisor.GetStatus().Configuration!.Watching);
            var next = fixture.Log.NextReload();
            File.WriteAllText(fixture.Path + ".new", "pref-format = flac");
            File.Move(fixture.Path + ".new", fixture.Path, overwrite: true);
            await next.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "flac" }, fixture.Settings().Search!.PreferredCond!.Formats!.Replace!.ToArray());
        }
        foreach (string[] args in new[] { new[] { "--no-config-watch" }, Array.Empty<string>() })
        {
            await using var fixture = new Fixture((args.Length == 0 ? "no-config-watch = true\n" : "") + "pref-format = mp3", args);
            await fixture.Watcher.StartAsync(CancellationToken.None);
            await fixture.Watcher.ExecuteTask!;
            fixture.Write("pref-format = flac");
            Assert.IsFalse(fixture.Supervisor.GetStatus().Configuration!.Watching);
            CollectionAssert.AreEqual(new[] { "mp3" }, fixture.Settings().Search!.PreferredCond!.Formats!.Replace!.ToArray());
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sockseek-reload-{Guid.NewGuid():N}", "sockseek.conf");
        public EngineSupervisor Supervisor { get; }
        public PeerRestrictionCoordinator Restrictions { get; }
        public DaemonConfigWatcher Watcher { get; }
        public ServerOptions ServerOptions { get; }
        public ReloadLog Log { get; } = new();
        public MockSoulseekClient Client { get; } = new([]);
        private readonly CancellationTokenSource lifetime = new();
        private Task? runtime;

        public Fixture(string text, string[] args)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            Write(text);
            var file = ConfigManager.Load(Path);
            var invocation = ConfigManager.ParseInvocation(args);
            var (engine, _, _, daemon, _) = ConfigManager.BindAll(file, invocation);
            var configured = daemon.CreateServerOptions(engine, file, invocation);
            configured.ClientFactory = _ => Client;
            configured.Persistence.Enabled = false;
            configured.Persistence.DataDirectory = System.IO.Path.GetDirectoryName(Path)!;
            var options = Options.Create(configured);
            ServerOptions = options.Value;
            Restrictions = new(options, new PersistenceCoordinator(options), NullLogger<PeerRestrictionCoordinator>.Instance);
            Supervisor = new EngineSupervisor(options, null, null, retireTerminalWorkflows: true,
                peerRestrictions: Restrictions.Policy);
            Watcher = new(file, invocation, daemon.NoConfigWatch, Supervisor, Log);
        }
        public void Write(string text) => File.WriteAllText(Path, text);
        public async Task StartRuntimeAsync()
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime = Supervisor.RunAsync(lifetime.Token, () => ready.TrySetResult());
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public DownloadSettingsPatchDto Settings() => Supervisor.Submissions.ResolveEffectiveSettings(
            new(new GenericSearchJobDraftDto("fixture"),
                new SubmissionOptionsDto(ProfileContext: new Dictionary<string, bool> { ["interactive"] = false }))).Settings.Values;
        public async ValueTask DisposeAsync()
        {
            await Watcher.StopAsync(CancellationToken.None);
            Watcher.Dispose();
            lifetime.Cancel();
            if (runtime is not null)
            {
                try { await runtime; }
                catch (OperationCanceledException) { }
            }
            lifetime.Dispose();
            await Restrictions.DisposeAsync();
            Supervisor.Dispose();
            Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
        }
    }

    // Await the actual reload notification, never poll or sleep in watcher tests.
    private sealed class ReloadLog : ILogger<DaemonConfigWatcher>
    {
        private TaskCompletionSource? next;
        public Task NextReload()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref next, completion);
            return completion.Task;
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 5010) Interlocked.Exchange(ref next, null)?.TrySetResult();
        }
    }
}
