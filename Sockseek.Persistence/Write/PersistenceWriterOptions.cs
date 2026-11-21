namespace Sockseek.Persistence.Write;

public sealed record PersistenceWriterOptions
{
    public const int DefaultMaximumBatchSize = 2_048;
    // Admit the first results promptly, then amortize preparation/index writes
    // across bounded batches. Views read live Core facts independently; these
    // sizes control raw-history transactions, not projection publication.
    public const int InitialSearchResultFlushCount = 2_000;
    public const int DefaultSearchResultFlushCount = 10_000;
    // Only request commands wait for admission. History notifications describe
    // work already accepted by Core; their callbacks must never park execution.
    public int CommandQueueCapacity { get; init; } = 2_048;
    public int BackgroundCommandQueueCapacity { get; init; } = 2_048;
    public int ProgressEntityCapacity { get; init; } = 512;
    public int SearchMutationQueueCapacity { get; init; } = 256;
    public int SearchResultFlushCount { get; init; } = DefaultSearchResultFlushCount;
    public TimeSpan SearchResultFlushInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan TransferProgressFlushInterval { get; init; } = TimeSpan.FromSeconds(3);
    // Group durable history writes to avoid rewriting the same index pages in
    // many small commits. Gather up to the batch target. Sparse work stays immediate;
    // interactive commands run between batches. Search rows have their own bound.
    public int MaximumBatchSize { get; init; } = DefaultMaximumBatchSize;
    public int BusyRetryCount { get; init; } = 3;
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan FailureRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public int MaximumRecoveryAttempts { get; init; } = 60;
    public int MaximumFailureTextLength { get; init; } = 2_048;

    public void Validate()
    {
        if (CommandQueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(CommandQueueCapacity));
        if (BackgroundCommandQueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(BackgroundCommandQueueCapacity));
        if (ProgressEntityCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(ProgressEntityCapacity));
        if (SearchMutationQueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(SearchMutationQueueCapacity));
        if (SearchResultFlushCount <= 0) throw new ArgumentOutOfRangeException(nameof(SearchResultFlushCount));
        if (SearchResultFlushInterval < TimeSpan.FromMilliseconds(100)
            || SearchResultFlushInterval > TimeSpan.FromMilliseconds(250))
            throw new ArgumentOutOfRangeException(nameof(SearchResultFlushInterval));
        if (TransferProgressFlushInterval < TimeSpan.FromSeconds(2)
            || TransferProgressFlushInterval > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(TransferProgressFlushInterval));
        if (MaximumBatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumBatchSize));
        if (BusyRetryCount < 0) throw new ArgumentOutOfRangeException(nameof(BusyRetryCount));
        if (BusyRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(BusyRetryDelay));
        if (FailureRetryDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FailureRetryDelay));
        if (MaximumRecoveryAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumRecoveryAttempts));
        if (MaximumFailureTextLength <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumFailureTextLength));
    }
}
