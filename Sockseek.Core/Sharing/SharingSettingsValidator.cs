using System.Text;
using System.Text.RegularExpressions;
using Sockseek.Core;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Sharing;

/// <summary>
/// Normalizes and validates all daemon-lifetime sharing, upload, and inbound
/// peer policy settings before any catalog or listener is started.
/// </summary>
public static class SharingSettingsValidator
{
    public const int MaximumRoots = 256;
    public const int MaximumExclusions = 4_096;
    public const int MaximumFilters = 1_024;
    public const int MaximumEncodedValueBytes = 4_096;
    public const int MaximumUploadSlots = 1_024;
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static void NormalizeAndValidate(
        EngineSettings settings,
        PathVariableContext pathContext)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pathContext);

        ValidateCount(settings.Sharing.Roots.Count, MaximumRoots, "share roots");
        ValidateCount(
            settings.Sharing.ExcludedDirectories.Count,
            MaximumExclusions,
            "share exclusions");
        ValidateCount(settings.Sharing.Filters.Count, MaximumFilters, "share filters");
        NormalizeRoots(settings.Sharing.Roots, pathContext);
        foreach (ShareRootSettings root in settings.Sharing.Roots)
        {
            // Missing roots remain a scan-time RootUnavailable condition.
            if (Directory.Exists(root.LocalPath))
            {
                FileAttributes attributes = File.GetAttributes(root.LocalPath);
                if ((attributes & FileAttributes.Directory) == 0
                    || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException(
                        $"Input error: Share root '{root.LocalPath}' must be a real directory, not a link or reparse point.");
                }
            }
        }
        NormalizeExclusions(settings.Sharing, pathContext);
        NormalizeFilters(settings.Sharing.Filters);
        ValidatePeerRestrictions(settings.PeerRestrictions);
        ValidateNumericSettings(settings.Sharing, settings.Uploads);
    }

    public static Regex CompileFilter(string pattern)
    {
        ValidateEncodedValue(pattern, "Share filter");

        const RegexOptions common = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        try
        {
            return new Regex(
                pattern,
                common | RegexOptions.NonBacktracking,
                RegexTimeout);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, common, RegexTimeout);
        }
    }

    private static void NormalizeRoots(
        List<ShareRootSettings> roots,
        PathVariableContext pathContext)
    {
        foreach (var root in roots)
        {
            ValidateEncodedValue(root.LocalPath, "Share root");
            string configuredPath = root.LocalPath.Trim();
            string configuredRoot = Path.GetPathRoot(configuredPath) ?? "";
            bool configuredAsVolumeRoot = Path.IsPathFullyQualified(configuredPath)
                                          && configuredRoot.Length > 0
                                          && PathsEqual(
                                              TrimEndingSeparators(configuredPath),
                                              TrimEndingSeparators(configuredRoot));
            // The shared path expander intentionally trims separators, which
            // would turn '/' into an empty string and 'C:\\' into 'C:'.
            string expanded = configuredAsVolumeRoot
                ? configuredPath
                : Utils.ExpandVariables(configuredPath, pathContext);
            if (!Path.IsPathFullyQualified(expanded))
                throw new ArgumentException($"Input error: Share root '{root.LocalPath}' must be absolute.");

            string fullPath = TrimEndingSeparators(Path.GetFullPath(expanded));
            string pathRoot = Path.GetPathRoot(fullPath) ?? "";
            bool isVolumeRoot = PathsEqual(fullPath, TrimEndingSeparators(pathRoot));
            if (isVolumeRoot && root.Alias is null)
            {
                throw new ArgumentException(
                    $"Input error: Filesystem root '{fullPath}' requires an explicit share alias.");
            }

            string alias = root.Alias is null
                ? Path.GetFileName(fullPath)
                : root.Alias.Trim();

            if (string.IsNullOrEmpty(alias))
                throw new ArgumentException(
                    $"Input error: Cannot derive a safe alias from share root '{fullPath}'.");

            ValidateEncodedValue(alias, "Share alias");
            root.LocalPath = fullPath;
            root.Alias = root.Alias is null ? null : RemotePathKey.NormalizeAlias(alias);
            root.EffectiveAlias = RemotePathKey.NormalizeAlias(alias);
        }

        RejectDuplicates(
            roots,
            static root => RemotePathKey.CreateAlias(root.EffectiveAlias),
            "share alias");

        // Local roots may overlap, or even expose the same directory under
        // different aliases. Remote aliases are the ambiguity boundary.
    }

    private static void NormalizeExclusions(
        SharingSettings settings,
        PathVariableContext pathContext)
    {
        for (int i = 0; i < settings.ExcludedDirectories.Count; i++)
        {
            string configured = settings.ExcludedDirectories[i];
            ValidateEncodedValue(configured, "Share exclusion");
            string expanded = Utils.ExpandVariables(configured, pathContext);
            if (!Path.IsPathFullyQualified(expanded))
            {
                throw new ArgumentException(
                    $"Input error: Share exclusion '{configured}' must be absolute.");
            }

            string fullPath = TrimEndingSeparators(Path.GetFullPath(expanded));
            bool equalsRoot = settings.Roots.Any(root => PathsEqual(fullPath, root.LocalPath));
            bool insideRoot = settings.Roots.Any(
                root => IsWithinOrEqual(fullPath, root.LocalPath)
                        && !PathsEqual(fullPath, root.LocalPath));

            if (equalsRoot || !insideRoot)
            {
                throw new ArgumentException(
                    $"Input error: Share exclusion '{fullPath}' must be below a share root.");
            }

            settings.ExcludedDirectories[i] = fullPath;
        }

        RejectDuplicates(
            settings.ExcludedDirectories,
            NormalizeLocalPathKey,
            "share exclusion");
    }

    private static void NormalizeFilters(List<string> filters)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            string filter = filters[i].Trim();
            if (filter.Length == 0)
                throw new ArgumentException("Input error: Share filter cannot be empty.");

            _ = CompileFilter(filter);
            filters[i] = filter;
        }

        RejectDuplicates(filters, static filter => filter, "share filter");
    }

    private static void ValidatePeerRestrictions(PeerRestrictionSettings settings)
    {
        NormalizeBlockedUsernames(
            settings.UploadAccess.BlockedUsernames,
            "upload-blocked username");
        NormalizeBlockedUsernames(
            settings.PrivateMessages.BlockedUsernames,
            "private-message-blocked username");

        for (int i = 0; i < settings.UploadAccess.BlockedIpAddresses.Count; i++)
        {
            ValidateEncodedValue(
                settings.UploadAccess.BlockedIpAddresses[i],
                "Upload-blocked IP address");
            settings.UploadAccess.BlockedIpAddresses[i] = PeerRestrictionPolicy
                .ParseIpAddress(settings.UploadAccess.BlockedIpAddresses[i])
                .ToString();
        }

        RejectDuplicates(
            settings.UploadAccess.BlockedIpAddresses,
            static address => address,
            "upload-blocked IP address");
    }

    private static void NormalizeBlockedUsernames(List<string> usernames, string description)
    {
        for (int i = 0; i < usernames.Count; i++)
        {
            ValidateEncodedValue(usernames[i], description);
            usernames[i] = PeerUsername.Validate(usernames[i]);
        }
        RejectDuplicates(usernames, static username => username, description);
    }

    private static void ValidateNumericSettings(
        SharingSettings sharing,
        UploadSettings uploads)
    {
        if (sharing.RescanInterval is { } interval)
        {
            if (interval < TimeSpan.FromMinutes(1)
                || interval > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            {
                throw new ArgumentException(
                    "Input error: share-rescan-interval must be at least one minute " +
                    "and fit the platform timer range.");
            }
        }

        if (uploads.Slots is < 1 or > MaximumUploadSlots)
            throw Range("upload-slots", 1, MaximumUploadSlots);
        ValidateOptionalPositive(
            uploads.SpeedLimitKiBPerSecond,
            "upload-speed-limit-kib");
        if (uploads.SpeedLimitKiBPerSecond is { } speed)
            _ = checked(speed * 1_024);
    }

    private static void ValidateOptionalPositive(int? value, string option)
    {
        if (value is <= 0)
            throw new ArgumentException($"Input error: {option} must be positive when configured.");
    }

    private static ArgumentException Range(string option, int minimum, int maximum)
        => new($"Input error: {option} must be between {minimum} and {maximum}.");

    private static void ValidateCount(int count, int maximum, string label)
    {
        if (count > maximum)
            throw new ArgumentException($"Input error: At most {maximum} {label} may be configured.");
    }

    private static void ValidateEncodedValue(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Input error: {label} cannot be empty.");
        if (Encoding.UTF8.GetByteCount(value) > MaximumEncodedValueBytes)
        {
            throw new ArgumentException(
                $"Input error: {label} exceeds {MaximumEncodedValueBytes} UTF-8 bytes.");
        }
    }

    private static string TrimEndingSeparators(string path)
    {
        string root = Path.GetPathRoot(path) ?? "";
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.Length < root.Length ? root : trimmed;
    }

    private static bool IsWithinOrEqual(string candidate, string root)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return relative == "."
               || !Path.IsPathRooted(relative)
               && relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(left, right, LocalPathComparison);

    private static string NormalizeLocalPathKey(string path)
        => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static StringComparison LocalPathComparison
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static void RejectDuplicates<T, TKey>(
        IEnumerable<T> values,
        Func<T, TKey> keySelector,
        string label)
        where TKey : notnull
    {
        var seen = new HashSet<TKey>();
        foreach (var value in values)
        {
            if (!seen.Add(keySelector(value)))
                throw new ArgumentException($"Input error: Duplicate {label}.");
        }
    }
}
