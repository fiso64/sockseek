using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Planning;

/// <summary>
/// Captured planning data, never a runtime job or a mutable settings object.
/// Relationships are flat; an ancestor cannot accidentally retain its whole tree.
/// Settings serialization is deferred until a consumer needs retained intent.
/// </summary>
public sealed class PlannedJobNode
{
    private readonly NormalizedJobCommand command;
    private readonly JobSettingsSnapshot? settings;
    private readonly SearchDefinition? search;

    public string Ref { get; }
    public string? ParentRef { get; }
    public PlannedJobRole Role { get; }
    public PlannedJobState State { get; }
    public Guid PlanningJobId { get; }
    public JobSnapshotKind Kind => command.Kind;
    public string? ItemName => command.ItemName;
    public string? Input => command.Input;
    public string? QueryText => command.QueryText;
    public InputType? SourceInputType => command.SourceInputType;
    public string? PreviewTitle { get; }
    public IReadOnlyList<Sockseek.Core.Settings.AppliedProfile> AppliedProfiles { get; }
    public int DirectChildCount { get; }
    public string? FailureCode { get; }
    public string? FailureMessage { get; }
    public SubmissionSourceRevision? SourceRevision { get; }

    internal PlannedJobNode(string reference, string? parentRef, PlannedJobRole role,
        PlannedJobState state, Job job, int directChildCount,
        string? failureCode = null, string? failureMessage = null, SubmissionSourceRevision? sourceRevision = null)
    {
        Ref = reference;
        ParentRef = parentRef;
        Role = role;
        State = state;
        PlanningJobId = job.Id;
        command = SubmissionSpecification.CommandFrom(job, includeDescendants: false);
        PreviewTitle = (job is ExtractJob { SourceInfo.UseResultName: true, Result.ItemName: { } resultName } ? resultName : null)
            ?? (parentRef is not null ? job.SourceInfo?.NestedPreviewTitle : null)
            ?? job.SourceInfo?.PreviewTitle ?? (job is RemoteDirectoryJob { Source: RemoteDirectorySource.PeerDirectory peer }
                ? $"{peer.Directory.Username} — {peer.Directory.FolderPath.Replace('\\', '/')}" : job.ItemName);
        DirectChildCount = directChildCount;
        FailureCode = failureCode;
        FailureMessage = failureMessage;
        SourceRevision = sourceRevision;
        // The preparation owner freezes this once for both execution and Review.
        settings = job.PlannedSettings;
        AppliedProfiles = settings?.AppliedProfiles ?? [];
        search = command.SearchDefinition;
    }

    /// <summary>Returns caller-owned settings; editing them cannot change the plan.</summary>
    public DownloadSettings? ReadSettings() => settings?.ReadSettings();

    /// <summary>A detached, shallow retained value for storage or commit assembly.</summary>
    public SubmissionSpecification? CreateSpecification()
        => CreateSpecification(writer: null);

    internal SubmissionSpecification? CreateSpecification(RetainedSettingsWriter? writer)
    {
        if (settings is null) return null;
        var retained = settings.Retain(writer);
        return new(SubmissionSpecification.CurrentSchemaVersion, CopyConditions(command), retained.Json,
            retained.CredentialBindings, ReadSearch(), SourceRevision);
    }

    // Only a submission root needs a derived music search description. Child
    // queries/settings are already retained; assembly discards their wrapper's
    // Search field. Actual Search jobs retain their own captured definition.
    private SearchDefinition? ReadSearch() => search ?? (ParentRef is not null || settings is null ? null : command.Kind switch
    {
        JobSnapshotKind.Song or JobSnapshotKind.SongGroups when command.SongQuery is { } query
            => settings.Describe(query),
        JobSnapshotKind.Album or JobSnapshotKind.AlbumGroups when command.AlbumQuery is { } query
            => settings.Describe(query),
        _ => null,
    });

    private static NormalizedJobCommand CopyConditions(NormalizedJobCommand value)
        => value.ExtractorConditions is null && value.ExtractorPreferredConditions is null
            && value.ExtractorFolderConditions is null && value.ExtractorPreferredFolderConditions is null
            ? value : value with
            {
                ExtractorConditions = value.ExtractorConditions?.Clone(),
                ExtractorPreferredConditions = value.ExtractorPreferredConditions?.Clone(),
                ExtractorFolderConditions = value.ExtractorFolderConditions?.Clone(),
                ExtractorPreferredFolderConditions = value.ExtractorPreferredFolderConditions?.Clone(),
            };
}

/// <summary>Assembles selected detached Review nodes into one retained submission.</summary>
public static class JobPlan
{
    internal static SubmissionSpecification AssembleSpecifications(
        IEnumerable<(string Ref, string? ParentRef, SubmissionSpecification Specification)> source)
    {
        var nodes = new List<Entry>();
        SubmissionSpecification? root = null;
        foreach (var node in source)
        {
            var specification = node.Specification;
            var command = specification.Command;
            if (command.Children is { Count: > 0 } || command.ExtractedResult is not null)
                throw new InvalidDataException("A plan node must contain only its own definition.");
            // Each flat specification may have its own compact settings table.
            // Bind its resolved settings before combining it with other nodes.
            if (command.PlannedSettingsRef is not null
                || command.PlannedEffectiveSettingsJson != specification.EffectiveSettingsJson
                || !ReferenceEquals(command.PlannedCredentialBindings, specification.CredentialBindings))
                command = command with
                {
                    PlannedSettingsRef = null,
                    PlannedEffectiveSettingsJson = specification.EffectiveSettingsJson,
                    PlannedCredentialBindings = specification.CredentialBindings,
                };
            nodes.Add(new(node.Ref, node.ParentRef, command));
            if (node.ParentRef is null) root = specification;
        }
        return Assemble(nodes, root ?? throw new InvalidDataException("A job plan requires a root."));
    }

    private readonly record struct Entry(string Ref, string? ParentRef, NormalizedJobCommand Command);

    private static SubmissionSpecification Assemble(IReadOnlyList<Entry> nodes, SubmissionSpecification retained)
    {
        var references = nodes.Select(node => node.Ref).ToHashSet(StringComparer.Ordinal);
        if (references.Count != nodes.Count) throw new InvalidDataException("A job plan contains duplicate references.");
        var children = nodes.Where(node => node.ParentRef is not null)
            .GroupBy(node => node.ParentRef!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var roots = nodes.Where(node => node.ParentRef is null).ToArray();
        if (roots.Length != 1 || nodes.Any(node => node.ParentRef is { } parent && !references.Contains(parent)))
            throw new InvalidDataException("A job plan requires one root and an existing parent for every node.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        NormalizedJobCommand AssembleNode(Entry node)
        {
            if (!visited.Add(node.Ref)) throw new InvalidDataException("A job plan contains a cycle.");
            var command = node.Command;
            var descendants = children.GetValueOrDefault(node.Ref) ?? [];
            return command.Kind switch
            {
                JobSnapshotKind.JobList => command with { Children = descendants.Select(AssembleNode).ToArray() },
                JobSnapshotKind.Extract when descendants.Length == 1 => command with
                {
                    HasPlannedExtraction = true, ExtractedResult = AssembleNode(descendants[0]),
                },
                _ when descendants.Length == 0 => command,
                _ => throw new InvalidDataException($"Plan node '{node.Ref}' cannot contain these children."),
            };
        }
        var result = retained with { Command = AssembleNode(roots[0]), SharedSettings = null };
        if (visited.Count != nodes.Count) throw new InvalidDataException("A job plan contains unreachable nodes.");
        return result;
    }
}
