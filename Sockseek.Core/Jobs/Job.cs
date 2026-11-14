using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Planning;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Sockseek.Core.Jobs;

    public class DiscoverySummary
    {
        // Raw search/browse items discovered before result projection, filtering, grouping, or bucketing.
        public int RawResultCount { get; set; }
        public int LockedFileCount { get; set; }
        public int ObservedPeerCount { get; set; }
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
        string? FailureDetail,
        long Revision = 0);

    public readonly record struct JobStateTransition(JobStateSnapshot Before, JobStateSnapshot After)
    {
        public bool Changed => Before != After;
        public bool ActivityChanged =>
            Before.ActivityPhase != After.ActivityPhase
            || Before.ActivityUntilUtc != After.ActivityUntilUtc;
    }

    // Lifecycle state and its revision have one mutation owner. Jobs remain mutable
    // execution objects; progress and structural data do not require graph copies.
    public abstract class Job : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        internal event Action<Job, JobStateTransition>? StateChanged;
        internal event Action<Job>? StructureChanged;
        internal event Action<Job>? ExecutionChanged;

        protected void NotifyStructureChanged() => StructureChanged?.Invoke(this);
        protected void NotifyExecutionChanged() => ExecutionChanged?.Invoke(this);

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly Lock stateGate = new();
        private JobStateSnapshot state;
        private Queue<JobStateTransition>? stateNotifications;
        private bool publishingState;

        private int _displayId;
        private readonly object displayIdLock = new();

        private SemaphoreSlim? executionGate;
        // Search, resume and terminal work for this identity cannot overlap.
        // Allocate only for jobs dispatched through the root queue.
        internal SemaphoreSlim ExecutionGate => LazyInitializer.EnsureInitialized(
            ref executionGate, static () => new SemaphoreSlim(1, 1));

        // New durable rows use time-ordered identities to keep SQLite index inserts local.
        public Guid Id { get; } = Guid.CreateVersion7();
        public int DisplayId => _displayId;

        public int EnsureDisplayId()
        {
            var existing = Volatile.Read(ref _displayId);
            if (existing != 0)
                return existing;

            lock (displayIdLock)
            {
                if (_displayId == 0)
                    _displayId = JobDisplayIds.Next();

                return _displayId;
            }
        }

        // Stable logical grouping for sequentially-related jobs.
        // Multiple executable jobs can share one workflow without sharing job identity.
        public Guid WorkflowId { get; set; } = Guid.NewGuid();

        // Set by the engine before execution registration, including queued work.
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

        public JobCancellationSource CancellationSource => StateSnapshot.CancellationSource;

        internal void MarkCancellationSource(JobCancellationSource source)
        {
            if (source == JobCancellationSource.None) return;
            ApplyStateTransition(current => current.CancellationSource == JobCancellationSource.None
                && current.LifecycleState != JobLifecycleState.Terminal
                ? current with { CancellationSource = source } : current);
        }

        private Settings.DownloadSettings? _config;
        public Settings.DownloadSettings Config
        {
            get
            {
                if (_config is { } config) return config;
                // Planning owns a frozen value. A mutable copy exists only when
                // execution or a caller actually asks for it, and never aliases it.
                return PlannedSettings is { } planned
                    ? LazyInitializer.EnsureInitialized(ref _config, planned.ReadSettings)
                    : null!;
            }
            set { if (_config != value) { _config = value; OnPropertyChanged(); } }
        }

        // Snapshot metadata must not materialize mutable execution settings for
        // every pending job. Prefer live edits once that copy actually exists.
        internal PrintOption EffectivePrintOption => _config?.PrintOption
            ?? PlannedSettings?.PrintOption ?? PrintOption.None;
        internal IEnumerable<Settings.AppliedProfile> EffectiveProfiles => _config is { } config
            ? config.AppliedProfiles
            : PlannedSettings?.AppliedProfiles ?? [];

        internal void BindPlannedSettings(Settings.DownloadSettings ownedSettings)
        {
            PlannedSettings = Settings.JobSettingsSnapshot.FromOwned(ownedSettings);
            _config = null;
            OnPropertyChanged(nameof(Config));
        }

        // A planner may bind the complete effective settings before runtime
        // preparation. This is transient execution input, not mutable config:
        // preview commit and direct Start use it to avoid resolving defaults or
        // auto profiles a second time.
        public Settings.JobSettingsSnapshot? PlannedSettings { get; set; }
        public SubmissionSourceRevision? PlannedSourceRevision { get; set; }

        /// <summary>Optional source-provided display information; never a search hint.</summary>
        public string? ExtractorInfo { get; set; }
        public JobSourceInfo? SourceInfo { get; set; }

        public JobLifecycleState LifecycleState => StateSnapshot.LifecycleState;
        public JobActivityPhase ActivityPhase => StateSnapshot.ActivityPhase;
        public DateTimeOffset? ActivityUntilUtc => StateSnapshot.ActivityUntilUtc;
        public JobTerminalOutcome TerminalOutcome => StateSnapshot.TerminalOutcome;
        public JobSkipReason SkipReason => StateSnapshot.SkipReason;
        public ExistingFileSource? ExistingFileSource { get; internal set; }

        public bool IsPending => LifecycleState == JobLifecycleState.Pending;
        public bool IsRunning => LifecycleState == JobLifecycleState.Running;
        public bool IsAwaitingSelection => LifecycleState == JobLifecycleState.AwaitingSelection;
        public bool IsTerminal => LifecycleState == JobLifecycleState.Terminal;
        public bool IsSuccessfulTerminal => Successful(StateSnapshot);
        public bool IsUnsuccessfulTerminal
        {
            get { var current = StateSnapshot; return current.LifecycleState == JobLifecycleState.Terminal && !Successful(current); }
        }
        public bool IsSkippedAlreadyExists => StateSnapshot is
            { TerminalOutcome: JobTerminalOutcome.Skipped, SkipReason: JobSkipReason.AlreadyExists };
        public bool IsSkippedNotFoundLastTime => StateSnapshot is
            { TerminalOutcome: JobTerminalOutcome.Skipped, SkipReason: JobSkipReason.NotFoundLastTime };

        private static bool Successful(JobStateSnapshot current) => current.TerminalOutcome == JobTerminalOutcome.Succeeded
            || current is { TerminalOutcome: JobTerminalOutcome.Skipped, SkipReason: JobSkipReason.AlreadyExists };

        // Extractor hints — set by extractors, consumed by JobPreparer when preparing this job's
        // Config and JobContext. JobPreparer clears them after use so they don't linger.
        public FileConditionPatch?      ExtractorCond         { get; set; }
        public FileConditionPatch?      ExtractorPrefCond     { get; set; }
        public FolderConditionPatch?    ExtractorFolderCond   { get; set; }
        public FolderConditionPatch?    ExtractorPrefFolderCond { get; set; }
        public bool                     EnablesIndexByDefault { get; set; }
        // Resolved source identity inherited by jobs produced by an extractor.
        // Auto-profile matching reads this instead of depending on a materialized
        // parent settings object being reused for every descendant.
        public InputType?               SourceInputType { get; set; }
        public JobSelectionSource?     SelectionSource { get; set; }
        // Outermost List provenance for presentation. Keep this separate from
        // SourceInputType: that immediate extractor identity selects auto-profiles.
        public InputType?               SourceCollectionInputType { get; set; }

        // Display / identity
        public string? ItemName { get; set; }

        // Durable submission ownership is distinct from workflow grouping. A
        // workflow may contain several independently accepted follow-up
        // submissions, while every generated node belongs to exactly one of them.
        public Guid? SubmissionId { get; set; }
        private JobRetention? retention;
        public JobRetention Retention
        {
            get => retention ?? ((EffectivePrintOption & (PrintOption.Jobs | PrintOption.Results | PrintOption.Link | PrintOption.Json)) != 0
                ? JobRetention.Memory : this is SearchJob ? JobRetention.SearchResults : JobRetention.History);
            internal set => retention = value;
        }
        public JobSemanticRole SemanticRole { get; set; } = JobSemanticRole.UserRoot;
        public DateTimeOffset? CreatedAtUtc { get; set; }
        public string? SubmissionSpecificationJson { get; set; }
        public Guid? RerunOfSubmissionId { get; set; }
        public Guid? PreviewId { get; set; }
        public string? ArtifactId { get; set; }

        // The immutable effective definition used when this job itself performs
        // a search. Captured when searching starts, before observations. It is needed
        // for search-capable children created after their root submission was
        // accepted, so retained results never fall back to current defaults.
        public SearchDefinition? ExecutedSearchDefinition { get; internal set; }
        public SearchSession? SearchObservationSession { get; internal set; }

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

        public JobFailureReason FailureReason => StateSnapshot.FailureReason;
        public string? FailureMessage => StateSnapshot.FailureMessage;
        public string? FailureDetail => StateSnapshot.FailureDetail;

        public JobStateSnapshot StateSnapshot { get { lock (stateGate) return state; } }

        // Structural/discovery publications share this revision clock with lifecycle
        // transitions. The caller receives the exact state belonging to that revision.
        internal JobStateSnapshot CaptureState(bool advanceRevision = false)
        {
            lock (stateGate)
            {
                if (advanceRevision) state = state with { Revision = checked(state.Revision + 1) };
                return state;
            }
        }

        private void ApplyStateTransition(Func<JobStateSnapshot, JobStateSnapshot> transition)
        {
            lock (stateGate)
            {
                var before = state;
                var after = transition(before);
                if (before == after) return;
                state = after = after with { Revision = checked(before.Revision + 1) };
                if (StateChanged is null && PropertyChanged is null) return;
                (stateNotifications ??= new()).Enqueue(new(before, after));
                if (publishingState) return;
                publishingState = true;
            }
            // A reentrant/concurrent transition queues behind this notification.
            // No subscriber runs under the mutation gate or rereads individual
            // lifecycle fields to reconstruct the transition it is observing.
            ExceptionDispatchInfo? failure = null;
            while (true)
            {
                JobStateTransition change;
                lock (stateGate)
                {
                    if (!stateNotifications!.TryDequeue(out change))
                    {
                        publishingState = false;
                        break;
                    }
                }
                if (StateChanged is { } observers)
                    foreach (Action<Job, JobStateTransition> observer in Delegate.EnumerateInvocationList(observers))
                        try { observer(this, change); }
                        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
                var (before, after) = change;
                Notify(nameof(LifecycleState), before.LifecycleState != after.LifecycleState);
                Notify(nameof(ActivityPhase), before.ActivityPhase != after.ActivityPhase);
                Notify(nameof(ActivityUntilUtc), before.ActivityUntilUtc != after.ActivityUntilUtc);
                Notify(nameof(TerminalOutcome), before.TerminalOutcome != after.TerminalOutcome);
                Notify(nameof(SkipReason), before.SkipReason != after.SkipReason);
                Notify(nameof(CancellationSource), before.CancellationSource != after.CancellationSource);
                Notify(nameof(FailureReason), before.FailureReason != after.FailureReason);
                Notify(nameof(FailureMessage), before.FailureMessage != after.FailureMessage);
                Notify(nameof(FailureDetail), before.FailureDetail != after.FailureDetail);
            }
            failure?.Throw();

            void Notify(string name, bool changed)
            {
                if (!changed || PropertyChanged is not { } observers) return;
                var args = new PropertyChangedEventArgs(name);
                foreach (PropertyChangedEventHandler observer in Delegate.EnumerateInvocationList(observers))
                    try { observer(this, args); }
                    catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
            }
        }

        public void Fail(JobFailureReason reason, string? message = null, string? detail = null)
        {
            if (reason == JobFailureReason.Cancelled)
                throw new ArgumentException("Use SetCancelled(source) for cancellation terminal state.", nameof(reason));
            ApplyStateTransition(current => Terminal(current, JobTerminalOutcome.Failed) with
                { FailureReason = reason, FailureMessage = message, FailureDetail = detail });
        }

        public void SetCancelled(JobCancellationSource source, string? message = null, string? detail = null)
        {
            if (source == JobCancellationSource.None)
                throw new ArgumentException("Cancellation terminal state must include a non-None source.", nameof(source));
            ApplyStateTransition(current => Terminal(current, JobTerminalOutcome.Cancelled) with
            {
                CancellationSource = current.CancellationSource == JobCancellationSource.None ? source : current.CancellationSource,
                FailureReason = JobFailureReason.Cancelled, FailureMessage = message, FailureDetail = detail,
            });
        }

        public void UpdateActivity(JobActivityPhase phase, DateTimeOffset? untilUtc = null)
            => ApplyStateTransition(current => current.LifecycleState == JobLifecycleState.Terminal ? current : current with
            {
                LifecycleState = phase == JobActivityPhase.None || current.LifecycleState == JobLifecycleState.AwaitingSelection
                    ? current.LifecycleState : JobLifecycleState.Running,
                ActivityPhase = phase, ActivityUntilUtc = phase == JobActivityPhase.None ? null : untilUtc,
            });

        public void SetAwaitingSelection()
            => ApplyStateTransition(current => current.LifecycleState == JobLifecycleState.Terminal ? current : current with
            {
                LifecycleState = JobLifecycleState.AwaitingSelection, ActivityPhase = JobActivityPhase.None,
                ActivityUntilUtc = null, TerminalOutcome = JobTerminalOutcome.None, SkipReason = JobSkipReason.None,
            });

        public virtual void SetDone()
            => ApplyStateTransition(current => Terminal(current, JobTerminalOutcome.Succeeded) with
                { FailureReason = JobFailureReason.None, FailureMessage = null, FailureDetail = null });

        public virtual void SetAlreadyExists() => SetSkipped(JobSkipReason.AlreadyExists);

        public void SetSkipped(JobSkipReason skipReason = JobSkipReason.None, JobFailureReason reason = JobFailureReason.None)
            => ApplyStateTransition(current => Terminal(current, JobTerminalOutcome.Skipped) with
                { SkipReason = skipReason, FailureReason = reason, FailureMessage = null, FailureDetail = null });

        public void SetPartialSuccess(string? message = null, JobCancellationSource cancellationSource = JobCancellationSource.None)
            => ApplyStateTransition(current => Terminal(current, JobTerminalOutcome.PartialSuccess) with
            {
                CancellationSource = current.CancellationSource == JobCancellationSource.None ? cancellationSource : current.CancellationSource,
                FailureMessage = message, FailureDetail = null, FailureReason = JobFailureReason.Other,
            });

        public void ResetToPending()
        {
            ExistingFileSource = null;
            if (this is FileDownloadJob file) file.Reused = false;
            ApplyStateTransition(current => default(JobStateSnapshot) with { Revision = current.Revision });
        }

        private static JobStateSnapshot Terminal(JobStateSnapshot current, JobTerminalOutcome outcome)
            => current with
            {
                LifecycleState = JobLifecycleState.Terminal, ActivityPhase = JobActivityPhase.None,
                ActivityUntilUtc = null, TerminalOutcome = outcome, SkipReason = JobSkipReason.None,
            };

        // Subclasses declare their default; callers can override with CanBeSkippedOverride.
        protected abstract bool DefaultCanBeSkipped { get; }
        public bool? CanBeSkippedOverride { get; set; }
        public bool  CanBeSkipped => CanBeSkippedOverride ?? DefaultCanBeSkipped;

        // Primary query used for display and key computation. Non-leaf types return null.
        public virtual SongQuery? QueryTrack => null;

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
            SourceInputType        = src.SourceInputType;
            SelectionSource        = src.SelectionSource;
            SourceInfo             = src.SourceInfo;
            SourceCollectionInputType = src.SourceCollectionInputType;
            ItemNumber            = src.ItemNumber;
            LineNumber            = src.LineNumber;
            SourceMutation        = src.SourceMutation;
            CanBeSkippedOverride  = src.CanBeSkippedOverride;
            WorkflowId            = src.WorkflowId;
            SubmissionId          = src.SubmissionId;
            retention             = src.retention;
            SemanticRole          = src.SemanticRole;
            CreatedAtUtc          = src.CreatedAtUtc;
            RerunOfSubmissionId   = src.RerunOfSubmissionId;
            PreviewId             = src.PreviewId;
            ArtifactId            = src.ArtifactId;
            ExtractorInfo         = src.ExtractorInfo;
            DownloadBehaviorPolicy = src.DownloadBehaviorPolicy;
        }
    }
