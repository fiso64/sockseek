using System.Diagnostics;
using Sockseek.Core.Jobs;

namespace Sockseek.Core.Services;

public partial class Searcher
{
    private sealed class DiscoveryProgress : IDisposable
    {
        private readonly Lock gate = new();
        private readonly Searcher owner;
        private readonly Job job;
        private readonly SearchSession session;
        private long? lastUpdate;
        private bool disposed;

        public DiscoveryProgress(Searcher owner, Job job, SearchSession session)
        {
            this.owner = owner;
            this.job = job;
            this.session = session;
            session.ResponseAdded += OnResponseAdded;
        }

        private void OnResponseAdded(SearchSession _)
        {
            lock (gate)
            {
                if (disposed || lastUpdate is long timestamp
                    && Stopwatch.GetElapsedTime(timestamp) < TimeSpan.FromMilliseconds(100))
                    return;

                lastUpdate = Stopwatch.GetTimestamp();
                owner.UpdateDiscoveryProgress(job, session);
            }
        }

        public void Flush()
        {
            lock (gate)
                owner.UpdateDiscoveryProgress(job, session);
        }

        public void Dispose()
        {
            session.ResponseAdded -= OnResponseAdded;
            lock (gate)
            {
                disposed = true;
                // Also publish exact counts when a search fails or is cancelled.
                owner.UpdateDiscoveryProgress(job, session);
            }
        }
    }
}
