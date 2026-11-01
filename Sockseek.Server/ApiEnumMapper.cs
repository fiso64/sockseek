using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Chat;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;

namespace Sockseek.Server;

/// <summary>Owns enum translation between the public wire contract and Core.</summary>
public static class ApiEnumMapper
{
    public static TransferFailureReason ToServer(this Sockseek.Core.Events.TransferFailureReason? reason)
        => reason switch
        {
            Sockseek.Core.Events.TransferFailureReason.PeerFailure => TransferFailureReason.ConnectionFailed,
            Sockseek.Core.Events.TransferFailureReason.Stale => TransferFailureReason.TransferTimedOut,
            Sockseek.Core.Events.TransferFailureReason.Finalization or Sockseek.Core.Events.TransferFailureReason.Unknown => TransferFailureReason.Unknown,
            _ => TransferFailureReason.None,
        };

    public static TransferFailureReason ToServer(this UploadFailureReason reason)
        => reason switch
        {
            UploadFailureReason.NotShared => TransferFailureReason.FileNoLongerShared,
            UploadFailureReason.Unavailable => TransferFailureReason.FileUnavailable,
            UploadFailureReason.InvalidOffset => TransferFailureReason.InvalidOffset,
            UploadFailureReason.Denied => TransferFailureReason.Denied,
            UploadFailureReason.InternalFailure => TransferFailureReason.Unknown,
            _ => TransferFailureReason.None,
        };

    public static TransferCancellationSource ToServer(this Sockseek.Core.Events.TransferCancellationReason? reason)
        => reason is Sockseek.Core.Events.TransferCancellationReason.Requested or Sockseek.Core.Events.TransferCancellationReason.ManualSkip
            ? TransferCancellationSource.User : TransferCancellationSource.None;

    public static TransferCancellationSource ToServer(this UploadCancellationSource reason)
        => reason switch
        {
            UploadCancellationSource.User => TransferCancellationSource.User,
            UploadCancellationSource.Peer => TransferCancellationSource.Peer,
            UploadCancellationSource.DaemonShutdown => TransferCancellationSource.DaemonShutdown,
            UploadCancellationSource.CatalogInvalidation => TransferCancellationSource.CatalogInvalidation,
            _ => TransferCancellationSource.None,
        };

    private const int KnownPrintOptions =
        (int)PrintOption.Jobs
        | (int)PrintOption.Results
        | (int)PrintOption.Full
        | (int)PrintOption.Link
        | (int)PrintOption.Json
        | (int)PrintOption.Index
        | (int)PrintOption.IndexFailed;

    public static ServerPrintOption ToServer(this PrintOption value)
        => (ServerPrintOption)ValidatePrintOption((int)value, nameof(value));

    public static PrintOption ToCore(this ServerPrintOption value)
        => (PrintOption)ValidatePrintOption((int)value, nameof(value));

    public static ServerDownloadBehavior ToServer(this DownloadBehavior value) => Map<ServerDownloadBehavior>(value);
    public static DownloadBehavior ToCore(this ServerDownloadBehavior value) => Map<DownloadBehavior>(value);

    public static ServerIncompleteAlbumActionKind ToServer(this IncompleteAlbumActionKind value) => Map<ServerIncompleteAlbumActionKind>(value);
    public static IncompleteAlbumActionKind ToCore(this ServerIncompleteAlbumActionKind value) => Map<IncompleteAlbumActionKind>(value);

    public static ServerSkipMode ToServer(this SkipMode value) => Map<ServerSkipMode>(value);
    public static SkipMode ToCore(this ServerSkipMode value) => Map<SkipMode>(value);

    public static ServerInputType ToServer(this InputType value) => Map<ServerInputType>(value);
    public static InputType ToCore(this ServerInputType value) => Map<InputType>(value);

    public static ServerExtractionMode ToServer(this ExtractionMode value) => Map<ServerExtractionMode>(value);
    public static ExtractionMode ToCore(this ServerExtractionMode value) => Map<ExtractionMode>(value);

    public static ServerAlbumArtOption ToServer(this AlbumArtOption value) => Map<ServerAlbumArtOption>(value);
    public static AlbumArtOption ToCore(this ServerAlbumArtOption value) => Map<AlbumArtOption>(value);

    public static ServerSearchSettingsBaselineKind ToServer(this SearchSettingsBaselineKind value) => Map<ServerSearchSettingsBaselineKind>(value);

    public static ServerSearchDefaultProjectionKind ToServer(this SearchDefaultProjectionKind value) => Map<ServerSearchDefaultProjectionKind>(value);
    public static SearchDefaultProjectionKind ToCore(this ServerSearchDefaultProjectionKind value) => Map<SearchDefaultProjectionKind>(value);

    public static ServerSearchViewProjectionKind ToServer(this SearchViewProjectionKind value) => Map<ServerSearchViewProjectionKind>(value);
    public static SearchViewProjectionKind ToCore(this ServerSearchViewProjectionKind value) => Map<SearchViewProjectionKind>(value);

    public static ServerSearchViewSortOrder ToServer(this SearchViewSortOrder value) => Map<ServerSearchViewSortOrder>(value);
    public static SearchViewSortOrder ToCore(this ServerSearchViewSortOrder value) => Map<SearchViewSortOrder>(value);

    public static ServerSearchResultVisibility ToServer(this SearchResultVisibility value) => Map<ServerSearchResultVisibility>(value);
    public static SearchResultVisibility ToCore(this ServerSearchResultVisibility value) => Map<SearchResultVisibility>(value);

    public static ServerSearchPreferenceTier ToServer(this SearchPreferenceTier value) => Map<ServerSearchPreferenceTier>(value);
    public static SearchPreferenceTier ToCore(this ServerSearchPreferenceTier value) => Map<SearchPreferenceTier>(value);

    public static ServerSearchPreferenceCondition ToServer(this SearchPreferenceCondition value) => Map<ServerSearchPreferenceCondition>(value);
    public static SearchPreferenceCondition ToCore(this ServerSearchPreferenceCondition value) => Map<SearchPreferenceCondition>(value);

    public static ServerChatTargetKind ToServer(this ChatTargetKind value) => Map<ServerChatTargetKind>(value);
    public static ChatTargetKind ToCore(this ServerChatTargetKind value) => Map<ChatTargetKind>(value);

    public static ServerChatMessageDirection ToServer(this ChatMessageDirection value) => Map<ServerChatMessageDirection>(value);

    public static ServerChatMessageState ToServer(this ChatMessageState value) => Map<ServerChatMessageState>(value);

    public static ServerChatRoomKind ToServer(this ChatRoomKind value) => Map<ServerChatRoomKind>(value);
    public static ChatRoomKind ToCore(this ServerChatRoomKind value) => Map<ChatRoomKind>(value);

    public static ServerChatRoomJoinPhase ToServer(this ChatRoomJoinPhase value) => Map<ServerChatRoomJoinPhase>(value);

    public static ServerUserNotificationKind ToServer(this UserNotificationKind value) => Map<ServerUserNotificationKind>(value);
    public static UserNotificationKind ToCore(this ServerUserNotificationKind value) => Map<UserNotificationKind>(value);

    public static ServerShareScanPhase ToServer(this ShareScanPhase value) => Map<ServerShareScanPhase>(value);

    private static TTarget Map<TTarget>(Enum value) where TTarget : struct, Enum
    {
        if (Enum.TryParse(value.ToString(), out TTarget target) && Enum.IsDefined(target))
            return target;

        throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            $"{value.GetType().Name}.{value} has no {typeof(TTarget).Name} mapping.");
    }

    private static int ValidatePrintOption(int value, string parameterName)
    {
        if ((value & ~KnownPrintOptions) == 0)
            return value;

        throw new ArgumentOutOfRangeException(parameterName, value, "Unknown print-option flags.");
    }
}
