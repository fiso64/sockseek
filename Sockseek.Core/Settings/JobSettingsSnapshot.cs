using Sockseek.Core.Planning;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Core.Settings;

/// <summary>
/// One frozen settings value shared by a job and its captured plan. Mutable
/// execution/presentation copies leave through ReadSettings; retained JSON is
/// produced once. Neither caller edits nor runtime preparation can change it.
/// </summary>
public sealed class JobSettingsSnapshot
{
    private readonly DownloadSettings settings;
    private RetainedJobSettings? retained;
    private object? retainLock;

    private JobSettingsSnapshot(DownloadSettings ownedSettings)
    {
        settings = ownedSettings;
        AppliedAutoProfiles = settings.AppliedAutoProfiles.Count == 0 ? []
            : Array.AsReadOnly(settings.AppliedAutoProfiles.Order(StringComparer.Ordinal).ToArray());
    }

    public static JobSettingsSnapshot Capture(DownloadSettings settings)
        => new(SettingsCloner.Clone(settings));

    // Only for fresh values created inside Core; an external mutable input must
    // use Capture. In particular, materialization has already cloned/rebound it.
    internal static JobSettingsSnapshot FromOwned(DownloadSettings settings) => new(settings);

    internal PrintOption PrintOption => settings.PrintOption;
    public IReadOnlyList<string> AppliedAutoProfiles { get; }
    public DownloadSettings ReadSettings() => SettingsCloner.Clone(settings);
    internal RetainedJobSettings Retain(RetainedSettingsWriter? writer = null) => Volatile.Read(ref retained)
        ?? LazyInitializer.EnsureInitialized(ref retained, ref retainLock,
            () => SubmissionSpecification.RetainSettings(settings, writer));
    internal SearchDefinition? Describe(Job job) => SubmissionSpecification.CaptureSearch(job, settings.ResolveSearchSettings());
    internal SearchDefinition Describe(SongQueryDefinition query) => SearchDefinition.Create(query, settings.ResolveSearchSettings());
    internal SearchDefinition Describe(AlbumQueryDefinition query) => SearchDefinition.Create(query, settings.ResolveSearchSettings());
}
