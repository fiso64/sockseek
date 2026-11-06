using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<UserProfilePresence>))]
public enum UserProfilePresence
{
    [JsonStringEnumMemberName("online")]
    Online,
    [JsonStringEnumMemberName("away")]
    Away,
    [JsonStringEnumMemberName("offline")]
    Offline,
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

public sealed record UserPresenceObservationDto(
    string Username,
    UserProfilePresence Presence,
    UserProfileSectionDto Status,
    DateTimeOffset? ObservedAtUtc);

public sealed record UserPictureRefDto(
    string Url,
    string MediaType,
    int ByteLength,
    string ETag);

public sealed record UserProfileDto(
    string Username,
    bool UploadAccessBlocked,
    bool PrivateMessagesBlocked,
    UserProfilePresence Presence,
    UserProfileSectionDto Status,
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
