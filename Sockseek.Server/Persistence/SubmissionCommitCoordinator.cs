using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sockseek.Api;
using Sockseek.Persistence.Read;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Server.Persistence;

/// <summary>
/// Submission-owned single-flight and durable receipt lookup for mutation
/// retries. Client selection state never enters this owner.
/// </summary>
public sealed class SubmissionCommitCoordinator(
    PersistenceCoordinator persistence,
    TimeProvider? timeProvider = null,
    ILogger<SubmissionCommitCoordinator>? logger = null)
{
    private static readonly TimeSpan MemoryRetention = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<Guid, Entry> entries = [];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger<SubmissionCommitCoordinator> log = logger ?? NullLogger<SubmissionCommitCoordinator>.Instance;

    public async Task<T> ExecuteAsync<T>(
        Guid idempotencyKey,
        string fingerprint,
        Func<CancellationToken, Task<T>> submit,
        CancellationToken cancellationToken = default)
    {
        if (idempotencyKey == Guid.Empty)
            throw new ArgumentException("A non-empty idempotency key is required.", nameof(idempotencyKey));
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentNullException.ThrowIfNull(submit);

        EvictCompleted();
        var candidate = new Entry(
            fingerprint,
            clock.GetUtcNow(),
            new Lazy<Task<string>>(
                () => ExecuteCoreAsync(
                    idempotencyKey,
                    fingerprint,
                    submit),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Entry entry = entries.GetOrAdd(idempotencyKey, candidate);
        if (!StringComparer.Ordinal.Equals(entry.Fingerprint, fingerprint))
            throw new IdempotencyConflictException();

        Task<string> receiptTask = entry.Receipt.Value;
        try
        {
            string json = await receiptTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json, SockseekApiJson.CreateSerializerOptions())
                ?? throw new InvalidDataException("The retained submission receipt is invalid.");
        }
        catch
        {
            if (receiptTask.IsCompleted && !receiptTask.IsCompletedSuccessfully)
                entries.TryRemove(new KeyValuePair<Guid, Entry>(idempotencyKey, entry));
            throw;
        }
    }

    public static string Fingerprint(
        string scope,
        Guid resourceId,
        long revision,
        RefSelectionExpressionDto selection,
        IReadOnlyList<string>? excludedFileRefs = null,
        IReadOnlyList<string>? includedFileRefs = null,
        IReadOnlyDictionary<string, string>? chosenOptions = null,
        IReadOnlyDictionary<string, RefSelectionExpressionDto>? albumFileSelections = null,
        SubmissionOptionsDto? options = null,
        IReadOnlyList<JobPreviewSelectionOverrideDto>? subtreeOverrides = null,
        IReadOnlyDictionary<string, FileCandidateRefDto>? excludedFileIdentities = null,
        bool useRepresentativeWhenUnchosen = false,
        IReadOnlyList<SearchViewDirectoryPathRuleDto>? directoryPathRules = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(selection.Refs);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, scope);
        hash.AppendData(resourceId.ToByteArray());
        hash.AppendData(BitConverter.GetBytes(revision));
        Append(hash, selection.Mode.ToString());
        foreach (string itemRef in selection.Refs
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            Append(hash, itemRef);
        }
        if (subtreeOverrides is { Count: > 0 })
        {
            byte[] selectionHash = hash.GetHashAndReset();
            hash.AppendData(selectionHash);
            Append(hash, "preview-subtree-overrides");
            foreach (JobPreviewSelectionOverrideDto rule in subtreeOverrides.OrderBy(
                         rule => rule.Ref, StringComparer.Ordinal))
            {
                Append(hash, rule.Ref);
                Append(hash, rule.Selected ? "selected" : "excluded");
            }
        }
        if (directoryPathRules is { Count: > 0 })
        {
            byte[] selectionHash = hash.GetHashAndReset();
            hash.AppendData(selectionHash);
            Append(hash, "directory-path-rules");
            foreach (SearchViewDirectoryPathRuleDto rule in directoryPathRules
                         .OrderBy(rule => rule.DirectoryRef, StringComparer.Ordinal)
                         .ThenBy(rule => rule.RelativePath, StringComparer.Ordinal))
            {
                Append(hash, rule.DirectoryRef);
                Append(hash, rule.RelativePath);
                Append(hash, rule.Selected ? "selected" : "excluded");
            }
        }
        if (excludedFileRefs is { Count: > 0 })
        {
            byte[] selectionHash = hash.GetHashAndReset();
            hash.AppendData(selectionHash);
            Append(hash, "excluded-files");
            foreach (string itemRef in excludedFileRefs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                Append(hash, itemRef);
        }
        if (includedFileRefs is { Count: > 0 })
        {
            byte[] selectionHash = hash.GetHashAndReset();
            hash.AppendData(selectionHash);
            Append(hash, "included-files");
            foreach (string itemRef in includedFileRefs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                Append(hash, itemRef);
        }
        if (excludedFileIdentities is { Count: > 0 })
        {
            byte[] selectionHash = hash.GetHashAndReset();
            hash.AppendData(selectionHash);
            Append(hash, "excluded-file-identities");
            foreach (var entry in excludedFileIdentities.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Append(hash, entry.Key);
                Append(hash, entry.Value.Username);
                Append(hash, entry.Value.Filename);
            }
        }
        if (chosenOptions is { Count: > 0 })
        {
            byte[] selectionHash = hash.GetHashAndReset();
            hash.AppendData(selectionHash);
            Append(hash, "chosen-options");
            foreach (var option in chosenOptions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Append(hash, option.Key);
                Append(hash, option.Value);
            }
        }
        if (useRepresentativeWhenUnchosen)
        {
            byte[] selectionHash = hash.GetHashAndReset();
            hash.AppendData(selectionHash);
            Append(hash, "use-representative-when-unchosen");
        }
        if (albumFileSelections is { Count: > 0 })
        {
            byte[] selectionHash = hash.GetHashAndReset();
            hash.AppendData(selectionHash);
            Append(hash, "album-file-selections");
            foreach (var group in albumFileSelections.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Append(hash, group.Key);
                Append(hash, group.Value.Mode.ToString());
                foreach (string fileRef in group.Value.Refs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    Append(hash, fileRef);
            }
        }
        if (options != null)
        {
            byte[] selectionHash = hash.GetHashAndReset();
            hash.AppendData(selectionHash);
            Append(hash, "download-options");
            // Profile context is a map, not an ordered caller preference.
            var canonical = options with
            {
                ProfileContext = options.ProfileContext?
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            };
            Append(hash, JsonSerializer.Serialize(canonical));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string FingerprintDirectoryRetrieval(
        Guid viewId,
        long revision,
        PeerDirectoryRefDto directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "search-view-directory-retrieval");
        hash.AppendData(viewId.ToByteArray());
        hash.AppendData(BitConverter.GetBytes(revision));
        Append(hash, directory.Ref);
        Append(hash, directory.Username);
        Append(hash, directory.FolderPath);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<string> ExecuteCoreAsync<T>(
        Guid idempotencyKey,
        string fingerprint,
        Func<CancellationToken, Task<T>> submit)
    {
        var started = Stopwatch.StartNew();
        ISubmissionStore? store = persistence.Submissions;
        if (store != null)
        {
            PersistedSubmissionCommitReceipt? existing = await store.GetCommitReceiptAsync(
                idempotencyKey,
                CancellationToken.None).ConfigureAwait(false);
            if (existing != null)
            {
                if (!StringComparer.Ordinal.Equals(existing.CommitFingerprint, fingerprint))
                    throw new IdempotencyConflictException();
                return existing.CommitReceiptJson
                    ?? throw new InvalidOperationException(
                        "The submission was accepted but its commit receipt is not yet available.");
            }
        }

        long lookupMs = started.ElapsedMilliseconds;
        T receipt = await submit(CancellationToken.None)
            .ConfigureAwait(false);
        long receiptStartMs = started.ElapsedMilliseconds;
        string receiptJson = SerializeReceipt(receipt!);
        long durationMs = started.ElapsedMilliseconds;
        ServerLogMessages.SubmissionCommitCompleted(log, idempotencyKey, durationMs);
        ServerLogMessages.SubmissionCommitTiming(log, idempotencyKey,
            lookupMs, receiptStartMs - lookupMs, durationMs - receiptStartMs);
        return receiptJson;
    }

    internal static string SerializeReceipt(object receipt)
        => JsonSerializer.Serialize(receipt, receipt.GetType(), SockseekApiJson.CreateSerializerOptions());

    private void EvictCompleted()
    {
        DateTimeOffset cutoff = clock.GetUtcNow() - MemoryRetention;
        foreach ((Guid key, Entry entry) in entries)
        {
            if (entry.CreatedAtUtc < cutoff
                && (!entry.Receipt.IsValueCreated || entry.Receipt.Value.IsCompleted))
            {
                entries.TryRemove(new KeyValuePair<Guid, Entry>(key, entry));
            }
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private sealed record Entry(
        string Fingerprint,
        DateTimeOffset CreatedAtUtc,
        Lazy<Task<string>> Receipt);
}
