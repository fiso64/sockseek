using Sockseek.Core.Services;

namespace Sockseek.Core.Models;

public class AlbumFolder
{
    public string Username { get; }
    public string FolderPath { get; }
    public List<AlbumFile> Files => files.Value;
    public int SearchFileCount { get; }
    public int SearchAudioFileCount { get; }
    public int[] SearchSortedAudioLengths => searchSortedAudioLengths.ToArray();
    public string? SearchRepresentativeAudioFilename { get; }
    public AlbumAudioQualityCoverage SearchAudioQualityCoverage { get; }
    public bool HasSearchMetadata { get; }
    public bool IsFullyRetrieved { get; set; }
    internal ResultSorter.SortEntry? SearchGroupSortEntry { get; }

    // Search metadata reads share admitted facts. Files remains the explicit
    // execution/CLI conversion, with independent lazy queries per snapshot.
    internal IReadOnlyList<ProjectedFileCandidate>? ProjectionFiles { get; }
    internal ProjectedFileCandidate? BestProjectionFile { get; }

    public PeerDirectoryIdentity DirectoryIdentity => new(Username, FolderPath);

    public PeerDirectorySnapshot Directory => new(
        DirectoryIdentity,
        Files.Select(file => file.Candidate.Target).ToArray(),
        IsFullyRetrieved);

    private readonly int[] searchSortedAudioLengths;

    private readonly Lazy<List<AlbumFile>> files;

    public AlbumFolder(string username, string folderPath, List<AlbumFile> files)
    {
        Username = username;
        FolderPath = folderPath;
        this.files = new Lazy<List<AlbumFile>>(() => files);

        var audioFiles = files
            .Where(f => !f.IsNotAudio)
            .ToList();
        SearchFileCount = files.Count;
        SearchAudioFileCount = audioFiles.Count;
        searchSortedAudioLengths = audioFiles.Select(file => file.Candidate.Length ?? -1).Order().ToArray();
        SearchRepresentativeAudioFilename = audioFiles.FirstOrDefault()?.Filename;
        SearchAudioQualityCoverage = AlbumAudioQualityCoverage.Inactive(audioFiles.Count);
        HasSearchMetadata = true;
    }

    public AlbumFolder(string username, string folderPath, Func<List<AlbumFile>> filesFactory)
        : this(username, folderPath, filesFactory, 0, 0, [], null, AlbumAudioQualityCoverage.Inactive(0), hasSearchMetadata: false)
    {
    }

    public AlbumFolder(
        string username,
        string folderPath,
        Func<List<AlbumFile>> filesFactory,
        int searchAudioFileCount,
        int[] searchSortedAudioLengths,
        string? searchRepresentativeAudioFilename)
        : this(username, folderPath, filesFactory, searchAudioFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, AlbumAudioQualityCoverage.Inactive(searchAudioFileCount), hasSearchMetadata: true)
    {
    }

    public AlbumFolder(
        string username,
        string folderPath,
        Func<List<AlbumFile>> filesFactory,
        int searchFileCount,
        int searchAudioFileCount,
        int[] searchSortedAudioLengths,
        string? searchRepresentativeAudioFilename)
        : this(username, folderPath, filesFactory, searchFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, AlbumAudioQualityCoverage.Inactive(searchAudioFileCount), hasSearchMetadata: true, searchGroupSortEntry: null)
    {
    }

    internal AlbumFolder(
        string username,
        string folderPath,
        ProjectedFileCandidate[] projectionFiles,
        SongQuery inferDefault,
        int searchAudioFileCount,
        int[] searchSortedAudioLengths,
        string? searchRepresentativeAudioFilename,
        AlbumAudioQualityCoverage searchAudioQualityCoverage,
        EvaluatedAlbumProjectionInput best)
        : this(username, folderPath, () => BuildAlbumFiles(projectionFiles, inferDefault),
            projectionFiles.Length, searchAudioFileCount, searchSortedAudioLengths,
            searchRepresentativeAudioFilename, searchAudioQualityCoverage,
            hasSearchMetadata: true, best.SortEntry)
    {
        // The projection hands off a fresh array; no working list survives
        // here, so later arrivals or Clear cannot change an earlier folder.
        ProjectionFiles = Array.AsReadOnly(projectionFiles);
        BestProjectionFile = best.File;
    }

    private static List<AlbumFile> BuildAlbumFiles(
        IReadOnlyList<ProjectedFileCandidate> projectionFiles, SongQuery inferDefault)
    {
        var result = new List<AlbumFile>(projectionFiles.Count);
        foreach (ProjectedFileCandidate file in projectionFiles)
            result.Add(AlbumFile.WithLazyQuery(
                () => Searcher.InferSongQuery(file.Input.Filename, inferDefault),
                file.Input.ToFileCandidate(file.ConditionFacts)));
        return result;
    }

    private AlbumFolder(
        string username,
        string folderPath,
        Func<List<AlbumFile>> filesFactory,
        int searchFileCount,
        int searchAudioFileCount,
        int[] searchSortedAudioLengths,
        string? searchRepresentativeAudioFilename,
        AlbumAudioQualityCoverage searchAudioQualityCoverage,
        bool hasSearchMetadata,
        ResultSorter.SortEntry? searchGroupSortEntry = null)
    {
        Username = username;
        FolderPath = folderPath;
        files = new Lazy<List<AlbumFile>>(filesFactory);
        if (searchFileCount < 0)
            throw new ArgumentOutOfRangeException(nameof(searchFileCount));
        if (searchAudioFileCount < 0 || searchAudioFileCount > searchFileCount)
            throw new ArgumentOutOfRangeException(nameof(searchAudioFileCount));
        SearchFileCount = searchFileCount;
        SearchAudioFileCount = searchAudioFileCount;
        this.searchSortedAudioLengths = searchSortedAudioLengths.ToArray();
        SearchRepresentativeAudioFilename = searchRepresentativeAudioFilename;
        SearchAudioQualityCoverage = searchAudioQualityCoverage;
        HasSearchMetadata = hasSearchMetadata;
        SearchGroupSortEntry = searchGroupSortEntry;
    }
}
