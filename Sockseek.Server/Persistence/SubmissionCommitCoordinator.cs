using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Sockseek.Api;
using Sockseek.Persistence.Read;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Server.Persistence;

public sealed class IdempotencyConflictException : InvalidOperationException;

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
    private long nextSweepTicks;

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

    private async Task<string> ExecuteCoreAsync<T>(
        Guid idempotencyKey,
        string fingerprint,
        Func<CancellationToken, Task<T>> submit)
    {
        var started = Stopwatch.StartNew();
        ISubmissionStore? store = persistence.Submissions;
        bool durablyAccepted = false;
        try
        {
            if (store != null)
            {
                PersistedSubmissionCommitReceipt? existing = await store.GetCommitReceiptAsync(
                    idempotencyKey,
                    CancellationToken.None).ConfigureAwait(false);
                if (existing != null)
                {
                    if (!StringComparer.Ordinal.Equals(existing.CommitFingerprint, fingerprint))
                        throw new IdempotencyConflictException();
                    durablyAccepted = true;
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
            // A valid print-only or empty selection has no durable submission.
            // Confirm ownership before dropping the single-flight receipt.
            durablyAccepted = store != null && await store.GetCommitReceiptAsync(
                idempotencyKey, CancellationToken.None).ConfigureAwait(false) != null;
            long durationMs = started.ElapsedMilliseconds;
            ServerLogMessages.SubmissionCommitCompleted(log, idempotencyKey, durationMs);
            ServerLogMessages.SubmissionCommitTiming(log, idempotencyKey,
                lookupMs, receiptStartMs - lookupMs, durationMs - receiptStartMs);
            return receiptJson;
        }
        finally
        {
            // Durable acceptance owns ordinary receipts; transient responses use
            // this owner's existing bounded lifetime, including disconnected callers.
            if (durablyAccepted) entries.TryRemove(idempotencyKey, out _);
        }
    }

    internal static string SerializeReceipt(object receipt)
        => JsonSerializer.Serialize(receipt, receipt.GetType(), SockseekApiJson.CreateSerializerOptions());

    private void EvictCompleted()
    {
        DateTimeOffset now = clock.GetUtcNow();
        long previous = Volatile.Read(ref nextSweepTicks);
        if (now.UtcTicks < previous || Interlocked.CompareExchange(ref nextSweepTicks,
            (now + TimeSpan.FromMinutes(1)).UtcTicks, previous) != previous) return;
        DateTimeOffset cutoff = now - MemoryRetention;
        foreach ((Guid key, Entry entry) in entries)
        {
            if (entry.CreatedAtUtc < cutoff
                && (!entry.Receipt.IsValueCreated || entry.Receipt.Value.IsCompleted))
            {
                entries.TryRemove(new KeyValuePair<Guid, Entry>(key, entry));
            }
        }
    }

    private sealed record Entry(
        string Fingerprint,
        DateTimeOffset CreatedAtUtc,
        Lazy<Task<string>> Receipt);
}

// Features define the canonical request. The shared owner only supplies hashing
// and normalization of common submission inputs, never feature selection policy.
internal static class SubmissionFingerprint
{
    internal static string Create<T>(string scope, T request)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Scope = scope, Request = request })));

    internal static RefSelectionExpressionDto Selection(RefSelectionExpressionDto selection)
        => selection with { Refs = Refs(selection.Refs) };

    internal static string[] Refs(IReadOnlyList<string>? refs)
        => refs?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];

    internal static Dictionary<string, T> Map<T>(IReadOnlyDictionary<string, T>? map)
        => map?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? [];

    internal static SubmissionOptionsDto? Options(SubmissionOptionsDto? options)
        => options is null ? null : options with { ProfileContext = Map(options.ProfileContext) };
}
