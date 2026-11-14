using Sockseek.Core;
using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;

    public sealed record FallbackSource(string Provider, string? Title = null, string? Reference = null);
    // Unified song job. Used for both search+download and pre-resolved downloads.
    // If ResolvedTarget is non-null the engine skips the search phase.
    public class SongJob : FileDownloadJob, IUpgradeable
    {
        public SongQuery Query { get; set; }

        // Provider-owned provenance, retained independently of peer candidates.
        private FallbackSource? fallback;
        public FallbackSource? Fallback
        {
            get => fallback;
            set
            {
                if (fallback == value) return;
                fallback = value;
                NotifyExecutionChanged();
                OnPropertyChanged();
            }
        }

        public override SongQuery QueryTrack => Query;
        // A selected peer file is an exact request, not a music-history lookup.
        protected override bool DefaultCanBeSkipped => ExactTarget == null;

        // True for non-audio files inside album folders (cover art, .txt, etc.).
        // Prefer the remote candidate classification, but fall back to a known local
        // path for already-existing/index-restored standalone files.
        public bool IsNotAudio => ResolvedPeerTarget != null
            ? !Utils.IsMusicFile(ResolvedPeerTarget.Filename)
            : !string.IsNullOrWhiteSpace(DownloadPath)
                ? !Utils.IsMusicFile(DownloadPath)
                : false;

        // Populated after search; ordered best-first. Null = not yet searched.
        public List<FileCandidate>? Candidates { get; set; }

        // Pre-set download target. When non-null the search phase is skipped.
        // After download this holds the chosen candidate.
        private FileCandidate? _resolvedTarget;
        public FileCandidate? ResolvedTarget
        {
            get => _resolvedTarget;
            set { if (_resolvedTarget != value) { _resolvedTarget = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResolvedPeerTarget)); } }
        }

        // Execution-scoped evidence for the current exact-file attempt. This is
        // not a resolved target: a failed candidate must not change discovery or
        // the retained outcome, but live transfer snapshots may show its peer.
        internal FileCandidate? ActiveCandidate { get; set; }

        // Observed execution evidence, never a discovery/preselection input.
        // Unlike ActiveCandidate it survives failure/cancellation for diagnostics.
        private FileCandidate? lastAttemptedCandidate;
        public FileCandidate? LastAttemptedCandidate
        {
            get => lastAttemptedCandidate;
            internal set
            {
                if (lastAttemptedCandidate == value) return;
                lastAttemptedCandidate = value;
                OnPropertyChanged();
            }
        }

        // Exact preselection without invented search-response evidence. Search-selected
        // songs continue to expose their search-selected ResolvedTarget.
        private PeerFileTarget? _exactTarget;
        public PeerFileTarget? ExactTarget
        {
            get => _exactTarget;
            set { if (_exactTarget != value) { _exactTarget = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResolvedPeerTarget)); } }
        }

        public PeerFileTarget? ResolvedPeerTarget => ResolvedTarget?.Target ?? ExactTarget;

        private SongDownloadSource _downloadSource = SongDownloadSource.None;
        public SongDownloadSource DownloadSource
        {
            get => _downloadSource;
            set { if (_downloadSource != value) { _downloadSource = value; OnPropertyChanged(); } }
        }

        public SongJob(SongQuery query)
        {
            Query = query;
        }

        public void SetDone(
            string? downloadPath,
            FileCandidate? candidate = null,
            SongDownloadSource downloadSource = SongDownloadSource.None)
        {
            if (candidate != null)
                ResolvedTarget = candidate;
            if (downloadPath != null)
                DownloadPath = downloadPath;
            if (downloadSource != SongDownloadSource.None)
                DownloadSource = downloadSource;
            else if (candidate != null)
                DownloadSource = SongDownloadSource.Soulseek;
            base.SetDone();
        }

        public override string ToString(bool noInfo) => Query.ToString(noInfo);
        public override string ToString()             => Query.ToString();

        public IEnumerable<Job> Upgrade(bool album, bool group)
        {
            if (album && group)
            {
                var newJob = new AlbumGroupsJob(AlbumQuery.FromSongQuery(Query));
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
            else if (group)
            {
                var newJob = new SongGroupsJob(Query);
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
