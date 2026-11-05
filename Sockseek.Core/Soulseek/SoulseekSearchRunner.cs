using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Models;
using Soulseek;

namespace Sockseek.Core.Services;

internal static class SoulseekSearchRunner
{
    public static async Task RunAsync(ISoulseekClient client, SearchQuery query, SearchOptions options,
        Action<SearchResponse> responseHandler, CancellationToken ct, ILogger? logger = null)
    {
        // Soulseek invokes callbacks while holding its search read lock. Keep that
        // callback cheap and drain accepted responses before exposing completion.
        var responses = Channel.CreateUnbounded<SearchResponse>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        using var searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var processing = Task.Run(async () =>
        {
            try
            {
                await foreach (var response in responses.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    var validResponse = ValidateResponse(response, logger);
                    if (validResponse != null)
                        responseHandler(validResponse);
                }
            }
            catch
            {
                // Stop the network search if application result processing fails.
                // Awaiting this task below preserves that original error.
                await searchCancellation.CancelAsync().ConfigureAwait(false);
                throw;
            }
        });

        var lastState = SearchStates.None;
        var observedOptions = new SearchOptions(
            searchTimeout: options.SearchTimeout,
            responseLimit: options.ResponseLimit,
            filterResponses: options.FilterResponses,
            minimumResponseFileCount: options.MinimumResponseFileCount,
            maximumPeerQueueLength: options.MaximumPeerQueueLength,
            minimumPeerUploadSpeed: options.MinimumPeerUploadSpeed,
            fileLimit: options.FileLimit,
            removeSingleCharacterSearchTerms: options.RemoveSingleCharacterSearchTerms,
            responseFilter: options.ResponseFilter,
            fileFilter: options.FileFilter,
            stateChanged: state =>
            {
                options.StateChanged?.Invoke(state);
                lastState = state.Search.State;
            },
            responseReceived: options.ResponseReceived);

        try
        {
            try
            {
                await client.SearchAsync(query, response => responses.Writer.TryWrite(response),
                    options: observedOptions, cancellationToken: searchCancellation.Token).ConfigureAwait(false);
            }
            catch (SynchronizationLockException error) when (IsSearchDisposalFailure(error))
            {
                // Soulseek.NET 10.0.2 removes a completed search then disposes its
                // ReaderWriterLockSlim without draining other completion waiters.
                // Its finally can mask success OR cancellation/errors (issue #214).
                ct.ThrowIfCancellationRequested();
                if (!lastState.HasFlag(SearchStates.Completed)
                    || (lastState & (SearchStates.Cancelled | SearchStates.Errored)) != 0)
                    throw;

                if (logger != null)
                    SoulseekLogMessages.SearchDisposalFailureIgnored(logger);
            }
        }
        finally
        {
            responses.Writer.TryComplete();
            await processing.ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    private static bool IsSearchDisposalFailure(SynchronizationLockException error)
        => error.TargetSite?.DeclaringType == typeof(ReaderWriterLockSlim)
            && error.StackTrace?.Contains("Soulseek.SearchInternal.Dispose", StringComparison.Ordinal) == true;

    private static SearchResponse? ValidateResponse(SearchResponse response, ILogger? logger)
    {
        // Reject malformed wire facts before raw storage and fast-search conversion.
        // Do not let one bad file poison a whole response or a later projection.
        try { PeerIdentityValidator.ValidateUsername(response.Username); }
        catch (ArgumentException)
        {
            if (logger != null)
                SoulseekLogMessages.InvalidSearchResponse(logger, response.Token, true, response.Files.Count + response.LockedFiles.Count);
            return null;
        }

        var files = ValidFiles(response.Files);
        var lockedFiles = ValidFiles(response.LockedFiles);
        if (ReferenceEquals(files, response.Files) && ReferenceEquals(lockedFiles, response.LockedFiles))
            return response;

        if (logger != null)
            SoulseekLogMessages.InvalidSearchResponse(logger, response.Token, false,
                response.Files.Count + response.LockedFiles.Count - files.Count - lockedFiles.Count);
        return new SearchResponse(response.Username, response.Token, response.HasFreeUploadSlot,
            response.UploadSpeed, response.QueueLength, files, lockedFiles);
    }

    private static IReadOnlyCollection<Soulseek.File> ValidFiles(IReadOnlyCollection<Soulseek.File> files)
    {
        List<Soulseek.File>? valid = null;
        int index = 0;
        foreach (var file in files)
        {
            bool accepted = file.Size >= 0;
            if (accepted)
            {
                try { PeerIdentityValidator.ValidateRemotePath(file.Filename); }
                catch (ArgumentException) { accepted = false; }
            }
            if (accepted)
                valid?.Add(file);
            else
                valid ??= files.Take(index).ToList();
            index++;
        }
        return valid ?? files;
    }
}
