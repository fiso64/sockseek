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

[JsonConverter(typeof(JsonStringEnumConverter<ServerDownloadBehavior>))]
public enum ServerDownloadBehavior
{
    Automatic,
    Manual,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerIncompleteAlbumActionKind>))]
public enum ServerIncompleteAlbumActionKind
{
    Move,
    Delete,
    Keep,
}

public enum ServerSkipMode
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
    Soulseek = 6,
    MusicBrainz = 7,
    None = -1,
}

public enum ServerExtractionMode
{
    Song = 0,
    Album = 1,
    General = 2,
}

public enum ServerAlbumArtOption
{
    Default = 0,
    Most = 1,
    Largest = 2,
}

public enum ServerSearchSettingsBaselineKind
{
    Generic = 0,
    Music = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerSearchDefaultProjectionKind>))]
public enum ServerSearchDefaultProjectionKind
{
    GenericFile,
    Track,
    Album,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerSearchViewProjectionKind>))]
public enum ServerSearchViewProjectionKind
{
    Files,
    GenericDirectories,
    AlbumDirectories,
    AggregateTracks,
    AggregateAlbums,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerSearchViewSortOrder>))]
public enum ServerSearchViewSortOrder
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

[JsonConverter(typeof(JsonStringEnumConverter<ServerSearchResultVisibility>))]
public enum ServerSearchResultVisibility
{
    Public,
    Locked,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerSearchPreferenceTier>))]
public enum ServerSearchPreferenceTier
{
    Preferred,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerSearchPreferenceCondition>))]
public enum ServerSearchPreferenceCondition
{
    Format,
    Length,
    Bitrate,
    SampleRate,
    BitDepth,
    Title,
    Artist,
    Album,
    Username,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerChatTargetKind>))]
public enum ServerChatTargetKind
{
    Direct,
    Room,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerChatMessageDirection>))]
public enum ServerChatMessageDirection
{
    Incoming,
    Outgoing,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerChatMessageState>))]
public enum ServerChatMessageState
{
    Received,
    Pending,
    Sent,
    Failed,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerChatRoomKind>))]
public enum ServerChatRoomKind
{
    Unknown,
    Public,
    Private,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerChatRoomJoinPhase>))]
public enum ServerChatRoomJoinPhase
{
    Disconnected,
    Joining,
    Joined,
    Leaving,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerUserNotificationKind>))]
public enum ServerUserNotificationKind
{
    PrivateMessage,
    RoomMention,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerShareScanPhase>))]
public enum ServerShareScanPhase
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
