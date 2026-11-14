using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using Sockseek.Core.IO;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Sharing;

namespace Sockseek.Persistence.PeerBrowsing;

/// <summary>
/// One immutable browse generation. Compact wire facts are persisted once; tree
/// indexes and counters are rebuilt together. Every reader uses this same model.
/// Array positions are stable IDs (one-based), including across reloads.
/// </summary>
public sealed class PeerBrowseArtifact
{
    internal sealed record Directory(int Parent, string WirePath, PeerBrowseEntryVisibility Visibility, bool Synthetic);
    internal readonly record struct File(int Directory, string Name, string? WireOverride, long Size,
        string? Extension, int Code, byte[]? Attributes, bool Locked);
    internal sealed record Data(int Format, Directory[] Directories, File[] Files);

    private readonly Data data;
    private readonly PeerBrowseDirectoryEntry[] directories;
    private readonly string[] identities, fileNames, prefixes;
    private readonly int[][] children, directoryFiles;
    private readonly int[] orderedDirectories, roots;
    public long EstimatedBytes { get; }

    internal PeerBrowseArtifact(Data data, CancellationToken cancellationToken)
    {
        if (data.Format != PeerBrowseArtifactStore.ArtifactFormatVersion)
            throw new InvalidDataException("The peer browse artifact format is unsupported.");
        this.data = data;
        int count = data.Directories.Length;
        directories = new PeerBrowseDirectoryEntry[count];
        identities = new string[count];
        prefixes = new string[count];
        var childLists = new List<int>[count];
        var fileLists = new List<int>[count];
        var rootList = new List<int>();
        var totals = new Counts[count];
        var lockedDescendants = new long[count];
        var hasPublic = new bool[count];
        var hasLocked = new bool[count];
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = data.Directories[i];
            if (directory.Parent < 0 || directory.Parent > i)
                throw new InvalidDataException("A browse directory has an invalid parent.");
            identities[i] = PeerBrowsePath.NormalizeDirectoryIdentity(directory.WirePath);
            string display = PeerBrowsePath.ToDisplayPath(identities[i]);
            if (display == identities[i])
                display = identities[i];
            prefixes[i] = display + "\\";
            childLists[i] = [];
            fileLists[i] = [];
            if (directory.Parent > 0)
                childLists[directory.Parent - 1].Add(i);
            else
                rootList.Add(i);
            hasPublic[i] = directory.Visibility != PeerBrowseEntryVisibility.Locked;
            hasLocked[i] = directory.Visibility != PeerBrowseEntryVisibility.Public;
            directories[i] = new(i + 1L, directory.Parent == 0 ? null : directory.Parent,
                PeerIdentityValidator.ToDisplayText(PeerBrowsePath.Leaf(identities[i])), display,
                directory.Visibility, directory.Synthetic, 0, 0, 0, 0, 0, false);
            EstimatedBytes += 256L + 2L * (directory.WirePath.Length + identities[i].Length + display.Length + prefixes[i].Length);
        }
        fileNames = new string[data.Files.Length];
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < data.Files.Length; i++)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            ref File file = ref data.Files[i];
            if (file.Directory <= 0 || file.Directory > count || file.Size < 0 || file.Attributes?.Length % 8 > 0)
                throw new InvalidDataException("A browse file has invalid facts.");
            if (file.Extension is { } extension)
            {
                if (!extensions.TryGetValue(extension, out var shared))
                    extensions.Add(extension, shared = extension);
                file = file with
                {
                    Extension = shared
                };
            }
            int directory = file.Directory - 1;
            fileNames[i] = PeerIdentityValidator.ToDisplayText(file.Name);
            fileLists[directory].Add(i);
            totals[directory].Add(file);
            EstimatedBytes += 96L + 2L * (file.Name.Length + (file.WireOverride?.Length ?? 0)) + (file.Attributes?.Length ?? 0);
        }
        children = new int[count][];
        directoryFiles = new int[count][];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = data.Directories[i];
            names.Clear();
            foreach (int file in fileLists[i])
                if (!names.Add(data.Files[file].Name))
                    throw PeerBrowseArtifactWriter.Invalid("the response contains a duplicate path identity");
            children[i] = childLists[i].ToArray();
            directoryFiles[i] = fileLists[i].ToArray();
            Array.Sort(children[i], CompareDirectories);
            Array.Sort(directoryFiles[i], CompareFiles);
            Counts total = totals[i];
            directories[i] = directories[i] with
            {
                Visibility = hasLocked[i] ? hasPublic[i] ? PeerBrowseEntryVisibility.Mixed : PeerBrowseEntryVisibility.Locked : PeerBrowseEntryVisibility.Public,
                DirectDirectoryCount = children[i].Length,
                DirectFileCount = directoryFiles[i].Length,
                RecursiveFileCount = total.Public,
                RecursiveFileBytes = total.PublicBytes,
                TotalRecursiveFileCount = total.Public + total.Locked,
                TotalRecursiveFileBytes = Sum(total.PublicBytes, total.LockedBytes),
                LockedDescendantCount = lockedDescendants[i],
                HasChildren = children[i].Length > 0 || directoryFiles[i].Length > 0,
            };
            if (directory.Parent > 0)
            {
                int parent = directory.Parent - 1;
                totals[parent].Add(total);
                lockedDescendants[parent] += lockedDescendants[i] + (directory.Visibility == PeerBrowseEntryVisibility.Locked ? 1 : 0);
                hasPublic[parent] |= hasPublic[i];
                hasLocked[parent] |= hasLocked[i];
            }
        }
        roots = rootList.ToArray();
        Array.Sort(roots, CompareDirectories);
        orderedDirectories = Enumerable.Range(0, count).ToArray();
        Array.Sort(orderedDirectories, CompareDirectories);
    }

    /// <summary>
    /// Uses the same tree, counters and filtering as peer shares. Built only when
    /// the operator opens their shares, from the immutable catalog being served;
    /// no second persisted catalog or network self-browse is needed.
    /// </summary>
    public static async Task<PeerBrowseArtifact> FromCatalogAsync(
        IShareCatalogReader catalog, CancellationToken cancellationToken)
    {
        var directoryIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var directories = new List<Directory>();
        var files = new List<File>();
        int directoryId = 0;
        await foreach (var row in catalog.EnumerateBrowseRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (row is ShareCatalogBrowseDirectoryRow header)
            {
                string path = header.Directory.RemotePath;
                int separator = path.LastIndexOf('\\');
                int parent = separator < 0 ? 0 : directoryIds[path[..separator]];
                directories.Add(new(parent, path, PeerBrowseEntryVisibility.Public, false));
                directoryIds.Add(path, directoryId = directories.Count);
            }
            else if (row is ShareCatalogBrowseFileRow item)
            {
                var file = item.File;
                byte[]? attributes = file.Attributes.Count == 0 ? null : new byte[file.Attributes.Count * 8];
                for (int i = 0; i < file.Attributes.Count; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(attributes!.AsSpan(i * 8), file.Attributes[i].Type);
                    BinaryPrimitives.WriteInt32LittleEndian(attributes.AsSpan(i * 8 + 4), file.Attributes[i].Value);
                }
                files.Add(new(directoryId, PeerBrowsePath.Leaf(file.RemotePath), null, file.SizeBytes,
                    file.Extension, file.ProtocolCode, attributes, false));
            }
        }
        return new PeerBrowseArtifact(new(PeerBrowseArtifactStore.ArtifactFormatVersion,
            directories.ToArray(), files.ToArray()), cancellationToken);
    }

    // This is one versioned immutable file, not a second database. BinaryReader/
    // BinaryWriter encode fixed scalar fields and length-prefixed UTF-8 strings;
    // buffering avoids a compression call for every individual scalar.
    internal void Write(string path, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
        OwnerOnlyFilePermissions.EnsureFile(path);
        using (var compressed = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
        using (var buffer = new BufferedStream(compressed, 64 * 1024))
        using (var writer = new BinaryWriter(buffer))
        {
            writer.Write(data.Format);
            writer.Write(data.Directories.Length);
            foreach (Directory directory in data.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(directory.Parent);
                writer.Write(directory.WirePath);
                writer.Write((byte)directory.Visibility);
                writer.Write(directory.Synthetic);
            }
            writer.Write(data.Files.Length);
            foreach (File entry in data.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(entry.Directory);
                writer.Write(entry.Name);
                WriteOptional(writer, entry.WireOverride);
                writer.Write(entry.Size);
                WriteOptional(writer, entry.Extension);
                writer.Write((byte)entry.Code);
                writer.Write(entry.Locked);
                writer.Write(entry.Attributes?.Length ?? 0);
                if (entry.Attributes is { } attributes)
                    writer.Write(attributes);
            }
        }
        file.Flush(flushToDisk: true);
    }

    internal static PeerBrowseArtifact Read(string path, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var compressed = new GZipStream(file, CompressionMode.Decompress);
        using var buffer = new BufferedStream(compressed, 64 * 1024);
        using var reader = new BinaryReader(buffer);
        int format = reader.ReadInt32();
        if (format != PeerBrowseArtifactStore.ArtifactFormatVersion)
            throw new InvalidDataException("The peer browse artifact format is unsupported.");
        var directories = new Directory[ReadCount(reader)];
        for (int i = 0; i < directories.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            directories[i] = new(reader.ReadInt32(), reader.ReadString(), (PeerBrowseEntryVisibility)reader.ReadByte(), reader.ReadBoolean());
        }
        var files = new File[ReadCount(reader)];
        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int directory = reader.ReadInt32();
            string name = reader.ReadString();
            string? wire = ReadOptional(reader);
            long size = reader.ReadInt64();
            string? extension = ReadOptional(reader);
            int code = reader.ReadByte();
            bool locked = reader.ReadBoolean();
            int length = ReadCount(reader);
            byte[]? attributes = length == 0 ? null : new byte[length];
            if (attributes is not null)
                buffer.ReadExactly(attributes);
            files[i] = new(directory, name, wire, size, extension, code, attributes, locked);
        }
        // Reading to the end also verifies the gzip trailer before publication.
        if (buffer.ReadByte() != -1)
            throw new InvalidDataException("The peer browse artifact contains trailing data.");
        return new(new(format, directories, files), cancellationToken);
    }

    private static void WriteOptional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            writer.Write(value);
    }

    private static string? ReadOptional(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
    private static int ReadCount(BinaryReader reader)
    {
        int value = reader.ReadInt32();
        return value >= 0 ? value : throw new InvalidDataException("The peer browse artifact contains a negative length.");
    }

    public PeerBrowseDirectoryEntry? DirectoryEntry(long id) => Valid(id, directories.Length) ? directories[(int)id - 1] : null;
    public PeerBrowseFileEntry? FileEntry(long id)
    {
        if (!Valid(id, data.Files.Length))
            return null;
        int index = (int)id - 1;
        File file = data.Files[index];
        var (metadata, attributes) = ReadAttributes(file, static (type, value) => new PeerBrowseFileAttribute(type, value));
        return new(id, file.Directory, Visibility(file), fileNames[index], file.Size, file.Extension,
            metadata.BitRate, metadata.BitDepth, metadata.SampleRate, metadata.Length, attributes);
    }

    internal PeerDirectorySnapshot ReadDirectory(PeerDirectoryIdentity identity, CancellationToken cancellationToken)
    {
        string path = PeerBrowsePath.NormalizeDirectoryIdentity(identity.FolderPath);
        var included = identities.Select(candidate => PeerBrowsePath.IsSameOrDescendant(candidate, path)).ToArray();
        var targets = new List<PeerFileTarget>();
        for (int i = 0; i < data.Files.Length; i++)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            File file = data.Files[i];
            if (!file.Locked && included[file.Directory - 1])
                targets.Add(Target(identity.Username, i));
        }
        return new(identity, targets, isComplete: true);
    }

    public PeerBrowsePage<PeerBrowseDirectoryEntry> ReadDirectories(long? parentId, string? query, bool recursive,
        string? afterKey, long? afterId, int limit, CancellationToken cancellationToken)
    {
        string? parentIdentity = null;
        int[] source;
        if (recursive)
        {
            if (parentId is { } id)
                parentIdentity = identities[RequireDirectory(id)];
            source = orderedDirectories;
        }
        else
            source = parentId is null ? roots : Valid(parentId.Value, directories.Length) ? children[(int)parentId.Value - 1] : [];
        var rows = new List<PeerBrowseDirectoryEntry>();
        foreach (int index in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = directories[index];
            if (parentIdentity is not null && !PeerBrowsePath.IsDescendant(identities[index], parentIdentity))
                continue;
            if (!string.IsNullOrEmpty(query) && !entry.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            if (afterKey is not null && CompareKey(entry.DisplayPath, entry.DirectoryId, afterKey, afterId!.Value) <= 0)
                continue;
            rows.Add(entry);
            if (rows.Count > limit)
                break;
        }
        return Page(rows, limit, row => row.DisplayPath, row => row.DirectoryId);
    }

    public PeerBrowsePage<PeerBrowseFileEntry> ReadFiles(long directoryId, string? query, string? afterKey,
        long? afterId, int limit, CancellationToken cancellationToken)
    {
        var rows = new List<PeerBrowseFileEntry>();
        foreach (int index in Valid(directoryId, directories.Length) ? directoryFiles[(int)directoryId - 1] : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(query) && !fileNames[index].Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            if (afterKey is not null && CompareKey(fileNames[index], index + 1L, afterKey, afterId!.Value) <= 0)
                continue;
            rows.Add(FileEntry(index + 1L)!);
            if (rows.Count > limit)
                break;
        }
        return Page(rows, limit, row => row.Name, row => row.FileId);
    }

    public string? SearchSortKey(PeerBrowseSearchEntryKind kind, long id) => kind switch
    {
        PeerBrowseSearchEntryKind.Directory => DirectoryEntry(id)?.DisplayPath,
        PeerBrowseSearchEntryKind.File => Valid(id, data.Files.Length) ? prefixes[data.Files[(int)id - 1].Directory - 1] + fileNames[(int)id - 1] : null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public PeerBrowseSearchPage Search(string query, string? afterPath, PeerBrowseSearchEntryKind? afterKind,
        long? afterId, int limit, CancellationToken cancellationToken)
    {
        int separator = query.LastIndexOf('\\');
        var included = new bool[directories.Length];
        var directMatches = new bool[directories.Length];
        var crossingMatches = separator < 0 ? [] : new bool[directories.Length];
        var counts = new Counts[directories.Length];
        for (int i = 0; i < directories.Length; i++)
        {
            directMatches[i] = included[i] = directories[i].DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (separator >= 0)
                crossingMatches[i] = directories[i].DisplayPath.AsSpan().EndsWith(query.AsSpan(0, separator), StringComparison.OrdinalIgnoreCase);
        }
        var page = new PriorityQueue<SearchReference, SearchReference>(SearchReference.Descending);
        void Offer(int index, PeerBrowseSearchEntryKind kind, string prefix, string name)
        {
            long id = index + 1L;
            if (afterPath is not null && CompareSearch(prefix, name, kind, id, afterPath, afterKind!.Value, afterId!.Value) <= 0)
                return;
            if (page.Count > limit && page.TryPeek(out var worst, out _)
                && CompareSearch(prefix, name, kind, id, worst.Path, worst.Kind, worst.Index + 1L) >= 0)
                return;
            var reference = new SearchReference(index, kind, prefix + name);
            page.Enqueue(reference, reference);
            if (page.Count > (long)limit + 1)
                page.Dequeue();
        }
        Counts total = default;
        for (int i = 0; i < data.Files.Length; i++)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            ref readonly File file = ref data.Files[i];
            int directory = file.Directory - 1;
            bool match = directMatches[directory] || (separator < 0
                ? fileNames[i].Contains(query, StringComparison.OrdinalIgnoreCase)
                : crossingMatches[directory] && fileNames[i].AsSpan().StartsWith(query.AsSpan(separator + 1), StringComparison.OrdinalIgnoreCase));
            if (!match)
                continue;
            total.Add(file);
            counts[directory].Add(file);
            included[directory] = true;
            Offer(i, PeerBrowseSearchEntryKind.File, prefixes[directory], fileNames[i]);
        }
        for (int i = directories.Length - 1; i >= 0; i--)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (!included[i])
                continue;
            Offer(i, PeerBrowseSearchEntryKind.Directory, "", directories[i].DisplayPath);
            int parent = data.Directories[i].Parent - 1;
            if (parent >= 0)
            {
                included[parent] = true;
                counts[parent].Add(counts[i]);
            }
        }
        var references = page.UnorderedItems.Select(item => item.Element).ToArray();
        Array.Sort(references, SearchReference.Ascending);
        bool more = references.Length > limit;
        if (more)
            Array.Resize(ref references, limit);
        var items = new List<PeerBrowseSearchEntry>(references.Length);
        foreach (SearchReference reference in references)
        {
            int index = reference.Index;
            if (reference.Kind == PeerBrowseSearchEntryKind.File)
            {
                File file = data.Files[index];
                var (facts, _) = ReadAttributes<object>(file, null);
                items.Add(new(reference.Kind, index + 1L, file.Directory, file.Directory, fileNames[index], reference.Path,
                    Visibility(file), file.Locked ? 0 : 1, file.Locked ? 0 : file.Size,
                    file.Locked ? 1 : 0, file.Locked ? file.Size : 0, file.Size, file.Extension,
                    facts.BitRate, facts.BitDepth, facts.SampleRate, facts.Length));
            }
            else
            {
                var entry = directories[index];
                Counts count = counts[index];
                items.Add(new(reference.Kind, index + 1L, index + 1L, entry.ParentId, entry.Name, entry.DisplayPath,
                    entry.Visibility, count.Public, count.PublicBytes, count.Locked, count.LockedBytes,
                    null, null, null, null, null, null, entry.RecursiveFileCount, entry.RecursiveFileBytes,
                    entry.TotalRecursiveFileCount, entry.TotalRecursiveFileBytes));
            }
        }
        var last = more ? references[^1] : null;
        return new(items, total.Public, total.PublicBytes, total.Locked, total.LockedBytes,
            last?.Path, last?.Kind, last is null ? null : last.Index + 1L);
    }

    internal PeerBrowseDownloadResolution Resolve(string username, IReadOnlyList<long> directoryIds,
        IReadOnlyList<long> fileIds, IReadOnlyList<long> excludedFiles, IReadOnlyList<long> excludedDirectories,
        CancellationToken cancellationToken)
    {
        if (directoryIds.Count == 0 && fileIds.Count == 0)
            throw new PeerBrowseSelectionException("At least one directory or file must be selected.");
        foreach (long id in directoryIds.Concat(excludedDirectories))
            if (!Valid(id, directories.Length))
                throw new PeerBrowseSelectionException("One or more selected or excluded directories do not exist in this browse.");
        foreach (long id in fileIds.Concat(excludedFiles))
            if (!Valid(id, data.Files.Length))
                throw new PeerBrowseSelectionException("One or more selected or excluded files do not exist in this browse.");
        int[] selectedDirectories = directoryIds.Distinct().Select(id => (int)id - 1)
            .OrderBy(index => identities[index].Length).ThenBy(index => identities[index], Comparer<string>.Create((a, b) => CompareText(a, b))).ToArray();
        int[] selectedFiles = fileIds.Distinct().Select(id => (int)id - 1).Order().ToArray();
        if (selectedDirectories.Any(index => directories[index].Visibility == PeerBrowseEntryVisibility.Locked))
            throw new PeerBrowseSelectionException("Locked directories cannot be selected for download.");
        if (selectedFiles.Any(index => data.Files[index].Locked))
            throw new PeerBrowseSelectionException("Locked files cannot be selected for download.");
        int redundant = directoryIds.Count - selectedDirectories.Length + fileIds.Count - selectedFiles.Length;
        var selected = selectedDirectories.ToHashSet();
        var excluded = excludedDirectories.Select(id => (int)id - 1).ToHashSet();
        var excludedFileSet = excludedFiles.Select(id => (int)id - 1).ToHashSet();
        var selectedFileSet = selectedFiles.ToHashSet();
        var canonical = new List<int>();
        var canonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        var includedPaths = new HashSet<string>(StringComparer.Ordinal);
        var excludedPaths = excluded.Select(index => identities[index]).ToHashSet(StringComparer.Ordinal);
        foreach (int index in selectedDirectories)
        {
            if (HasSameOrAncestor(canonicalPaths, identities[index]))
            {
                if (IncludedByRule(identities[index], includedPaths, excludedPaths))
                    redundant++;
            }
            else
            {
                canonical.Add(index);
                canonicalPaths.Add(identities[index]);
            }
            includedPaths.Add(identities[index]);
        }
        // Walk only selected subtrees. A single-file selection is a direct ID
        // lookup, independent of the size of the peer's library. Nearest explicit
        // directory rules win; explicit files override directory exclusion only.
        var standalone = selectedFiles.Where(index => !excludedFileSet.Contains(index)).ToHashSet();
        var entries = canonical.ToDictionary(index => index, _ => new List<int>());
        var skipped = new Dictionary<int, long>();
        foreach (int owner in canonical)
        {
            var pending = new Stack<(int Directory, bool Included)>();
            pending.Push((owner, false));
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int directory = current.Directory;
                bool included = !excluded.Contains(directory) && (selected.Contains(directory) || current.Included);
                if (included && directory != owner && directories[directory].Visibility == PeerBrowseEntryVisibility.Locked)
                    skipped[owner] = skipped.GetValueOrDefault(owner) + 1;
                foreach (int index in directoryFiles[directory])
                {
                    if (data.Files[index].Locked || excludedFileSet.Contains(index))
                        continue;
                    bool explicitFile = selectedFileSet.Contains(index);
                    if (explicitFile)
                    {
                        standalone.Remove(index);
                        if (included)
                            redundant++;
                    }
                    if (included || explicitFile)
                        entries[owner].Add(index);
                }
                foreach (int child in children[directory])
                    pending.Push((child, included));
            }
            entries[owner].Sort(); // Stable source/file-ID order, as in retained plans.
        }
        var plans = new List<(string Key, DirectoryTransferPlan Plan)>();
        long totalFiles = 0, totalBytes = 0, lockedSkipped = 0;
        foreach (int index in canonical)
        {
            if (entries[index].Count == 0)
                continue;
            var transfers = entries[index].Select(file =>
            {
                int directory = data.Files[file].Directory - 1;
                string[] relative = directory == index ? [] : identities[directory][(identities[index].Length + 1)..].Split('\\');
                return new DirectoryTransferEntry(Target(username, file), relative);
            }).ToArray();
            var plan = new DirectoryTransferPlan(PeerBrowsePath.Leaf(identities[index]), transfers);
            plans.Add(("0\0" + directories[index].DisplayPath, plan));
            totalFiles += plan.Entries.Count;
            totalBytes = Sum(totalBytes, plan.TotalKnownBytes);
            lockedSkipped += excluded.Count > 0 ? skipped.GetValueOrDefault(index) : directories[index].LockedDescendantCount;
        }
        foreach (var group in standalone.GroupBy(index => data.Files[index].Directory - 1))
        {
            var targets = group.Select(index => Target(username, index)).OrderBy(target => target.Filename, StringComparer.Ordinal)
                .Select(target => new DirectoryTransferEntry(target, [])).ToArray();
            var plan = new DirectoryTransferPlan(PeerBrowsePath.Leaf(identities[group.Key]), targets, representsDirectorySelection: false);
            plans.Add(("1\0" + directories[group.Key].DisplayPath, plan));
            totalFiles += targets.Length;
            totalBytes = Sum(totalBytes, plan.TotalKnownBytes);
        }
        return new(plans.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Plan).ToArray(),
            canonical.Count, standalone.Count, totalFiles, totalBytes, redundant, lockedSkipped);
    }

    private static bool HasSameOrAncestor(HashSet<string> paths, string path)
    {
        while (true)
        {
            if (paths.Contains(path))
                return true;
            int separator = path.LastIndexOf('\\');
            if (separator < 0)
                return false;
            path = path[..separator];
        }
    }

    private static bool IncludedByRule(string path, HashSet<string> included, HashSet<string> excluded)
    {
        while (true)
        {
            if (excluded.Contains(path))
                return false;
            if (included.Contains(path))
                return true;
            int separator = path.LastIndexOf('\\');
            if (separator < 0)
                return false;
            path = path[..separator];
        }
    }

    private static int CompareSearch(string prefix, string name, PeerBrowseSearchEntryKind kind, long id,
        string path, PeerBrowseSearchEntryKind otherKind, long otherId)
    {
        int length = Math.Min(prefix.Length, path.Length);
        int comparison = CompareText(prefix.AsSpan(0, length), path.AsSpan(0, length));
        if (comparison == 0)
            comparison = prefix.Length > path.Length ? 1 : CompareText(name, path.AsSpan(prefix.Length));
        if (comparison == 0)
            comparison = kind.CompareTo(otherKind);
        return comparison == 0 ? id.CompareTo(otherId) : comparison;
    }

    private sealed record SearchReference(int Index, PeerBrowseSearchEntryKind Kind, string Path)
    {
        public static readonly IComparer<SearchReference> Ascending = Comparer<SearchReference>.Create((a, b) => CompareSearch("", a.Path, a.Kind, a.Index, b.Path, b.Kind, b.Index));
        public static readonly IComparer<SearchReference> Descending = Comparer<SearchReference>.Create((a, b) => Ascending.Compare(b, a));
    }

    private int CompareDirectories(int a, int b) => CompareKey(directories[a].DisplayPath, a, directories[b].DisplayPath, b);
    private int CompareFiles(int a, int b) => CompareKey(fileNames[a], a, fileNames[b], b);
    private static int CompareKey(string a, long aid, string b, long bid)
    {
        int comparison = CompareText(a, b);
        return comparison == 0 ? aid.CompareTo(bid) : comparison;
    }
    // Preserve the former SQLite BINARY order: UTF-8 scalar order, including
    // supplementary Unicode characters. No culture-dependent ordering of peers.
    private static int CompareText(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int length = a.CommonPrefixLength(b);
        if (length == Math.Min(a.Length, b.Length))
            return a.Length.CompareTo(b.Length);
        int left = char.IsHighSurrogate(a[length]) && length + 1 < a.Length && char.IsLowSurrogate(a[length + 1])
            ? char.ConvertToUtf32(a[length], a[length + 1]) : a[length];
        int right = char.IsHighSurrogate(b[length]) && length + 1 < b.Length && char.IsLowSurrogate(b[length + 1])
            ? char.ConvertToUtf32(b[length], b[length + 1]) : b[length];
        return left.CompareTo(right);
    }

    private static PeerBrowsePage<T> Page<T>(List<T> rows, int limit, Func<T, string> key, Func<T, long> id) where T : class
    {
        bool more = rows.Count > limit;
        if (more)
            rows.RemoveAt(rows.Count - 1);
        return new(rows, more ? key(rows[^1]) : null, more ? id(rows[^1]) : null);
    }

    private int RequireDirectory(long id) => Valid(id, directories.Length) ? (int)id - 1
        : throw new KeyNotFoundException($"Peer browse directory '{id}' does not exist.");
    private static bool Valid(long id, int count) => id > 0 && id <= count;
    private static PeerBrowseEntryVisibility Visibility(File file) => file.Locked ? PeerBrowseEntryVisibility.Locked : PeerBrowseEntryVisibility.Public;
    internal static long Sum(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    private PeerFileTarget Target(string username, int index)
    {
        File file = data.Files[index];
        var (metadata, attributes) = ReadAttributes(file,
            static (type, value) => new FileAttributeSnapshot(type.ToString(CultureInfo.InvariantCulture), value, type));
        string wire = file.WireOverride ?? data.Directories[file.Directory - 1].WirePath + "\\" + file.Name;
        return new(new PeerFileIdentity(username, wire), file.Size, file.Extension,
            metadata.BitRate, metadata.BitDepth, metadata.SampleRate, metadata.Length, attributes);
    }

    private static (PeerBrowseFileMetadata Metadata, List<T>? Attributes) ReadAttributes<T>(File file, Func<int, int, T>? create)
    {
        PeerBrowseFileMetadata metadata = default;
        List<T>? attributes = create is not null && file.Attributes is { Length: > 0 } ? new(file.Attributes.Length / 8) : null;
        ReadOnlySpan<byte> bytes = file.Attributes;
        for (int offset = 0; offset < bytes.Length; offset += 8)
        {
            int type = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
            int value = BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..]);
            metadata = metadata.WithAttribute(type, value);
            if (create is not null)
                attributes!.Add(create(type, value));
        }
        return (metadata, attributes);
    }

    private struct Counts
    {
        public long Public, PublicBytes, Locked, LockedBytes;
        public void Add(File file)
        {
            if (file.Locked)
            {
                Locked++;
                LockedBytes = Sum(LockedBytes, file.Size);
            }
            else
            {
                Public++;
                PublicBytes = Sum(PublicBytes, file.Size);
            }
        }
        public void Add(Counts other)
        {
            Public += other.Public;
            Locked += other.Locked;
            PublicBytes = Sum(PublicBytes, other.PublicBytes);
            LockedBytes = Sum(LockedBytes, other.LockedBytes);
        }
    }
}

