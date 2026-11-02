using Sockseek.Core;
using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;
    public enum FolderRetrievalOutcome
    {
        None,
        Completed,
        Cancelled,
        Failed,
    }

    public class RetrieveFolderJob : Job
    {
        public PeerDirectoryIdentity Directory { get; }
        private PeerDirectorySnapshot? result;
        public PeerDirectorySnapshot? Result
        {
            get => result;
            set
            {
                if (ReferenceEquals(result, value)) return;
                result = value;
                NotifyStructureChanged();
            }
        }
        public int NewFilesFoundCount { get; set; }
        public FolderRetrievalOutcome RetrievalOutcome { get; set; } = FolderRetrievalOutcome.None;
        /// <summary>
        /// Optional caller-side association invoked after retrieval and before the
        /// terminal snapshot is published. It may return a caller-relative added
        /// count without introducing album/search data into this generic job.
        /// </summary>
        public Func<PeerDirectorySnapshot, CancellationToken, int>? ResultObserver { get; init; }
        public bool RetrievalCompleted => RetrievalOutcome == FolderRetrievalOutcome.Completed;
        public bool RetrievalCancelled => RetrievalOutcome == FolderRetrievalOutcome.Cancelled;

        public RetrieveFolderJob(PeerDirectoryIdentity directory)
        {
            ArgumentNullException.ThrowIfNull(directory);
            directory.RequireAdvertisedFolder();
            Directory = directory;
            ItemName = $"{directory.Username}\\{directory.FolderPath.Replace('/', '\\').TrimStart('\\')}";
        }

        public override SongQuery? QueryTrack => null;
        public override string ToString() => ItemName ?? Directory.FolderPath;
        protected override bool DefaultCanBeSkipped => false;
    }
