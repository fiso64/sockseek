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
            set { if (_state != value) { _state = value; OnPropertyChanged(); } }
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

        // Source provenance (position in the input file / playlist)
        public int ItemNumber { get; set; } = 1;
        public int LineNumber { get; set; } = 1;

        // Discovery results (populated during Search or Folder Retrieval phases)
        public DiscoverySummary? Discovery { get; set; }

        // Job-level outcome (set after the job completes or fails)
        private FailureReason _failureReason = FailureReason.None;
        public FailureReason FailureReason
        {
            get => _failureReason;
            set { if (_failureReason != value) { _failureReason = value; OnPropertyChanged(); } }
        }

        // Optional human-readable explanation for the failure (complements FailureReason).
        public string? FailureMessage { get; set; }

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
                Logger.Info(line);
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
        }
    }
