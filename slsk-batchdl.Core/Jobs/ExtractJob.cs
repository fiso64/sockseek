using Sldl.Core;
using Sldl.Core.Models;

namespace Sldl.Core.Jobs;
    // Holds a single input string to be resolved by an extractor.
    // The engine runs the appropriate extractor, sets Result, then processes Result as a child.
    // The ExtractJob itself stays in the tree as a historical record of what was extracted.
    public class ExtractJob : Job
    {
        public string     Input     { get; }
        public InputType? InputType { get; set; }
        public bool AutoProcessResult { get; set; } = true;

        // Set by the engine after extraction. Null until the engine processes this job.
        public Job? Result { get; set; }

        protected override bool DefaultCanBeSkipped => false;

        public ExtractJob(string input, InputType? inputType = null)
        {
            Input     = input;
            InputType = inputType;
        }

        public override string ToString(bool noInfo) => Input;
    }
