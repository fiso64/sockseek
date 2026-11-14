using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

public sealed record NameFormatContext(
    PeerFileTarget? Target,
    IReadOnlyList<string> RelativeDirectoryComponents,
    string FolderName,
    string ItemName,
    string DefaultFolder,
    string OutputDirectory,
    string JobType,
    string OutputExtension,
    string ExtractorName = "",
    string InputSource = "",
    string ConfigDirectory = "");

public sealed class NameFormatVariableProvider : INameFormatVariableProvider
{
    private static readonly string[] Variables =
    [
        "peer-username", "username", "filename", "slsk-filename",
        "ext",
        "relative-path",
        "foldername", "slsk-foldername", "item-name", "default-folder", "output-dir", "outputdir", "type",
        "extractor", "input", "bindir", "configdir",
    ];

    public static IReadOnlyCollection<string> Supported => Variables;
    public static IReadOnlyCollection<NameFormatVariableDescriptor> Capabilities { get; } =
        Array.AsReadOnly(Variables.Select(name => new NameFormatVariableDescriptor(
            name,
            NameFormatVariableApplicability.Shared,
            NameFormatEvaluationPhase.Placement)).ToArray());

    private readonly NameFormatContext context;

    public NameFormatVariableProvider(NameFormatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    public IReadOnlyCollection<string> SupportedVariables => Variables;
    public IReadOnlyCollection<NameFormatVariableDescriptor> VariableDescriptors => Capabilities;

    public bool TryResolve(string name, out NameFormatVariableValue value)
    {
        string remotePath = context.Target?.Filename ?? "";
        string remoteLeaf = RemoteLeaf(remotePath);
        string filename = Path.GetFileNameWithoutExtension(remoteLeaf);
        string outputExtension = NormalizeExtension(context.OutputExtension);
        string relativePath = Path.Join([.. context.RelativeDirectoryComponents, filename]);

        value = name switch
        {
            "peer-username" or "username" => Component(context.Target?.Username ?? ""),
            "filename" or "slsk-filename" => Component(filename),
            "ext" => Component(outputExtension),
            "relative-path" => PathValue(relativePath),
            "foldername" or "slsk-foldername" => PathValue(context.FolderName),
            "item-name" => Component(context.ItemName),
            "default-folder" => PathValue(context.DefaultFolder),
            "output-dir" or "outputdir" => Raw(context.OutputDirectory),
            "type" => Component(context.JobType),
            "extractor" => Component(context.ExtractorName),
            "input" => Component(context.InputSource),
            "bindir" => Raw(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('/', '\\')),
            "configdir" => Raw(context.ConfigDirectory),
            _ => default,
        };
        return Variables.Contains(name, StringComparer.Ordinal);
    }

    private static NameFormatVariableValue Component(string value)
        => new(value, NameFormatValueKind.Component);

    private static NameFormatVariableValue PathValue(string value)
        => new(value, NameFormatValueKind.Path);

    private static NameFormatVariableValue Raw(string value)
        => new(value, NameFormatValueKind.Raw);

    private static string RemoteLeaf(string path)
    {
        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        int separator = normalized.LastIndexOf('\\');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return "";
        return extension[0] == '.' ? extension : "." + extension;
    }
}
