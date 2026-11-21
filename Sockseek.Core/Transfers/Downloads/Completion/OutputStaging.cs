using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

internal static class OutputStaging
{
    public const string IncompleteExtension = ".incomplete";
    public const string DirectoryName = ".sockseek-staging";

    public static string Root(OutputSettings output)
    {
        var parentDir = string.IsNullOrWhiteSpace(output.ParentDir)
            ? Directory.GetCurrentDirectory()
            : output.ParentDir;
        return Path.Join(parentDir, DirectoryName);
    }

    public static bool Contains(string? path, OutputSettings output)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var relative = Path.GetRelativePath(
                Path.GetFullPath(Root(output)),
                Path.GetFullPath(path));
            return relative != "."
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathFullyQualified(relative);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
