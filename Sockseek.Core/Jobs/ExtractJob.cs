using Sockseek.Core;
using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;
    // Holds a single input string to be resolved by an extractor.
    // The engine runs the appropriate extractor, sets Result, then processes Result as a child.
    // The ExtractJob itself stays in the tree as a historical record of what was extracted.
    public class ExtractJob : Job
    {
        public string     Input     { get; }
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
        private Job? result;
        public Job? Result
        {
            get => result;
            set
            {
                if (ReferenceEquals(result, value)) return;
                result = value;
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

        public override string ToString(bool noInfo) => Input;
    }
