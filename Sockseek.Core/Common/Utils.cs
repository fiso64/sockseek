using System.Buffers;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Sockseek.Core;

public static partial class Utils
{
    // TODO [MEDIA TYPES]: Evaluate the rest of TagLib#'s supported audio extensions with
    // real fixtures before expanding this classification. Each addition also changes album
    // track counting, playlist inclusion, failure criticality, and progressive placement;
    // container extensions such as .webm must not be classified as unconditionally audio.
    public static IReadOnlySet<string> KnownAudioExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".m4a", ".m4b", ".opus", ".wav", ".aac", ".alac",
        ".aif", ".aiff",
    };

    public static readonly string[] imageExtensions = new string[] { ".jpg", ".png", ".jpeg", ".gif", ".webp" };

    // Matching, naming and sharing need tags and audio properties. Cover art can stay on disk.
    internal const TagLib.ReadStyle AudioMetadataReadStyle = TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy;

    public static bool IsMusicExtension(string extension)
    {
        return KnownAudioExtensions.Contains(extension);
    }

    public static bool IsMusicFile(string fileName)
    {
        return KnownAudioExtensions.Contains(Path.GetExtension(fileName));
    }

    public static bool IsImageFile(string fileName)
    {
        return imageExtensions.Contains(Path.GetExtension(fileName).ToLower());
    }

    public static bool IsInternetUrl(this string str)
    {
        str = str.TrimStart();
        return str.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || str.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetFullPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        return Path.GetFullPath(path);
    }

    // TODO [ARCHITECTURE]: Introduce explicit path value types for local filesystem paths
    // and Soulseek remote paths. Several cross-platform bugs have come from treating those
    // as interchangeable strings and relying on ad hoc separator normalization at call sites.
    // Keep raw config paths, normalized local paths, and Soulseek paths distinct enough that
    // the compiler helps prevent accidental mixing.
    public static string GetAsPathSlsk(string fname)
    {
        return fname.Replace('\\', Path.DirectorySeparatorChar);
    }

    public static string GetFileNameSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFileName(fname);
    }

    public static string GetBaseNameSlsk(string path)
    {
        path = path.Replace('\\', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFileName(path);
    }

    public static string GetFileNameWithoutExtSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFileNameWithoutExtension(fname);
    }

    public static string GetExtensionSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetExtension(fname);
    }

    public static string GetDirectoryNameSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        var directoryName = Path.GetDirectoryName(fname);
        if (string.IsNullOrEmpty(directoryName))
        {
            return String.Empty;
        }
        else
        {
            return directoryName;
        }
    }

    public static string ExpandVariables(string path)
        => ExpandVariables(path, PathVariableContext.Empty);

    public static string ExpandVariables(string path, PathVariableContext context)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        path = NormalizedPath(path);

        if (path.Length == 0)
            return path;

        if (path[0] == '~' && (path.Length == 1 || path[1] == '/'))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = JoinPathPrefix(home, path[1..]);
        }
        else if (path.StartsWith("{bindir}") && (path.Length == 8 || path[8] == '/'))
        {
            string bindir = AppDomain.CurrentDomain.BaseDirectory;
            path = JoinPathPrefix(bindir, path[8..]);
        }
        else if (!string.IsNullOrWhiteSpace(context.ConfigDir)
            && path.StartsWith("{configdir}") && (path.Length == 11 || path[11] == '/'))
        {
            path = JoinPathPrefix(context.ConfigDir, path[11..]);
        }

        return path;
    }

    private static string JoinPathPrefix(string prefix, string suffix)
    {
        var parts = suffix.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? prefix : Path.Join([prefix, .. parts]);
    }

    public static decimal Normalize(this double value)
    {
        return ((decimal)value) / 1.000000000000000000000000000000000m;
    }

    public static int FileCountRecursive(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        int count = Directory.GetFiles(directory).Length;
        foreach (string subDirectory in Directory.GetDirectories(directory))
            count += FileCountRecursive(subDirectory);

        return count;
    }

    public static void Move(string sourceFilePath, string destinationFilePath, bool overwrite = true)
    {
        string source = Path.GetFullPath(sourceFilePath);
        string destination = Path.GetFullPath(destinationFilePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Source file does not exist.", source);
        if (source == destination) return;

        if (Path.GetDirectoryName(source) == Path.GetDirectoryName(destination))
        {
            File.Move(source, destination, overwrite);
            return;
        }

        // File.Move can fall back to a copy between mounts. Let that copy finish
        // under a temporary name before replacing the user's previous recording.
        string temporary = TemporarySibling(destination);
        try
        {
            File.Move(source, temporary);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
        try
        {
            File.Move(temporary, destination, overwrite);
        }
        catch
        {
            File.Move(temporary, source);
            throw;
        }
    }

    internal static void CopyToDestination(string source, string destination, bool overwrite)
    {
        string temporary = TemporarySibling(destination);
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Keep generated names portable to filesystems with a 255-byte component
    // limit. Retain the file type and distinguish names with the same prefix.
    internal static string FitPathComponent(string name)
    {
        const int maxBytes = 255;
        if (Encoding.UTF8.GetByteCount(name) <= maxBytes) return name;
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name));
        string suffix = "~" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
        string extension = Path.GetExtension(name);
        int available = maxBytes - suffix.Length - Encoding.UTF8.GetByteCount(extension);
        if (available < 1)
        {
            extension = "";
            available = maxBytes - suffix.Length;
        }
        int chars = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > available) break;
            available -= rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        return name[..chars] + suffix + extension;
    }

    // A planner whose reservations only grow can reuse the next suffix for the
    // exact spelling of each path. Filesystem callers must check from the start.
    internal static string ResolvePathCollision(string path, Func<string, bool> tryReserve,
        Dictionary<string, int>? nextSuffixByPath = null)
    {
        if (tryReserve(path))
            return path;

        string directory = Path.GetDirectoryName(path)!;
        string extension = Path.GetExtension(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        int suffix = nextSuffixByPath?.GetValueOrDefault(path, 2) ?? 2;
        for (; ; suffix++)
        {
            string candidate = Path.Combine(directory, FitPathComponent($"{stem} ({suffix}){extension}"));
            if (tryReserve(candidate))
            {
                if (nextSuffixByPath != null) nextSuffixByPath[path] = suffix + 1;
                return candidate;
            }
        }
    }

    internal static string FitFileName(string path)
        => Path.Join(Path.GetDirectoryName(path), FitPathComponent(Path.GetFileName(path)));

    // File replacement changes the inode. Lock the destination name so two
    // Sockseek processes can read, merge and publish as one operation.
    internal static void UpdateFile(string path, Action update)
    {
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
            fullPath = File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath;
        if (OperatingSystem.IsWindows()) fullPath = fullPath.ToUpperInvariant();
        string name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
        using var mutex = new Mutex(false, "Sockseek.File." + name);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* The previous writer exited. We own the mutex. */ }
        try { update(); }
        finally { mutex.ReleaseMutex(); }
    }

    internal static void WriteAtomically(string path, Action<FileStream> write)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path))
            path = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        string temporary = TemporarySibling(path);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows() && File.Exists(path))
                options.UnixCreateMode = File.GetUnixFileMode(path);
            using (var output = new FileStream(temporary, options))
                write(output);
            if (!OperatingSystem.IsWindows() && options.UnixCreateMode is { } mode)
                File.SetUnixFileMode(temporary, mode);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static string TemporarySibling(string destination) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, $".{Guid.NewGuid():N}.incomplete");

    public static void DeleteAncestorsIfEmpty(string startDir, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        string boundary = Path.GetFullPath(root);
        for (string? path = Path.GetFullPath(startDir);
             path != null && IsInDirectory(path, boundary, strict: true);
             path = Path.GetDirectoryName(path))
        {
            // A nonempty folder or a concurrent creator ends cleanup. The
            // completed file has already been published at its destination.
            try { Directory.Delete(path, recursive: false); }
            catch (IOException) { break; }
            catch (UnauthorizedAccessException) { break; }
        }
    }

    public static void MoveAndDeleteParent(string oldPath, string newPath, string recurseUntil, bool overwrite = true)
    {
        if (Path.GetFullPath(oldPath) != Path.GetFullPath(newPath))
        {
            var newParent = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(newParent))
                Directory.CreateDirectory(newParent);
            Move(oldPath, newPath, overwrite);
            var oldParent = Path.GetDirectoryName(oldPath);
            if (!string.IsNullOrEmpty(oldParent))
                DeleteAncestorsIfEmpty(oldParent, recurseUntil);
        }
    }

    public static void DeleteFileAndParentsIfEmpty(string filepath, string recurseUntil)
    {
        File.Delete(filepath);
        var parent = Path.GetDirectoryName(filepath);
        if (!string.IsNullOrEmpty(parent))
            DeleteAncestorsIfEmpty(parent, recurseUntil);
    }

    public static bool EqualsAny(this string input, string[] values, StringComparison comparison = StringComparison.Ordinal)
    {
        foreach (var value in values)
        {
            if (input.Equals(value, comparison))
                return true;
        }
        return false;
    }

    public static string Replace(this string s, string[] separators, string newVal)
    {
        if (s.Length == 0)
            return s;

        foreach (var sep in separators)
            s = s.Replace(sep, newVal);

        return s;
    }

    public static string UnHtmlString(this string s)
    {
        s = WebUtility.HtmlDecode(s);
        string[] zeroWidthChars = { "\u200B", "\u200C", "\u200D", "\u00AD", "\u200E", "\u200F" };
        foreach (var zwChar in zeroWidthChars)
            s = s.Replace(zwChar, "");

        s = s.Replace('\u00A0', ' ');
        return s;
    }

    private static readonly char[] s_controlChars = Enumerable.Range(0, 0x20)
        .Concat(Enumerable.Range(0x7f, 0x21))
        .Select(static value => (char)value)
        .ToArray();

    private static readonly SearchValues<char> s_windowsInvalid =
        SearchValues.Create(new[] { ':', '|', '?', '>', '<', '*', '"', '/', '\\' }
            .Concat(s_controlChars)
            .Distinct()
            .ToArray());

    private static readonly SearchValues<char> s_windowsInvalidNoSlash =
        SearchValues.Create(new[] { ':', '|', '?', '>', '<', '*', '"' }
            .Concat(s_controlChars)
            .Distinct()
            .ToArray());

    private static readonly SearchValues<char> s_platformInvalid =
        SearchValues.Create(Path.GetInvalidFileNameChars()
            .Concat(s_controlChars)
            .Distinct()
            .ToArray());

    private static readonly SearchValues<char> s_platformInvalidNoSlash =
        SearchValues.Create(Path.GetInvalidFileNameChars()
            .Where(static c => c is not '/' and not '\\')
            .Concat(s_controlChars)
            .Distinct()
            .ToArray());

    private static SearchValues<char> InvalidSet(bool windows, bool removeSlash) => windows
        ? (removeSlash ? s_windowsInvalid : s_windowsInvalidNoSlash)
        : (removeSlash ? s_platformInvalid : s_platformInvalidNoSlash);

    public static string ReplaceInvalidChars(this string str, char replaceChar, bool windows = false, bool removeSlash = true)
    {
        if (str.Length == 0)
            return str;

        var invalid = InvalidSet(windows, removeSlash);
        int first = str.AsSpan().IndexOfAny(invalid);
        if (first < 0)
            return str;

        return string.Create(str.Length, (str, replaceChar, invalid, first), static (dst, s) =>
        {
            s.str.AsSpan().CopyTo(dst);
            dst[s.first] = s.replaceChar;
            for (int i = s.first + 1; i < dst.Length; i++)
                if (s.invalid.Contains(dst[i]))
                    dst[i] = s.replaceChar;
        });
    }

    public static string ReplaceInvalidChars(this string str, string replaceStr, bool windows = false, bool removeSlash = true)
    {
        if (str.Length == 0)
            return str;

        var invalid = InvalidSet(windows, removeSlash);
        int first = str.AsSpan().IndexOfAny(invalid);
        if (first < 0)
            return str;

        if (replaceStr.Length == 1)
            return str.ReplaceInvalidChars(replaceStr[0], windows, removeSlash);

        if (replaceStr.Length == 0)
        {
            int keep = first;
            for (int i = first; i < str.Length; i++)
                if (!invalid.Contains(str[i])) keep++;

            return string.Create(keep, (str, invalid, first), static (dst, s) =>
            {
                s.str.AsSpan()[..s.first].CopyTo(dst);
                int di = s.first;
                for (int i = s.first; i < s.str.Length; i++)
                    if (!s.invalid.Contains(s.str[i]))
                        dst[di++] = s.str[i];
            });
        }

        var sb = new StringBuilder(str.Length * 2);
        sb.Append(str, 0, first);
        sb.Append(replaceStr);
        for (int i = first + 1; i < str.Length; i++)
        {
            if (invalid.Contains(str[i])) sb.Append(replaceStr);
            else sb.Append(str[i]);
        }
        return sb.ToString();
    }

    public static string CleanPath(this string fullPath, string replaceWith)
    {
        fullPath = Utils.NormalizedPath(fullPath);

        string[] pathParts = fullPath.Split('/');

        foreach (char badChar in Path.GetInvalidPathChars())
        {
            if (badChar != ':')
            {
                pathParts[0] = pathParts[0].Replace(badChar.ToString(), replaceWith);
            }
        }

        var chars = Path.GetInvalidFileNameChars();

        for (int i = 1; i < pathParts.Length; i++)
        {
            foreach (char badChar in chars)
            {
                pathParts[i] = pathParts[i].Replace(badChar.ToString(), replaceWith);
            }
        }

        if (pathParts.Length > 0)
            pathParts[pathParts.Length - 1] = pathParts[pathParts.Length - 1].TrimEnd('.');

        return string.Join('/', pathParts.Select(a => a.Trim()));
    }

    private static readonly SearchValues<char> s_special = SearchValues.Create(
        [';', ':', '：', '\'', '"', '|', '?', '？', '!', '！', '<', '>', '*', '/', '\\', '+',
        '[', ']', '{', '}', '(', ')', '-',
        '–', '—', '―',
        '&', '%', '^', '$', '#', '＃', '@', '+', '=', '`', '~', '_',
        '；', '：', '｜', '？', '！', '＜', '＞', '／', '＼', '［', '］', '｛', '｝', '＝', '｀', '＿',
        '꞉', 'ː',
        '\u2018', '\u2019', '"', '"',
        '•', '·',
        '【', '】', '「', '」', '『', '』', '《', '》',
        '～', '«', '»', '“', '”', '＂', '＄', '％', '＆', '＇', '（', '）', '＊', '＋', '，', '－', '．']);

    public static string ReplaceSpecialChars(this string str, string replaceStr)
    {
        if (str.Length == 0)
            return str;

        int first = str.AsSpan().IndexOfAny(s_special);
        if (first < 0)
            return str;

        if (replaceStr.Length == 1)
        {
            char rc = replaceStr[0];
            return string.Create(str.Length, (str, rc, first), static (dst, s) =>
            {
                s.str.AsSpan().CopyTo(dst);
                dst[s.first] = s.rc;
                for (int i = s.first + 1; i < dst.Length; i++)
                    if (s_special.Contains(dst[i]))
                        dst[i] = s.rc;
            });
        }

        if (replaceStr.Length == 0)
        {
            int keep = first;
            for (int i = first; i < str.Length; i++)
                if (!s_special.Contains(str[i])) keep++;

            return string.Create(keep, (str, first), static (dst, s) =>
            {
                s.str.AsSpan()[..s.first].CopyTo(dst);
                int di = s.first;
                for (int i = s.first; i < s.str.Length; i++)
                    if (!s_special.Contains(s.str[i]))
                        dst[di++] = s.str[i];
            });
        }

        var sb = new StringBuilder(str.Length * 2);
        sb.Append(str, 0, first);
        sb.Append(replaceStr);
        for (int i = first + 1; i < str.Length; i++)
        {
            if (s_special.Contains(str[i])) sb.Append(replaceStr);
            else sb.Append(str[i]);
        }
        return sb.ToString();
    }

    public static bool IsSpecialChar(char c) => s_special.Contains(c);

    public static string RemoveFt(this string str, bool removeParentheses = true)
    {
        if (str.Length == 0)
            return str;

        int ftIndex = FeatureCreditIndex(str);
        if (ftIndex == -1)
            return str;

        if (removeParentheses && ftIndex > 0)
        {
            int openIdx = ftIndex - 1;
            while (openIdx > 0 && char.IsWhiteSpace(str[openIdx]))
                openIdx--;
            char closeChar = str[openIdx] switch
            {
                '(' => ')',
                '[' => ']',
                _ => '\0',
            };

            if (closeChar != '\0')
            {
                int closeIdx = str.IndexOf(closeChar, ftIndex);
                if (closeIdx != -1)
                {
                    int versionIdx = str.IndexOf(" - ", ftIndex, closeIdx - ftIndex, StringComparison.Ordinal);
                    if (versionIdx >= 0)
                        return str.Remove(ftIndex, versionIdx + 3 - ftIndex).RemoveFt(removeParentheses);
                    int add = 0;
                    if (openIdx > 0 && closeIdx < str.Length - 1 && char.IsWhiteSpace(str[openIdx - 1]) && char.IsWhiteSpace(str[closeIdx + 1]))
                        add = 1;
                    return str.Remove(openIdx, closeIdx - openIdx + 1 + add).TrimEnd().RemoveFt(removeParentheses);
                }
            }
        }

        var separatorIdx = str.IndexOf(" - ", ftIndex);
        return separatorIdx != -1
            ? str.Remove(ftIndex, separatorIdx - ftIndex + 1).TrimEnd().RemoveFt(removeParentheses)
            : str[..ftIndex].TrimEnd();
    }

    internal static bool HasFeatureCredit(string str) => FeatureCreditIndex(str) >= 0;
    internal static bool HasFeatureCredit(ReadOnlySpan<char> str) => FeatureCreditIndex(str) >= 0;

    private static int FeatureCreditIndex(ReadOnlySpan<char> str)
    {
        int offset = 0;
        while (offset < str.Length)
        {
            int found = str[offset..].IndexOfAny('f', 'F');
            if (found < 0)
                return -1;
            int i = offset + found;
            offset = i + 1;
            if (i > 0 && !char.IsWhiteSpace(str[i - 1]) && str[i - 1] is not '(' and not '[')
                continue;
            var suffix = str[i..];
            int markerLength = suffix.StartsWith("feat.", StringComparison.OrdinalIgnoreCase) ? 5
                : suffix.StartsWith("ft.", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
            if (markerLength > 0 && (suffix.Length == markerLength || char.IsWhiteSpace(suffix[markerLength])))
                return i;
        }
        return -1;
    }

    public static string RemoveConsecutiveWs(this string input)
    {
        if (input.Length == 0)
            return string.Empty;

        char[]? buffer = null;
        int index = 0;
        bool previousWhitespace = false;

        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            bool isWhitespace = char.IsWhiteSpace(ch);

            if (isWhitespace && previousWhitespace)
            {
                if (buffer == null)
                {
                    buffer = input.ToCharArray();
                    index = i;
                }
                continue;
            }

            if (buffer != null)
                buffer[index++] = ch;

            previousWhitespace = isWhitespace;
        }

        return buffer == null ? input : new string(buffer, 0, index);
    }

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex SquareBracketsRegex();

    public static string RemoveSquareBrackets(this string str)
    {
        if (str.Length == 0)
            return str;
        if (!str.Contains('['))
            return str;
        return SquareBracketsRegex().Replace(str, "").Trim();
    }

    public static bool ContainsIgnoreCase(this string s, string other)
    {
        return s.Contains(other, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBoundaryChar(char c)
        => c is '-' or '|' or '.' or '\\' or '/' or '_' or '—' or '–' or '―'
            or '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>'
            or '「' or '」' or '【' or '】' or '『' or '』' or '《' or '》'
            or ',' or ':' or '?' or '!' or ';'
            or '@' or '#' or '*' or '=' or '+'
            or '\'' or '"' or '\u2018' or '\u2019' or '\u201C' or '\u201D'
            or '$' or '^' or '&' or '`' or '~' or '～' or '%'
            or '•' or '·' or '«' or '»';

    public static bool ContainsWithBoundary(this string str, string? value, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        if (str.Length == 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        int index = 0;
        while ((index = str.IndexOf(value, index, comp)) != -1)
        {
            bool hasLeftBoundary = index == 0 || char.IsWhiteSpace(str[index - 1]) || IsBoundaryChar(str[index - 1]);
            bool hasRightBoundary = index + value.Length >= str.Length || char.IsWhiteSpace(str[index + value.Length]) || IsBoundaryChar(str[index + value.Length]);

            if (hasLeftBoundary && hasRightBoundary)
                return true;

            index++;
        }

        return false;
    }

    public static bool ContainsWithBoundaryIgnoreWs(this string str, string? value, bool ignoreCase = false, bool acceptLeftDigit = false)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        if (str.Length == 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        int index = 0;
        while ((index = str.IndexOf(value, index, comp)) != -1)
        {
            int leftIndex = index - 1;
            while (leftIndex >= 0 && char.IsWhiteSpace(str[leftIndex]))
                leftIndex--;

            bool hasLeftBoundary = leftIndex < 0 || acceptLeftDigit && leftIndex < index - 1 && char.IsDigit(str[leftIndex]) || IsBoundaryChar(str[leftIndex]);

            int rightIndex = index + value.Length;
            while (rightIndex < str.Length && char.IsWhiteSpace(str[rightIndex]))
                rightIndex++;

            bool hasRightBoundary = rightIndex >= str.Length || IsBoundaryChar(str[rightIndex]);

            if (hasLeftBoundary && hasRightBoundary)
                return true;

            index++;
        }

        return false;
    }

    [GeneratedRegex(@"\[(.*?)\]|\((.*?)\)")]
    private static partial Regex BracketsRegex();

    public static bool ContainsInBrackets(this string str, string searchTerm, bool ignoreCase = false)
    {
        if (str.Length == 0 && searchTerm.Length > 0)
            return false;

        var matches = BracketsRegex().Matches(str);
        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (Match match in matches)
        {
            if (match.Value.Contains(searchTerm, comp))
                return true;
        }

        return false;
    }

    public static string GreatestCommonPath(IEnumerable<string> paths)
    {
        using var enumerator = paths.GetEnumerator();
        if (!enumerator.MoveNext())
            return "";

        string? path = enumerator.Current;

        if (path == null || path.Length == 0)
            return "";

        static int commonPathIndex(string path1, string path2, int maxIndex)
        {
            var minLength = Math.Min(path1.Length, Math.Min(path2.Length, maxIndex));
            var commonPathLength = 0;
            for (int i = 0; i < minLength; i++)
            {
                if ((path1[i] == '/' || path1[i] == '\\') && (path2[i] == '/' || path2[i] == '\\'))
                    commonPathLength = i + 1;
                else if (path1[i] != path2[i])
                    break;
            }
            return commonPathLength;
        }

        int index = path.Length;

        while (enumerator.MoveNext())
        {
            var p = enumerator.Current;
            index = commonPathIndex(path, p, index);
        }

        return path[..index];
    }

    public static string GreatestCommonDirectory(IEnumerable<string> paths)
    {
        var pathList = paths as IReadOnlyList<string> ?? paths.ToArray();
        return pathList.Count > 1
            ? NormalizedPath(GreatestCommonPath(pathList))
            : NormalizedPath(Path.GetDirectoryName(pathList[0].TrimEnd('/').TrimEnd('\\')) ?? "");
    }

    public static string GreatestCommonDirectorySlsk(IEnumerable<string> paths)
    {
        var pathList = paths as IReadOnlyList<string> ?? paths.ToArray();
        return pathList.Count > 1
            ? Utils.GreatestCommonPath(pathList).Replace('/', '\\').TrimEnd('\\')
            : Utils.GetDirectoryNameSlsk(pathList[0]).Replace('/', '\\').TrimEnd('\\');
    }

    public static string NormalizedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        path = path.Replace('\\', '/').TrimEnd('/').Trim();

        while (path.Contains("//"))
            path = path.Replace("//", "/");

        return path;
    }

    public static bool IsInDirectory(string path, string dir, bool strict)
    {
        path = NormalizedPath(path);
        dir = NormalizedPath(dir);
        return strict ? path.StartsWith(dir + '/') : path.StartsWith(dir);
    }

    public static (string? Artist, string Title) SplitArtistAndTitle(string inputTitle)
    {
        if (string.IsNullOrEmpty(inputTitle))
        {
            return (null, inputTitle);
        }

        const string separator = " - ";
        inputTitle = inputTitle.Replace(" — ", separator).Replace(" – ", separator).Replace(" -- ", separator);

        int separatorLength = separator.Length;
        int lastValidSeparatorIndex = -1;
        int validSeparatorCount = 0;
        int parenLevel = 0;
        int bracketLevel = 0;

        for (int i = 0; i < inputTitle.Length; i++)
        {
            char c = inputTitle[i];

            bool isPotentialSeparator = (i <= inputTitle.Length - separatorLength) &&
                                        string.CompareOrdinal(inputTitle, i, separator, 0, separatorLength) == 0;

            if (isPotentialSeparator && parenLevel == 0 && bracketLevel == 0)
            {
                validSeparatorCount++;
                lastValidSeparatorIndex = i;
                if (validSeparatorCount > 1) break;
                i += separatorLength - 1;
                continue;
            }

            if (c == '(') parenLevel++;
            else if (c == ')') parenLevel = Math.Max(0, parenLevel - 1);
            else if (c == '[') bracketLevel++;
            else if (c == ']') bracketLevel = Math.Max(0, bracketLevel - 1);
        }

        if (validSeparatorCount == 1)
        {
            string potentialArtist = inputTitle[..lastValidSeparatorIndex].Trim();
            string potentialTitle = inputTitle[(lastValidSeparatorIndex + separatorLength)..].Trim();

            if (potentialArtist.Length > 0 && potentialTitle.Length > 0)
            {
                return (potentialArtist, potentialTitle);
            }
        }

        return (null, inputTitle);
    }

    // Display filters compare human-readable text. Wire names and path identities keep their exact spelling.
    public static string FoldDisplayText(string value)
    {
        if (Ascii.IsValid(value)) return value;
        try
        {
            return DisplayAccentMarks().Replace(value.Normalize(NormalizationForm.FormD), "");
        }
        catch (ArgumentException)
        {
            // An ill-formed peer name is still searchable by its literal spelling.
            return value;
        }
    }

    [GeneratedRegex(@"\p{M}+")]
    private static partial Regex DisplayAccentMarks();

    public static bool ContainsDisplayText(string value, string filter)
        => FoldDisplayText(value).Contains(FoldDisplayText(filter), StringComparison.OrdinalIgnoreCase);

    public static bool RemoveDiacriticsIfExist(this string s, out string res)
    {
        res = s.RemoveDiacritics();
        return !ReferenceEquals(res, s);
    }

    public static char RemoveDiacritics(this char c)
    {
        if (diacriticChars.TryGetValue(c, out var res)) return res;
        return c;
    }

    public static string RemoveDiacritics(this string s)
    {
        if (s.Length == 0)
            return s;

        int first = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 128 && diacriticChars.ContainsKey(c))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
            return s;

        return string.Create(s.Length, (s, first), static (dst, state) =>
        {
            state.s.AsSpan().CopyTo(dst);

            if (diacriticChars.TryGetValue(dst[state.first], out char firstReplacement))
                dst[state.first] = firstReplacement;

            for (int i = state.first + 1; i < dst.Length; i++)
                if (dst[i] >= 128 && diacriticChars.TryGetValue(dst[i], out char replacement))
                    dst[i] = replacement;
        });
    }

    static readonly Dictionary<char, char> diacriticChars = new()
    {
        { 'ä', 'a' }, { 'æ', 'a' }, { 'ǽ', 'a' }, { 'œ', 'o' }, { 'ö', 'o' }, { 'ü', 'u' },
        { 'Ä', 'A' }, { 'Ü', 'U' }, { 'Ö', 'O' }, { 'À', 'A' }, { 'Á', 'A' }, { 'Â', 'A' },
        { 'Ã', 'A' }, { 'Å', 'A' }, { 'Ǻ', 'A' }, { 'Ā', 'A' }, { 'Ă', 'A' }, { 'Ą', 'A' },
        { 'Ǎ', 'A' }, { 'Ά', 'A' }, { 'Ả', 'A' }, { 'Ạ', 'A' }, { 'Ầ', 'A' }, { 'Ấ', 'A' },
        { 'Ẫ', 'A' }, { 'Ẩ', 'A' }, { 'Ậ', 'A' }, { 'à', 'a' }, { 'á', 'a' }, { 'â', 'a' },
        { 'ã', 'a' }, { 'å', 'a' }, { 'ǻ', 'a' }, { 'ā', 'a' }, { 'ă', 'a' }, { 'ą', 'a' },
        { 'ǎ', 'a' }, { 'ả', 'a' }, { 'ạ', 'a' }, { 'Ç', 'C' }, { 'Ć', 'C' }, { 'Ĉ', 'C' },
        { 'Ċ', 'C' }, { 'Č', 'C' }, { 'ç', 'c' }, { 'ć', 'c' }, { 'ĉ', 'c' }, { 'ċ', 'c' },
        { 'č', 'c' }, { 'Ð', 'D' }, { 'Ď', 'D' }, { 'Đ', 'D' }, { 'ð', 'd' }, { 'ď', 'd' },
        { 'đ', 'd' }, { 'È', 'E' }, { 'É', 'E' }, { 'Ê', 'E' }, { 'Ë', 'E' }, { 'Ē', 'E' },
        { 'Ĕ', 'E' }, { 'Ė', 'E' }, { 'Ę', 'E' }, { 'Ě', 'E' }, { 'Έ', 'E' }, { 'Ẽ', 'E' },
        { 'Ẻ', 'E' }, { 'Ẹ', 'E' }, { 'Ề', 'E' }, { 'Ế', 'E' }, { 'Ễ', 'E' }, { 'Ể', 'E' },
        { 'Ệ', 'E' }, { 'è', 'e' }, { 'é', 'e' }, { 'ê', 'e' }, { 'ë', 'e' }, { 'ē', 'e' },
        { 'ĕ', 'e' }, { 'ė', 'e' }, { 'ę', 'e' }, { 'ě', 'e' }, { 'ẽ', 'e' }, { 'ẻ', 'e' },
        { 'ẹ', 'e' }, { 'Ĝ', 'G' }, { 'Ğ', 'G' }, { 'Ġ', 'G' }, { 'Ģ', 'G' }, { 'ĝ', 'g' },
        { 'ğ', 'g' }, { 'ġ', 'g' }, { 'ģ', 'g' }, { 'Ĥ', 'H' }, { 'Ħ', 'H' }, { 'Ή', 'H' },
        { 'ĥ', 'h' }, { 'ħ', 'h' }, { 'Ì', 'I' }, { 'Í', 'I' }, { 'Î', 'I' }, { 'Ï', 'I' },
        { 'Ĩ', 'I' }, { 'Ī', 'I' }, { 'Ĭ', 'I' }, { 'Ǐ', 'I' }, { 'Į', 'I' }, { 'İ', 'I' },
        { 'Ί', 'I' }, { 'Ϊ', 'I' }, { 'Ỉ', 'I' }, { 'Ị', 'I' }, { 'Ї', 'I' }, { 'ì', 'i' },
        { 'í', 'i' }, { 'î', 'i' }, { 'ï', 'i' }, { 'ĩ', 'i' }, { 'ī', 'i' }, { 'ĭ', 'i' },
        { 'ǐ', 'i' }, { 'į', 'i' }, { 'ı', 'i' }, { 'ΰ', 'y' }, { 'Ĵ', 'J' }, { 'ĵ', 'j' },
        { 'Ķ', 'K' }, { 'ķ', 'k' }, { 'Ĺ', 'L' }, { 'Ļ', 'L' }, { 'Ľ', 'L' }, { 'Ŀ', 'L' },
        { 'Ł', 'L' }, { 'ĺ', 'l' }, { 'ļ', 'l' }, { 'ľ', 'l' }, { 'ŀ', 'l' }, { 'ł', 'l' },
        { 'Ñ', 'N' }, { 'Ń', 'N' }, { 'Ņ', 'N' }, { 'Ň', 'N' }, { 'ñ', 'n' }, { 'ń', 'n' },
        { 'ņ', 'n' }, { 'ň', 'n' }, { 'ŉ', 'n' }, { 'Ò', 'O' }, { 'Ó', 'O' }, { 'Ô', 'O' },
        { 'Õ', 'O' }, { 'Ō', 'O' }, { 'Ŏ', 'O' }, { 'Ǒ', 'O' }, { 'Ő', 'O' }, { 'Ơ', 'O' },
        { 'Ø', 'O' }, { 'Ǿ', 'O' }, { 'Ό', 'O' }, { 'Ỏ', 'O' }, { 'Ọ', 'O' }, { 'Ồ', 'O' },
        { 'Ố', 'O' }, { 'Ỗ', 'O' }, { 'Ổ', 'O' }, { 'Ộ', 'O' }, { 'Ờ', 'O' }, { 'Ớ', 'O' },
        { 'Ỡ', 'O' }, { 'Ở', 'O' }, { 'Ợ', 'O' }, { 'ò', 'o' }, { 'ó', 'o' }, { 'ô', 'o' },
        { 'õ', 'o' }, { 'ō', 'o' }, { 'ŏ', 'o' }, { 'ǒ', 'o' }, { 'ő', 'o' }, { 'ơ', 'o' },
        { 'ø', 'o' }, { 'ǿ', 'o' }, { 'º', 'o' }, { 'ό', 'o' }, { 'ỏ', 'o' }, { 'ọ', 'o' },
        { 'ồ', 'o' }, { 'ố', 'o' }, { 'ỗ', 'o' }, { 'ổ', 'o' }, { 'ộ', 'o' }, { 'ờ', 'o' },
        { 'ớ', 'o' }, { 'ỡ', 'o' }, { 'ở', 'o' }, { 'ợ', 'o' }, { 'Ŕ', 'R' }, { 'Ŗ', 'R' },
        { 'Ř', 'R' }, { 'ŕ', 'r' }, { 'ŗ', 'r' }, { 'ř', 'r' }, { 'Ś', 'S' }, { 'Ŝ', 'S' },
        { 'Ş', 'S' }, { 'Ș', 'S' }, { 'Š', 'S' }, { 'ś', 's' }, { 'ŝ', 's' }, { 'ş', 's' },
        { 'ș', 's' }, { 'š', 's' }, { 'Ț', 'T' }, { 'Ţ', 'T' }, { 'Ť', 'T' }, { 'Ŧ', 'T' },
        { 'Т', 'T' }, { 'ț', 't' }, { 'ţ', 't' }, { 'ť', 't' }, { 'ŧ', 't' }, { 'Ù', 'U' },
        { 'Ú', 'U' }, { 'Û', 'U' }, { 'Ũ', 'U' }, { 'Ū', 'U' }, { 'Ŭ', 'U' }, { 'Ů', 'U' },
        { 'Ű', 'U' }, { 'Ų', 'U' }, { 'Ư', 'U' }, { 'Ǔ', 'U' }, { 'Ǖ', 'U' }, { 'Ǘ', 'U' },
        { 'Ǚ', 'U' }, { 'Ǜ', 'U' }, { 'Ủ', 'U' }, { 'Ụ', 'U' }, { 'Ừ', 'U' }, { 'ё', 'e' },
        { 'Ứ', 'U' }, { 'Ữ', 'U' }, { 'Ử', 'U' }, { 'Ự', 'U' }, { 'ù', 'u' }, { 'ú', 'u' },
        { 'û', 'u' }, { 'ũ', 'u' }, { 'ū', 'u' }, { 'ŭ', 'u' }, { 'ů', 'u' }, { 'ű', 'u' },
        { 'ų', 'u' }, { 'ư', 'u' }, { 'ǔ', 'u' }, { 'ǖ', 'u' }, { 'ǘ', 'u' }, { 'ǚ', 'u' },
        { 'ǜ', 'u' }, { 'ủ', 'u' }, { 'ụ', 'u' }, { 'ừ', 'u' }, { 'ứ', 'u' }, { 'ữ', 'u' },
        { 'ử', 'u' }, { 'ự', 'u' }, { 'Ý', 'Y' }, { 'Ÿ', 'Y' }, { 'Ŷ', 'Y' }, { 'Ύ', 'Y' },
        { 'Ϋ', 'Y' }, { 'Ỳ', 'Y' }, { 'Ỹ', 'Y' }, { 'Ỷ', 'Y' }, { 'Ỵ', 'Y' }, { 'й', 'и' },
        { 'ý', 'y' }, { 'ÿ', 'y' }, { 'ŷ', 'y' }, { 'ỳ', 'y' }, { 'ỹ', 'y' }, { 'ỷ', 'y' },
        { 'ỵ', 'y' }, { 'Ŵ', 'W' }, { 'ŵ', 'w' }, { 'Ź', 'Z' }, { 'Ż', 'Z' }, { 'Ž', 'Z' },
        { 'ź', 'z' }, { 'ż', 'z' }, { 'ž', 'z' }, { 'Æ', 'A' }, { 'ß', 's' }, { 'Œ', 'O' },
        { 'Ё', 'E' },
    };
}
