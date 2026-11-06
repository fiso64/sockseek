using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Soulseek;
using SlDirectory = Soulseek.Directory;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

public interface ISoulseekInboundRequestRouter
{
    bool TryUpdateExcludedSearchPhrases(IReadOnlyCollection<string> phrases);

    Task<SearchResponse?> ResolveSearchAsync(
        string username,
        int token,
        SearchQuery query);

    Task<BrowseResponse> ResolveBrowseAsync(
        string username,
        IPEndPoint endpoint);

    Task<IEnumerable<SlDirectory>> ResolveDirectoryAsync(
        string username,
        IPEndPoint endpoint,
        int token,
        string remotePath);

    Task<UserInfo> ResolveUserInfoAsync(
        string username,
        IPEndPoint endpoint);

    Task EnqueueUploadAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath);

    Task<int?> ResolvePlaceInQueueAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath);
}

/// <summary>
/// Safe callback boundary for all inbound sharing protocol operations.
/// </summary>
public sealed class SoulseekSharingAdapter : ISoulseekInboundRequestRouter, IDisposable
{
    public const int MaximumSearchTerms = 64;
    public const int MaximumSearchExclusions = 64;
    public const int MaximumSearchUtf8Bytes = 4_096;
    public const int MaximumExcludedPhraseCount = 256;
    public const int MaximumExcludedPhraseUtf8Bytes = 1_024;
    public const int MaximumExcludedPhraseSetUtf8Bytes = 64 * 1_024;
    public const int MaximumSearchCandidates = 2_000;
    public const int SearchResponseFileLimit = 500;
    public const int IncomingSearchConcurrency = 10;
    public const int IncomingSearchQueueCapacity = 500;
    public const int InboundUploadConcurrency = 10;
    public const int InboundUploadQueueCapacity = 500;
    public const int InboundDirectoryConcurrency = 10;
    public const int InboundDirectoryQueueCapacity = 500;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BrowseIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly IShareCatalogProvider catalogs;
    private readonly ILogger logger;
    private readonly RepeatedWarningGate warningGate = new();
    private readonly UploadCoordinator uploads;
    private readonly PeerRestrictionPolicy restrictions;
    private readonly UploadSettings uploadSettings;
    private readonly Func<ISoulseekClient?> clientProvider;
    private readonly string userDescription;
    private readonly byte[]? userPicture;
    private readonly bool uploadServingEnabled;
    private readonly SemaphoreSlim searchConcurrency =
        new(IncomingSearchConcurrency, IncomingSearchConcurrency);
    private readonly SemaphoreSlim browseConcurrency = new(10, 10);
    private readonly BoundedCallbackGate directoryGate =
        new(InboundDirectoryConcurrency, InboundDirectoryQueueCapacity);
    private readonly BoundedCallbackGate uploadGate =
        new(InboundUploadConcurrency, InboundUploadQueueCapacity);
    private string[] excludedPhrases = [];
    private int pendingSearches;
    private bool disposed;

    public SoulseekSharingAdapter(
        IShareCatalogProvider catalogs,
        UploadCoordinator uploads,
        PeerRestrictionPolicy restrictions,
        UploadSettings uploadSettings,
        Func<ISoulseekClient?> clientProvider,
        string? userDescription = null,
        bool uploadServingEnabled = true,
        byte[]? userPicture = null,
        ILogger? logger = null)
    {
        this.logger = logger ?? NullLogger.Instance;
        this.catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        this.uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        this.restrictions = restrictions ?? throw new ArgumentNullException(nameof(restrictions));
        this.uploadSettings = uploadSettings ?? throw new ArgumentNullException(nameof(uploadSettings));
        this.clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        this.userDescription = userDescription ?? "";
        this.userPicture = userPicture?.ToArray();
        this.uploadServingEnabled = uploadServingEnabled;
    }

    public bool TryUpdateExcludedSearchPhrases(IReadOnlyCollection<string> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        if (phrases.Count > MaximumExcludedPhraseCount)
            return false;

        int bytes = 0;
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string phraseValue in phrases)
        {
            if (phraseValue is null)
                return false;
            string phrase;
            try
            {
                phrase = phraseValue.Trim().Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                return false;
            }
            int phraseBytes = Encoding.UTF8.GetByteCount(phrase);
            if (phrase.Length == 0 || phraseBytes > MaximumExcludedPhraseUtf8Bytes)
                return false;
            bytes = checked(bytes + phraseBytes);
            if (bytes > MaximumExcludedPhraseSetUtf8Bytes)
                return false;
            normalized.Add(phrase);
        }

        Volatile.Write(
            ref excludedPhrases,
            normalized.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        return true;
    }

    public async Task<SearchResponse?> ResolveSearchAsync(
        string username,
        int token,
        SearchQuery query)
    {
        if (disposed
            || !uploadServingEnabled
            || !IsValidUsername(username)
            || !IsValidSearch(query)
            || restrictions.IsUploadUsernameBlocked(username))
        {
            return null;
        }
        if (Interlocked.Increment(ref pendingSearches)
            > IncomingSearchQueueCapacity)
        {
            Interlocked.Decrement(ref pendingSearches);
            SharingTelemetry.RecordDroppedRequest("search");
            return null;
        }

        bool entered = false;
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            await searchConcurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;

            if (!catalogs.TryAcquire(out IShareCatalogLease? lease) || lease is null)
                return null;
            await using (lease.ConfigureAwait(false))
            {
                const int responseLimit = SearchResponseFileLimit;
                int candidateLimit = Math.Min(
                    MaximumSearchCandidates,
                    checked(responseLimit * 4));
                IReadOnlyList<ShareCatalogFile> rows = await lease.Reader.SearchAsync(
                    query.Query,
                    query.Exclusions,
                    candidateLimit,
                    timeout.Token).ConfigureAwait(false);
                string[] exclusions = query.Exclusions
                    .Concat(Volatile.Read(ref excludedPhrases))
                    .ToArray();
                var files = rows
                    .Where(file => !ContainsExcluded(file.RemotePath, exclusions))
                    .Take(responseLimit)
                    .Select(ToSearchFile)
                    .ToArray();
                if (files.Length == 0)
                    return null;

                if (restrictions.Snapshot.HasUploadBlockedIpAddresses)
                {
                    ISoulseekClient? client = clientProvider();
                    if (client is null)
                        return null;
                    IPEndPoint endpoint = await client
                        .GetUserEndPointAsync(username, cancellationToken: timeout.Token)
                        .ConfigureAwait(false);
                    if (restrictions.IsUploadIpAddressBlocked(endpoint.Address))
                        return null;
                }

                UploadQueueRuntimeSnapshot capacity = uploads.GetQueueSnapshot();
                return new SearchResponse(
                    clientProvider()?.Username ?? "",
                    token,
                    uploadServingEnabled && uploads.CouldStartImmediately(username),
                    EffectiveUploadSpeedBytesPerSecond,
                    capacity.QueuedFiles,
                    files);
            }
        }
        catch (Exception exception)
        {
            ReportCallbackFailure("search", username, exception);
            return null;
        }
        finally
        {
            if (entered)
                searchConcurrency.Release();
            Interlocked.Decrement(ref pendingSearches);
        }
    }

    public Task<BrowseResponse> ResolveBrowseAsync(
        string username,
        IPEndPoint endpoint)
    {
        if (disposed
            || !IsValidUsername(username)
            || restrictions.IsUploadAccessBlocked(username, endpoint))
        {
            return Task.FromResult<BrowseResponse>(new BrowseResponse());
        }
        if (!browseConcurrency.Wait(0))
        {
            SharingTelemetry.RecordDroppedRequest("browse");
            return Task.FromResult<BrowseResponse>(new BrowseResponse());
        }

        IShareCatalogLease? lease = null;
        try
        {
            if (!catalogs.TryAcquire(out lease) || lease is null)
            {
                browseConcurrency.Release();
                return Task.FromResult<BrowseResponse>(new BrowseResponse());
            }

            ShareBrowseStream browse = lease.OpenBrowseStream(
                BrowseIdleTimeout,
                SafeReleaseBrowsePermit);
            lease = null; // ownership moved into the stream wrapper
            return Task.FromResult<BrowseResponse>(
                new RawBrowseResponse(browse.Length, browse.Stream));
        }
        catch (Exception exception)
        {
            ReportCallbackFailure("browse", username, exception);
            lease?.Dispose();
            browseConcurrency.Release();
            return Task.FromResult<BrowseResponse>(new BrowseResponse());
        }
    }

    public async Task<IEnumerable<SlDirectory>> ResolveDirectoryAsync(
        string username,
        IPEndPoint endpoint,
        int token,
        string remotePath)
    {
        if (disposed
            || !IsValidUsername(username)
            || restrictions.IsUploadAccessBlocked(username, endpoint)
            || !IsValidRemotePath(remotePath))
        {
            return [];
        }

        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            await using BoundedCallbackGate.Lease? gate =
                await directoryGate.TryEnterAsync(timeout.Token).ConfigureAwait(false);
            if (gate is null)
                return [];

            var key = RemotePathKey.Create(remotePath);
            if (!catalogs.TryAcquire(out IShareCatalogLease? lease) || lease is null)
                return [];
            await using (lease.ConfigureAwait(false))
            {
                ShareCatalogBrowseDirectory? directory =
                    await lease.Reader.GetDirectoryAsync(
                        key,
                        timeout.Token).ConfigureAwait(false);
                if (directory is null)
                    return [];
                return
                [
                    new SlDirectory(
                        directory.Directory.RemotePath,
                        directory.Files.Select(ToBrowseFile)),
                ];
            }
        }
        catch (Exception exception)
        {
            ReportCallbackFailure("directory", username, exception);
            return [];
        }
    }

    public Task<UserInfo> ResolveUserInfoAsync(
        string username,
        IPEndPoint endpoint)
    {
        if (disposed
            || !IsValidUsername(username)
            || restrictions.IsUploadAccessBlocked(username, endpoint))
        {
            return Task.FromResult(new UserInfo("", 0, 0, false));
        }
        if (!catalogs.TryAcquire(out IShareCatalogLease? lease) || lease is null)
            return Task.FromResult(new UserInfo(userDescription, 0, 0, false, userPicture));
        lease.Dispose();

        UploadQueueRuntimeSnapshot capacity = uploads.GetQueueSnapshot();
        return Task.FromResult(new UserInfo(
            userDescription,
            uploadServingEnabled ? capacity.TotalSlots : 0,
            capacity.QueuedFiles,
            uploadServingEnabled && uploads.CouldStartImmediately(username),
            userPicture));
    }

    public async Task EnqueueUploadAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath)
    {
        if (disposed)
            throw new DownloadEnqueueException("Pending shutdown.");
        if (!uploadServingEnabled
            || !IsValidUsername(username)
            || !IsValidRemotePath(remotePath)
            || restrictions.IsUploadAccessBlocked(username, endpoint))
        {
            throw new DownloadEnqueueException("File not shared.");
        }

        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            await using BoundedCallbackGate.Lease? global =
                await uploadGate.TryEnterAsync(timeout.Token).ConfigureAwait(false);
            if (global is null)
            {
                SharingTelemetry.RecordDroppedRequest("upload");
                throw new DownloadEnqueueException("Temporarily unavailable; try again later.");
            }

            string exactUsername = PeerUsername.Validate(username);

            UploadCoordinatorAdmission result = await uploads.AdmitAsync(
                exactUsername,
                endpoint,
                remotePath,
                timeout.Token).ConfigureAwait(false);
            if (result.Kind is UploadAdmissionResultKind.Accepted
                or UploadAdmissionResultKind.Duplicate)
            {
                return;
            }

            throw new DownloadEnqueueException(result.Rejection switch
            {
                UploadAdmissionRejection.Unavailable => "Temporarily unavailable; try again later.",
                _ => "File not shared.",
            });
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new DownloadEnqueueException("Temporarily unavailable; try again later.");
        }
    }

    public Task<int?> ResolvePlaceInQueueAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath)
    {
        if (disposed
            || !uploadServingEnabled
            || !IsValidUsername(username)
            || restrictions.IsUploadAccessBlocked(username, endpoint))
            return Task.FromResult<int?>(null);

        try
        {
            UploadQueueEstimate estimate = uploads.GetQueueEstimate(username, remotePath);
            return Task.FromResult(estimate.AheadCount);
        }
        catch
        {
            return Task.FromResult<int?>(null);
        }
    }

    private void ReportCallbackFailure(string operation, string username, Exception exception)
    {
        if (exception is OperationCanceledException) return;
        if (warningGate.TryAcquire(out long suppressed))
            SoulseekLogMessages.SharingCallbackFailed(logger, exception, operation, LogIdentity.PeerHash(username), suppressed);
    }

    private int EffectiveUploadSpeedBytesPerSecond
        => uploadSettings.SpeedLimitKiBPerSecond is { } kib
            ? Math.Min(uploads.MeasuredUploadSpeed, checked(kib * 1_024))
            : uploads.MeasuredUploadSpeed;

    private void SafeReleaseBrowsePermit()
    {
        try
        {
            browseConcurrency.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsValidSearch(SearchQuery? query)
        => query is not null
           && query.Terms.Count is > 0 and <= MaximumSearchTerms
           && query.Exclusions.Count <= MaximumSearchExclusions
           && Encoding.UTF8.GetByteCount(query.SearchText) <= MaximumSearchUtf8Bytes;

    private static bool IsValidUsername(string username)
    {
        try
        {
            return PeerUsername.Validate(username).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidRemotePath(string remotePath)
    {
        try
        {
            return RemotePathKey.Create(remotePath).Bytes.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ContainsExcluded(string remotePath, IEnumerable<string> exclusions)
        => exclusions.Any(exclusion =>
            remotePath.Contains(exclusion, StringComparison.OrdinalIgnoreCase));

    private static SlFile ToSearchFile(ShareCatalogFile file)
        => new(
            file.ProtocolCode,
            file.RemotePath,
            file.SizeBytes,
            file.Extension,
            file.Attributes.Select(
                attribute => new FileAttribute(
                    (FileAttributeType)attribute.Type,
                    attribute.Value)));

    private static SlFile ToBrowseFile(ShareCatalogFile file)
        => new(
            file.ProtocolCode,
            RemoteFileName(file.RemotePath),
            file.SizeBytes,
            file.Extension,
            file.Attributes.Select(
                attribute => new FileAttribute(
                    (FileAttributeType)attribute.Type,
                    attribute.Value)));

    private static string RemoteFileName(string remotePath)
    {
        int separator = remotePath.LastIndexOf('\\');
        return separator < 0 ? remotePath : remotePath[(separator + 1)..];
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        // In-flight callback and browse-stream owners can release after the
        // session begins shutting down. These bounded semaphores are therefore
        // left for GC instead of being disposed underneath those owners.
    }
}
