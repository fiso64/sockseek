using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Planning;
using Sockseek.Core.Snapshots;
using System.Collections.Frozen;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;

// Enums that mean the same thing to Core and to clients are declared once, in
// the API assembly, and Core uses them directly. Only enums whose public form
// differs from Core's are translated here.
// TODO [ARCHITECTURE]: InputType, JobSnapshotKind, JobSemanticRole and
// FolderRetrievalOutcome have the same members as their API counterparts and
// differ only in wire spelling and name. Share them once stored payloads that
// carry the Core spelling are migrated.
// TODO [ARCHITECTURE]: Core and the API both declare TransferFailureReason and
// TransferGroupKind with different members, so files that see both alias one.
// Rename the API ones or map them at one boundary type.
// TODO [ARCHITECTURE]: Cancellation has four vocabularies (job, download transfer, upload,
// API), and a user's cancel is marked on the cancelled job only, so a transfer
// two levels below reports OwningJob. Mark the subtree, and reduce the enums.
namespace Sockseek.Server;

/// <summary>Translates the enums whose public form differs from Core's.</summary>
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

    public static ServerInputType ToServer(this InputType value) => EnumMap<InputType, ServerInputType>.Get(value);
    public static InputType ToCore(this ServerInputType value) => EnumMap<ServerInputType, InputType>.Get(value);

    public static ServerJobKind ToServer(this JobSnapshotKind value)
        => EnumMap<JobSnapshotKind, ServerJobKind>.Values.GetValueOrDefault(value, ServerJobKind.Generic);

    public static ServerJobRole ToServer(this JobSemanticRole value)
        => EnumMap<JobSemanticRole, ServerJobRole>.Get(value);

    public static JobFailureReason? ToServer(this JobFailureReason value)
        => value == JobFailureReason.None ? null : value;

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
