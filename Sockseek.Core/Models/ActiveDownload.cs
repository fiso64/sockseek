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

        // Set by the Soulseek client stateChanged callback; read by UpdateLoop for stale detection.
        public Transfer? Transfer { get; set; }
        public bool IsManuallySkipped { get; set; }
        public DateTimeOffset? LastTransferActivityUtc { get; private set; }

        private TransferStates? observedState;
        private long? observedBytesTransferred;

        public ActiveDownload(SongJob song, FileCandidate candidate, CancellationTokenSource cts)
        {
            Song      = song;
            Candidate = candidate;
            Cts       = cts;
        }

        internal bool ObserveTransferSnapshot(DateTimeOffset now)
        {
            var state = Transfer?.State;
            var bytesTransferred = Transfer?.BytesTransferred;

            if (LastTransferActivityUtc.HasValue
                && observedState == state
                && observedBytesTransferred == bytesTransferred)
                return false;

            observedState = state;
            observedBytesTransferred = bytesTransferred;
            LastTransferActivityUtc = now;
            return true;
        }
    }
