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

    // TODO [ARCHITECTURE]: Introduce an atomic job-state reducer boundary before immutable jobs.
    // Currently, lifecycle/activity/outcome/failure fields are mutated through individual property
    // setters. That makes correctness depend on setter order and can briefly expose incoherent
    // snapshots such as Terminal + Searching. Add a JobStateTransition/JobStateSnapshot applier that
    // updates lifecycle, activity, terminal outcome, skip reason, and failure data as one transition,
    // then emits exactly one coherent state-change event. Illegal combinations should be rejected in
    // that centralized transition path. This reducer can still mutate the existing Job instance at
    // first; the important step is making transition ownership and event emission atomic.
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

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

        public void Fail(JobFailureReason reason, string? message = null, string? detail = null)
        {
            if (reason == JobFailureReason.Cancelled)
                throw new ArgumentException("Use SetCancelled(source) for cancellation terminal state.", nameof(reason));

            FailureMessage = message;
            FailureDetail = detail;
            FailureReason = reason;
            SetTerminal(JobTerminalOutcome.Failed);
        }

        public void SetCancelled(JobCancellationSource source, string? message = null, string? detail = null)
        {
            if (source == JobCancellationSource.None)
                throw new ArgumentException("Cancellation terminal state must include a non-None source.", nameof(source));

            MarkCancellationSource(source);
            FailureMessage = message;
            FailureDetail = detail;
            FailureReason = JobFailureReason.Cancelled;
            SetTerminal(JobTerminalOutcome.Cancelled);
        }

        public void ClearFailure()
        {
            FailureMessage = null;
            FailureDetail = null;
            FailureReason = JobFailureReason.None;
            if (TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.Cancelled)
                TerminalOutcome = JobTerminalOutcome.None;
        }

        public void UpdateActivity(JobActivityPhase phase, DateTimeOffset? untilUtc = null)
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
        }

        public void SetAwaitingSelection()
        {
            ActivityPhase = JobActivityPhase.None;
            ActivityUntilUtc = null;
            TerminalOutcome = JobTerminalOutcome.None;
            SkipReason = JobSkipReason.None;
            LifecycleState = JobLifecycleState.AwaitingSelection;
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
            FailureReason = reason;
            SkipReason = skipReason;
            SetTerminal(JobTerminalOutcome.Skipped);
        }

        public void SetPartialSuccess(string? message = null, JobCancellationSource cancellationSource = JobCancellationSource.None)
        {
            if (cancellationSource != JobCancellationSource.None)
                MarkCancellationSource(cancellationSource);

            FailureMessage = message;
            FailureReason = JobFailureReason.Other;
            SetTerminal(JobTerminalOutcome.PartialSuccess);
        }

        public void ResetToPending()
        {
            ActivityPhase = JobActivityPhase.None;
            ActivityUntilUtc = null;
            TerminalOutcome = JobTerminalOutcome.None;
            SkipReason = JobSkipReason.None;
            CancellationSource = JobCancellationSource.None;
            ClearFailure();
            LifecycleState = JobLifecycleState.Pending;
        }

        private void SetTerminal(JobTerminalOutcome outcome)
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
