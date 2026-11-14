using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
public class ConfigIsolationTests
{
    [TestMethod]
    [DoNotParallelize] // Exercise command output without leaking the generated secret to test logs.
    public async Task AuthenticationCommands_CreateListRevokeAndHashUsingOfflineConfiguration()
    {
        string directory = Directory.CreateTempSubdirectory("sockseek-auth-commands-").FullName;
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var diagnostics = new StringWriter();
        try
        {
            Console.SetError(diagnostics);
            async Task<string> Run(params string[] args)
            {
                using var output = new StringWriter();
                Console.SetOut(output);
                var result = await ConfiguredCommandDispatcher.TryRunAsync(["auth", .. args, "--no-config"]);
                Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.Success, result, diagnostics.ToString());
                return output.ToString().Trim();
            }

            string secret = await Run("keys", "create", "Laptop CLI", "--data-dir", directory);
            Assert.IsTrue(secret.StartsWith("ssk_", StringComparison.Ordinal));
            Assert.AreEqual(70, secret.Length);
            string listing = await Run("keys", "list", "--data-dir", directory);
            Assert.DoesNotContain(secret, listing);
            string id = listing.Split('\t')[0];
            Assert.IsTrue(Guid.TryParse(id, out _));
            await Run("keys", "revoke", id, "--data-dir", directory);
            Assert.AreEqual("", await Run("keys", "list", "--data-dir", directory));

            string file = Path.Combine(directory, "password");
            File.WriteAllText(file, " fixture password \n");
            string hash = await Run("hash-password", "--web-password-file", file);
            Assert.AreNotEqual(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed,
                new Microsoft.AspNetCore.Identity.PasswordHasher<string>()
                    .VerifyHashedPassword("operator", hash, " fixture password "));
            Assert.DoesNotContain(secret, diagnostics.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void AuthenticationConfiguration_SeparatesDaemonAndRemoteCredentialsAndPreservesSecrets()
    {
        string directory = Directory.CreateTempSubdirectory("sockseek-auth-config-").FullName;
        try
        {
            string password = Path.Combine(directory, "password");
            string key = Path.Combine(directory, "key");
            string config = Path.Combine(directory, "sockseek.conf");
            File.WriteAllText(password, "  private password  \r\n");
            File.WriteAllText(key, "fixture-api-key\n");
            File.WriteAllText(config, $"web-password-file = {password}\nweb-username = operator\n"
                + $"remote = https://sockseek.example\nremote-api-key-file = {key}\ntrusted-proxies = 192.0.2.1,192.0.2.2\n");
            var file = ConfigManager.Load(config);
            var invocation = ConfigManager.ParseInvocation(
                ["--web-session-lifetime", "3.00:00:00", "--trusted-proxies", "127.0.0.2,::1"]);
            var bound = ConfigManager.BindAll(file, invocation);
            var host = bound.Daemon.CreateServerOptions(bound.Engine, file, invocation);
            Assert.AreEqual("operator", bound.Daemon.WebUsername);
            Assert.AreEqual(TimeSpan.FromDays(3), bound.Daemon.WebSessionLifetime);
            CollectionAssert.AreEqual(new[] { "127.0.0.2", "::1" }, bound.Daemon.TrustedProxies);
            Assert.AreEqual("  private password  ", Sockseek.Server.Authentication.AuthenticationOptions.ReadSecretFile(password));
            Assert.AreEqual("operator", host.Authentication.Username);
            Assert.AreEqual(TimeSpan.FromDays(3), host.Authentication.SessionLifetime);
            CollectionAssert.AreEqual(new[] { "127.0.0.2", "::1" }, host.Authentication.TrustedProxies);
            Assert.IsNotNull(host.Authentication.PreparePassword());
            bound.Daemon.TrustedProxies[0] = "192.0.2.99";
            Assert.AreEqual("127.0.0.2", host.Authentication.TrustedProxies[0]);
            var cleared = ConfigManager.BindAll(file, ["--trusted-proxies", ""]);
            Assert.AreEqual(0, cleared.Daemon.CreateServerOptions(cleared.Engine, file, invocation).Authentication.TrustedProxies.Length);
            CollectionAssert.AreEqual(new[] { "127.0.0.1", "::1" }, new DaemonSettings().TrustedProxies);
            using var http = bound.Remote.CreateHttpClient();
            Assert.AreEqual(443, http.BaseAddress!.Port);
            Assert.AreEqual("fixture-api-key", http.DefaultRequestHeaders.GetValues("X-API-Key").Single());
            bound.Remote.ApiKey = "do-not-print-this-secret";
            var error = Assert.ThrowsExactly<ArgumentException>(() => bound.Remote.CreateHttpClient());
            Assert.DoesNotContain(bound.Remote.ApiKey, error.Message);
            File.WriteAllText(config, "[job]\nweb-password-hash = do-not-print-this-hash\n");
            error = Assert.ThrowsExactly<ArgumentException>(() => ConfigManager.Load(config));
            Assert.DoesNotContain("do-not-print-this-hash", error.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void ImplicitConfigLoading_StartsWithoutLocalProfiles()
    {
        var file = ConfigManager.Load();

        Assert.AreEqual("", ConfigManager.ExtractConfigPath(["input"]));
        Assert.AreEqual("", file.Path);
        Assert.AreEqual(0, file.Profiles.Count);
    }

    [TestMethod]
    public void ExplicitConfigLoading_StillUsesTheRequestedFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sockseek-config-isolation-{Guid.NewGuid():N}.conf");
        try
        {
            File.WriteAllText(path, "concurrent-searches = 7");

            var extracted = ConfigManager.ExtractConfigPath(["input", "--config", path]);
            var file = ConfigManager.Load(extracted);
            var (engine, _, _) = ConfigManager.Bind(file, ["input"]);

            Assert.AreEqual(path, Path.GetFullPath(extracted));
            Assert.AreEqual(path, Path.GetFullPath(file.Path));
            Assert.AreEqual(7, engine.ConcurrentSearches);
            Assert.AreEqual("none", ConfigManager.ExtractConfigPath(["--config", path, "--no-config"]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
