using System.Net;
using Soulseek;
using SlDirectory = Soulseek.Directory;

namespace Sockseek.Core.Services;

/// <summary>
/// Stable daemon-lifetime callback target whose active sharing implementation
/// can be attached before the lazily-created Soulseek client is configured.
/// </summary>
public sealed class SoulseekInboundRequestRouter : ISoulseekInboundRequestRouter
{
    private readonly object gate = new();
    private ISoulseekInboundRequestRouter? current;

    public IDisposable Attach(ISoulseekInboundRequestRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        lock (gate)
        {
            if (current is not null)
                throw new InvalidOperationException("A Soulseek inbound request router is already attached.");
            current = router;
        }
        return new Registration(this, router);
    }

    public bool TryUpdateExcludedSearchPhrases(IReadOnlyCollection<string> phrases)
        => Snapshot()?.TryUpdateExcludedSearchPhrases(phrases) ?? true;

    public Task<SearchResponse?> ResolveSearchAsync(string username, int token, SearchQuery query)
        => Snapshot()?.ResolveSearchAsync(username, token, query)
           ?? Task.FromResult<SearchResponse?>(null);

    public Task<BrowseResponse> ResolveBrowseAsync(string username, IPEndPoint endpoint)
        => Snapshot()?.ResolveBrowseAsync(username, endpoint)
           ?? Task.FromResult(new BrowseResponse([]));

    public Task<IEnumerable<SlDirectory>> ResolveDirectoryAsync(
        string username, IPEndPoint endpoint, int token, string remotePath)
        => Snapshot()?.ResolveDirectoryAsync(username, endpoint, token, remotePath)
           ?? Task.FromResult<IEnumerable<SlDirectory>>([]);

    public Task<UserInfo> ResolveUserInfoAsync(string username, IPEndPoint endpoint)
        => Snapshot()?.ResolveUserInfoAsync(username, endpoint)
           ?? Task.FromResult(new UserInfo(description: "", uploadSlots: 0, queueLength: 0, hasFreeUploadSlot: false));

    public Task EnqueueUploadAsync(string username, IPEndPoint endpoint, string remotePath)
        => Snapshot()?.EnqueueUploadAsync(username, endpoint, remotePath)
           ?? Task.FromException(new DownloadEnqueueException("Pending shutdown."));

    public Task<int?> ResolvePlaceInQueueAsync(string username, IPEndPoint endpoint, string remotePath)
        => Snapshot()?.ResolvePlaceInQueueAsync(username, endpoint, remotePath)
           ?? Task.FromResult<int?>(null);

    private ISoulseekInboundRequestRouter? Snapshot()
    {
        lock (gate)
            return current;
    }

    private void Detach(ISoulseekInboundRequestRouter router)
    {
        lock (gate)
        {
            if (ReferenceEquals(current, router))
                current = null;
        }
    }

    private sealed class Registration(
        SoulseekInboundRequestRouter owner,
        ISoulseekInboundRequestRouter router) : IDisposable
    {
        private SoulseekInboundRequestRouter? owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref owner, null)?.Detach(router);
    }
}
