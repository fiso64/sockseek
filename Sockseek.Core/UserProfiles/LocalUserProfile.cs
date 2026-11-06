using Sockseek.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core.Services;

namespace Sockseek.Core.UserProfiles;

/// <summary>Immutable daemon-lifetime local profile advertised to allowed peers.</summary>
public sealed record LocalUserProfile(string Description, UserPicture? Picture)
{
    public static async Task<LocalUserProfile> LoadAsync(
        EngineSettings settings,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string description = UserProfileText.NormalizeDescription(settings.UserDescription);
        UserPicture? picture = null;
        if (!string.IsNullOrWhiteSpace(settings.UserPicturePath))
        {
            try
            {
                picture = await UserPictureCodec.LoadAndNormalizeLocalAsync(
                    settings.UserPicturePath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SoulseekLogMessages.ProfilePictureTimedOut(
                    logger ?? NullLogger.Instance);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or SixLabors.ImageSharp.ImageProcessingException)
            {
                SoulseekLogMessages.ProfilePictureRejected(
                    logger ?? NullLogger.Instance,
                    ex.GetType().Name);
            }
        }

        return new LocalUserProfile(description, picture);
    }
}
