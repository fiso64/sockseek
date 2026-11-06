using Sockseek.Api;
using Sockseek.Persistence.Authentication;
using Sockseek.Server;
using Sockseek.Server.Authentication;

namespace Sockseek.Cli;

internal static class AuthenticationCommandRunner
{
    public static async Task<Program.CliExitCode> RunAsync(ConfiguredCommandInvocation invocation, CancellationToken ct)
    {
        var args = invocation.CommandArguments.Skip(1).ToArray();
        if (args.Length == 0 || args.Any(x => x is "--help" or "-h"))
        {
            Console.WriteLine("sockseek auth hash-password [--web-password-file <path>]\n"
                + "sockseek auth keys <list|create <name>|revoke <id>> [--data-dir <path>]\n"
                + "Add --remote <url> --remote-api-key-file <path> to manage a running daemon.\n"
                + "Offline key management requires the daemon to be stopped.");
            return Program.CliExitCode.Success;
        }
        if (args is ["hash-password"])
        {
            string password = invocation.Daemon.WebPasswordFile is { } file
                ? AuthenticationOptions.ReadSecretFile(file) : ReadPassword();
            Console.WriteLine(AuthenticationOptions.HashPassword(password));
            return Program.CliExitCode.Success;
        }
        if (args is not ["keys", ..]) throw new ArgumentException("Expected 'auth hash-password' or 'auth keys'.");
        if (invocation.Remote.IsEnabled)
        {
            using var http = invocation.Remote.CreateHttpClient();
            var api = new SockseekApiClient(http);
            return await ManageAsync(args[1..],
                api.GetApiKeysAsync,
                api.CreateApiKeyAsync,
                api.RevokeApiKeyAsync, ct);
        }
        string path = SockseekDataPaths.ResolveDatabasePath(invocation.Daemon.DataDirectory);
        return await ApiKeyStore.OfflineAsync(path, async (store, token) =>
            await ManageAsync(args[1..],
                async t => (await store.ReadAllAsync(t)).Select(ToDto).ToArray(),
                async (name, t) =>
                {
                    var created = await store.CreateAsync(name, DateTimeOffset.UtcNow, t);
                    return new CreatedApiKeyDto(ToDto(created.Record), created.Secret);
                },
                async (id, t) =>
                {
                    if (!await store.RevokeAsync(id, t)) throw new ArgumentException("API key not found.");
                }, token), ct);
    }

    private static async Task<Program.CliExitCode> ManageAsync(string[] args,
        Func<CancellationToken, Task<IReadOnlyList<ApiKeyDto>>> list,
        Func<string, CancellationToken, Task<CreatedApiKeyDto>> create,
        Func<Guid, CancellationToken, Task> revoke, CancellationToken ct)
    {
        switch (args)
        {
            case ["list"]:
                foreach (var key in await list(ct)) Console.WriteLine($"{key.Id}\t{key.CreatedAt:O}\t{key.Name}");
                break;
            case ["create", var name]:
                var created = await create(name, ct);
                Console.Error.WriteLine($"Created key {created.Key.Id}. Save the secret now; it cannot be retrieved later.");
                Console.WriteLine(created.Secret);
                break;
            case ["revoke", var id] when Guid.TryParse(id, out var parsed):
                await revoke(parsed, ct);
                Console.WriteLine($"Revoked key {parsed}.");
                break;
            default: throw new ArgumentException("Expected 'auth keys list', 'auth keys create <name>' or 'auth keys revoke <id>'.");
        }
        return Program.CliExitCode.Success;
    }

    private static ApiKeyDto ToDto(StoredApiKey key) => new(key.Id, key.Name, key.CreatedAt);

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        Console.Error.Write("WebUI password: ");
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
            else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return password.ToString();
    }
}
