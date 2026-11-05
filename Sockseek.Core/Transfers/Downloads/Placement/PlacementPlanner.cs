using System.Text;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed record FilePlacement(
    PeerFileTarget Target,
    RelativeOutputPath RelativePath,
    string OutputPath);

/// <summary>
/// Resolves ordinary remote-transfer destinations without interpreting files as music.
/// Every result is sanitized and proven to remain below the configured parent.
/// </summary>
public sealed class PlacementPlanner
{
    public FilePlacement PlanFile(
        PeerFileTarget target,
        RelativeOutputPath relativePath,
        OutputSettings output)
        => PlanFile(target, relativePath, output, settings: null);

    public FilePlacement PlanFile(
        PeerFileTarget target,
        RelativeOutputPath relativePath,
        DownloadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return PlanFile(target, relativePath, settings.Output, settings);
    }

    private static FilePlacement PlanFile(
        PeerFileTarget target,
        RelativeOutputPath relativePath,
        OutputSettings output,
        DownloadSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(output);

        string parent = ResolveParent(output);
        string remoteLeaf = RemoteLeaf(target.Filename);
        var plannedRelative = string.IsNullOrWhiteSpace(output.NameFormat)
            ? relativePath
            : RenderRelativePath(
                target,
                relativeDirectoryComponents: relativePath.Components.Take(relativePath.Components.Count - 1).ToArray(),
                folderName: RemoteLeaf(RemoteDirectory(target.Filename)),
                itemName: Path.GetFileNameWithoutExtension(remoteLeaf),
                defaultFolder: "",
                jobType: "RemoteFile",
                parent,
                output,
                settings);
        string path = ResolveContainedPath(parent, plannedRelative.Components, output.InvalidReplaceStr);
        return new FilePlacement(target, plannedRelative, path);
    }

    public IReadOnlyList<FilePlacement> PlanDirectory(
        DirectoryTransferPlan plan,
        OutputSettings output)
        => PlanDirectory(plan, output, settings: null);

    public IReadOnlyList<FilePlacement> PlanDirectory(
        DirectoryTransferPlan plan,
        DownloadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return PlanDirectory(plan, settings.Output, settings);
    }

    private static IReadOnlyList<FilePlacement> PlanDirectory(
        DirectoryTransferPlan plan,
        OutputSettings output,
        DownloadSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);

        string parent = ResolveParent(output);
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placements = new List<FilePlacement>(plan.Entries.Count);

        foreach (var entry in plan.Entries)
        {
            IReadOnlyList<string> components;
            if (string.IsNullOrWhiteSpace(output.NameFormat))
            {
                string leaf = RemoteLeaf(entry.Target.Filename);
                var tree = new List<string>(entry.RelativeDirectoryComponents.Count + 2);
                if (plan.DisplayRoot.Length != 0)
                    tree.Add(plan.DisplayRoot);
                tree.AddRange(entry.RelativeDirectoryComponents);
                tree.Add(leaf);
                components = tree;
            }
            else
            {
                components = RenderRelativePath(
                    entry.Target,
                    entry.RelativeDirectoryComponents,
                    Path.Join([plan.DisplayRoot, .. entry.RelativeDirectoryComponents]),
                    plan.DisplayRoot,
                    plan.DisplayRoot,
                    "RemoteDirectory",
                    parent,
                    output,
                    settings).Components;
            }

            string resolved = ResolveContainedPath(parent, components, output.InvalidReplaceStr);
            resolved = ResolveStableCollision(resolved, occupied);
            placements.Add(new FilePlacement(entry.Target, ToRelativePath(parent, resolved), resolved));
        }

        return placements.AsReadOnly();
    }

    private static string ResolveParent(OutputSettings output)
        => Path.GetFullPath(output.ParentDir ?? Directory.GetCurrentDirectory());

    private static string ResolveContainedPath(
        string parent,
        IEnumerable<string> logicalComponents,
        string replacement)
    {
        string[] safe = logicalComponents.Select(component => SafeComponent(component, replacement)).ToArray();
        if (safe.Length == 0)
            throw new InvalidOperationException("An output path must contain at least one component.");

        string path = Path.GetFullPath(Path.Combine([parent, .. safe]));
        if (!Utils.IsInDirectory(path, parent, strict: true))
            throw new InvalidOperationException("The resolved output path escapes its configured parent.");
        return path;
    }

    private static string SafeComponent(string component, string replacement)
    {
        // Use one portable destination contract regardless of the daemon host.
        // A share downloaded on Linux may later be accessed from Windows, and
        // collision suffixes must not change with the test/runtime platform.
        string safe = component.ReplaceInvalidChars(replacement, windows: true).Trim(' ', '.');
        if (safe.Length == 0 || safe is "." or "..")
            safe = "_";

        string stem = Path.GetFileNameWithoutExtension(safe);
        if (WindowsReservedNames.Contains(stem))
            safe += "_";
        return safe;
    }

    private static string ResolveStableCollision(string path, HashSet<string> occupied)
    {
        string key = CollisionKey(path);
        if (occupied.Add(key))
            return path;

        string directory = Path.GetDirectoryName(path)!;
        string extension = Path.GetExtension(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        for (int suffix = 2; ; suffix++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (occupied.Add(CollisionKey(candidate)))
                return candidate;
        }
    }

    private static string CollisionKey(string path)
        => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Normalize(NormalizationForm.FormC);

    private static RelativeOutputPath ToRelativePath(string parent, string path)
    {
        string relative = Path.GetRelativePath(parent, path);
        return new RelativeOutputPath(relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries));
    }

    private static RelativeOutputPath RenderRelativePath(
        PeerFileTarget target,
        IReadOnlyList<string> relativeDirectoryComponents,
        string folderName,
        string itemName,
        string defaultFolder,
        string jobType,
        string outputParent,
        OutputSettings output,
        DownloadSettings? settings)
    {
        var context = new NameFormatContext(
            target,
            relativeDirectoryComponents,
            folderName,
            itemName,
            defaultFolder,
            outputParent,
            jobType,
            NameFormatVariableProvider.NormalizeExtension(
                target.Extension ?? Path.GetExtension(RemoteLeaf(target.Filename))),
            settings?.Extraction.InputType.ToString() ?? "",
            settings?.Extraction.Input ?? "",
            settings?.RuntimePathContext.ConfigDir ?? "");
        string rendered = NameFormatRenderer.Render(
            output.NameFormat,
            output.InvalidReplaceStr,
            new NameFormatVariableProvider(context),
            rejectUnsupportedVariables: true);
        string[] components = rendered.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
            throw new InvalidOperationException("Name format produced an empty output path.");

        string extension = NameFormatVariableProvider.NormalizeExtension(
            target.Extension ?? Path.GetExtension(RemoteLeaf(target.Filename)));
        if (extension.Length > 0
            && !components[^1].EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            components[^1] += extension;
        }
        return new RelativeOutputPath(components);
    }

    private static string RemoteLeaf(string filename)
    {
        string normalized = filename.Replace('/', '\\').TrimEnd('\\');
        int separator = normalized.LastIndexOf('\\');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static string RemoteDirectory(string filename)
    {
        string normalized = filename.Replace('/', '\\').TrimEnd('\\');
        int separator = normalized.LastIndexOf('\\');
        return separator < 0 ? "" : normalized[..separator];
    }

    private static readonly HashSet<string> WindowsReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);
}
