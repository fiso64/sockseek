using Sockseek.Core.Settings;

namespace Sockseek.Core.Chat;

public static class ChatSettingsValidator
{
    public static void NormalizeAndValidate(EngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Chat);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(settings.Chat.AutoJoinRooms.Count);
        foreach (string room in settings.Chat.AutoJoinRooms)
        {
            string value = ChatIdentity.NormalizeRoom(room);
            if (seen.Add(value))
                normalized.Add(value);
        }
        if (normalized.Count > ChatLimits.MaximumDesiredRooms)
            throw new ArgumentException(
                $"Input error: At most {ChatLimits.MaximumDesiredRooms} auto-join rooms may be configured.");
        settings.Chat.AutoJoinRooms = normalized;
    }
}
