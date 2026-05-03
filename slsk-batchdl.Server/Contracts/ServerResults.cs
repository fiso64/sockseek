namespace Sldl.Server;

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
    int? UploadSpeed = null);

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
    int? Length);

/// <summary>
/// Revisioned search result view. Clients can use search.updated events to decide when to refetch.
/// </summary>
/// <param name="Revision">Monotonic revision for this result view.</param>
/// <param name="IsComplete">True when the underlying search job has finished collecting results.</param>
public sealed record SearchResultSnapshotDto<T>(
    int Revision,
    bool IsComplete,
    IReadOnlyList<T> Items);

/// <summary>
/// Downloadable file candidate shown in track search results.
/// </summary>
public sealed record FileCandidateDto(
    FileCandidateRefDto Ref,
    string Username,
    string Filename,
    PeerInfoDto Peer,
    long Size,
    int? BitRate,
    int? SampleRate,
    int? Length,
    string? Extension = null,
    IReadOnlyList<FileAttributeDto>? Attributes = null);

/// <summary>
/// Album folder candidate shown in album search results.
/// </summary>
/// <param name="FileCount">
/// Number of files from this folder that appeared in the search results. This may be lower than the folder's real file count;
/// retrieve the full folder to see authoritative contents.
/// </param>
/// <param name="AudioFileCount">
/// Number of audio files from this folder that appeared in the search results. This may be lower than the folder's real audio-file count;
/// retrieve the full folder to see authoritative contents.
/// </param>
/// <param name="Files">
/// Optional file list. Present only when requested with includeFiles=true. May be incomplete; retrieve the full folder to see authoritative contents.
/// </param>
/// <param name="IsFullyRetrieved">
/// True after the server has browsed the peer's folder and merged the full contents into this result.
/// </param>
public sealed record AlbumFolderDto(
    AlbumFolderRefDto Ref,
    string Username,
    string FolderPath,
    PeerInfoDto Peer,
    int FileCount,
    int AudioFileCount,
    IReadOnlyList<FileCandidateDto>? Files = null,
    bool IsFullyRetrieved = false);

/// <summary>
/// Aggregate track candidate produced by aggregate search result views.
/// </summary>
public sealed record AggregateTrackCandidateDto(
    SongQueryDto Query,
    string? ItemName,
    List<FileCandidateDto>? Candidates = null);

/// <summary>
/// Aggregate album candidate produced by album-aggregate search result views.
/// </summary>
public sealed record AggregateAlbumCandidateDto(
    AlbumQueryDto Query,
    string? ItemName,
    List<AlbumFolderDto>? Folders = null);

/// <summary>
/// Soulseek file attribute pair.
/// </summary>
public sealed record FileAttributeDto(
    string Type,
    int Value);
