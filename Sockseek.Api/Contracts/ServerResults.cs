namespace Sockseek.Api;

/// <summary>
/// Song query shape used by search, download, and song payloads.
/// </summary>
/// <param name="Artist">Expected artist name, or null when unknown.</param>
/// <param name="Title">Expected track title, or null for artist-level searches.</param>
/// <param name="Album">Optional album hint used for matching/filtering.</param>
/// <param name="Uri">Optional source URI/ID metadata, such as Spotify or YouTube identity.</param>
/// <param name="Length">Expected track length in seconds, or null when unknown.</param>
/// <param name="ArtistMaybeWrong">True when the artist came from weak metadata and should be treated as a hint rather than a strict identity.</param>
public sealed record SongQueryDto(
    string? Artist = null,
    string? Title = null,
    string? Album = null,
    string? Uri = null,
    int? Length = null,
    bool ArtistMaybeWrong = false);

/// <summary>
/// Album query shape used by album search/download jobs.
/// </summary>
/// <param name="Artist">Expected album artist, or null when unknown.</param>
/// <param name="Album">Expected album/folder name, or null for artist-level album searches.</param>
/// <param name="SearchHint">Optional track-title hint used to find albums by a song they contain.</param>
/// <param name="Uri">Optional source URI/ID metadata, such as Spotify or MusicBrainz identity.</param>
/// <param name="ArtistMaybeWrong">True when the artist came from weak metadata and should be treated as a hint rather than a strict identity.</param>
public sealed record AlbumQueryDto(
    string? Artist = null,
    string? Album = null,
    string? SearchHint = null,
    string? Uri = null,
    bool ArtistMaybeWrong = false);

/// <summary>
/// Stable identity for a file candidate within a search result.
/// </summary>
public sealed record FileCandidateRefDto(
    string Username,
    string Filename);

/// <summary>
/// Stable identity for an album folder within an album result view.
/// </summary>
public sealed record AlbumFolderRefDto(
    string Username,
    string FolderPath);

/// <summary>
/// Peer state attached to a search response or folder result.
/// </summary>
public sealed record PeerInfoDto(
    string Username,
    bool? HasFreeUploadSlot = null,
    int? UploadSpeed = null,
    int? QueueLength = null,
    DateTimeOffset? ObservedAtUtc = null);

/// <summary>
/// Raw search result row, primarily for diagnostics or advanced clients.
/// </summary>
public sealed record SearchRawResultDto(
    long Sequence,
    int Revision,
    string Username,
    string Filename,
    long Size,
    int? BitRate,
    int? SampleRate,
    int? Length,
    ServerSearchResultVisibility Visibility = ServerSearchResultVisibility.Public,
    int? QueueLength = null,
    DateTimeOffset? ObservedAtUtc = null);

/// <summary>
/// Presentation-safe facts about a file leaf. This is not a remote identity;
/// resource-specific DTOs retain their own references.
/// </summary>
public sealed record FileMetadataDto(
    string Name,
    long Size,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    IReadOnlyList<FileAttributeDto>? Attributes = null);

/// <summary>
/// Soulseek file attribute pair.
/// </summary>
public sealed record FileAttributeDto(
    string Type,
    int Value);
