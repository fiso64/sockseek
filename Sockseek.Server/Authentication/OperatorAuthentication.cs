using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Sockseek.Api;
using Sockseek.Persistence.Authentication;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Authentication;

/// <summary>
/// Owns the current credentials, browser tickets and their live-connection revocation.
/// Key mutations are serialized through durable publication; readers never query SQLite.
/// </summary>
public sealed class OperatorAuthentication(
    IOptions<ServerOptions> server,
    PersistenceCoordinator persistence,
    TimeProvider clock,
    ILogger<OperatorAuthentication> logger) : BackgroundService, ITicketStore
{
    public const string CredentialClaim = "sockseek:credential";
    private readonly AuthenticationOptions options = server.Value.Authentication;
    private readonly object gate = new();
    private readonly SemaphoreSlim keyChanges = new(1, 1);
    private readonly Dictionary<string, Credential> credentials = new(StringComparer.Ordinal);
    private string? passwordHash;
    public bool KeyStoreAvailable => persistence.ApiKeys is not null;

    private sealed class Credential(AuthenticationTicket? ticket = null, StoredApiKey? key = null)
    {
        public AuthenticationTicket? Ticket { get; } = ticket;
        public StoredApiKey? Key { get; } = key;
        public CancellationTokenSource Revoked { get; } = new();
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        passwordHash = options.PreparePassword();
        if (persistence.ApiKeys is { } store)
        {
            var keys = await store.ReadAllAsync(cancellationToken);
            lock (gate)
                foreach (var key in keys) credentials.Add(KeyId(key.Id), new(key: key));
        }
        if (!options.Enabled)
            ServerLogMessages.AuthenticationDisabled(logger);
        else
            ServerLogMessages.AuthenticationEnabled(logger, options.Username);
        if (options.AllowHttp)
            ServerLogMessages.HttpAuthenticationAllowed(logger);
        await base.StartAsync(cancellationToken);
    }

    public bool VerifyPassword(string username, string password)
    {
        if (passwordHash is null) return false;
        bool nameMatches = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(username)),
                SHA256.HashData(Encoding.UTF8.GetBytes(options.Username)));
        bool passwordMatches = new PasswordHasher<string>().VerifyHashedPassword("operator", passwordHash, password)
            != PasswordVerificationResult.Failed;
        return nameMatches & passwordMatches;
    }

    public ClaimsPrincipal? AuthenticateKey(string secret)
    {
        if (!ApiKeyStore.TryGetId(secret, out Guid id)) return null;
        lock (gate)
            return credentials.TryGetValue(KeyId(id), out var item) && item.Key is { } key
                && ApiKeyStore.Verify(secret, key)
                    ? Principal(key.Name, AuthSetup.ApiKeyScheme, KeyId(id)) : null;
    }

    public static ClaimsPrincipal Principal(string name, string scheme, string? credential = null)
    {
        var identity = new ClaimsIdentity(scheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        if (credential is not null) identity.AddClaim(new Claim(CredentialClaim, credential));
        return new(identity);
    }

    public IReadOnlyList<ApiKeyDto> ListKeys()
    {
        RequireStore();
        lock (gate)
            return credentials.Values.Where(x => x.Key is not null)
                .Select(x => ToDto(x.Key!)).OrderBy(x => x.CreatedAt).ToArray();
    }

    public async Task<CreatedApiKeyDto> CreateKeyAsync(string name, CancellationToken ct)
    {
        var store = RequireStore();
        await keyChanges.WaitAsync(ct);
        try
        {
            var created = await store.CreateAsync(name, clock.GetUtcNow(), ct);
            lock (gate) credentials.Add(KeyId(created.Record.Id), new(key: created.Record));
            ServerLogMessages.ApiKeyCreated(logger, created.Record.Id);
            return new(ToDto(created.Record), created.Secret);
        }
        finally { keyChanges.Release(); }
    }

    public async Task<bool> RevokeKeyAsync(Guid id, CancellationToken ct)
    {
        var store = RequireStore();
        await keyChanges.WaitAsync(ct);
        try
        {
            bool removed = await store.RevokeAsync(id, ct);
            Revoke(KeyId(id));
            if (removed) ServerLogMessages.ApiKeyRevoked(logger, id);
            return removed;
        }
        finally { keyChanges.Release(); }
    }

    // Registration under the same gate as removal closes the authenticate/connect/revoke race.
    internal CancellationTokenRegistration ObserveRevocation(ClaimsPrincipal user, Action disconnect)
    {
        if (!options.Enabled) return default;
        string? id = user.FindFirstValue(CredentialClaim);
        lock (gate)
        {
            if (id is not null && credentials.TryGetValue(id, out var item)
                && (item.Ticket is null || item.Ticket.Properties.ExpiresUtc > clock.GetUtcNow()))
                return item.Revoked.Token.Register(disconnect);
        }
        disconnect();
        throw new UnauthorizedAccessException("The authentication credential is no longer valid.");
    }

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        string id = "session:" + Guid.NewGuid().ToString("N");
        ((ClaimsIdentity)ticket.Principal.Identity!).AddClaim(new Claim(CredentialClaim, id));
        lock (gate) credentials.Add(id, new(ticket: ticket));
        return Task.FromResult(id);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
        => throw new InvalidOperationException("Browser sessions have a fixed lifetime and cannot be renewed.");

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        lock (gate)
            if (credentials.TryGetValue(key, out var item)
                && item.Ticket?.Properties.ExpiresUtc > clock.GetUtcNow())
                return Task.FromResult<AuthenticationTicket?>(item.Ticket);
        Revoke(key);
        return Task.FromResult<AuthenticationTicket?>(null);
    }

    public Task RemoveAsync(string key)
    {
        Revoke(key);
        return Task.CompletedTask;
    }

    private void Revoke(string id)
    {
        Credential? item;
        lock (gate) credentials.Remove(id, out item);
        if (item is null) return;
        // Callbacks abort hubs outside the owner lock; registration/disposal cannot deadlock it.
        item.Revoked.Cancel();
        item.Revoked.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            string[] expired;
            lock (gate) expired = credentials.Where(x => x.Value.Ticket is { } ticket
                && ticket.Properties.ExpiresUtc <= clock.GetUtcNow()).Select(x => x.Key).ToArray();
            foreach (string id in expired) Revoke(id);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        string[] ids;
        lock (gate) ids = credentials.Keys.ToArray();
        foreach (string id in ids) Revoke(id);
        keyChanges.Dispose();
    }

    private ApiKeyStore RequireStore() => persistence.ApiKeys
        ?? throw new InvalidOperationException("API-key management requires daemon persistence.");
    private static string KeyId(Guid id) => "key:" + id.ToString("N");
    private static ApiKeyDto ToDto(StoredApiKey key) => new(key.Id, key.Name, key.CreatedAt);
}
