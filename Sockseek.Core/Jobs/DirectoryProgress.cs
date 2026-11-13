using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Jobs;

/// <summary>
/// Shared by execution and live projection. A changed file replaces only its own
/// contribution; reading directory progress never enumerates the other files.
/// Planned sizes remain the accepted plan's facts, supplemental sizes remain live.
/// </summary>
internal sealed class DirectoryProgress
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, FileContribution> files = [];
    private DirectoryProgressSummary summary;

    public DirectoryProgressSummary Summary
    {
        get { lock (gate) return summary; }
    }

    public void EnsureCapacity(int count)
    {
        lock (gate) files.EnsureCapacity(count);
    }

    public void Add(Guid id, long bytes, long? size, JobStateSnapshot state, bool supplemental, bool reused = false)
        => Add(id, bytes, size, state.LifecycleState, state.TerminalOutcome, state.SkipReason, supplemental, reused);

    public void Add(Guid id, long bytes, long? size, JobLifecycleState lifecycle,
        JobTerminalOutcome outcome, JobSkipReason skip, bool supplemental, bool reused = false)
    {
        var contribution = FileContribution.Create(bytes, size, lifecycle, outcome, skip, supplemental, reused);
        lock (gate)
        {
            files.Add(id, contribution);
            Apply(default, contribution);
            summary = summary with { FileCount = summary.FileCount + 1 };
        }
    }

    public bool Update(Guid id, long bytes, long? size, JobStateSnapshot state, bool reused = false)
        => Update(id, bytes, size, state.LifecycleState, state.TerminalOutcome, state.SkipReason, reused);

    public bool Update(Guid id, long bytes, long? size, JobLifecycleState lifecycle,
        JobTerminalOutcome outcome, JobSkipReason skip, bool reused = false)
    {
        lock (gate)
        {
            if (!files.TryGetValue(id, out var previous))
                return false;
            var current = FileContribution.Create(bytes, size, lifecycle, outcome, skip, previous.Supplemental, reused);
            if (previous == current)
                return false;
            files[id] = current;
            Apply(previous, current);
            return true;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            files.Clear();
            summary = default;
        }
    }

    private void Apply(FileContribution previous, FileContribution current)
        => summary = summary with
        {
            BytesTransferred = summary.BytesTransferred + current.Bytes - previous.Bytes,
            SupplementalKnownBytes = summary.SupplementalKnownBytes + current.KnownBytes - previous.KnownBytes,
            SupplementalUnknownCount = summary.SupplementalUnknownCount + current.UnknownCount - previous.UnknownCount,
            TerminalFileCount = summary.TerminalFileCount + current.Terminal - previous.Terminal,
            SuccessfulFileCount = summary.SuccessfulFileCount + current.Successful - previous.Successful,
            FailedFileCount = summary.FailedFileCount + current.Failed - previous.Failed,
            ProgressBytes = summary.ProgressBytes + current.ProgressBytes - previous.ProgressBytes,
            SkippedFileCount = summary.SkippedFileCount + current.Skipped - previous.Skipped,
            DownloadedFileCount = summary.DownloadedFileCount + current.Downloaded - previous.Downloaded,
            ReusedFileCount = summary.ReusedFileCount + current.Reused - previous.Reused,
        };

    private readonly record struct FileContribution(
        bool Supplemental, long Bytes, long KnownBytes, int UnknownCount,
        int Terminal, int Successful, int Failed, double ProgressBytes, int Skipped, int Downloaded, int Reused)
    {
        public static FileContribution Create(long bytes, long? size, JobLifecycleState lifecycle,
            JobTerminalOutcome outcome, JobSkipReason skip, bool supplemental, bool reused = false)
        {
            bool terminal = lifecycle == JobLifecycleState.Terminal;
            bool successful = JobOutcomeSummary.IsSuccessful(outcome, skip);
            return new(supplemental, bytes, supplemental ? size ?? 0 : 0,
                supplemental && size is null ? 1 : 0, terminal ? 1 : 0, successful ? 1 : 0,
                terminal && !successful && outcome != JobTerminalOutcome.Skipped ? 1 : 0,
                successful && size.HasValue ? size.Value : bytes,
                outcome == JobTerminalOutcome.Skipped ? 1 : 0,
                outcome == JobTerminalOutcome.Succeeded && !reused ? 1 : 0,
                outcome == JobTerminalOutcome.Succeeded && reused ? 1 : 0);
        }
    }
}

internal readonly record struct DirectoryProgressSummary(
    int FileCount, int TerminalFileCount, int SuccessfulFileCount, int FailedFileCount,
    long BytesTransferred, long SupplementalKnownBytes, int SupplementalUnknownCount, double ProgressBytes,
    int SkippedFileCount = 0, int DownloadedFileCount = 0, int ReusedFileCount = 0)
{
    // Satisfied local files advance completion without inventing network traffic.
    public double GetProgress(long plannedBytes)
        => FileCount > 0 && SuccessfulFileCount == FileCount ? 1
            : plannedBytes + SupplementalKnownBytes > 0
                ? Math.Clamp(ProgressBytes / (plannedBytes + SupplementalKnownBytes), 0, 1) : 0;
}
