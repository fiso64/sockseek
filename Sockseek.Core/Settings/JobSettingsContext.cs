using Sockseek.Core.Jobs;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Settings;

/// <summary>
/// The immutable facts settings composition needs. Profiles must not depend on
/// mutable execution state, search sessions, descendant trees or transfer state.
/// Planning and runtime both compose settings from this same value.
/// </summary>
public readonly record struct JobSettingsContext(
    Guid Id,
    Guid WorkflowId,
    JobSnapshotKind Kind,
    InputType? SourceInputType = null,
    string? Input = null,
    InputType? InputType = null,
    ExtractionMode? RequestedMode = null,
    SearchDefaultProjectionKind? SearchProjection = null,
    string? QueryText = null,
    bool MusicSearchDefaults = false,
    bool GroupResults = false,
    JobSelectionSource? SelectionSource = null)
{
    public static JobSettingsContext FromCommand(NormalizedJobCommand command, Guid workflowId)
        => new(command.AcceptedJobId ?? Guid.Empty, workflowId, command.Kind,
            command.SourceInputType, command.Input, command.InputType, command.RequestedMode,
            command.Kind != JobSnapshotKind.Search ? null
                : command.AlbumQuery is not null ? SearchDefaultProjectionKind.Album
                : command.SongQuery is not null ? SearchDefaultProjectionKind.Track
                : SearchDefaultProjectionKind.GenericFile, command.QueryText,
            GroupResults: command.GroupResults, SelectionSource: command.SelectionSource);

    public static implicit operator JobSettingsContext(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job.Id, job.WorkflowId, CoreSnapshotFactory.GetJobKind(job), job.SourceInputType,
            (job as ExtractJob)?.Input, (job as ExtractJob)?.InputType,
            (job as ExtractJob)?.RequestedModeOverride, job is not SearchJob search ? null
                : search.DefaultFolderProjection is not null
                    ? SearchDefaultProjectionKind.Album
                : search.DefaultFileProjection is not null
                    ? SearchDefaultProjectionKind.Track : SearchDefaultProjectionKind.GenericFile,
            (job as SearchJob)?.QueryText,
            job is SearchJob { DefaultTrackGroupProjection: not null } or SearchJob { DefaultAlbumGroupProjection: not null },
            job is SearchJob { GroupResults: true }, job.SelectionSource);
    }
}
