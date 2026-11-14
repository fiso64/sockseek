using Sockseek.Core.Services;

namespace Sockseek.Core.Models;

public class AlbumFolder
{
    public string Username { get; }
    public string FolderPath { get; }
    public List<AlbumFile> Files => files.Value;
    public int SearchFileCount => Evidence.SearchFileCount;
    public int SearchAudioFileCount => Evidence.SearchAudioFileCount;
    public int[] SearchSortedAudioLengths => Evidence.SearchSortedAudioLengths.ToArray();
    public string? SearchRepresentativeAudioFilename => Evidence.SearchRepresentativeAudioFilename;
    public AlbumAudioQualityCoverage SearchAudioQualityCoverage => Evidence.SearchAudioQualityCoverage;
    public bool HasSearchMetadata => Evidence.HasSearchMetadata;
    public bool IsFullyRetrieved { get; set; }
    internal ResultSorter.SortEntry? SearchGroupSortEntry => Evidence.GroupSortEntry;

    // Search metadata reads share admitted facts. Files remains the explicit
    // execution/CLI conversion, with independent lazy queries per snapshot.
    internal IReadOnlyList<ProjectedFileCandidate>? ProjectionFiles { get; }
    internal ProjectedFileCandidate? BestProjectionFile { get; }

    public PeerDirectoryIdentity DirectoryIdentity => new(Username, FolderPath);

    public PeerDirectorySnapshot Directory => new(
        DirectoryIdentity,
        Files.Select(file => file.Candidate.Target).ToArray(),
        IsFullyRetrieved);

    public AlbumSearchEvidence Evidence { get; }

    public AlbumDirectoryCandidate ToDirectoryCandidate()
        => new(
            Directory,
            Files.Select(file => file.Match).ToArray(),
            Evidence);

    private readonly Lazy<List<AlbumFile>> files;

    public AlbumFolder(string username, string folderPath, List<AlbumFile> files)
    {
        Username = username;
        FolderPath = folderPath;
        this.files = new Lazy<List<AlbumFile>>(() => files);

        var audioFiles = files
            .Where(f => !f.IsNotAudio)
            .ToList();
        int searchFileCount = files.Count;
        int searchAudioFileCount = audioFiles.Count;
        int[] searchSortedAudioLengths = audioFiles
            .Select(f => f.Candidate.Length ?? -1)
            .OrderBy(x => x)
            .ToArray();
        string? searchRepresentativeAudioFilename = audioFiles
            .FirstOrDefault()
            ?.Filename;
        Evidence = new AlbumSearchEvidence(
            searchFileCount,
            searchAudioFileCount,
            searchSortedAudioLengths,
            searchRepresentativeAudioFilename,
            AlbumAudioQualityCoverage.Inactive(searchAudioFileCount),
            hasSearchMetadata: true);
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
            result.Add(AlbumFile.WithProjectionEvidence(
                () => Searcher.InferSongQuery(file.Input.Filename, inferDefault),
                file));
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
        Evidence = new AlbumSearchEvidence(
            searchFileCount,
            searchAudioFileCount,
            searchSortedAudioLengths,
            searchRepresentativeAudioFilename,
            searchAudioQualityCoverage,
            hasSearchMetadata,
            searchGroupSortEntry);
    }
}
