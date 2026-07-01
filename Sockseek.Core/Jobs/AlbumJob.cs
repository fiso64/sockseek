using Sockseek.Core;
using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;
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
        private AlbumFolder? _trackJobFolder;
        public AlbumFolder? ResolvedTarget
        {
            get => _resolvedTarget;
            set { if (!ReferenceEquals(_resolvedTarget, value)) { _resolvedTarget = value; OnPropertyChanged(); } }
        }

        // Runtime child jobs for the currently selected/downloaded album folder.
        // AlbumFolder.Files stays as pure search-result candidate data.
        public List<SongJob> TrackJobs { get; } = [];

        public List<SongJob> EnsureTrackJobs(AlbumFolder folder)
        {
            if (!ReferenceEquals(_trackJobFolder, folder))
            {
                TrackJobs.Clear();
                _trackJobFolder = folder;
            }

            var existing = TrackJobs
                .Where(song => song.ResolvedTarget != null)
                .Select(song => (song.ResolvedTarget!.Username, song.ResolvedTarget!.Filename))
                .ToHashSet();

            foreach (var file in folder.Files)
            {
                var key = (file.Candidate.Username, file.Candidate.Filename);
                if (existing.Add(key))
                    TrackJobs.Add(CreateTrackJob(file));
            }

            return TrackJobs;
        }

        internal static SongJob CreateTrackJob(AlbumFile file)
            => new(new SongQuery(file.Query))
            {
                ResolvedTarget = file.Candidate,
                Candidates = [file.Candidate],
            };

        public void ClearTrackJobs()
        {
            TrackJobs.Clear();
            _trackJobFolder = null;
        }

        // When a folder is explicitly selected up front (for example from an interactive
        // SearchJob flow), this controls whether the engine may still browse the selected
        // folder for additional files during album download.
        public bool AllowBrowseResolvedTarget { get; set; } = true;

        // When a folder is explicitly accepted by a user, the candidate has already passed
        // the human selection step; hidden folder contents do not need to be browsed just
        // to prove min/max track-count conditions before downloading.
        public bool SkipResolvedTargetTrackCountVerification { get; set; }

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

        public override void SetDone()
            => SetDone(downloadPath: null);

        public void SetDone(string? downloadPath)
        {
            if (downloadPath != null)
                DownloadPath = downloadPath;
            base.SetDone();
        }

        public override void SetAlreadyExists()
            => SetAlreadyExists(path: null);

        public void SetAlreadyExists(string? path)
        {
            if (path != null)
                DownloadPath = path;
            base.SetAlreadyExists();
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
