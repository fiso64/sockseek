using Sockseek.Core.Events;
using Sockseek.Core.Models;
using Soulseek;

namespace Sockseek.Core.Services;

public sealed class SearchSession
{
    private readonly object admissionGate = new();
    private readonly object publicationGate = new();
    private readonly Queue<CoreChange?> publications = [];
    private bool publishing;
    private readonly List<SearchProjectionInput> rawResults = [];
    private readonly HashSet<(PeerPathKey Path, SearchResultVisibility Visibility)> resultKeys = [];
    private readonly HashSet<string> observedPeers = new(StringComparer.Ordinal);
    private TimeProvider timeProvider;
    private int _revision;
    private int _resultCount;
    private int _lockedFileCount;
    private long _sequence;
    private int _isComplete;

    public Guid JobId { get; }
    public string QueryText { get; }
    public int ResultCount => Volatile.Read(ref _resultCount);
    public int PublicFileCount => ResultCount;
    public int Revision => Volatile.Read(ref _revision);
    public int LockedFileCount => Volatile.Read(ref _lockedFileCount);
    public int ObservedPeerCount { get { lock (admissionGate) return observedPeers.Count; } }
    public bool IsComplete => Volatile.Read(ref _isComplete) != 0;

    public event Action<SearchResultsAddedChange>? ResultsAdded;
    public event Action<SearchCompletedChange>? SearchCompleted;
    public event Action<CoreChange>? ChangePublished;
    internal event Action<string, Exception>? ObserverFailed;
    internal event Action<SearchSession>? ResponseAdded;

    public SearchSession()
        : this(Guid.Empty)
    {
    }

    public SearchSession(Guid jobId, TimeProvider? timeProvider = null, string? queryText = null)
    {
        JobId = jobId;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        QueryText = queryText ?? "";
    }

    internal void ConfigureTimeProvider(TimeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (admissionGate)
        {
            if (_revision != 0 || _isComplete != 0)
                throw new InvalidOperationException("The search clock must be configured before results or completion.");
            timeProvider = provider;
        }
    }

    public IReadOnlyList<SearchProjectionInput> RawSnapshot(long afterSequence = 0, int limit = int.MaxValue)
        => ReadProjectionPage(afterSequence, limit).Items;

    // Metadata and this bounded prefix are captured under the same admission
    // lock, so an arriving completion cannot finish a page that missed its rows.
    public (IReadOnlyList<SearchProjectionInput> Items, int Revision, bool IsComplete) ReadProjectionPage(long afterSequence, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (admissionGate)
        {
            int start = (int)Math.Clamp(afterSequence, 0, rawResults.Count);
            int count = Math.Min(limit, rawResults.Count - start);
            return (rawResults.GetRange(start, count), _revision, _isComplete != 0 && start + count == rawResults.Count);
        }
    }

    internal void AddResponse(SearchResponse response)
    {
        // Serialize result/completion notifications, including slow consumers,
        // independently of the short lock protecting readable session state.
        lock (publicationGate)
        {
            var added = new List<SearchProjectionInput>();
            int revision;
            lock (admissionGate)
            {
                if (_isComplete != 0)
                    return;
                if (response.Files.Count == 0 && response.LockedFiles.Count == 0)
                    return;

                revision = _revision;
                // Use the same millisecond precision in live and retained facts.
                DateTimeOffset observedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                AddFiles(response.Files, SearchResultVisibility.Public);
                AddFiles(response.LockedFiles, SearchResultVisibility.Locked);

                void AddFiles(
                    IReadOnlyCollection<Soulseek.File> files,
                    SearchResultVisibility visibility)
                {
                    foreach (var file in files)
                    {
                        if (!resultKeys.Add((new PeerPathKey(response.Username, file.Filename), visibility)))
                            continue;
                        revision = ++_revision;
                        var rawResult = SearchProjectionInput.FromLive(++_sequence, revision, response,
                            file, observedAtUtc, visibility);
                        rawResults.Add(rawResult);
                        added.Add(rawResult);
                        if (visibility == SearchResultVisibility.Public)
                            _resultCount++;
                        else
                            _lockedFileCount++;
                        observedPeers.Add(response.Username);
                    }
                }
            }

            SearchResultsAddedChange? change = added.Count > 0
                ? new SearchResultsAddedChange(
                    CoreChangeSequencer.Next(), timeProvider.GetUtcNow(), JobId, revision,
                    added.AsReadOnly())
                : null;
            publications.Enqueue(change);
            PublishPending();
        }
    }

    public void Complete()
    {
        lock (publicationGate)
        {
            SearchCompletedChange change;
            lock (admissionGate)
            {
                if (_isComplete != 0)
                    return;
                _isComplete = 1;
                change = new SearchCompletedChange(
                    CoreChangeSequencer.Next(), timeProvider.GetUtcNow(), JobId, ++_revision,
                    QueryText, _resultCount, _lockedFileCount, observedPeers.Count);
            }
            publications.Enqueue(change);
            PublishPending();
        }
    }

    // publicationGate serializes threads but its monitor is reentrant. An observer
    // may admit another response or complete the search: queue those notifications
    // behind the entire current response, including its remaining subscribers.
    // Otherwise a cursor/completion can overtake files that readers then skip.
    // Drain inline without tasks or per-file queue entries; readable state still
    // uses the separate, short admissionGate.
    private void PublishPending()
    {
        if (publishing) return;
        publishing = true;
        try
        {
            while (publications.TryDequeue(out var change))
            {
                if (change != null) Publish(change);
                // Even a duplicate-only response updates discovery progress.
                if (change is not SearchCompletedChange)
                    InvokeObservers(ResponseAdded, this, nameof(ResponseAdded));
            }
        }
        finally { publishing = false; }
    }

    private void Publish(CoreChange change)
    {
        switch (change)
        {
            case SearchResultsAddedChange added:
                InvokeObservers(ResultsAdded, added, nameof(ResultsAdded));
                break;
            case SearchCompletedChange completed:
                InvokeObservers(SearchCompleted, completed, nameof(SearchCompleted));
                break;
        }

        InvokeObservers(ChangePublished, change, nameof(ChangePublished));
    }

    private void InvokeObservers<T>(Action<T>? observers, T value, string eventName)
    {
        if (observers == null)
            return;

        foreach (Action<T> observer in Delegate.EnumerateInvocationList(observers))
        {
            try
            {
                observer(value);
            }
            catch (Exception ex)
            {
                LogObserverFailure(eventName, ex);
            }
        }
    }

    private void LogObserverFailure(string eventName, Exception exception)
    {
        if (ObserverFailed is null)
            return;

        foreach (Action<string, Exception> observer in Delegate.EnumerateInvocationList(ObserverFailed))
        {
            try { observer(eventName, exception); }
            catch { /* Observer-failure reporting must not affect search domain work. */ }
        }
    }
}
