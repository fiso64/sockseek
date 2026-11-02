using System.Text.Json.Serialization;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Planning;

/// <summary>
/// Versioned, transport-independent command shape. Optional members are
/// discriminated by Kind; exact peer usernames and paths are never normalized.
/// </summary>
public sealed record NormalizedJobCommand
{
    public required JobSnapshotKind Kind { get; init; }
    // Identifies a captured child before acceptance rebinds its runtime identity,
    // without relying on its position or re-running its parent extractor.
    public Guid? AcceptedJobId { get; init; }
    public string? ItemName { get; init; }
    public int ItemNumber { get; init; } = 1;
    public int LineNumber { get; init; }
    public SourceMutation? SourceMutation { get; init; }
    public InputType? SourceInputType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobSourceInfo? SourceInfo { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InputType? SourceCollectionInputType { get; init; }
    public bool EnablesIndexByDefault { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanBeSkippedOverride { get; init; }
    /// <summary>Optional extractor-provided display information; never a search hint.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtractorInfo { get; init; }
    public DownloadBehaviorPolicy DownloadBehavior { get; init; } = new();
    public FileConditionPatch? ExtractorConditions { get; init; }
    public FileConditionPatch? ExtractorPreferredConditions { get; init; }
    public FolderConditionPatch? ExtractorFolderConditions { get; init; }
    public FolderConditionPatch? ExtractorPreferredFolderConditions { get; init; }
    public string? ArtifactId { get; init; }
    // Planner nodes capture inline settings before submission serialization.
    // The codec writes each distinct settings/bindings pair once per submission.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlannedEffectiveSettingsJson { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? PlannedCredentialBindings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlannedSettingsRef { get; init; }

    public string? Input { get; init; }
    public InputType? InputType { get; init; }
    public ExtractionMode? RequestedMode { get; init; }
    public DownloadBehaviorPolicy? ResultDownloadBehavior { get; init; }
    public string? QueryText { get; init; }
    public SongQueryDefinition? SongQuery { get; init; }
    public AlbumQueryDefinition? AlbumQuery { get; init; }
    public bool IncludeFullResults { get; init; }
    public IReadOnlyList<NormalizedJobCommand>? Children { get; init; }
    public PeerFileTarget? PeerFile { get; init; }
    public IReadOnlyList<string>? OutputPathComponents { get; init; }
    public PeerDirectoryIdentitySnapshot? PeerDirectory { get; init; }
    public IReadOnlyList<string>? ExcludedDirectoryFilenames { get; init; }
    private DirectoryTransferPlanSnapshot? directoryPlan;
    private DirectoryTransferPlan? resolvedDirectoryPlan;
    private Lazy<DirectoryTransferPlanSnapshot>? directoryPlanSnapshot;

    public DirectoryTransferPlanSnapshot? DirectoryPlan
    {
        get => directoryPlan ?? directoryPlanSnapshot?.Value;
        init
        {
            directoryPlan = value;
            resolvedDirectoryPlan = null;
            directoryPlanSnapshot = null;
        }
    }

    [JsonIgnore]
    internal DirectoryTransferPlan? ResolvedDirectoryPlan => resolvedDirectoryPlan;

    // A validated source plan is already immutable. Settings reads need only its
    // kind, and execution can reuse it directly. Produce the serialization shape
    // only when retention/presentation requests it, never as an input round trip.
    internal NormalizedJobCommand WithDirectoryPlan(DirectoryTransferPlan plan)
        => this with
        {
            directoryPlan = null,
            resolvedDirectoryPlan = plan,
            directoryPlanSnapshot = new(() => CoreSnapshotFactory.CreateDirectoryTransferPlan(plan)),
        };
    // Accepted execution inputs, not browser choice state or later runtime results.
    // Null candidates mean discovery is still required; an empty list does not.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SearchProjectionInput>? SongCandidates { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SearchProjectionInput? SelectedSong { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AlbumFolderSnapshot>? AlbumOptions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SelectedAlbumOption { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlbumFolderSnapshot? SelectedAlbum { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlbumDirectoryResolutionPolicy? AlbumResolutionPolicy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlbumValidationRequirement? AlbumValidation { get; init; }
    public bool HasPlannedExtraction { get; init; }
    public string? PlannedExtractionFailure { get; init; }
    public NormalizedJobCommand? ExtractedResult { get; init; }
    public SearchDefinition? SearchDefinition { get; init; }
}

