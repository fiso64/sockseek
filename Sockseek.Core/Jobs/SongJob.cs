using Sockseek.Core;
using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;
    // Unified song job. Used for both search+download and pre-resolved downloads.
    // If ResolvedTarget is non-null the engine skips the search phase.
    public class SongJob : Job, IUpgradeable
    {
        public SongQuery Query { get; set; }

        // YouTube-specific display metadata (title + uploader JSON). Not a search hint.
        public string? Other { get; set; }

        public override SongQuery QueryTrack => Query;
        protected override bool  DefaultCanBeSkipped => true;

        // True for non-audio files inside album folders (cover art, .txt, etc.).
        // Computed from the candidate filename.
        public bool IsNotAudio => ResolvedTarget != null
            ? !Utils.IsMusicFile(ResolvedTarget.Filename)
            : false;

        // Populated after search; ordered best-first. Null = not yet searched.
        public List<FileCandidate>? Candidates { get; set; }

        // Pre-set download target. When non-null the search phase is skipped.
        // After download this holds the chosen candidate.
        private FileCandidate? _resolvedTarget;
        public FileCandidate? ResolvedTarget
        {
            get => _resolvedTarget;
            set { if (_resolvedTarget != value) { _resolvedTarget = value; OnPropertyChanged(); OnPropertyChanged(nameof(ChosenCandidate)); } }
        }

        // Alias kept for consumer compat — same backing field as ResolvedTarget.
        public FileCandidate? ChosenCandidate
        {
            get => _resolvedTarget;
            set => ResolvedTarget = value;
        }

        private string? _downloadPath;
        public string? DownloadPath
        {
            get => _downloadPath;
            set { if (_downloadPath != value) { _downloadPath = value; OnPropertyChanged(); } }
        }

        private SongDownloadSource _downloadSource = SongDownloadSource.None;
        public SongDownloadSource DownloadSource
        {
            get => _downloadSource;
            set { if (_downloadSource != value) { _downloadSource = value; OnPropertyChanged(); } }
        }

        private long _bytesTransferred;
        public long BytesTransferred
        {
            get => _bytesTransferred;
            set
            {
                if (_bytesTransferred != value)
                {
                    _bytesTransferred = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Progress));
                }
            }
        }

        private long _fileSize;
        public long FileSize
        {
            get => _fileSize;
            set { if (_fileSize != value) { _fileSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(Progress)); } }
        }

        public double Progress => FileSize > 0 ? (double)BytesTransferred / FileSize : 0;

        public SongJob(SongQuery query)
        {
            Query = query;
        }

        public override void SetDone()
            => SetDone(downloadPath: null);

        public void SetDone(
            string? downloadPath,
            FileCandidate? candidate = null,
            SongDownloadSource downloadSource = SongDownloadSource.None)
        {
            if (candidate != null)
                ChosenCandidate = candidate;
            if (downloadPath != null)
                DownloadPath = downloadPath;
            if (downloadSource != SongDownloadSource.None)
                DownloadSource = downloadSource;
            else if (candidate != null)
                DownloadSource = SongDownloadSource.Soulseek;
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

        public override string ToString(bool noInfo) => Query.ToString(noInfo);
        public override string ToString()             => Query.ToString();

        public IEnumerable<Job> Upgrade(bool album, bool aggregate)
        {
            if (album && aggregate)
            {
                var newJob = new AlbumAggregateJob(AlbumQuery.FromSongQuery(Query));
                newJob.CopySharedFieldsFrom(this);
                if (Query.Title.Length > 0)
                {
                    newJob.ExtractorFolderCond ??= new FolderConditionPatch();
                    newJob.ExtractorFolderCond.AddRequiredTrackTitle(Query.Title);
                }
                newJob.ItemName ??= newJob.ToString(noInfo: true);
                yield return newJob;
            }
            else if (album)
            {
                var newJob = new AlbumJob(AlbumQuery.FromSongQuery(Query));
                newJob.CopySharedFieldsFrom(this);
                if (Query.Title.Length > 0)
                {
                    newJob.ExtractorFolderCond ??= new FolderConditionPatch();
                    newJob.ExtractorFolderCond.AddRequiredTrackTitle(Query.Title);
                }
                newJob.UpgradeSources.Add(new SongQuery(Query));
                yield return newJob;
            }
            else if (aggregate)
            {
                var newJob = new AggregateJob(Query);
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
