using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;

namespace Sockseek.Core.Jobs;
    public enum AlbumDirectoryResolutionPolicy
    {
        CompleteIfNeeded,
        UseSelectedSnapshot,
        RetrieveBeforeSelection,
    }

    public enum AlbumValidationRequirement
    {
        Standard,
        // Explicitly accepted input is not rechecked against discovery conditions.
        UserAccepted,
    }

    // Unified album job. If ResolvedTarget is null the engine searches; once
    // a folder is chosen it's set on ResolvedTarget and download proceeds.
    public class AlbumJob : DirectoryDownloadJob, IUpgradeable
    {
        public AlbumQuery Query { get; set; }

        // SongQuery-shaped view of the album query (used for display and key computation).
        // Recomputed from Query so it stays current after preprocessing.
        public override SongQuery QueryTrack =>
            new SongQuery { Artist = Query.Artist, Title = Query.Album, URI = Query.URI };

        protected override bool DefaultCanBeSkipped => ValidationRequirement != AlbumValidationRequirement.UserAccepted;

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
                ClearDirectoryChildren();
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

        public DirectoryTransferAttempt BeginAlbumTransferAttempt(AlbumFolder folder)
        {
            ArgumentNullException.ThrowIfNull(folder);
            var tracks = EnsureTrackJobs(folder);
            var attempt = BeginDirectoryAttempt(AlbumTransferPlanner.FromSelectedDirectory(folder));
            MaterializeDirectoryChildren(tracks);
            BeginDirectoryTransfer();
            return attempt;
        }

        internal SongJob CreateTrackJob(AlbumFile file)
        {
            var child = new SongJob(new SongQuery(file.Query))
            {
                ResolvedTarget = file.Candidate,
                ExactTarget = !CanBeSkipped ? file.Candidate.Target : null,
                Candidates = [file.Candidate],
                FileSize = file.Candidate.Target.Size,
            };
            SubmissionIdentity.AssignExecutionChild(this, child);
            return child;
        }

        internal List<SongJob> EnsureSupplementalTrackJobs(IReadOnlyList<AlbumFile> files)
        {
            if (ActiveAttempt?.ChildrenMaterialized != true)
                throw new InvalidOperationException("Supplemental directory work requires a materialized active attempt.");
            var jobs = EnsureSelectedTrackJobs(files);
            AddSupplementalDirectoryChildren(jobs);
            return jobs;
        }

        private List<SongJob> EnsureSelectedTrackJobs(IReadOnlyList<AlbumFile> files)
        {
            var existing = new Dictionary<PeerFileIdentity, SongJob>();
            foreach (var song in TrackJobs)
                if (song.ResolvedTarget is { } candidate)
                    existing.TryAdd(candidate.Target.Identity, song);
            var jobs = new List<SongJob>(files.Count);
            foreach (var file in files)
            {
                if (!existing.TryGetValue(file.Candidate.Target.Identity, out var song))
                {
                    song = CreateTrackJob(file);
                    existing.Add(file.Candidate.Target.Identity, song);
                    TrackJobs.Add(song);
                }
                jobs.Add(song);
            }
            return jobs;
        }

        internal List<SongJob> BeginAlbumArtTransferAttempt(
            AlbumFolder folder,
            IReadOnlyList<AlbumFile> selectedImages)
        {
            ArgumentNullException.ThrowIfNull(folder);
            ArgumentNullException.ThrowIfNull(selectedImages);

            var jobs = EnsureSelectedTrackJobs(selectedImages);

            BeginDirectoryAttempt(AlbumTransferPlanner.FromSelectedDirectory(folder, selectedImages));
            MaterializeDirectoryChildren(jobs);
            BeginDirectoryTransfer();
            return jobs;
        }

        public void ClearTrackJobs()
        {
            TrackJobs.Clear();
            _trackJobFolder = null;
            ClearDirectoryChildren();
        }

        // Album-only resolution policy. It explicitly distinguishes a search candidate,
        // an exact user-selected snapshot, and a directory identity which must first be
        // retrieved; the common directory lifecycle does not need workflow booleans.
        public AlbumDirectoryResolutionPolicy DirectoryResolutionPolicy { get; set; }
            = AlbumDirectoryResolutionPolicy.CompleteIfNeeded;

        public AlbumValidationRequirement ValidationRequirement { get; set; }
            = AlbumValidationRequirement.Standard;

        public AlbumJob(AlbumQuery query)
        {
            Query = query;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);

        public IEnumerable<Job> Upgrade(bool album, bool group)
        {
            if (group)
            {
                var newJob = new AlbumGroupsJob(Query);
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
