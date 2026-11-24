using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Soulseek;
using Sockseek.Core.UserProfiles;

namespace Sockseek.Server;

/// <summary>Daemon-lifetime owner of the one shared Soulseek session.</summary>
public sealed class DaemonSoulseekRuntime : IAsyncDisposable
{
    private readonly EngineSettings settings;
    private readonly ILogger? logger;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object startupGate = new();
    private Task? startupTask;
    private string? profilePicturePath;

    public DaemonSoulseekRuntime(
        EngineSettings settings,
        Func<EngineSettings, ISoulseekClient>? clientFactory = null,
        LocalUserProfile? localProfile = null,
        ILogger<SoulseekClientManager>? logger = null,
        PeerRestrictionPolicy? restrictions = null,
        Func<ISoulseekClient, ISoulseekClient>? clientDecorator = null)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.logger = logger;
        InboundRequests = new SoulseekInboundRequestRouter();
        Restrictions = restrictions ?? new PeerRestrictionPolicy(settings.PeerRestrictions);
        LocalProfile = localProfile ?? new LocalUserProfile(
            UserProfileText.NormalizeDescription(settings.UserDescription),
            null);
        profilePicturePath = settings.UserPicturePath;
        ClientManager = new SoulseekClientManager(
            settings,
            clientFactory?.Invoke(settings),
            InboundRequests,
            LocalProfile,
            logger,
            clientDecorator);
    }

    public SoulseekClientManager ClientManager { get; }
    public SoulseekInboundRequestRouter InboundRequests { get; }
    public PeerRestrictionPolicy Restrictions { get; }
    public LocalUserProfile LocalProfile { get; private set; }

    public async Task<bool> ReloadProfileAsync(EngineSettings candidate, CancellationToken cancellationToken = default)
    {
        var profile = await LocalUserProfile.LoadAsync(candidate, cancellationToken, logger).ConfigureAwait(false);
        if (profile.Picture == null && candidate.UserPicturePath != null && candidate.UserPicturePath == profilePicturePath)
            profile = profile with { Picture = LocalProfile.Picture };
        profilePicturePath = candidate.UserPicturePath;
        if (LocalProfile.Description == profile.Description && LocalProfile.Picture?.ETag == profile.Picture?.ETag)
            return false;
        LocalProfile = profile;
        return true;
    }

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        lock (startupGate)
        {
            if (startupTask is { IsFaulted: true } or { IsCanceled: true })
                startupTask = null;
            if (startupTask?.IsCompleted == true
                && !ClientManager.IsConnectedAndLoggedIn)
            {
                startupTask = null;
            }
            return startupTask ??= StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        await ClientManager.EnsureConnectedAndLoggedInAsync(
            settings,
            linked.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        Task? pending;
        lock (startupGate)
            pending = startupTask;
        if (pending is not null)
        {
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* The observed startup failure was already published/logged. */ }
        }
        await ClientManager.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}
