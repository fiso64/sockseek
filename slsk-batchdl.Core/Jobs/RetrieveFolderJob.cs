using Sldl.Core;
using Sldl.Core.Models;

namespace Sldl.Core.Jobs;
    public class RetrieveFolderJob : Job
    {
        public AlbumFolder TargetFolder { get; set; }
        public int NewFilesFoundCount { get; set; }

        public RetrieveFolderJob(AlbumFolder targetFolder)
        {
            TargetFolder = targetFolder;
        }

        public override SongQuery? QueryTrack => null;
        public override string ToString() => $"RetrieveFolderJob: {TargetFolder.FolderPath}";
        protected override bool DefaultCanBeSkipped => false;
    }
