using Sldl.Core;
using Sldl.Core.Models;
using Sldl.Core.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sldl.Core.Jobs;

    public class DiscoverySummary
    {
        public int ResultCount { get; set; }
        public int LockedFileCount { get; set; }
    }

    // TODO [ARCHITECTURE]: Convert Job models to immutable types and implement Unidirectional Data Flow.
    // Currently, Jobs act as globally mutable state containers. Properties like `State`, `BytesTransferred`, 
    // and `DownloadPath` are mutated directly by Downloader/Searcher on background threads.
    // Because INotifyPropertyChanged fires on the mutating thread, this forces the UI/CLI layers to use 
    // liberal lock() statements to avoid race conditions and visual tearing.
    // Refactor:
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
        public void Cancel() => Cts?.Cancel();

        private Settings.DownloadSettings? _config;
        public Settings.DownloadSettings Config
        {
            get => _config!;
            set { if (_config != value) { _config = value; OnPropertyChanged(); } }
        }

        private JobState _state = JobState.Pending;
        public JobState State
        {
            get => _state;
            private set { if (_state != value) { _state = value; OnPropertyChanged(); } }
        }

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

        // Discovery results (populated during Search or Folder Retrieval phases)
        public DiscoverySummary? Discovery { get; set; }

        // Job-level outcome (set after the job completes or fails)
        private FailureReason _failureReason = FailureReason.None;
        public FailureReason FailureReason
        {
            get => _failureReason;
            private set { if (_failureReason != value) { _failureReason = value; OnPropertyChanged(); } }
        }

        // Optional human-readable explanation for the failure (complements FailureReason).
        public string? FailureMessage { get; private set; }

        public void Fail(FailureReason reason, string? message = null)
        {
            FailureMessage = message;
            FailureReason = reason;
            State = JobState.Failed;
        }

        public void UpdateState(JobState state)
        {
            if (state is JobState.Failed)
                throw new InvalidOperationException("Use Fail() to transition to Failed state.");
            if (state is JobState.Done or JobState.AlreadyExists)
                throw new InvalidOperationException("Use SetDone() or SetAlreadyExists() to transition to terminal states.");
            if (state is JobState.Skipped or JobState.NotFoundLastTime)
                throw new InvalidOperationException("Use SetSkipped() to transition to skipped states.");
            State = state;
        }

        public virtual void SetDone()
        {
            State = JobState.Done;
        }

        public virtual void SetAlreadyExists()
        {
            State = JobState.AlreadyExists;
        }

        public void SetSkipped(JobState skipState, FailureReason reason = FailureReason.None)
        {
            if (skipState != JobState.Skipped && skipState != JobState.AlreadyExists && skipState != JobState.NotFoundLastTime)
                throw new ArgumentException("skipState must be a skipped state type.", nameof(skipState));

            FailureReason = reason;
            State = skipState;
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
                SldlLog.Info(line);
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
            CanBeSkippedOverride  = src.CanBeSkippedOverride;
            WorkflowId            = src.WorkflowId;
            DownloadBehaviorPolicy = src.DownloadBehaviorPolicy;
        }
    }
