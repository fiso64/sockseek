using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<UserPresenceState>))]
public enum UserPresenceState
{
    [JsonStringEnumMemberName("online")]
    Online,
    [JsonStringEnumMemberName("away")]
    Away,
    [JsonStringEnumMemberName("offline")]
    Offline,
    [JsonStringEnumMemberName("checking")]
    Checking,
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
    [JsonStringEnumMemberName("disconnected")]
    Disconnected,
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<ResourceSectionState>))]
public enum ResourceSectionState
{
    [JsonStringEnumMemberName("available")]
    Available,
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
    [JsonStringEnumMemberName("timed-out")]
    TimedOut,
}

public sealed record UserProfileSectionDto(
    ResourceSectionState State,
    string? Reason);

/// <summary>Live server status. Offline is confirmed by Soulseek; disconnected
/// and unavailable must never be presented as a peer being offline.</summary>
public sealed record UserPresenceDto(string Username, UserPresenceState State);

public sealed record UserPictureRefDto(
    string Url,
    string MediaType,
    int ByteLength,
    string ETag);

public sealed record UserProfileDto(
    string Username,
    bool UploadAccessBlocked,
    bool PrivateMessagesBlocked,
    UserProfileSectionDto Info,
    UserProfileSectionDto Statistics,
    UserProfileSectionDto PictureSection,
    string? Description,
    long? SharedFileCount,
    long? SharedDirectoryCount,
    long? AverageUploadSpeed,
    int? UploadCount,
    int? UploadSlots,
    int? QueueLength,
    bool? HasFreeUploadSlot,
    UserPictureRefDto? Picture,
    DateTimeOffset ObservedAt);
