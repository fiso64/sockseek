using System.ComponentModel;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Jobs;

/// <summary>
/// Owns container membership and its outcome contributions. Callers receive a
/// stable read-only membership snapshot; every mutation updates subscriptions and
/// reports one structural change. A child transition never recounts its siblings.
/// </summary>
internal sealed class JobMembers<T>(bool followExtractResults, Action changed) where T : Job
{
    private readonly object gate = new();
    private readonly bool followResults = followExtractResults;
    private readonly List<T> items = [];
    private readonly Dictionary<Guid, Member> members = [];
    private IReadOnlyList<T>? snapshot;
    private JobOutcomeSummary summary;

    public IReadOnlyList<T> Snapshot
    {
        get { lock (gate) return snapshot ??= Array.AsReadOnly(items.ToArray()); }
    }

    public int Count { get { lock (gate) return items.Count; } }
    public JobOutcomeSummary Summary { get { lock (gate) return summary; } }

    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            AddCore(item);
            snapshot = null;
        }
        changed();
    }

    public void AddRange(IEnumerable<T> source)
    {
        T[] added = source.ToArray();
        if (added.Length == 0) return;
        if (added.Any(item => item is null)) throw new ArgumentException("Members cannot be null.", nameof(source));
        lock (gate)
        {
            foreach (T item in added) AddCore(item);
            snapshot = null;
        }
        changed();
    }

    public void Replace(IEnumerable<T> source)
    {
        T[] replacement = source.ToArray();
        if (replacement.Any(item => item is null)) throw new ArgumentException("Members cannot be null.", nameof(source));
        lock (gate)
        {
            foreach (Member member in members.Values) member.Dispose();
            members.Clear();
            items.Clear();
            summary = default;
            foreach (T item in replacement) AddCore(item);
            snapshot = null;
        }
        changed();
    }

    public void RemoveRange(int index, int count)
    {
        lock (gate)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (index > items.Count - count) throw new ArgumentException("The range exceeds the membership.");
            if (count == 0) return;
            for (int i = index; i < index + count; i++) RemoveCore(items[i]);
            items.RemoveRange(index, count);
            snapshot = null;
        }
        changed();
    }

    public void RemoveWhere(Predicate<T> predicate)
    {
        lock (gate)
        {
            int removed = items.RemoveAll(item =>
            {
                if (!predicate(item)) return false;
                RemoveCore(item);
                return true;
            });
            if (removed == 0) return;
            snapshot = null;
        }
        changed();
    }

    public void Reverse()
    {
        lock (gate)
        {
            if (items.Count < 2) return;
            items.Reverse();
            snapshot = null;
        }
        changed();
    }

    private void AddCore(T item)
    {
        if (!members.TryGetValue(item.Id, out Member? member))
        {
            member = new Member(this, item);
            members.Add(item.Id, member);
            member.Watch();
            member.Outcome = Outcome(item);
        }
        member.Count++;
        var outcome = member.Outcome;
        summary = summary.Include(outcome.Lifecycle, outcome.Terminal, outcome.Skip);
        items.Add(item);
    }

    private void RemoveCore(T item)
    {
        Member member = members[item.Id];
        var outcome = member.Outcome;
        summary = summary.Exclude(outcome.Lifecycle, outcome.Terminal, outcome.Skip);
        if (--member.Count == 0)
        {
            member.Dispose();
            members.Remove(item.Id);
        }
    }

    private LogicalJobProgress.Outcome Outcome(Job job)
        => followResults ? LogicalJobProgress.FromRuntime(job)
            : new(job.LifecycleState, job.TerminalOutcome, job.SkipReason);

    private void Update(Member member, bool relationshipsChanged)
    {
        lock (gate)
        {
            // An already-dispatched callback can arrive after membership removal.
            if (member.Count == 0) return;
            if (relationshipsChanged) member.Watch();
            var current = Outcome(member.Job);
            var previous = member.Outcome;
            if (current == previous) return;
            summary = summary.Exclude(previous.Lifecycle, previous.Terminal, previous.Skip, member.Count)
                .Include(current.Lifecycle, current.Terminal, current.Skip, member.Count);
            member.Outcome = current;
        }
    }

    private sealed class Member(JobMembers<T> owner, T job) : IDisposable
    {
        private List<Job>? resultChain;
        public T Job { get; } = job;
        public int Count { get; set; }
        public LogicalJobProgress.Outcome Outcome { get; set; }

        public void Watch()
        {
            Unsubscribe();
            Job.StateChanged += StateChanged;
            if (!owner.followResults || Job is not ExtractJob extract) return;
            resultChain = [];
            var visited = new HashSet<Guid> { Job.Id };
            extract.PropertyChanged += RelationshipChanged;
            while (extract.Result is Job result && visited.Add(result.Id))
            {
                resultChain.Add(result);
                result.StateChanged += StateChanged;
                if (result is not ExtractJob nested) break;
                nested.PropertyChanged += RelationshipChanged;
                extract = nested;
            }
        }

        private void StateChanged(Job _, JobStateTransition transition)
        {
            if (transition.Before.LifecycleState != transition.After.LifecycleState
                || transition.Before.TerminalOutcome != transition.After.TerminalOutcome
                || transition.Before.SkipReason != transition.After.SkipReason)
                owner.Update(this, relationshipsChanged: false);
        }

        private void RelationshipChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ExtractJob.Result) or nameof(ExtractJob.AutoProcessResult))
                owner.Update(this, relationshipsChanged: true);
        }

        private void Unsubscribe()
        {
            Job.StateChanged -= StateChanged;
            if (Job is ExtractJob extract) extract.PropertyChanged -= RelationshipChanged;
            if (resultChain == null) return;
            foreach (Job result in resultChain)
            {
                result.StateChanged -= StateChanged;
                if (result is ExtractJob nested) nested.PropertyChanged -= RelationshipChanged;
            }
            resultChain = null;
        }

        public void Dispose()
        {
            Count = 0;
            Unsubscribe();
        }
    }
}
