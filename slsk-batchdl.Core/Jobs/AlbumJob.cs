using Sldl.Core;
using Sldl.Core.Models;

namespace Sldl.Core.Jobs;
    // Unified album job. If ResolvedTarget is null the engine searches; once
    // a folder is chosen it's set on ResolvedTarget and download proceeds.
    public class AlbumJob : Job, IUpgradeable
    {
        public AlbumQuery Query { get; set; }

        // SongQuery-shaped view of the album query (used for display and key computation).
        // Recomputed from Query so it stays current after preprocessing.
        public override SongQuery QueryTrack =>
            new SongQuery { Artist = Query.Artist, Title = Query.Album, URI = Query.URI };

        protected override bool DefaultCanBeSkipped => true;

        // Populated after search. Each element is one candidate folder version.
        public List<AlbumFolder> Results { get; set; } = new();

        // Non-empty only for album jobs produced by upgrading song jobs. Used to safely
        // deduplicate playlist tracks from the same album without merging explicit album jobs.
        public List<SongQuery> UpgradeSources { get; } = new();

        // Set by the engine after the user/callback selects a folder.
        // When pre-set (e.g. direct link), the search phase is skipped.
        private AlbumFolder? _resolvedTarget;
        public AlbumFolder? ResolvedTarget
        {
            get => _resolvedTarget;
            set { if (!ReferenceEquals(_resolvedTarget, value)) { _resolvedTarget = value; OnPropertyChanged(); } }
        }

        // When a folder is explicitly selected up front (for example from an interactive
        // SearchJob flow), this controls whether the engine may still browse the selected
        // folder for additional files during album download.
        public bool AllowBrowseResolvedTarget { get; set; } = true;

        // Used by direct folder links: the target folder identity is known up front,
        // but its file list must be browsed before album download starts.
        public bool ResolvedTargetNeedsInitialFolderRetrieval { get; set; }

        // Set by the engine when the download phase completes.
        private string? _downloadPath;
        public string? DownloadPath
        {
            get => _downloadPath;
            set { if (_downloadPath != value) { _downloadPath = value; OnPropertyChanged(); } }
        }

        // True only when ALL in-progress files in the resolved folder are stale.
        public bool IsStale(int maxStaleTimeMs)
        {
            if (_resolvedTarget == null) return false;
            var inProgress = _resolvedTarget.Files.Where(f => f.State == JobState.Pending).ToList();
            if (inProgress.Count == 0) return false;
            return inProgress.All(f =>
                f.LastActivityTime.HasValue &&
                (DateTime.Now - f.LastActivityTime.Value).TotalMilliseconds > maxStaleTimeMs);
        }

        public List<SongJob> GetStaleFiles(int maxStaleTimeMs)
        {
            if (_resolvedTarget == null) return new();
            return _resolvedTarget.Files
                .Where(f => f.State == JobState.Pending
                    && f.LastActivityTime.HasValue
                    && (DateTime.Now - f.LastActivityTime.Value).TotalMilliseconds > maxStaleTimeMs)
                .ToList();
        }

        public AlbumJob(AlbumQuery query)
        {
            Query = query;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);

        public IEnumerable<Job> Upgrade(bool album, bool aggregate)
        {
            if (aggregate)
            {
                var newJob = new AlbumAggregateJob(Query);
                newJob.CopySharedFieldsFrom(this);
                newJob.ItemName ??= newJob.ToString(noInfo: true);
                yield return newJob;
            }
            else
            {
                yield return this;
            }
        }
    }
