using System.Text.Json.Serialization;

namespace Sockseek.Core;

    // Legacy index-file state. Values are persisted in user index files; do not reorder.
    // New runtime code should use JobLifecycleState, JobActivityPhase, JobTerminalOutcome,
    // and JobSkipReason. Keep this enum boxed into index read/write compatibility.
    public enum JobStateOld
    {
        Pending          = 0,
        Done             = 1,
        Failed           = 2,
        AlreadyExists    = 3,
        NotFoundLastTime = 4,
    }

    public enum InputType
    {
        CSV,
        YouTube,
        Spotify,
        Bandcamp,
        String,
        List,
        [JsonStringEnumMemberName("soulseek-link")]
        SoulseekLink,
        MusicBrainz,
        SoundCloud,
        None = -1,
    }

    public static class InputTypes
    {
        public static bool TryParse(string? value, out InputType type)
            => Enum.TryParse(value?.Replace("-", ""), ignoreCase: true, out type) && Enum.IsDefined(type);

        public static string ToInputName(this InputType type)
            => type == InputType.SoulseekLink ? "soulseek-link" : type.ToString();
    }

    public enum PrintOption
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

    public enum Verbosity
    {
        Silent,
        Error,
        Warning,
        Normal,
        Verbose
    }

