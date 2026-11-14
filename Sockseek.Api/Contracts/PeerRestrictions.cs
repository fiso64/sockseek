using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<UserRestrictionKind>))]
public enum UserRestrictionKind
{
    [JsonStringEnumMemberName("upload-access")]
    UploadAccess,
    [JsonStringEnumMemberName("private-messages")]
    PrivateMessages,
}

[JsonConverter(typeof(JsonStringEnumConverter<UserRestrictionOverrideState>))]
public enum UserRestrictionOverrideState
{
    [JsonStringEnumMemberName("blocked")]
    Blocked,
    [JsonStringEnumMemberName("allowed")]
    Allowed,
}

public sealed record UsernameRestrictionStateDto(
    bool IsBlocked,
    bool ConfiguredUsernameBlocked,
    UserRestrictionOverrideState? Override);

public sealed record UserRestrictionsDto(
    string Username,
    UsernameRestrictionStateDto UploadAccess,
    UsernameRestrictionStateDto PrivateMessages);

/// <summary>
/// Sets one exact-username restriction override. Null removes that override and
/// returns only that restriction kind to its configured baseline.
/// </summary>
public sealed record SetUserRestrictionOverrideRequestDto(
    UserRestrictionKind Kind,
    UserRestrictionOverrideState? Override);
