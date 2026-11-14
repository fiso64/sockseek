using Sockseek.Core.Services;

namespace Sockseek.Core.Models;

/// <summary>
/// The peer and advertised folder path. An empty path groups observed bare
/// filenames only; it never denotes the peer root or a retrievable directory.
/// </summary>
public sealed record PeerDirectoryIdentity
{
    public PeerDirectoryIdentity(string username, string folderPath)
    {
        Username = PeerIdentityValidator.ValidateUsername(username);
        ArgumentNullException.ThrowIfNull(folderPath);
        FolderPath = folderPath.Length == 0 ? folderPath : PeerIdentityValidator.ValidateRemotePath(folderPath);
    }

    public string Username { get; }
    public string FolderPath { get; }

    public void RequireAdvertisedFolder()
    {
        if (FolderPath.Length == 0)
            throw new ArgumentException("These results did not advertise a folder. Select their observed files instead.");
    }
}

/// <summary>An owned, immutable result of retrieving one peer directory subtree.</summary>
public sealed record PeerDirectorySnapshot
{
    public PeerDirectorySnapshot(
        PeerDirectoryIdentity identity,
        IReadOnlyList<PeerFileTarget> files,
        bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(files);

        var owned = files.ToArray();
        if (owned.Any(file => file is null))
            throw new ArgumentException("Directory files cannot contain null targets.", nameof(files));
        if (owned.Any(file => !StringComparer.Ordinal.Equals(file.Username, identity.Username)))
            throw new ArgumentException("Every directory file must belong to the directory peer.", nameof(files));
        if (owned.Select(file => file.Identity).Distinct().Count() != owned.Length)
            throw new ArgumentException("A directory snapshot cannot contain duplicate exact targets.", nameof(files));

        Identity = identity;
        Files = Array.AsReadOnly(owned);
        IsComplete = isComplete;
    }

    public PeerDirectoryIdentity Identity { get; }
    public IReadOnlyList<PeerFileTarget> Files { get; }
    public bool IsComplete { get; }
}

/// <summary>Album-search facts which are not part of directory identity.</summary>
public sealed class AlbumSearchEvidence
{
    public AlbumSearchEvidence(
        int searchFileCount,
        int searchAudioFileCount,
        IReadOnlyList<int> searchSortedAudioLengths,
        string? searchRepresentativeAudioFilename,
        AlbumAudioQualityCoverage searchAudioQualityCoverage,
        bool hasSearchMetadata)
        : this(
            searchFileCount,
            searchAudioFileCount,
            searchSortedAudioLengths,
            searchRepresentativeAudioFilename,
            searchAudioQualityCoverage,
            hasSearchMetadata,
            groupSortEntry: null)
    {
    }

    internal AlbumSearchEvidence(
        int searchFileCount,
        int searchAudioFileCount,
        IReadOnlyList<int> searchSortedAudioLengths,
        string? searchRepresentativeAudioFilename,
        AlbumAudioQualityCoverage searchAudioQualityCoverage,
        bool hasSearchMetadata,
        ResultSorter.SortEntry? groupSortEntry)
    {
        if (searchFileCount < 0)
            throw new ArgumentOutOfRangeException(nameof(searchFileCount));
        if (searchAudioFileCount < 0 || searchAudioFileCount > searchFileCount)
            throw new ArgumentOutOfRangeException(nameof(searchAudioFileCount));
        ArgumentNullException.ThrowIfNull(searchSortedAudioLengths);

        SearchFileCount = searchFileCount;
        SearchAudioFileCount = searchAudioFileCount;
        SearchSortedAudioLengths = Array.AsReadOnly(searchSortedAudioLengths.ToArray());
        SearchRepresentativeAudioFilename = searchRepresentativeAudioFilename;
        SearchAudioQualityCoverage = searchAudioQualityCoverage;
        HasSearchMetadata = hasSearchMetadata;
        GroupSortEntry = groupSortEntry;
    }

    public int SearchFileCount { get; }
    public int SearchAudioFileCount { get; }
    public IReadOnlyList<int> SearchSortedAudioLengths { get; }
    public string? SearchRepresentativeAudioFilename { get; }
    public AlbumAudioQualityCoverage SearchAudioQualityCoverage { get; }
    public bool HasSearchMetadata { get; }
    internal ResultSorter.SortEntry? GroupSortEntry { get; }
}

/// <summary>Associates album query evidence with one exact peer target.</summary>
public sealed class AlbumFileMatch
{
    private readonly Lazy<SongQuery> query;

    public AlbumFileMatch(SongQuery query, FileCandidate candidate)
        : this(() => query, candidate)
    {
    }

    public static AlbumFileMatch WithLazyQuery(Func<SongQuery> queryFactory, FileCandidate candidate)
        => new(queryFactory, candidate);

    internal static AlbumFileMatch WithProjectionEvidence(
        Func<SongQuery> queryFactory,
        ProjectedFileCandidate file)
        => new(queryFactory, file.Input.ToFileCandidate(file.ConditionFacts), file.ConditionFacts, file.SortKey);

    private AlbumFileMatch(
        Func<SongQuery> queryFactory,
        FileCandidate candidate,
        SearchConditionFacts? conditionFacts = null,
        SearchProjectionSortKey? sortKey = null)
    {
        ArgumentNullException.ThrowIfNull(queryFactory);
        ArgumentNullException.ThrowIfNull(candidate);
        query = new Lazy<SongQuery>(() => new SongQuery(queryFactory()));
        Candidate = candidate;
        ConditionFacts = conditionFacts;
        SortKey = sortKey;
    }

    public SongQuery Query => query.Value;
    public FileCandidate Candidate { get; }
    public PeerFileTarget Target => Candidate.Target;
    internal SearchConditionFacts? ConditionFacts { get; }
    internal SearchProjectionSortKey? SortKey { get; }
}

/// <summary>A peer directory composed with the album evidence that selected it.</summary>
public sealed class AlbumDirectoryCandidate
{
    public AlbumDirectoryCandidate(
        PeerDirectorySnapshot directory,
        IReadOnlyList<AlbumFileMatch> matches,
        AlbumSearchEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(evidence);

        var owned = matches.ToArray();
        if (owned.Any(match => match is null))
            throw new ArgumentException("Album matches cannot contain null values.", nameof(matches));
        if (owned.Any(match => !StringComparer.Ordinal.Equals(match.Target.Username, directory.Identity.Username)))
            throw new ArgumentException("Album matches must belong to the directory peer.", nameof(matches));

        Directory = directory;
        Matches = Array.AsReadOnly(owned);
        Evidence = evidence;
    }

    public PeerDirectorySnapshot Directory { get; }
    public IReadOnlyList<AlbumFileMatch> Matches { get; }
    public AlbumSearchEvidence Evidence { get; }
}
