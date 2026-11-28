using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Server;

public static class RemoteTransferSubmissionPolicy
{
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
        bool containsRemoteTransfer = ((JobSettingsContext)job).IsOrdinaryRemoteTransfer(settings);
        if (job is JobList list)
        {
            var children = list.Jobs;
            for (int index = 0; index < children.Count; index++)
            {
                // With no child overrides, one remote descendant settles the root policy.
                if (childDrafts is null && containsRemoteTransfer) break;
                Job child = children[index];
                JobDraftDto? draft = childDrafts is not null && index < childDrafts.Count ? childDrafts[index] : null;
                DownloadSettingsPatchDto? childPatch = draft?.DownloadSettings;
                containsRemoteTransfer |= ValidateOverrides(child, resolveChildSettings(settings, child, childPatch),
                    childPatch, resolveChildSettings, (draft as JobListJobDraftDto)?.Jobs);
            }
        }

        if (containsRemoteTransfer && patch != null)
            RemoteTransferSettingsValidator.ValidateExplicitPatch(patch);
        return containsRemoteTransfer;
    }
}
