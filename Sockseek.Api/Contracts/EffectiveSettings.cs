namespace Sockseek.Api;

public sealed record AppliedProfileDto(string Name, string Source);

public sealed record ResolveEffectiveSettingsRequestDto(
    JobDraftDto Job,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// A fully materialized, UI-safe settings document. Section types are shared
/// with request patches, but every non-redacted scalar and collection is
/// populated here; null credential/command fields are deliberate redactions.
/// </summary>
public sealed record EffectiveDownloadSettingsDto(
    DownloadSettingsPatchDto Values,
    int OnCompleteCommandCount,
    int RegexRuleCount,
    bool SpotifyClientIdConfigured,
    bool SpotifyClientSecretConfigured,
    bool SpotifyTokenConfigured,
    bool SpotifyRefreshConfigured,
    bool YouTubeApiKeyConfigured,
    bool YtDlpArgumentConfigured,
    bool BandcampHtmlFileConfigured,
    bool SoundCloudTokenConfigured = false);

public sealed record ResolveEffectiveSettingsResponseDto(
    ServerSearchSettingsBaselineKind Baseline,
    EffectiveDownloadSettingsDto Settings,
    IReadOnlyList<AppliedProfileDto> AppliedProfiles,
    IReadOnlyDictionary<string, string> Provenance,
    bool IsProvisional = false);
