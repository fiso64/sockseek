using Sldl.Core.Jobs;

namespace Sldl.Core.Models;
    public class AlbumFolder
    {
        public string        Username   { get; }
        public string        FolderPath { get; }
        public List<SongJob> Files      => files.Value;
        public int           SearchFileCount { get; }
        public int           SearchAudioFileCount { get; }
        public int[]         SearchSortedAudioLengths { get; }
        public string?       SearchRepresentativeAudioFilename { get; }
        public bool          HasSearchMetadata { get; }
        public bool          IsFullyRetrieved { get; set; }

        private readonly Lazy<List<SongJob>> files;

        public AlbumFolder(string username, string folderPath, List<SongJob> files)
        {
            Username = username;
            FolderPath = folderPath;
            this.files = new Lazy<List<SongJob>>(() => files);

            var audioFiles = files
                .Where(f => !f.IsNotAudio && f.ResolvedTarget != null)
                .ToList();
            SearchFileCount = files.Count;
            SearchAudioFileCount = audioFiles.Count;
            SearchSortedAudioLengths = audioFiles
                .Select(f => f.ResolvedTarget!.File.Length ?? -1)
                .OrderBy(x => x)
                .ToArray();
            SearchRepresentativeAudioFilename = audioFiles
                .FirstOrDefault()
                ?.ResolvedTarget!
                .Filename;
            HasSearchMetadata = true;
        }

        public AlbumFolder(string username, string folderPath, Func<List<SongJob>> filesFactory)
            : this(username, folderPath, filesFactory, 0, 0, [], null, hasSearchMetadata: false)
        {
        }

        public AlbumFolder(
            string username,
            string folderPath,
            Func<List<SongJob>> filesFactory,
            int searchAudioFileCount,
            int[] searchSortedAudioLengths,
            string? searchRepresentativeAudioFilename)
            : this(username, folderPath, filesFactory, searchAudioFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, hasSearchMetadata: true)
        {
        }

        public AlbumFolder(
            string username,
            string folderPath,
            Func<List<SongJob>> filesFactory,
            int searchFileCount,
            int searchAudioFileCount,
            int[] searchSortedAudioLengths,
            string? searchRepresentativeAudioFilename)
            : this(username, folderPath, filesFactory, searchFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, hasSearchMetadata: true)
        {
        }

        private AlbumFolder(
            string username,
            string folderPath,
            Func<List<SongJob>> filesFactory,
            int searchFileCount,
            int searchAudioFileCount,
            int[] searchSortedAudioLengths,
            string? searchRepresentativeAudioFilename,
            bool hasSearchMetadata)
        {
            Username = username;
            FolderPath = folderPath;
            files = new Lazy<List<SongJob>>(filesFactory);
            SearchFileCount = searchFileCount;
            SearchAudioFileCount = searchAudioFileCount;
            SearchSortedAudioLengths = searchSortedAudioLengths;
            SearchRepresentativeAudioFilename = searchRepresentativeAudioFilename;
            HasSearchMetadata = hasSearchMetadata;
        }
    }
