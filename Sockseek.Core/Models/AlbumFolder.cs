using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Core.Models;
    public class AlbumFolder
    {
        public string        Username   { get; }
        public string        FolderPath { get; }
        public List<SongJob> Files      => files.Value;
        public int           SearchFileCount { get; }
        public int           SearchAudioFileCount { get; }
        public int[]         SearchSortedAudioLengths { get; }
        public string?       SearchRepresentativeAudioFilename { get; }
        public AlbumAudioQualityCoverage SearchAudioQualityCoverage { get; }
        public bool          HasSearchMetadata { get; }
        public bool          IsFullyRetrieved { get; set; }
        internal ResultSorter.SortEntry? SearchAggregateSortEntry { get; }

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
            SearchAudioQualityCoverage = AlbumAudioQualityCoverage.Inactive(SearchAudioFileCount);
            HasSearchMetadata = true;
            SearchAggregateSortEntry = null;
        }

        public AlbumFolder(string username, string folderPath, Func<List<SongJob>> filesFactory)
            : this(username, folderPath, filesFactory, 0, 0, [], null, AlbumAudioQualityCoverage.Inactive(0), hasSearchMetadata: false)
        {
        }

        public AlbumFolder(
            string username,
            string folderPath,
            Func<List<SongJob>> filesFactory,
            int searchAudioFileCount,
            int[] searchSortedAudioLengths,
            string? searchRepresentativeAudioFilename)
            : this(username, folderPath, filesFactory, searchAudioFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, AlbumAudioQualityCoverage.Inactive(searchAudioFileCount), hasSearchMetadata: true)
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
            : this(username, folderPath, filesFactory, searchFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, AlbumAudioQualityCoverage.Inactive(searchAudioFileCount), hasSearchMetadata: true, searchAggregateSortEntry: null)
        {
        }

        internal AlbumFolder(
            string username,
            string folderPath,
            Func<List<SongJob>> filesFactory,
            int searchFileCount,
            int searchAudioFileCount,
            int[] searchSortedAudioLengths,
            string? searchRepresentativeAudioFilename,
            AlbumAudioQualityCoverage searchAudioQualityCoverage,
            ResultSorter.SortEntry? searchAggregateSortEntry)
            : this(username, folderPath, filesFactory, searchFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, searchAudioQualityCoverage, hasSearchMetadata: true, searchAggregateSortEntry)
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
            AlbumAudioQualityCoverage searchAudioQualityCoverage,
            bool hasSearchMetadata,
            ResultSorter.SortEntry? searchAggregateSortEntry = null)
        {
            Username = username;
            FolderPath = folderPath;
            files = new Lazy<List<SongJob>>(filesFactory);
            SearchFileCount = searchFileCount;
            SearchAudioFileCount = searchAudioFileCount;
            SearchSortedAudioLengths = searchSortedAudioLengths;
            SearchRepresentativeAudioFilename = searchRepresentativeAudioFilename;
            SearchAudioQualityCoverage = searchAudioQualityCoverage;
            HasSearchMetadata = hasSearchMetadata;
            SearchAggregateSortEntry = searchAggregateSortEntry;
        }
    }
