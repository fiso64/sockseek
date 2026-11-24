using System.Text.Json.Serialization;

namespace Sockseek.Api;

public enum ServerPrintOption
{
    None = 0,
    Jobs = 1,
    Results = 2,
    Full = 4,
    Link = 8,
    Json = 16,
    Index = 32,
    IndexFailed = 64,
}

[JsonConverter(typeof(JsonStringEnumConverter<DownloadBehavior>))]
public enum DownloadBehavior
{
    Automatic,
    Manual,
}

[JsonConverter(typeof(JsonStringEnumConverter<IncompleteAlbumActionKind>))]
public enum IncompleteAlbumActionKind
{
    Move,
    Delete,
    Keep,
}

public enum SkipMode
{
    Name = 0,
    Tag = 2,
    Index = 4,
}

public enum ServerInputType
{
    CSV = 0,
    YouTube = 1,
    Spotify = 2,
    Bandcamp = 3,
    String = 4,
    List = 5,
    [JsonStringEnumMemberName("soulseek-link")]
    SoulseekLink = 6,
    MusicBrainz = 7,
    SoundCloud = 8,
    None = -1,
}

public enum ExtractionMode
{
    Song = 0,
    Album = 1,
    General = 2,
}

public enum AlbumArtOption
{
    Default = 0,
    Most = 1,
    Largest = 2,
}

public enum SearchSettingsBaselineKind
{
    Generic = 0,
    Music = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchDefaultProjectionKind>))]
public enum SearchDefaultProjectionKind
{
    GenericFile,
    Track,
    Album,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewProjectionKind>))]
public enum SearchViewProjectionKind
{
    Files,
    GenericDirectories,
    AlbumDirectories,
    TrackGroups,
    AlbumGroups,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewSortOrder>))]
public enum SearchViewSortOrder
{
    Relevance,
    UploadSpeed,
    QueueDepth,
    ItemSizeAscending,
    ItemSizeDescending,
    DirectorySizeAscending,
    DirectorySizeDescending,
    FileCountAscending,
    FileCountDescending,
    DirectoryNameAscending,
    DirectoryNameDescending,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchResultVisibility>))]
public enum SearchResultVisibility
{
    Public,
    Locked,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchPreferenceTier>))]
public enum SearchPreferenceTier
{
    Preferred,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchPreferenceCondition>))]
public enum SearchPreferenceCondition
{
    Format = 0,
    Length = 1,
    Bitrate = 2,
    SampleRate = 3,
    BitDepth = 4,
    Title = 5,
    Artist = 6,
    Album = 7,
    Username = 8,
    ExcludeLocked = 9,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatTargetKind>))]
public enum ChatTargetKind
{
    Direct,
    Room,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageDirection>))]
public enum ChatMessageDirection
{
    Incoming,
    Outgoing,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageState>))]
public enum ChatMessageState
{
    Received,
    Pending,
    Sent,
    Failed,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatRoomKind>))]
public enum ChatRoomKind
{
    Unknown,
    Public,
    Private,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatRoomJoinPhase>))]
public enum ChatRoomJoinPhase
{
    Disconnected,
    Joining,
    Joined,
    Leaving,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<UserNotificationKind>))]
public enum UserNotificationKind
{
    PrivateMessage,
    RoomMention,
}

[JsonConverter(typeof(JsonStringEnumConverter<ShareScanPhase>))]
public enum ShareScanPhase
{
    Idle,
    Preparing,
    Enumerating,
    FinalizingIndex,
    BuildingBrowseArtifact,
    Validating,
    Publishing,
    Completed,
    Cancelling,
    Cancelled,
    Failed,
}
