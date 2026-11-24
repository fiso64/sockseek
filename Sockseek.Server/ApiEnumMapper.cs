using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Chat;
using Sockseek.Core.Jobs;
using Sockseek.Core.Planning;
using Sockseek.Core.Snapshots;
using System.Collections.Frozen;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;

// TODO [ARCHITECTURE]: Core and the API declare enums under the same names (TransferPhase,
// TransferFailureReason, TransferGroupKind), so server code qualifies every
// use. Prefix the API ones or map them at one boundary type.
// TODO [ARCHITECTURE]: Cancellation has four vocabularies (job, download transfer, upload,
// API), and a user's cancel is marked on the cancelled job only, so a transfer
// two levels below reports OwningJob. Mark the subtree, and reduce the enums.
namespace Sockseek.Server;

/// <summary>Owns enum translation between the public wire contract and Core.</summary>
public static class ApiEnumMapper
{
    public static TransferFailureReason ToServer(this Sockseek.Core.Events.TransferFailureReason? reason)
        => reason switch
        {
            Sockseek.Core.Events.TransferFailureReason.ConnectionFailed => TransferFailureReason.ConnectionFailed,
            Sockseek.Core.Events.TransferFailureReason.PeerFailure => TransferFailureReason.Unknown,
            Sockseek.Core.Events.TransferFailureReason.Denied => TransferFailureReason.Denied,
            Sockseek.Core.Events.TransferFailureReason.LocalStorageError => TransferFailureReason.LocalStorageError,
            Sockseek.Core.Events.TransferFailureReason.FileChanged => TransferFailureReason.FileChanged,
            Sockseek.Core.Events.TransferFailureReason.Stale or Sockseek.Core.Events.TransferFailureReason.TimedOut => TransferFailureReason.TransferTimedOut,
            Sockseek.Core.Events.TransferFailureReason.Finalization or Sockseek.Core.Events.TransferFailureReason.Unknown => TransferFailureReason.Unknown,
            _ => TransferFailureReason.None,
        };

    public static Api.TransferPhase ToServer(this Sockseek.Core.Snapshots.TransferPhase phase)
        => EnumMap<Sockseek.Core.Snapshots.TransferPhase, Api.TransferPhase>.Get(phase);

    public static TransferFailureReason ToServer(this UploadFailureReason reason)
        => reason switch
        {
            UploadFailureReason.NotShared => TransferFailureReason.FileNoLongerShared,
            UploadFailureReason.Unavailable => TransferFailureReason.FileUnavailable,
            UploadFailureReason.InvalidOffset => TransferFailureReason.InvalidOffset,
            UploadFailureReason.Denied => TransferFailureReason.Denied,
            UploadFailureReason.InternalFailure => TransferFailureReason.InternalError,
            UploadFailureReason.ConnectionFailed => TransferFailureReason.ConnectionFailed,
            UploadFailureReason.TimedOut => TransferFailureReason.TransferTimedOut,
            _ => TransferFailureReason.None,
        };

    public static TransferCancellationSource ToServer(this Sockseek.Core.Events.TransferCancellationReason? reason)
        => reason switch
        {
            Sockseek.Core.Events.TransferCancellationReason.Requested or Sockseek.Core.Events.TransferCancellationReason.ManualSkip
                => TransferCancellationSource.User,
            Sockseek.Core.Events.TransferCancellationReason.OwningJob => TransferCancellationSource.OwningJob,
            Sockseek.Core.Events.TransferCancellationReason.DaemonShutdown => TransferCancellationSource.DaemonShutdown,
            _ => TransferCancellationSource.None,
        };

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

    public static DownloadBehavior ToCore(this ServerDownloadBehavior value) => EnumMap<ServerDownloadBehavior, DownloadBehavior>.Get(value);

    public static ServerIncompleteAlbumActionKind ToServer(this IncompleteAlbumActionKind value) => EnumMap<IncompleteAlbumActionKind, ServerIncompleteAlbumActionKind>.Get(value);

    public static ServerSkipMode ToServer(this SkipMode value) => EnumMap<SkipMode, ServerSkipMode>.Get(value);

    public static ServerInputType ToServer(this InputType value) => EnumMap<InputType, ServerInputType>.Get(value);
    public static InputType ToCore(this ServerInputType value) => EnumMap<ServerInputType, InputType>.Get(value);

    public static ServerExtractionMode ToServer(this ExtractionMode value) => EnumMap<ExtractionMode, ServerExtractionMode>.Get(value);
    public static ExtractionMode ToCore(this ServerExtractionMode value) => EnumMap<ServerExtractionMode, ExtractionMode>.Get(value);

    public static ServerAlbumArtOption ToServer(this AlbumArtOption value) => EnumMap<AlbumArtOption, ServerAlbumArtOption>.Get(value);

    public static ServerSearchSettingsBaselineKind ToServer(this SearchSettingsBaselineKind value) => EnumMap<SearchSettingsBaselineKind, ServerSearchSettingsBaselineKind>.Get(value);

    public static ServerSearchDefaultProjectionKind ToServer(this SearchDefaultProjectionKind value) => EnumMap<SearchDefaultProjectionKind, ServerSearchDefaultProjectionKind>.Get(value);

    public static ServerSearchViewProjectionKind ToServer(this SearchViewProjectionKind value) => EnumMap<SearchViewProjectionKind, ServerSearchViewProjectionKind>.Get(value);
    public static SearchViewProjectionKind ToCore(this ServerSearchViewProjectionKind value) => EnumMap<ServerSearchViewProjectionKind, SearchViewProjectionKind>.Get(value);

    public static ServerSearchViewSortOrder ToServer(this SearchViewSortOrder value) => EnumMap<SearchViewSortOrder, ServerSearchViewSortOrder>.Get(value);
    public static SearchViewSortOrder ToCore(this ServerSearchViewSortOrder value) => EnumMap<ServerSearchViewSortOrder, SearchViewSortOrder>.Get(value);

    public static ServerSearchResultVisibility ToServer(this SearchResultVisibility value) => EnumMap<SearchResultVisibility, ServerSearchResultVisibility>.Get(value);
    public static SearchResultVisibility ToCore(this ServerSearchResultVisibility value) => EnumMap<ServerSearchResultVisibility, SearchResultVisibility>.Get(value);

    public static ServerSearchPreferenceTier ToServer(this SearchPreferenceTier value) => EnumMap<SearchPreferenceTier, ServerSearchPreferenceTier>.Get(value);
    public static SearchPreferenceTier ToCore(this ServerSearchPreferenceTier value) => EnumMap<ServerSearchPreferenceTier, SearchPreferenceTier>.Get(value);

    public static ServerSearchPreferenceCondition ToServer(this SearchPreferenceCondition value) => EnumMap<SearchPreferenceCondition, ServerSearchPreferenceCondition>.Get(value);
    public static SearchPreferenceCondition ToCore(this ServerSearchPreferenceCondition value) => EnumMap<ServerSearchPreferenceCondition, SearchPreferenceCondition>.Get(value);

    public static ServerChatTargetKind ToServer(this ChatTargetKind value) => EnumMap<ChatTargetKind, ServerChatTargetKind>.Get(value);

    public static ServerChatMessageDirection ToServer(this ChatMessageDirection value) => EnumMap<ChatMessageDirection, ServerChatMessageDirection>.Get(value);

    public static ServerChatMessageState ToServer(this ChatMessageState value) => EnumMap<ChatMessageState, ServerChatMessageState>.Get(value);

    public static ServerChatRoomKind ToServer(this ChatRoomKind value) => EnumMap<ChatRoomKind, ServerChatRoomKind>.Get(value);

    public static ServerChatRoomJoinPhase ToServer(this ChatRoomJoinPhase value) => EnumMap<ChatRoomJoinPhase, ServerChatRoomJoinPhase>.Get(value);

    public static ServerUserNotificationKind ToServer(this UserNotificationKind value) => EnumMap<UserNotificationKind, ServerUserNotificationKind>.Get(value);
    public static UserNotificationKind ToCore(this ServerUserNotificationKind value) => EnumMap<ServerUserNotificationKind, UserNotificationKind>.Get(value);

    public static ServerShareScanPhase ToServer(this ShareScanPhase value) => EnumMap<ShareScanPhase, ServerShareScanPhase>.Get(value);

    public static ServerJobKind ToServer(this JobSnapshotKind value)
        => EnumMap<JobSnapshotKind, ServerJobKind>.Values.GetValueOrDefault(value, ServerJobKind.Generic);

    public static ServerJobRole ToServer(this JobSemanticRole value)
        => EnumMap<JobSemanticRole, ServerJobRole>.Get(value);

    public static ServerJobLifecycleState ToServer(this JobLifecycleState value)
        => EnumMap<JobLifecycleState, ServerJobLifecycleState>.Get(value);

    public static ServerJobActivityPhase ToServer(this JobActivityPhase value)
        => EnumMap<JobActivityPhase, ServerJobActivityPhase>.Get(value);

    public static ServerJobTerminalOutcome ToServer(this JobTerminalOutcome value)
        => EnumMap<JobTerminalOutcome, ServerJobTerminalOutcome>.Get(value);

    public static ServerSongDownloadSource ToServer(this SongDownloadSource value)
        => EnumMap<SongDownloadSource, ServerSongDownloadSource>.Get(value);

    public static ServerJobSkipReason ToServer(this JobSkipReason value)
        => EnumMap<JobSkipReason, ServerJobSkipReason>.Get(value);

    public static ServerJobFailureReason? ToServer(this JobFailureReason value)
        => value == JobFailureReason.None ? null : EnumMap<JobFailureReason, ServerJobFailureReason>.Get(value);

    public static ServerJobCancellationSource ToServer(this JobCancellationSource value)
        => EnumMap<JobCancellationSource, ServerJobCancellationSource>.Get(value);

    public static ServerFolderRetrievalOutcome ToServer(this FolderRetrievalOutcome value)
        => EnumMap<FolderRetrievalOutcome, ServerFolderRetrievalOutcome>.Get(value);

    private static class EnumMap<TSource, TTarget>
        where TSource : struct, Enum
        where TTarget : struct, Enum
    {
        internal static readonly FrozenDictionary<TSource, TTarget> Values = Build();

        private static FrozenDictionary<TSource, TTarget> Build()
        {
            var values = new Dictionary<TSource, TTarget>();
            foreach (var source in Enum.GetValues<TSource>())
                if (Enum.TryParse<TTarget>(source.ToString(), out var target))
                    values[source] = target;
            return values.ToFrozenDictionary();
        }

        internal static TTarget Get(TSource value) => Values.TryGetValue(value, out var target) ? target
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{typeof(TSource).Name}.{value} has no {typeof(TTarget).Name} mapping.");
    }

    private static int ValidatePrintOption(int value, string parameterName)
    {
        if ((value & ~KnownPrintOptions) == 0)
            return value;

        throw new ArgumentOutOfRangeException(parameterName, value, "Unknown print-option flags.");
    }
}
