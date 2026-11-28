using System.Reflection;

namespace Sockseek.Core;

public static class ProductVersion
{
    private static readonly string[] VersionParts = typeof(ProductVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2);

    public static string Release => VersionParts[0];
    public static string Display { get; } = GetDisplayVersion();

    private static string GetDisplayVersion()
    {
        // The .NET SDK appends the source revision as SemVer build metadata.
        if (VersionParts.Length != 2 || VersionParts[1].Length < 8)
            return Release;

        var prerelease = Release.Split('-', 2);
        if (prerelease.Length == 2 && prerelease[1].Split('.')[0] is "alpha" or "beta" or "preview")
            return $"{Release}+g{VersionParts[1][..8]}";

        return Release;
    }
}
