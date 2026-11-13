using Sockseek.Core.Models;
using System.ComponentModel;

namespace Sockseek.Core.Jobs;

public abstract record DirectoryExecutionState
{
    private DirectoryExecutionState() { }

    public sealed record Unresolved : DirectoryExecutionState;
    public sealed record Resolving : DirectoryExecutionState;
    public sealed record Planned(int AttemptNumber) : DirectoryExecutionState;
    public sealed record Transferring(int AttemptNumber) : DirectoryExecutionState;
}

/// <summary>One immutable plan and the file jobs materialized for that plan.</summary>
public sealed class DirectoryTransferAttempt
{
    private IReadOnlyList<FileDownloadJob> fileJobs = Array.Empty<FileDownloadJob>();
    private bool childrenMaterialized;

    internal DirectoryTransferAttempt(int attemptNumber, DirectoryTransferPlan plan)
    {
        if (attemptNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        ArgumentNullException.ThrowIfNull(plan);
        AttemptNumber = attemptNumber;
        Plan = plan;
    }

    public int AttemptNumber { get; }
    public DirectoryTransferPlan Plan { get; }
    public IReadOnlyList<FileDownloadJob> FileJobs => fileJobs;
    public bool ChildrenMaterialized => childrenMaterialized;

    internal void MaterializeChildren(IEnumerable<FileDownloadJob> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (childrenMaterialized)
            throw new InvalidOperationException("Directory attempt children have already been materialized.");

        var owned = children.ToArray();
        if (owned.Any(child => child is null))
            throw new ArgumentException("Directory attempt children cannot contain null jobs.", nameof(children));
        if (owned.Length != Plan.Entries.Count)
            throw new ArgumentException("Directory attempt must materialize one file job per plan entry.", nameof(children));

        fileJobs = Array.AsReadOnly(owned);
        childrenMaterialized = true;
    }
}

/// <summary>
/// Observable lifecycle shared by directory-shaped downloads. It owns state and
/// attempt association, but no search, retrieval, placement, or finalization.
/// </summary>
public abstract class DirectoryDownloadJob : Job
{
    // Coarse ownership changes, not transport progress. The tracker publishes
    // these through the existing immutable job-state stream for every client.
    private DirectoryExecutionState directoryState = new DirectoryExecutionState.Unresolved();
    private DirectoryTransferAttempt? activeAttempt;
    private readonly List<FileDownloadJob> fileJobs = [];
    private readonly DirectoryProgress progress = new();
    private int attemptNumber;
    private string? downloadPath;

    public DirectoryExecutionState DirectoryState => directoryState;
    public DirectoryTransferAttempt? ActiveAttempt => activeAttempt;
    /// <summary>
    /// All file jobs currently owned by this directory. The active attempt owns the
    /// exact one-per-plan-entry prefix; a specialization may append explicit
    /// post-plan work, such as album art selected during music finalization.
    /// </summary>
    public IReadOnlyList<FileDownloadJob> FileJobs => fileJobs;
    public long BytesTransferred => progress.Summary.BytesTransferred;
    public long TotalKnownBytes => (ActiveAttempt?.Plan.TotalKnownBytes ?? 0)
        + progress.Summary.SupplementalKnownBytes;
    public int UnknownFileSizeCount => (ActiveAttempt?.Plan.UnknownFileSizeCount ?? 0)
        + progress.Summary.SupplementalUnknownCount;
    public double Progress => progress.Summary.GetProgress(ActiveAttempt?.Plan.TotalKnownBytes ?? 0);
    internal DirectoryProgressSummary ProgressSummary => progress.Summary;

    public string? DownloadPath
    {
        get => downloadPath;
        set
        {
            if (downloadPath == value)
                return;
            downloadPath = value;
            OnPropertyChanged();
        }
    }

    public void BeginDirectoryResolution()
    {
        if (directoryState is not DirectoryExecutionState.Unresolved)
            throw new InvalidOperationException("Only an unresolved directory can begin resolution.");
        SetDirectoryState(new DirectoryExecutionState.Resolving());
    }

    public DirectoryTransferAttempt BeginDirectoryAttempt(DirectoryTransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (directoryState is DirectoryExecutionState.Transferring
            && (activeAttempt == null || fileJobs.Any(child => !child.IsTerminal)))
        {
            throw new InvalidOperationException("A directory with active transfers cannot change plans.");
        }

        ClearDirectoryChildren();
        activeAttempt = new DirectoryTransferAttempt(checked(++attemptNumber), plan);
        OnPropertyChanged(nameof(ActiveAttempt));
        OnPropertyChanged(nameof(FileJobs));
        OnPropertyChanged(nameof(UnknownFileSizeCount));
        PublishDirectoryStructure(new DirectoryExecutionState.Planned(activeAttempt.AttemptNumber));
        return activeAttempt;
    }

    public void MaterializeDirectoryChildren(IEnumerable<FileDownloadJob> children)
    {
        if (activeAttempt is null || directoryState is not DirectoryExecutionState.Planned)
            throw new InvalidOperationException("A planned directory attempt is required before materializing children.");
        activeAttempt.MaterializeChildren(children);
        progress.EnsureCapacity(activeAttempt.FileJobs.Count);
        foreach (var child in activeAttempt.FileJobs)
            AttachDirectoryChild(child);
        NotifyDirectoryChildrenChanged();
        NotifyStructureChanged();
    }

    public void BeginDirectoryTransfer()
    {
        if (activeAttempt is null
            || directoryState is not DirectoryExecutionState.Planned planned
            || planned.AttemptNumber != activeAttempt.AttemptNumber
            || !activeAttempt.ChildrenMaterialized)
        {
            throw new InvalidOperationException("A materialized planned attempt is required before transfer.");
        }

        SetDirectoryState(new DirectoryExecutionState.Transferring(activeAttempt.AttemptNumber));
    }

    public void ResetDirectoryResolution()
    {
        if (directoryState is DirectoryExecutionState.Transferring)
            throw new InvalidOperationException("A transferring directory cannot be reset.");
        ClearDirectoryChildren();
        activeAttempt = null;
        OnPropertyChanged(nameof(ActiveAttempt));
        OnPropertyChanged(nameof(FileJobs));
        OnPropertyChanged(nameof(UnknownFileSizeCount));
        PublishDirectoryStructure(new DirectoryExecutionState.Unresolved());
    }

    private void ChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FileDownloadJob child)
            return;
        if (e.PropertyName is nameof(FileDownloadJob.BytesTransferred)
            or nameof(FileDownloadJob.FileSize))
        {
            progress.Update(child.Id, child.BytesTransferred, child.FileSize, child.StateSnapshot, child.Reused);
            OnPropertyChanged(nameof(BytesTransferred));
            OnPropertyChanged(nameof(TotalKnownBytes));
            if (e.PropertyName == nameof(FileDownloadJob.FileSize))
                OnPropertyChanged(nameof(UnknownFileSizeCount));
            OnPropertyChanged(nameof(Progress));
        }
        else if (e.PropertyName is nameof(Job.LifecycleState) or nameof(Job.TerminalOutcome) or nameof(Job.SkipReason))
        {
            if (progress.Update(child.Id, child.BytesTransferred, child.FileSize, child.StateSnapshot, child.Reused))
                OnPropertyChanged(nameof(Progress));
        }
    }

    /// <summary>
    /// Registers work deliberately added by a semantic specialization after the
    /// immutable directory plan has been materialized.
    /// </summary>
    protected void AddSupplementalDirectoryChildren(IReadOnlyList<FileDownloadJob> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (activeAttempt is null || !activeAttempt.ChildrenMaterialized)
            throw new InvalidOperationException("Supplemental directory work requires a materialized active attempt.");
        if (children.Any(child => child is null))
            throw new ArgumentException("Supplemental directory work cannot contain null jobs.", nameof(children));

        var owned = fileJobs.ToHashSet();
        int previousCount = fileJobs.Count;
        foreach (var child in children)
        {
            if (!owned.Add(child)) continue;
            AttachDirectoryChild(child, supplemental: true);
        }
        if (fileJobs.Count == previousCount)
            return;
        // Admission is one structural change: publishing each new member would
        // repeatedly snapshot the growing directory graph.
        NotifyDirectoryChildrenChanged();
        NotifyStructureChanged();
    }

    protected void ClearDirectoryChildren()
    {
        if (directoryState is DirectoryExecutionState.Transferring
            && fileJobs.Any(child => !child.IsTerminal))
        {
            throw new InvalidOperationException("Active directory children cannot be discarded.");
        }

        foreach (var child in fileJobs)
            child.PropertyChanged -= ChildPropertyChanged;
        fileJobs.Clear();
        progress.Clear();
        NotifyDirectoryChildrenChanged();
    }

    private void AttachDirectoryChild(FileDownloadJob child, bool supplemental = false)
    {
        fileJobs.Add(child);
        progress.Add(child.Id, child.BytesTransferred, child.FileSize, child.StateSnapshot, supplemental, child.Reused);
        child.PropertyChanged += ChildPropertyChanged;
    }

    private void NotifyDirectoryChildrenChanged()
    {
        OnPropertyChanged(nameof(FileJobs));
        OnPropertyChanged(nameof(BytesTransferred));
        OnPropertyChanged(nameof(TotalKnownBytes));
        OnPropertyChanged(nameof(UnknownFileSizeCount));
        OnPropertyChanged(nameof(Progress));
    }

    private void SetDirectoryState(DirectoryExecutionState value)
    {
        directoryState = value;
        OnPropertyChanged(nameof(DirectoryState));
        NotifyExecutionChanged();
    }

    private void PublishDirectoryStructure(DirectoryExecutionState value)
    {
        directoryState = value;
        OnPropertyChanged(nameof(DirectoryState));
        NotifyStructureChanged();
    }

    public override void SetDone()
        => SetDone(downloadPath: null);

    public void SetDone(string? downloadPath)
    {
        if (downloadPath != null)
            DownloadPath = downloadPath;
        base.SetDone();
    }

    public override void SetAlreadyExists()
        => SetAlreadyExists(path: null);

    public void SetAlreadyExists(string? path)
    {
        if (path != null)
            DownloadPath = path;
        base.SetAlreadyExists();
    }
}
