using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core.Services;

public sealed class FileOrganizationException : IOException
{
    public FileOrganizationException(string message, string sourcePath, string targetPath, Exception innerException)
        : base(message, innerException)
    {
        SourcePath = sourcePath;
        TargetPath = targetPath;
    }

    public string SourcePath { get; }
    public string TargetPath { get; }
}

// Context object passed to music-variable extractors and name-format helpers.
// Constructed from a SongJob so name format works uniformly across single songs and album files.
public struct FileManagerContext
{
    public Job Job;
    public string ExtractorName;  // {extractor}
    public string InputSource;    // {input}
    public string OutputDir;      // {output-dir}
    public string ConfigDir;      // {configdir}
    public string DefaultFolder;   // {default-folder}
    public SongQuery Query;         // artist, title, album, length, uri, artistMaybeWrong
    public FileCandidate? Candidate;    // slsk-filename, slsk-foldername
    public PeerFileTarget? PeerTarget;  // exact remote identity; does not imply search evidence
    public string? DownloadPath;  // path, path-noext, ext
    public JobTerminalOutcome TerminalOutcome;
    public JobSkipReason SkipReason;
    public JobFailureReason FailureReason;
    public bool IsNotAudio;
    public int LineNumber;
    public int ItemNumber;
    public string? RemoteBaseDir;

    public static FileManagerContext FromSongJob(SongJob song, Job job, string? remoteBaseDir = null)
    {
        return new FileManagerContext
        {
            Job = job,
            Query = song.Query,
            Candidate = song.ResolvedTarget ?? song.Candidates?.FirstOrDefault(),
            PeerTarget = song.ResolvedPeerTarget ?? song.Candidates?.FirstOrDefault()?.Target,
            DownloadPath = song.DownloadPath,
            TerminalOutcome = song.TerminalOutcome,
            SkipReason = song.SkipReason,
            FailureReason = song.FailureReason,
            IsNotAudio = song.IsNotAudio,
            LineNumber = song.LineNumber,
            ItemNumber = song.ItemNumber,
            RemoteBaseDir = remoteBaseDir,
        };
    }

}

public sealed class OutputScope
{
    private readonly string[] defaultFolderSegments;

    private OutputScope(IEnumerable<string> defaultFolderSegments)
    {
        this.defaultFolderSegments = defaultFolderSegments.ToArray();
    }

    public static OutputScope Empty { get; } = new([]);

    public IReadOnlyList<string> DefaultFolderSegments => defaultFolderSegments;

    public string DefaultFolder => defaultFolderSegments.Length == 0
        ? ""
        : Path.Join(defaultFolderSegments);

    public static string OutputParentDir(OutputSettings output)
        => string.IsNullOrWhiteSpace(output.ParentDir)
            ? Directory.GetCurrentDirectory()
            : output.ParentDir;

    public string DefaultDirectory(OutputSettings output)
        => defaultFolderSegments.Length == 0
            ? OutputParentDir(output)
            : Path.Join([OutputParentDir(output), .. defaultFolderSegments]);

    public string ScopedPath(OutputSettings output, params string[] pathParts)
        => Path.Join([DefaultDirectory(output), .. pathParts]);

    public OutputScope WithDefaultFolder(string? folderName, string invalidReplaceStr)
    {
        var cleaned = CleanDefaultFolderSegment(folderName, invalidReplaceStr);
        if (string.IsNullOrEmpty(cleaned))
            return this;

        return new OutputScope(defaultFolderSegments.Append(cleaned));
    }

    public static OutputScope ForLegacyOwner(Job job, OutputSettings output)
        => Empty.WithDefaultFolder(job.DefaultFolderName(), output.InvalidReplaceStr);

    public static OutputScope ForPreparedJob(Job job, OutputScope inherited, OutputSettings output)
        => JobCreatesDefaultFolderScope(job)
            ? inherited.WithDefaultFolder(job.DefaultFolderName(), output.InvalidReplaceStr)
            : inherited;

    private static bool JobCreatesDefaultFolderScope(Job job)
        => job is JobList or SongGroupsJob or AlbumGroupsJob;

    private static string CleanDefaultFolderSegment(string? folderName, string invalidReplaceStr)
        => string.IsNullOrWhiteSpace(folderName)
            ? ""
            : folderName
                .ReplaceInvalidChars(invalidReplaceStr)
                .Trim(' ', '.');
}


public class FileManager
{
    readonly Job job;
    // TODO [PLACEMENT STATE]: Replace this organizer-local bookkeeping and the
    // remainingOnly flag with explicit per-child placement state when Job state is
    // moved to the planned immutable reducer. Progressive album-track placement must
    // remain observable; this is not a request to defer all album files until the end.
    readonly HashSet<object> organized = new();
    readonly Lock sync = new();
    public string? remoteBaseDir { get; private set; }
    public string? remoteImagesCommonDir { get; private set; }
    public string? defaultFolderName { get; private set; }
    private bool downloadingAdditionalImagesValue = false;
    public bool downloadingAdditionalImages
    {
        get { lock (sync) return downloadingAdditionalImagesValue; }
        set { lock (sync) downloadingAdditionalImagesValue = value; }
    }
    private readonly OutputSettings output;
    private readonly ExtractionSettings extraction;
    private readonly OutputScope outputScope;
    private readonly ILogger<FileManager> logger;
    private int metadataReadFailureLogged;

    private string OutputParentDir => OutputScope.OutputParentDir(output);
    private string DefaultOutputDir => outputScope.DefaultDirectory(output);

    public FileManager(
        Job job,
        OutputSettings output,
        ExtractionSettings extraction,
        ILogger<FileManager> logger,
        OutputScope? outputScope = null)
    {
        this.job = job;
        this.output = output;
        this.extraction = extraction;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.outputScope = outputScope ?? OutputScope.ForLegacyOwner(job, output);
    }

    public string GetSavePath(string sourceFname)
    {
        return GetSavePathNoExt(sourceFname) + Path.GetExtension(sourceFname);
    }

    public string GetSavePathNoExt(string sourceFname)
    {
        lock (sync)
        {
            string? rcd = downloadingAdditionalImagesValue ? remoteImagesCommonDir : remoteBaseDir;
            string parent = DefaultOutputDir;
            string name = Utils.GetFileNameWithoutExtSlsk(sourceFname);

            if (job is AlbumJob && !string.IsNullOrEmpty(rcd))
            {
                string dirname = defaultFolderName ?? Path.GetFileName(rcd);
                string normFname = Utils.NormalizedPath(sourceFname);
                string relpath = normFname.StartsWith(rcd) ? Path.GetRelativePath(rcd, normFname) : "";
                parent = Path.Join(parent, dirname, Path.GetDirectoryName(relpath) ?? "");
            }

            return Path.Join(parent, name).CleanPath(output.InvalidReplaceStr);
        }
    }

    public void SetremoteBaseDir(string? dir)
    {
        lock (sync)
            this.remoteBaseDir = dir != null ? Utils.NormalizedPath(dir) : null;
    }

    public void SetRemoteCommonImagesDir(string? dir)
    {
        lock (sync)
            this.remoteImagesCommonDir = dir != null ? Utils.NormalizedPath(dir) : null;
    }

    public void SetDefaultFolderName(string? name)
    {
        lock (sync)
            this.defaultFolderName = name != null ? Utils.NormalizedPath(name) : null;
    }

    // Organizes all files in a completed album download.
    public void OrganizeAlbum(Job albumJob, List<SongJob> allFiles, List<SongJob>? additionalImages, bool remainingOnly = true, Func<SongJob, string, bool>? preparePlacement = null)
    {
        lock (sync)
        {
            foreach (var file in allFiles.Where(f => !f.IsNotAudio))
            {
                if (remainingOnly && organized.Contains(file))
                    continue;
                OrganizeDownloadedFile(file, preparePlacement);
            }

            var nonAudioToOrganize = string.IsNullOrEmpty(output.NameFormat)
                ? additionalImages
                : (IEnumerable<SongJob>)allFiles.Where(f => f.IsNotAudio);

            if (nonAudioToOrganize == null || !nonAudioToOrganize.Any())
                return;

            var completedAudioPaths = allFiles
                .Where(f => !f.IsNotAudio && f.IsSuccessfulTerminal && !string.IsNullOrEmpty(f.DownloadPath))
                .Select(f => f.DownloadPath!)
                .ToList();
            string parent = completedAudioPaths.Count == 0 ? OutputParentDir : Utils.GreatestCommonDirectory(
                completedAudioPaths);

            foreach (var file in nonAudioToOrganize)
            {
                if (remainingOnly && organized.Contains(file))
                    continue;
                OrganizeNonAudio(file, parent, additionalImages != null && additionalImages.Contains(file), preparePlacement);
            }
        }
    }

    public void OrganizeDownloadedFile(SongJob song, Func<SongJob, string, bool>? preparePlacement = null)
    {
        lock (sync)
        {
            if (string.IsNullOrEmpty(song.DownloadPath))
                return;

            if (output.NameFormat.Length == 0)
            {
                organized.Add(song);
                return;
            }

            string pathPart = ApplyNameFormat(output.NameFormat, FileManagerContext.FromSongJob(song, job, remoteBaseDir) with
            {
                ExtractorName = extraction.InputType.ToInputName(),
                InputSource = extraction.Input ?? "",
                OutputDir = OutputParentDir,
                DefaultFolder = outputScope.DefaultFolder,
                ConfigDir = job.Config?.RuntimePathContext.ConfigDir ?? "",
            });
            string extension = Path.GetExtension(song.DownloadPath);
            if (!string.IsNullOrEmpty(extension)
                && !pathPart.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                pathPart += extension;
            }
            string newFilePath = Path.Join(OutputParentDir, pathPart);

            if (Utils.NormalizedPath(newFilePath) != Utils.NormalizedPath(song.DownloadPath))
            {
                var oldFilePath = song.DownloadPath;
                try
                {
                    if (preparePlacement?.Invoke(song, newFilePath) == false)
                    {
                        organized.Add(song);
                        return;
                    }
                    Utils.MoveAndDeleteParent(oldFilePath, newFilePath, CleanupRootForSourcePath(oldFilePath));
                }
                catch (Exception ex)
                {
                    if (File.Exists(newFilePath) && !File.Exists(oldFilePath))
                    {
                        song.DownloadPath = newFilePath;
                        organized.Add(song);
                        return;
                    }

                    throw new FileOrganizationException(
                        $"Failed to move organized file from '{oldFilePath}' to '{newFilePath}'.",
                        oldFilePath,
                        newFilePath,
                        ex);
                }
            }

            song.DownloadPath = newFilePath;
            organized.Add(song);
        }
    }

    [Obsolete("Use OrganizeDownloadedFile. Placement applies to downloaded files of any requested format.")]
    public void OrganizeSong(SongJob song)
        => OrganizeDownloadedFile(song);

    private void OrganizeNonAudio(SongJob file, string parent, bool isAdditionalImage, Func<SongJob, string, bool>? preparePlacement)
    {
        if (string.IsNullOrEmpty(file.DownloadPath))
            return;

        string? part = null;
        string? rcd = isAdditionalImage ? remoteImagesCommonDir : remoteBaseDir;
        string filename = file.ResolvedTarget?.Filename ?? file.DownloadPath;

        if (rcd != null && Utils.IsInDirectory(Utils.GetDirectoryNameSlsk(filename), rcd, true))
            part = Utils.GetFileNameSlsk(Utils.GetDirectoryNameSlsk(filename));

        string newFilePath = Path.Join(parent, part, Path.GetFileName(file.DownloadPath));

        if (Utils.NormalizedPath(newFilePath) != Utils.NormalizedPath(file.DownloadPath))
        {
            var oldFilePath = file.DownloadPath;
            try
            {
                if (preparePlacement?.Invoke(file, newFilePath) == false)
                {
                    organized.Add(file);
                    return;
                }
                Utils.MoveAndDeleteParent(oldFilePath, newFilePath, CleanupRootForSourcePath(oldFilePath));
            }
            catch (Exception ex)
            {
                if (File.Exists(newFilePath) && !File.Exists(oldFilePath))
                {
                    file.DownloadPath = newFilePath;
                    organized.Add(file);
                    return;
                }

                throw new FileOrganizationException(
                    $"Failed to move album ancillary file from '{oldFilePath}' to '{newFilePath}'.",
                    oldFilePath,
                    newFilePath,
                    ex);
            }
        }

        file.DownloadPath = newFilePath;
        organized.Add(file);
    }

    private string CleanupRootForSourcePath(string sourcePath)
    {
        var stagingRoot = OutputStaging.Root(output);
        return Utils.IsInDirectory(sourcePath, stagingRoot, strict: true)
            ? stagingRoot
            : OutputParentDir;
    }

    private string ApplyNameFormat(string format, FileManagerContext ctx)
    {
        TagLib.File? tagFile = null;
        bool tried = false;
        TagLib.File? getTagFile()
        {
            if (!tried)
            {
                tried = true;
                try { tagFile = TagLib.File.Create(ctx.DownloadPath); }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref metadataReadFailureLogged, 1) == 0)
                        DownloadLogMessages.MetadataReadFailed(logger, ex.GetType().Name);
                }
            }
            return tagFile;
        }
        try { return ApplyNameFormatInternal(format, output.InvalidReplaceStr, ctx, getTagFile); }
        finally { tagFile?.Dispose(); }
    }

    static string ApplyNameFormatInternal(string format, string invalidReplaceStr, FileManagerContext ctx, Func<TagLib.File?> getTagFile)
        => NameFormatRenderer.Render(
            format,
            invalidReplaceStr,
            new MusicNameFormatVariableProvider(ctx, getTagFile),
            rejectUnsupportedVariables: true);

    // Music-only enrichment. Structural variables are resolved by
    // NameFormatVariableProvider for every download type.
    private static readonly Dictionary<string, Func<FileManagerContext, TagLib.File?, string>> MusicVarExtractors = new()
    {
        // Tag-based (read from the downloaded file's embedded tags)
        { "artist",       (_, f) => f?.Tag.FirstPerformer ?? "" },
        { "artists",      (_, f) => f != null ? string.Join(" & ", f.Tag.Performers) : "" },
        { "albumartist",  (_, f) => f?.Tag.FirstAlbumArtist ?? "" },
        { "albumartists", (_, f) => f != null ? string.Join(" & ", f.Tag.AlbumArtists) : "" },
        { "title",        (_, f) => f?.Tag.Title ?? "" },
        { "album",        (_, f) => f?.Tag.Album ?? "" },
        { "year",         (_, f) => f?.Tag.Year.ToString() ?? "" },
        { "track",        (_, f) => f?.Tag.Track.ToString("D2") ?? "" },
        { "disc",         (_, f) => f?.Tag.Disc.ToString() ?? "" },
        { "length",       (_, f) => f?.Tag.Length.ToString() ?? "" },

        // Search-query fields (from the original query, prefix 's' = "source")
        { "sartist",  (ctx, _) => ctx.Query.Artist },
        { "sartists", (ctx, _) => ctx.Query.Artist },
        { "stitle",   (ctx, _) => ctx.Query.Title },
        { "salbum",   (ctx, _) => ctx.Query.Album },
        { "slength",  (ctx, _) => ctx.Query.Length.ToString() },
        { "uri",      (ctx, _) => ctx.Query.URI },
        { "url",      (ctx, _) => ctx.Query.URI },

        { "artist-maybe-wrong", (ctx, _) => ctx.Query.ArtistMaybeWrong.ToString().ToLower() },
        { "row",              (ctx, _) => ctx.LineNumber.ToString() },
        { "line",             (ctx, _) => ctx.LineNumber.ToString() },
        { "snum",             (ctx, _) => ctx.ItemNumber.ToString() },
    };

    // Generic values available after a local payload exists.
    private static readonly Dictionary<string, Func<FileManagerContext, string>> PostDownloadVarExtractors = new()
    {
        { "is-audio",         ctx => (!ctx.IsNotAudio).ToString().ToLower() },
        { "path",             ctx => LocalCommandPath(ctx.DownloadPath) },
        { "path-noext",       ctx => LocalCommandPathNoExtension(ctx.DownloadPath) },
    };

    // Outcome values only make sense to on-complete commands. Name formatting
    // runs while a successful payload is being organized, before terminal commit.
    private static readonly Dictionary<string, Func<FileManagerContext, string>> OnCompleteVarExtractors = new()
    {
        { "terminal-outcome", ctx => ctx.TerminalOutcome.ToString() },
        { "skip-reason",      ctx => ctx.SkipReason.ToString() },
        { "failure-reason",   ctx => ctx.FailureReason.ToString() },
    };

    private static readonly HashSet<string> NoCleanSeparatorVars = new()
    {
        "path",
        "path-noext",
    };

    private static readonly HashSet<string> TagVars = new()
    {
        "artist", "artists", "albumartist", "albumartists",
        "title", "album", "year", "track", "disc", "length"
    };

    public static bool HasTagVariables(string x)
    {
        return TagVars.Any(v => x.Contains($"{{{v}}}"));
    }

    private static string LocalCommandPath(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? ""
            : Path.GetFullPath(path).TrimEnd('/').TrimEnd('\\');

    private static string LocalCommandPathNoExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var fullPath = Path.GetFullPath(path);
        return Path.Combine(Path.GetDirectoryName(fullPath) ?? "", Path.GetFileNameWithoutExtension(fullPath));
    }

    private static string GetFolderName(string? remoteFilename, string? remoteBaseDir)
    {
        if (string.IsNullOrEmpty(remoteBaseDir) || string.IsNullOrEmpty(remoteFilename))
        {
            if (!string.IsNullOrEmpty(remoteBaseDir))
                return Path.GetFileName(Utils.NormalizedPath(remoteBaseDir)) ?? "";
            if (!string.IsNullOrEmpty(remoteFilename))
                return Path.GetFileName(Path.GetDirectoryName(Utils.NormalizedPath(remoteFilename))) ?? "";
            return "";
        }

        string normalizedRbd = Utils.NormalizedPath(remoteBaseDir);
        string d = Path.GetDirectoryName(Utils.NormalizedPath(remoteFilename)) ?? "";
        string r = Path.GetFileName(normalizedRbd) ?? "";
        string result = Path.Join(r, Path.GetRelativePath(normalizedRbd, d));
        return result;
    }

    private static string GetRelativeRemoteDirectory(FileManagerContext context)
    {
        string? filename = context.PeerTarget?.Filename ?? context.Candidate?.Filename;
        if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(context.RemoteBaseDir))
            return "";

        string root = Utils.NormalizedPath(context.RemoteBaseDir);
        string directory = Path.GetDirectoryName(Utils.NormalizedPath(filename)) ?? "";
        string relative = Path.GetRelativePath(root, directory);
        return relative == "." ? "" : relative;
    }

    internal static NameFormatContext GetStructuralNameFormatContext(FileManagerContext context)
    {
        PeerFileTarget? target = context.PeerTarget ?? context.Candidate?.Target;
        string? filename = target?.Filename;
        string relativeDirectory = GetRelativeRemoteDirectory(context);
        string[] relativeComponents = relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string outputExtension = !string.IsNullOrWhiteSpace(context.DownloadPath)
            ? Path.GetExtension(context.DownloadPath)
            : target?.Extension ?? Path.GetExtension(filename ?? "");

        return new NameFormatContext(
            target,
            relativeComponents,
            GetFolderName(filename, context.RemoteBaseDir),
            context.Job.ItemNameOrSource(),
            !string.IsNullOrEmpty(context.DefaultFolder)
                ? context.DefaultFolder
                : context.Job.DefaultFolderName(),
            context.OutputDir,
            context.Job.GetType().Name.Replace("Job", ""),
            outputExtension,
            context.ExtractorName,
            context.InputSource,
            context.ConfigDir);
    }

    private sealed class MusicNameFormatVariableProvider : INameFormatVariableProvider
    {
        private static readonly IReadOnlyCollection<NameFormatVariableDescriptor> MusicCapabilities =
            Array.AsReadOnly(MusicVarExtractors.Keys.Select(name => new NameFormatVariableDescriptor(
                name,
                NameFormatVariableApplicability.Music,
                NameFormatEvaluationPhase.MusicFinalization)).ToArray());

        private static readonly IReadOnlyCollection<NameFormatVariableDescriptor> PostDownloadCapabilities =
            Array.AsReadOnly(PostDownloadVarExtractors.Keys.Select(name => new NameFormatVariableDescriptor(
                name,
                NameFormatVariableApplicability.Shared,
                NameFormatEvaluationPhase.Completion)).ToArray());

        private static readonly IReadOnlyCollection<NameFormatVariableDescriptor> OnCompleteCapabilities =
            Array.AsReadOnly(OnCompleteVarExtractors.Keys.Select(name => new NameFormatVariableDescriptor(
                name,
                NameFormatVariableApplicability.Shared,
                NameFormatEvaluationPhase.OnComplete)).ToArray());

        internal static readonly IReadOnlyCollection<NameFormatVariableDescriptor> NameFormatCapabilities =
            Array.AsReadOnly(NameFormatVariableProvider.Capabilities
                .Concat(PostDownloadCapabilities)
                .Concat(MusicCapabilities)
                .ToArray());

        internal static readonly IReadOnlyCollection<NameFormatVariableDescriptor> AllCapabilities =
            Array.AsReadOnly(NameFormatCapabilities
                .Concat(OnCompleteCapabilities)
                .ToArray());

        private static readonly IReadOnlyCollection<string> NameFormatVariables =
            Array.AsReadOnly(NameFormatCapabilities.Select(capability => capability.Name).ToArray());

        private static readonly IReadOnlyCollection<string> AllVariables =
            Array.AsReadOnly(AllCapabilities.Select(capability => capability.Name).ToArray());

        private readonly FileManagerContext context;
        private readonly Func<TagLib.File?> getFile;
        private readonly NameFormatVariableProvider structural;
        private readonly bool includeOnCompleteVariables;

        public MusicNameFormatVariableProvider(
            FileManagerContext context,
            Func<TagLib.File?> getFile,
            bool includeOnCompleteVariables = false)
        {
            this.context = context;
            this.getFile = getFile;
            this.includeOnCompleteVariables = includeOnCompleteVariables;
            structural = new NameFormatVariableProvider(GetStructuralNameFormatContext(context));
        }

        public IReadOnlyCollection<string> SupportedVariables
            => includeOnCompleteVariables ? AllVariables : NameFormatVariables;

        public IReadOnlyCollection<NameFormatVariableDescriptor> VariableDescriptors
            => includeOnCompleteVariables ? AllCapabilities : NameFormatCapabilities;

        public bool TryResolve(string name, out NameFormatVariableValue value)
        {
            if (structural.TryResolve(name, out value))
                return true;

            if (PostDownloadVarExtractors.TryGetValue(name, out var postDownloadExtractor))
            {
                var kind = NoCleanSeparatorVars.Contains(name)
                    ? NameFormatValueKind.Raw
                    : NameFormatValueKind.Component;
                value = new NameFormatVariableValue(postDownloadExtractor(context), kind);
                return true;
            }

            if (!MusicVarExtractors.TryGetValue(name, out var extractor))
            {
                if (includeOnCompleteVariables
                    && OnCompleteVarExtractors.TryGetValue(name, out var onCompleteExtractor))
                {
                    value = new NameFormatVariableValue(
                        onCompleteExtractor(context),
                        NameFormatValueKind.Component);
                    return true;
                }

                value = default;
                return false;
            }

            var tagFile = TagVars.Contains(name) ? getFile() : null;
            value = new NameFormatVariableValue(
                extractor(context, tagFile),
                NameFormatValueKind.Component);
            return true;
        }
    }

    public static bool TryGetCleanVarValue(string x, FileManagerContext ctx, Func<TagLib.File?> getFile, string replaceWith, out string res)
    {
        var provider = new MusicNameFormatVariableProvider(ctx, getFile);
        if (provider.TryResolve(x, out var value))
        {
            res = value.Kind switch
            {
                NameFormatValueKind.Raw => value.Value,
                NameFormatValueKind.Path => value.Value.CleanPath(replaceWith),
                _ => value.Value.ReplaceInvalidChars(replaceWith),
            };
            return true;
        }

        res = x.ReplaceInvalidChars(replaceWith);
        return false;
    }

    public static IEnumerable<string> GetAllVariableNames()
    {
        return MusicNameFormatVariableProvider.AllCapabilities.Select(capability => capability.Name);
    }

    internal static IReadOnlyCollection<NameFormatVariableDescriptor> GetNameFormatVariableDescriptors()
        => MusicNameFormatVariableProvider.AllCapabilities;

    internal static bool TryResolveNameFormatVariable(
        string name,
        FileManagerContext context,
        Func<TagLib.File?> getFile,
        out NameFormatVariableValue value,
        bool includeOnCompleteVariables = false)
        => new MusicNameFormatVariableProvider(
            context,
            getFile,
            includeOnCompleteVariables).TryResolve(name, out value);

    public static string ReplaceVariables(string x, FileManagerContext ctx, TagLib.File? tagFile)
    {
        var provider = new MusicNameFormatVariableProvider(
            ctx,
            () => tagFile,
            includeOnCompleteVariables: true);
        foreach (string key in provider.SupportedVariables)
        {
            var k = '{' + key + '}';
            if (x.Contains(k) && provider.TryResolve(key, out var value))
            {
                x = x.Replace(k, value.Value);
            }
        }
        return x;
    }
}
