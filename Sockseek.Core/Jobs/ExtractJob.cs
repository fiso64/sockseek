using Sockseek.Api;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;
    // Holds a single input string to be resolved by an extractor.
    // The engine runs the appropriate extractor, sets Result, then processes Result as a child.
    // The ExtractJob itself stays in the tree as a historical record of what was extracted.
    public class ExtractJob : Job
    {
        public override JobSnapshotKind Kind => JobSnapshotKind.Extract;

        public string     Input     { get; }
        // Used only while planning an upload. The retained input keeps its original name.
        public string? UploadedFilePath { get; private set; }
        public void ReadFromUpload(string path)
        {
            UploadedFilePath = path;
            ItemName = Input;
        }

        public InputType? InputType { get; set; }
        public ExtractionMode? RequestedModeOverride { get; set; }
        private bool autoProcessResult = true;
        public bool AutoProcessResult
        {
            get => autoProcessResult;
            set
            {
                if (autoProcessResult == value) return;
                autoProcessResult = value;
                OnPropertyChanged();
            }
        }
        public DownloadBehaviorPolicy? ResultDownloadBehaviorPolicy { get; set; }

        // Set by the engine after extraction. Null until the engine processes this job.
        private IReadOnlyList<Job> children = [];
        internal override IReadOnlyList<Job> Children => children;
        public Job? Result
        {
            get => children is [var result] ? result : null;
            set
            {
                if (ReferenceEquals(Result, value)) return;
                children = value is null ? [] : [value];
                OnPropertyChanged();
            }
        }

        // Set by JobPlanner. Runtime consumes the captured result/failure and
        // must not read the source or invoke the extractor again.
        public bool HasPlannedExtraction { get; set; }
        public string? PlannedExtractionFailure { get; set; }

        protected override bool DefaultCanBeSkipped => false;

        public ExtractJob(string input, InputType? inputType = null)
        {
            Input     = input;
            InputType = inputType;
        }

        // An extractor can enumerate a playlist position even when the provider
        // withholds its metadata. Planning and direct execution report it alike.
        internal string? SourceUnavailableReason { get; private init; }

        internal static ExtractJob UnavailablePlaylistEntry(string input, InputType provider, int position)
            => new(input, provider)
            {
                ItemNumber = position,
                ItemName = $"Track {position} unavailable",
                SourceUnavailableReason = $"{provider} returned no track details for playlist position {position}.",
            };

        public override string ToString(bool noInfo) => Input;
    }
