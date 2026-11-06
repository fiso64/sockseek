using Sockseek.Core.Services;
using Soulseek;

namespace Sockseek.Core.PeerBrowsing;

/// <summary>
/// Acquires a materialized Soulseek.NET browse response and writes its public and
/// locked rows to Sockseek's artifact sink.
/// </summary>
public sealed class SoulseekPeerBrowseTransport(
    SoulseekClientManager clientManager,
    Func<CancellationToken, Task>? ensureSessionStarted = null) : IPeerBrowseTransport
{
    private readonly Func<CancellationToken, Task> ensureSessionStarted = ensureSessionStarted
        ?? clientManager.WaitUntilReadyAsync;

    public async Task ReceiveAsync(
        string username,
        IPeerBrowseRowSink sink,
        Action<PeerBrowseTransportProgress>? transportProgress = null,
        Action<PeerBrowseIndexProgress>? indexProgress = null,
        CancellationToken cancellationToken = default)
    {
        username = Models.PeerIdentityValidator.ValidateUsername(username);
        ArgumentNullException.ThrowIfNull(sink);
        await ensureSessionStarted(cancellationToken).ConfigureAwait(false);
        await clientManager.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        ISoulseekClient client = clientManager.Client
            ?? throw new InvalidOperationException("Soulseek is not connected.");

        var options = new BrowseOptions(progressUpdated: progress =>
            transportProgress?.Invoke(new PeerBrowseTransportProgress(
                progress.BytesTransferred,
                progress.Size >= 0 ? progress.Size : null)));

        // Soulseek.NET 10.0.2 materializes the complete response. This deliberately
        // matches slskd's current behavior and may cause a large temporary memory
        // spike for peers with enormous shares.
        // TODO: Monitor upstream Soulseek.NET and switch this boundary to its public
        // streaming browse API once one is available, without carrying a local fork.
        BrowseResponse response = await client.BrowseAsync(
            username,
            options,
            cancellationToken).ConfigureAwait(false);

        long directories = 0;
        long files = 0;
        long bytes = 0;
        indexProgress?.Invoke(new PeerBrowseIndexProgress(directories, files, bytes));
        await WriteDirectoriesAsync(
            response.Directories,
            PeerShareVisibility.Public).ConfigureAwait(false);
        await WriteDirectoriesAsync(
            response.LockedDirectories,
            PeerShareVisibility.Locked).ConfigureAwait(false);
        await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);

        async Task WriteDirectoriesAsync(
            IReadOnlyCollection<Soulseek.Directory> browseDirectories,
            PeerShareVisibility visibility)
        {
            foreach (Soulseek.Directory directory in browseDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await sink.BeginDirectoryAsync(
                    directory.Name,
                    visibility,
                    directory.Files.Count,
                    cancellationToken).ConfigureAwait(false);
                directories = checked(directories + 1);
                foreach (Soulseek.File file in directory.Files)
                {
                    IReadOnlyCollection<FileAttribute> attributes = file.Attributes ?? [];
                    await sink.BeginFileAsync(
                        new PeerBrowseWireFile(
                            file.Code,
                            file.Filename,
                            file.Size,
                            file.Extension,
                            attributes.Count),
                        cancellationToken).ConfigureAwait(false);
                    foreach (FileAttribute attribute in attributes)
                    {
                        await sink.AddAttributeAsync(
                            new PeerBrowseWireAttribute((int)attribute.Type, attribute.Value),
                            cancellationToken).ConfigureAwait(false);
                    }
                    await sink.EndFileAsync(cancellationToken).ConfigureAwait(false);
                    files = checked(files + 1);
                    bytes = SaturatingAdd(bytes, Math.Max(0, file.Size));
                }
                indexProgress?.Invoke(new PeerBrowseIndexProgress(directories, files, bytes));
            }
        }
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;
}
