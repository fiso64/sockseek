using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Server;

namespace Sockseek.Cli;

// A completed CLI renderer may collect all cursor pages. These types are a
// private adapter shared by local and remote CLI modes, not an HTTP contract or
// an alternative projection model.
internal sealed record FileSearchProjectionRequestDto(
    SongQueryDto? SongQuery = null,
    bool IncludeFullResults = false);

internal sealed record FolderSearchProjectionRequestDto(
    AlbumQueryDto AlbumQuery,
    bool IncludeFiles = false);

internal sealed record AggregateTrackProjectionRequestDto(
    SongQueryDto? SongQuery = null,
    bool IncludeCandidates = false);

internal sealed record AggregateAlbumProjectionRequestDto(
    AlbumQueryDto? AlbumQuery = null,
    bool IncludeFolders = false);

internal sealed record SearchResultSnapshotDto<T>(
    int Revision,
    bool IsComplete,
    IReadOnlyList<T> Items,
    string? PersistenceState = null,
    DateTimeOffset? ResultsPrunedAtUtc = null);

internal sealed record FileCandidateDto(
    FileCandidateRefDto Ref,
    string Username,
    string Filename,
    PeerInfoDto Peer,
    FileMetadataDto File,
    SearchResultVisibility Visibility = SearchResultVisibility.Public,
    SearchPreferenceTier? PreferenceTier = null,
    bool? NecessaryConditionsSatisfied = null,
    IReadOnlyList<SearchPreferenceCondition>? SatisfiedPreferredConditions = null,
    IReadOnlyList<SearchPreferenceCondition>? UnsatisfiedPreferredConditions = null);

internal sealed record AlbumFolderDto(
    AlbumFolderRefDto Ref,
    string Username,
    string FolderPath,
    PeerInfoDto Peer,
    int FileCount,
    int AudioFileCount,
    IReadOnlyList<FileCandidateDto>? Files = null,
    bool IsFullyRetrieved = false);

internal sealed record AggregateTrackCandidateDto(
    SongQueryDto Query,
    string? ItemName,
    List<FileCandidateDto>? Candidates = null);

internal sealed record AggregateAlbumCandidateDto(
    AlbumQueryDto Query,
    string? ItemName,
    List<AlbumFolderDto>? Folders = null);

internal static class CliSearchProjectionMapper
{
    public static FileCandidateDto ToDto(FileCandidate candidate)
        => new(
            new FileCandidateRefDto(candidate.Username, candidate.Filename),
            candidate.Username,
            candidate.Filename,
            new PeerInfoDto(
                candidate.Username,
                candidate.HasFreeUploadSlot,
                candidate.UploadSpeed,
                candidate.QueueLength,
                candidate.ObservedAtUtc),
            new FileMetadataDto(
                Utils.GetFileNameSlsk(candidate.Filename),
                candidate.Size,
                candidate.Extension,
                candidate.BitRate,
                candidate.BitDepth,
                candidate.SampleRate,
                candidate.Length,
                candidate.Attributes?.Select(x => new FileAttributeDto(x.Type, x.Value)).ToList()),
            candidate.Visibility,
            candidate.ProjectionFacts?.PreferenceTier,
            candidate.ProjectionFacts?.NecessaryConditionsSatisfied,
            candidate.ProjectionFacts?.SatisfiedPreferredConditions,
            candidate.ProjectionFacts?.UnsatisfiedPreferredConditions);

    public static FileCandidateDto ToDto(SearchViewFileDto file)
        => new(
            new FileCandidateRefDto(file.Peer.Username, file.RemoteFilename),
            file.Peer.Username,
            file.RemoteFilename,
            file.Peer,
            file.File,
            file.Visibility.ToCore(),
            file.PreferenceTier.ToCore(),
            file.NecessaryConditionsSatisfied,
            file.SatisfiedPreferredConditions.Select(condition => condition.ToCore()).ToArray(),
            file.UnsatisfiedPreferredConditions.Select(condition => condition.ToCore()).ToArray());

    public static AlbumFolderDto ToDto(AlbumFolder folder, bool includeFiles)
    {
        FileCandidate? representative = folder.Files.FirstOrDefault()?.Candidate;
        return new(
            new AlbumFolderRefDto(folder.Username, folder.FolderPath),
            folder.Username,
            folder.FolderPath,
            new PeerInfoDto(
                folder.Username,
                representative?.HasFreeUploadSlot,
                representative?.UploadSpeed,
                representative?.QueueLength,
                representative?.ObservedAtUtc),
            folder.IsFullyRetrieved ? folder.Files.Count : folder.SearchFileCount,
            folder.IsFullyRetrieved ? folder.Files.Count(file => !file.IsNotAudio) : folder.SearchAudioFileCount,
            includeFiles ? folder.Files.Select(file => ToDto(file.Candidate)).ToList() : null,
            folder.IsFullyRetrieved);
    }

    public static AlbumFolderDto ToDto(
        SearchViewDirectoryDto directory,
        IReadOnlyList<FileCandidateDto> children,
        bool includeFiles)
        => new(
            new AlbumFolderRefDto(directory.Ref.Username, directory.Ref.FolderPath),
            directory.Ref.Username,
            directory.Ref.FolderPath,
            directory.Peer,
            checked((int)(directory.PublicMatchingFileCount + directory.LockedMatchingFileCount)),
            children.Count(file => Utils.IsMusicFile(file.Filename)),
            includeFiles ? children : null,
            directory.RetrievalState == SearchViewDirectoryRetrievalState.Complete);

    public static AlbumFolder ToCore(AlbumFolderDto folder)
        => new(
            folder.Username,
            folder.FolderPath,
            () => folder.Files?.Select(ToAlbumFile).ToList() ?? [])
        {
            IsFullyRetrieved = folder.IsFullyRetrieved,
        };

    public static FileCandidate ToCore(FileCandidateDto candidate)
    {
        SearchConditionFacts? facts = candidate.PreferenceTier is { } tier
            ? new SearchConditionFacts(
                candidate.NecessaryConditionsSatisfied ?? true,
                tier == SearchPreferenceTier.Preferred,
                candidate.SatisfiedPreferredConditions ?? [],
                (candidate.SatisfiedPreferredConditions ?? [])
                    .Concat(candidate.UnsatisfiedPreferredConditions ?? [])
                    .Distinct()
                    .ToArray())
            : null;
        return new FileCandidate(
            new PeerFileTarget(
                new PeerFileIdentity(candidate.Username, candidate.Filename),
                candidate.File.Size < 0 ? null : candidate.File.Size,
                candidate.File.Extension ?? Path.GetExtension(candidate.Filename),
                candidate.File.BitRate,
                candidate.File.BitDepth,
                candidate.File.SampleRate,
                candidate.File.Length,
                candidate.File.Attributes?.Select(x => new FileAttributeSnapshot(x.Type, x.Value)).ToList()),
            new SearchPeerSnapshot(
                candidate.Username,
                responseFileCount: 0,
                candidate.Peer.UploadSpeed,
                candidate.Peer.HasFreeUploadSlot,
                candidate.Peer.QueueLength,
                candidate.Peer.ObservedAtUtc),
            new FileSearchEvidence(
                0,
                0,
                candidate.Peer.ObservedAtUtc ?? DateTimeOffset.UnixEpoch,
                candidate.Visibility),
            facts);
    }

    private static AlbumFile ToAlbumFile(FileCandidateDto file)
    {
        FileCandidate candidate = ToCore(file);
        return AlbumFile.WithLazyQuery(
            () => Searcher.InferSongQuery(candidate.Filename, new SongQuery()),
            candidate);
    }
}
