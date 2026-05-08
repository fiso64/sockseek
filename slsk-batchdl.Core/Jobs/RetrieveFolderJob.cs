using Sldl.Core;
using Sldl.Core.Models;

namespace Sldl.Core.Jobs;
    public enum FolderRetrievalOutcome
    {
        None,
        Completed,
        Cancelled,
        Failed,
    }

    public class RetrieveFolderJob : Job
    {
        public AlbumFolder TargetFolder { get; set; }
        public int NewFilesFoundCount { get; set; }
        public FolderRetrievalOutcome RetrievalOutcome { get; set; } = FolderRetrievalOutcome.None;
        public bool RetrievalCompleted => RetrievalOutcome == FolderRetrievalOutcome.Completed;
        public bool RetrievalCancelled => RetrievalOutcome == FolderRetrievalOutcome.Cancelled;

        public RetrieveFolderJob(AlbumFolder targetFolder)
        {
            TargetFolder = targetFolder;
        }

        public override SongQuery? QueryTrack => null;
        public override string ToString() => $"RetrieveFolderJob: {TargetFolder.FolderPath}";
        protected override bool DefaultCanBeSkipped => false;
    }
