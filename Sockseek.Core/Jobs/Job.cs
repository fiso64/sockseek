using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sockseek.Core.Jobs;

    public class DiscoverySummary
    {
        public int ResultCount { get; set; }
        public int LockedFileCount { get; set; }
    }

    public readonly record struct JobStateSnapshot(
        JobLifecycleState LifecycleState,
        JobActivityPhase ActivityPhase,
        DateTimeOffset? ActivityUntilUtc,
        JobTerminalOutcome TerminalOutcome,
        JobSkipReason SkipReason,
        JobCancellationSource CancellationSource,
        JobFailureReason FailureReason,
        string? FailureMessage,
        string? FailureDetail);

    public readonly record struct JobStateTransition(JobStateSnapshot Before, JobStateSnapshot After)
    {
        public bool Changed => Before != After;
        public bool ActivityChanged =>
            Before.ActivityPhase != After.ActivityPhase
            || Before.ActivityUntilUtc != After.ActivityUntilUtc;
    }

    // TODO [ARCHITECTURE]: Harden the atomic job-state transition boundary into a real reducer.
    // ApplyStateTransition now prevents observers from seeing half-applied lifecycle/activity/outcome
    // snapshots, but callers still imperatively choose individual transitions and the reducer does not
    // yet validate the full state machine. Next step: make transitions explicit command/result values,
    // reject illegal state combinations centrally, and keep all lifecycle/activity/outcome/failure
    // mutations inside that reducer before moving the Job model itself toward immutability.
    //
    // TODO [ARCHITECTURE]: Convert Job models to immutable types and implement Unidirectional Data Flow.
    // Jobs still act as globally mutable state containers. Properties like `BytesTransferred`
    // and `DownloadPath` are mutated directly by Downloader/Searcher on background threads.
    // Because INotifyPropertyChanged fires on the mutating thread, this forces the UI/CLI layers to use
    // liberal lock() statements to avoid race conditions and visual tearing.
    // Later refactor:
    // 1. Make Job a C# `record` with `init` only properties.
    // 2. Background workers should yield `ProgressEvent` structs to a Channel.
    // 3. A central reducer reads the channel, creates a *new* copy of the Job via the `with` expression,
    //    and pushes the unified snapshot to the UI.
    public abstract class Job : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<Job, JobStateTransition>? StateChanged;

        private bool _deferPropertyChanged;
        private readonly List<string> _deferredPropertyChanges = [];

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            if (_deferPropertyChanged)
            {
                if (name != null && !_deferredPropertyChanges.Contains(name))
                    _deferredPropertyChanges.Add(name);
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static int _nextDisplayId = 0;

        public Guid Id { get; } = Guid.NewGuid();
        public int DisplayId { get; } = Interlocked.Increment(ref _nextDisplayId);

        // Stable logical grouping for sequentially-related jobs.
        // Multiple executable jobs can share one workflow without sharing job identity.
        public Guid WorkflowId { get; set; } = Guid.NewGuid();

        // Set by the engine immediately before processing begins.
        // Linked to appCts (and the parent job's Cts if any) so that cancelling a parent
        // propagates to all descendants. Cancelling this only affects this job and its children.
        public CancellationTokenSource? Cts { get; internal set; }
        /// <summary>
        /// Requests cancellation for this job token. The source describes terminal job
        /// cancellation provenance, not every lower-level transfer-attempt cancellation.
        /// </summary>
        public void Cancel(JobCancellationSource source)
        {
            MarkCancellationSource(source);
            Cts?.Cancel();
        }

        private JobCancellationSource _cancellationSource = JobCancellationSource.None;
        public JobCancellationSource CancellationSource
        {
            get => _cancellationSource;
            private set { if (_cancellationSource != value) { _cancellationSource = value; OnPropertyChanged(); } }
        }

        internal void MarkCancellationSource(JobCancellationSource source)
        {
            if (source == JobCancellationSource.None || CancellationSource != JobCancellationSource.None)
                return;

            CancellationSource = source;
        }

        private Settings.DownloadSettings? _config;
        public Settings.DownloadSettings Config
        {
            get => _config!;
            set { if (_config != value) { _config = value; OnPropertyChanged(); } }
        }

        private JobLifecycleState _lifecycleState = JobLifecycleState.Pending;
        public JobLifecycleState LifecycleState
        {
            get => _lifecycleState;
            private set { if (_lifecycleState != value) { _lifecycleState = value; OnPropertyChanged(); } }
        }

        private JobActivityPhase _activityPhase = JobActivityPhase.None;
        public JobActivityPhase ActivityPhase
        {
            get => _activityPhase;
            private set { if (_activityPhase != value) { _activityPhase = value; OnPropertyChanged(); } }
        }

        private DateTimeOffset? _activityUntilUtc;
        public DateTimeOffset? ActivityUntilUtc
        {
            get => _activityUntilUtc;
            private set { if (_activityUntilUtc != value) { _activityUntilUtc = value; OnPropertyChanged(); } }
        }

        private JobTerminalOutcome _terminalOutcome = JobTerminalOutcome.None;
        public JobTerminalOutcome TerminalOutcome
        {
            get => _terminalOutcome;
            private set { if (_terminalOutcome != value) { _terminalOutcome = value; OnPropertyChanged(); } }
        }

        private JobSkipReason _skipReason = JobSkipReason.None;
        public JobSkipReason SkipReason
        {
            get => _skipReason;
            private set { if (_skipReason != value) { _skipReason = value; OnPropertyChanged(); } }
        }

        public bool IsPending => LifecycleState == JobLifecycleState.Pending;
        public bool IsRunning => LifecycleState == JobLifecycleState.Running;
        public bool IsAwaitingSelection => LifecycleState == JobLifecycleState.AwaitingSelection;
        public bool IsTerminal => LifecycleState == JobLifecycleState.Terminal;
        public bool IsSuccessfulTerminal =>
            TerminalOutcome == JobTerminalOutcome.Succeeded
            || (TerminalOutcome == JobTerminalOutcome.Skipped && SkipReason == JobSkipReason.AlreadyExists);
        public bool IsUnsuccessfulTerminal =>
            LifecycleState == JobLifecycleState.Terminal && !IsSuccessfulTerminal;
        public bool IsSkippedAlreadyExists =>
            TerminalOutcome == JobTerminalOutcome.Skipped && SkipReason == JobSkipReason.AlreadyExists;
        public bool IsSkippedNotFoundLastTime =>
            TerminalOutcome == JobTerminalOutcome.Skipped && SkipReason == JobSkipReason.NotFoundLastTime;

        // Extractor hints — set by extractors, consumed by JobPreparer when preparing this job's
        // Config and JobContext. JobPreparer clears them after use so they don't linger.
        public FileConditionPatch?      ExtractorCond         { get; set; }
        public FileConditionPatch?      ExtractorPrefCond     { get; set; }
        public FolderConditionPatch?    ExtractorFolderCond   { get; set; }
        public FolderConditionPatch?    ExtractorPrefFolderCond { get; set; }
        public bool                     EnablesIndexByDefault { get; set; }

        // Display / identity
        public string? ItemName { get; set; }

        public DownloadBehaviorPolicy DownloadBehaviorPolicy { get; set; } = new();
        public DownloadBehavior DownloadBehavior => DownloadBehaviorPolicy.For(this);
        public bool ShouldDownloadAutomatically => DownloadBehavior == DownloadBehavior.Automatic;

        // Source provenance (position in the input file / playlist)
        public int ItemNumber { get; set; } = 1;
        public int LineNumber { get; set; } = 0;

        // Durable source mutation to apply after this job succeeds. This is job metadata, not
        // call-stack state, so manual/interactive pause-resume and follow-up submissions do not
        // lose remove-from-source behavior.
        public SourceMutation? SourceMutation { get; set; }

        // Discovery results (populated during Search or Folder Retrieval phases)
        public DiscoverySummary? Discovery { get; set; }

        // Job-level outcome (set after the job completes or fails)
        private JobFailureReason _failureReason = JobFailureReason.None;
        public JobFailureReason FailureReason
        {
            get => _failureReason;
            private set { if (_failureReason != value) { _failureReason = value; OnPropertyChanged(); } }
        }

        // Optional human-readable explanation for the failure (complements FailureReason).
        public string? FailureMessage { get; private set; }
        public string? FailureDetail { get; private set; }

        public JobStateSnapshot StateSnapshot => new(
            LifecycleState,
            ActivityPhase,
            ActivityUntilUtc,
            TerminalOutcome,
            SkipReason,
            CancellationSource,
            FailureReason,
            FailureMessage,
            FailureDetail);

        private void ApplyStateTransition(Action mutate)
        {
            var before = StateSnapshot;
            _deferPropertyChanged = true;
            try
            {
                mutate();
            }
            finally
            {
                _deferPropertyChanged = false;
            }

            var after = StateSnapshot;
            var changedProperties = _deferredPropertyChanges.ToArray();
            _deferredPropertyChanges.Clear();

            foreach (var propertyName in changedProperties)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

            var transition = new JobStateTransition(before, after);
            if (transition.Changed)
                StateChanged?.Invoke(this, transition);
        }

        public void Fail(JobFailureReason reason, string? message = null, string? detail = null)
        {
            if (reason == JobFailureReason.Cancelled)
                throw new ArgumentException("Use SetCancelled(source) for cancellation terminal state.", nameof(reason));

            ApplyStateTransition(() =>
            {
                FailureMessage = message;
                FailureDetail = detail;
                FailureReason = reason;
                SetTerminalCore(JobTerminalOutcome.Failed);
            });
        }

        public void SetCancelled(JobCancellationSource source, string? message = null, string? detail = null)
        {
            if (source == JobCancellationSource.None)
                throw new ArgumentException("Cancellation terminal state must include a non-None source.", nameof(source));

            ApplyStateTransition(() =>
            {
                MarkCancellationSource(source);
                FailureMessage = message;
                FailureDetail = detail;
                FailureReason = JobFailureReason.Cancelled;
                SetTerminalCore(JobTerminalOutcome.Cancelled);
            });
        }

        public void ClearFailure()
        {
            ApplyStateTransition(ClearFailureCore);
        }

        public void UpdateActivity(JobActivityPhase phase, DateTimeOffset? untilUtc = null)
        {
            ApplyStateTransition(() =>
            {
                if (phase == JobActivityPhase.None)
                {
                    ActivityPhase = JobActivityPhase.None;
                    ActivityUntilUtc = null;
                    return;
                }

                if (LifecycleState != JobLifecycleState.AwaitingSelection)
                    LifecycleState = JobLifecycleState.Running;
                TerminalOutcome = JobTerminalOutcome.None;
                SkipReason = JobSkipReason.None;
                ActivityPhase = phase;
                ActivityUntilUtc = untilUtc;
            });
        }

        public void SetAwaitingSelection()
        {
            ApplyStateTransition(() =>
            {
                ActivityPhase = JobActivityPhase.None;
                ActivityUntilUtc = null;
                TerminalOutcome = JobTerminalOutcome.None;
                SkipReason = JobSkipReason.None;
                LifecycleState = JobLifecycleState.AwaitingSelection;
            });
        }

        public virtual void SetDone()
        {
            SetTerminal(JobTerminalOutcome.Succeeded);
        }

        public virtual void SetAlreadyExists()
        {
            SetSkipped(JobSkipReason.AlreadyExists);
        }

        public void SetSkipped(JobSkipReason skipReason = JobSkipReason.None, JobFailureReason reason = JobFailureReason.None)
        {
            ApplyStateTransition(() =>
            {
                FailureReason = reason;
                SkipReason = skipReason;
                SetTerminalCore(JobTerminalOutcome.Skipped);
            });
        }

        public void SetPartialSuccess(string? message = null, JobCancellationSource cancellationSource = JobCancellationSource.None)
        {
            ApplyStateTransition(() =>
            {
                if (cancellationSource != JobCancellationSource.None)
                    MarkCancellationSource(cancellationSource);

                FailureMessage = message;
                FailureReason = JobFailureReason.Other;
                SetTerminalCore(JobTerminalOutcome.PartialSuccess);
            });
        }

        public void ResetToPending()
        {
            ApplyStateTransition(() =>
            {
                ActivityPhase = JobActivityPhase.None;
                ActivityUntilUtc = null;
                TerminalOutcome = JobTerminalOutcome.None;
                SkipReason = JobSkipReason.None;
                CancellationSource = JobCancellationSource.None;
                ClearFailureCore();
                LifecycleState = JobLifecycleState.Pending;
            });
        }

        private void ClearFailureCore()
        {
            FailureMessage = null;
            FailureDetail = null;
            FailureReason = JobFailureReason.None;
            if (TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.Cancelled)
                TerminalOutcome = JobTerminalOutcome.None;
        }

        private void SetTerminal(JobTerminalOutcome outcome)
            => ApplyStateTransition(() => SetTerminalCore(outcome));

        private void SetTerminalCore(JobTerminalOutcome outcome)
        {
            ActivityPhase = JobActivityPhase.None;
            ActivityUntilUtc = null;
            TerminalOutcome = outcome;
            if (outcome != JobTerminalOutcome.Skipped)
                SkipReason = JobSkipReason.None;
            LifecycleState = JobLifecycleState.Terminal;
        }

        // Subclasses declare their default; callers can override with CanBeSkippedOverride.
        protected abstract bool DefaultCanBeSkipped { get; }
        public bool? CanBeSkippedOverride { get; set; }
        public bool  CanBeSkipped => CanBeSkippedOverride ?? DefaultCanBeSkipped;

        // Primary query used for display and key computation. Non-leaf types return null.
        public virtual SongQuery? QueryTrack => null;

        private List<string>? _printLines;

        public void AddPrintLine(string line)
        {
            _printLines ??= new List<string>();
            _printLines.Add(line);
        }

        public void PrintLines()
        {
            if (_printLines == null) return;
            foreach (var line in _printLines)
                SockseekLog.Info(line);
            _printLines = null;
        }

        public string DefaultFolderName()
        {
            return (ItemName ?? "").ReplaceInvalidChars(" ").Trim();
        }

        public string ItemNameOrSource() => ItemName ?? ToString(noInfo: true);

        public string DefaultPlaylistName()
        {
            var name = ItemName ?? ToString(noInfo: true);
            return $"_{name.ReplaceInvalidChars(" ").Trim()}.m3u8";
        }

        public virtual string ToString(bool noInfo) => ItemName ?? QueryTrack?.ToString(noInfo) ?? "";

        public void CopySourceMutationFrom(Job src)
        {
            if (LineNumber == 0)
                LineNumber = src.LineNumber;
            if (ItemNumber == 1)
                ItemNumber = src.ItemNumber;
            SourceMutation ??= src.SourceMutation;
        }

        public void CopySharedFieldsFrom(Job src)
        {
            ExtractorCond             = src.ExtractorCond;
            ExtractorPrefCond         = src.ExtractorPrefCond;
            ExtractorFolderCond       = src.ExtractorFolderCond;
            ExtractorPrefFolderCond   = src.ExtractorPrefFolderCond;
            ItemName                  = src.ItemName;
            EnablesIndexByDefault = src.EnablesIndexByDefault;
            ItemNumber            = src.ItemNumber;
            LineNumber            = src.LineNumber;
            SourceMutation        = src.SourceMutation;
            CanBeSkippedOverride  = src.CanBeSkippedOverride;
            WorkflowId            = src.WorkflowId;
            DownloadBehaviorPolicy = src.DownloadBehaviorPolicy;
        }
    }
