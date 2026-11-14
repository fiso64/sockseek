using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;

namespace Sockseek.Server;

public static class RemoteTransferSubmissionPolicy
{
    public static void NormalizeSettings(JobSettingsContext job, DownloadSettings settings)
    {
        if (IsOrdinaryRemoteTransfer(job, settings))
            RemoteTransferNameFormatPolicy.ApplyInherited(settings.Output);

        // Normalize only the value the caller will use. Walking children here
        // resolved temporary copies that were discarded; each child's real
        // settings are normalized by JobPreparer when that child is prepared.
    }

    // Start, Review and local CLI validate the same explicit subtree overrides.
    // Return its capabilities from this walk instead of resolving children a
    // second time just to decide whether the root's override applies.
    public static bool ValidateOverrides(
        Job job,
        DownloadSettings settings,
        DownloadSettingsPatchDto? patch,
        Func<DownloadSettings, Job, DownloadSettingsPatchDto?, DownloadSettings>
            resolveChildSettings,
        IReadOnlyList<JobDraftDto>? childDrafts = null)
    {
        bool containsRemoteTransfer = IsOrdinaryRemoteTransfer(job, settings);
        if (job is JobList list)
        {
            if (childDrafts is null)
            {
                // Follow-ups without drafts have no per-child explicit patches.
                // Finding one remote descendant is sufficient for root validation.
                foreach (Job child in list.Jobs)
                {
                    if (!ContainsOrdinaryRemoteTransfer(
                        child, resolveChildSettings(settings, child, null), resolveChildSettings)) continue;
                    containsRemoteTransfer = true;
                    break;
                }
            }
            else
            {
                for (int index = 0; index < list.Jobs.Count; index++)
                {
                    Job child = list.Jobs[index];
                    JobDraftDto? draft = index < childDrafts.Count ? childDrafts[index] : null;
                    DownloadSettingsPatchDto? childPatch = draft is null ? null
                        : JobRequestMapper.DraftDownloadSettings(draft);
                    containsRemoteTransfer |= ValidateOverrides(
                        child, resolveChildSettings(settings, child, childPatch), childPatch,
                        resolveChildSettings, (draft as JobListJobDraftDto)?.Jobs);
                }
            }
        }

        if (containsRemoteTransfer && patch != null)
            RemoteTransferSettingsValidator.ValidateExplicitPatch(patch);
        return containsRemoteTransfer;
    }

    private static bool ContainsOrdinaryRemoteTransfer(
        Job job,
        DownloadSettings settings,
        Func<DownloadSettings, Job, DownloadSettingsPatchDto?, DownloadSettings> resolveChildSettings)
    {
        if (IsOrdinaryRemoteTransfer(job, settings)) return true;
        if (job is JobList list)
            foreach (Job child in list.Jobs)
                if (ContainsOrdinaryRemoteTransfer(
                    child, resolveChildSettings(settings, child, null), resolveChildSettings)) return true;
        return false;
    }

    public static bool IsOrdinaryRemoteTransfer(JobSettingsContext job, DownloadSettings settings)
        => job.Kind is JobSnapshotKind.RemoteFile or JobSnapshotKind.RemoteDirectory
            || job.Kind == JobSnapshotKind.Extract && job.Input is { } input
            && Sockseek.Core.Extractors.SoulseekExtractor.InputMatches(input)
            && settings.Extraction.RequestedMode is null or ExtractionMode.General;
}
