using Sockseek.Core.Jobs;
using Soulseek;

namespace Sockseek.Core.Models;
    // Engine-internal session object for one file download in progress.
    // Holds only what the engine needs: the job, the chosen file, and the CTS.
    // No progress bar, no stale bookkeeping, no display logic — those belong in the CLI layer.
    public class ActiveDownload
    {
        public SongJob       Song      { get; }
        public FileCandidate Candidate { get; }
        public CancellationTokenSource Cts { get; }

        // Set by the Soulseek client callbacks for live display and command handling.
        public Transfer? Transfer { get; set; }
        public bool IsManuallySkipped { get; set; }

        public ActiveDownload(SongJob song, FileCandidate candidate, CancellationTokenSource cts)
        {
            Song      = song;
            Candidate = candidate;
            Cts       = cts;
        }
    }
