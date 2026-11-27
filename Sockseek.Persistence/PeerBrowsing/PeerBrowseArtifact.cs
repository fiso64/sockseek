using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Sharing;
using Sockseek.Core.Snapshots;

namespace Sockseek.Persistence.PeerBrowsing;

/// <summary>
/// One immutable browse generation, read in place from a memory-mapped file.
/// The file already holds the finished tree (fixed-size records, sort orders,
/// totals and UTF-8 text), so opening it loads nothing: the operating system
/// pages in only what a query touches and reclaims it when memory is needed.
/// Array positions are stable IDs (one-based), including across reopens.
/// </summary>
public sealed class PeerBrowseArtifact : IDisposable
{
    // Layout: a fixed header, then sections aligned to 8 bytes. Fixed-size
    // sections are arrays of the records below; text sections are UTF-8 blocks
    // addressed by per-file end offsets or by TextRef.
    internal static ReadOnlySpan<byte> Magic => "SSKBRWSE"u8;
    internal const int HeaderSize = 512;
    internal const int SectionCount = 15;
    internal const byte LockedFlag = 1, ExceptionFlag = 2;

    internal enum Section
    {
        Directories, Files, NameEnds, SearchEnds, AttributeEnds, FileOrder, ChildOrder,
        DirectoryOrder, Roots, Extensions, Exceptions, Names, Search, Attributes, Strings,
    }

    /// <summary>A UTF-8 string in the Strings section; a negative length is null.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct TextRef(long offset, int length)
    {
        public readonly long Offset = offset;
        public readonly int Length = length;
        private readonly int padding;
        public static TextRef Null => new(0, -1);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DirectoryRecord
    {
        public TextRef WirePath, Identity, DisplayPath, Name, Search;
        public long RecursiveFileCount, RecursiveFileBytes, TotalRecursiveFileCount, TotalRecursiveFileBytes, LockedDescendantCount;
        public int Parent; // One-based; 0 for a root.
        public int ChildStart, ChildCount; // Into ChildOrder, sorted by display path.
        public int FileStart, FileCount; // Into FileOrder, sorted by display name.
        public byte Visibility, Synthetic;
        private readonly short padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileRecord
    {
        public long Size;
        public int Directory; // One-based.
        public int Extension; // One-based into Extensions; 0 for none.
        public byte Code, Flags;
        private readonly short padding1;
        private readonly int padding2;
    }

    /// <summary>The rare file whose wire spelling differs from its display name or directory.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileException
    {
        public int File;
        private readonly int padding;
        public TextRef RawName, WireOverride;
    }

    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly SafeMemoryMappedViewHandle handle;
    private readonly unsafe byte* data;
    private readonly long[] offsets = new long[SectionCount];
    private readonly long[] lengths = new long[SectionCount];
    private readonly int directoryCount, fileCount, rootCount, exceptionCount;
    private readonly string[] extensions;
    private int disposed;

    private unsafe PeerBrowseArtifact(MemoryMappedFile map, MemoryMappedViewAccessor view, long fileLength)
    {
        this.map = map;
        this.view = view;
        handle = view.SafeMemoryMappedViewHandle;
        byte* pointer = null;
        handle.AcquirePointer(ref pointer);
        data = pointer + view.PointerOffset;
        try
        {
            var header = new ReadOnlySpan<byte>(data, HeaderSize);
            if (!header[..8].SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != PeerBrowseArtifactStore.ArtifactFormatVersion)
                throw new InvalidDataException("The peer browse artifact format is unsupported.");
            directoryCount = Count(header[12..]);
            fileCount = Count(header[16..]);
            rootCount = Count(header[20..]);
            int extensionCount = Count(header[24..]);
            exceptionCount = Count(header[28..]);
            for (int section = 0; section < SectionCount; section++)
            {
                long offset = BinaryPrimitives.ReadInt64LittleEndian(header[(32 + section * 16)..]);
                long length = BinaryPrimitives.ReadInt64LittleEndian(header[(40 + section * 16)..]);
                if (offset < HeaderSize || length < 0 || offset % 8 != 0 || offset > fileLength - length)
                    throw Corrupt();
                offsets[section] = offset;
                lengths[section] = length;
            }
            RequireArray<DirectoryRecord>(Section.Directories, directoryCount);
            RequireArray<FileRecord>(Section.Files, fileCount);
            RequireArray<long>(Section.NameEnds, fileCount);
            RequireArray<long>(Section.SearchEnds, fileCount);
            RequireArray<long>(Section.AttributeEnds, fileCount);
            RequireArray<int>(Section.FileOrder, fileCount);
            RequireArray<int>(Section.ChildOrder, directoryCount - rootCount);
            RequireArray<int>(Section.DirectoryOrder, directoryCount);
            RequireArray<int>(Section.Roots, rootCount);
            RequireArray<TextRef>(Section.Extensions, extensionCount);
            RequireArray<FileException>(Section.Exceptions, exceptionCount);
            extensions = new string[extensionCount];
            for (int i = 0; i < extensionCount; i++)
                extensions[i] = String(Item<TextRef>(Section.Extensions, i)) ?? throw Corrupt();
        }
        catch
        {
            handle.ReleasePointer();
            throw;
        }
    }

    internal static PeerBrowseArtifact Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            long length = file.Length;
            if (length < HeaderSize)
                throw Corrupt();
            map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: false);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new PeerBrowseArtifact(map, view, length);
        }
        catch
        {
            view?.Dispose();
            if (map is null)
                file.Dispose();
            map?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Unmaps once every query already running on this artifact has finished;
    /// a query that starts afterwards throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        handle.ReleasePointer();
        view.Dispose();
        map.Dispose();
    }

    /// <summary>
    /// Uses the same tree, counters and filtering as peer shares. Built only when
    /// the operator opens their shares, from the immutable catalog being served;
    /// no second catalog database or network self-browse is needed.
    /// </summary>
    public static async Task<PeerBrowseArtifact> FromCatalogAsync(
        IShareCatalogReader catalog, string path, CancellationToken cancellationToken)
    {
        using (var builder = new PeerBrowseArtifactBuilder(path))
        {
            var directoryIds = new Dictionary<string, int>(StringComparer.Ordinal);
            int directoryId = 0;
            byte[] attributes = [];
            await foreach (var row in catalog.EnumerateBrowseRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (row is ShareCatalogBrowseDirectoryRow header)
                {
                    string remotePath = header.Directory.RemotePath;
                    int separator = remotePath.LastIndexOf('\\');
                    int parent = separator < 0 ? 0 : directoryIds[remotePath[..separator]];
                    builder.Directories.Add(new(parent, remotePath, PeerBrowseEntryVisibility.Public, false));
                    directoryIds.Add(remotePath, directoryId = builder.Directories.Count);
                }
                else if (row is ShareCatalogBrowseFileRow item)
                {
                    var file = item.File;
                    if (attributes.Length < file.Attributes.Count * 8)
                        attributes = new byte[file.Attributes.Count * 8];
                    for (int i = 0; i < file.Attributes.Count; i++)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(attributes.AsSpan(i * 8), file.Attributes[i].Type);
                        BinaryPrimitives.WriteInt32LittleEndian(attributes.AsSpan(i * 8 + 4), file.Attributes[i].Value);
                    }
                    builder.AddFile(directoryId, PeerBrowsePath.Leaf(file.RemotePath), null, file.SizeBytes,
                        file.Extension, file.ProtocolCode, attributes.AsSpan(0, file.Attributes.Count * 8), false);
                }
            }
            builder.Complete(cancellationToken);
        }
        return Open(path);
    }

    public PeerBrowseDirectoryEntry? DirectoryEntry(long id)
    {
        using var scope = Enter();
        return Valid(id, directoryCount) ? DirectoryEntryAt((int)id - 1) : null;
    }

    public PeerBrowseFileEntry? FileEntry(long id)
    {
        using var scope = Enter();
        return Valid(id, fileCount) ? FileEntryAt((int)id - 1) : null;
    }

    internal PeerDirectorySnapshot ReadDirectory(PeerDirectoryIdentity identity, CancellationToken cancellationToken)
    {
        using var scope = Enter();
        byte[] root = Encoding.UTF8.GetBytes(PeerBrowsePath.NormalizeDirectoryIdentity(identity.FolderPath));
        var included = new bool[directoryCount];
        ReadOnlySpan<DirectoryRecord> directories = Records<DirectoryRecord>(Section.Directories);
        for (int i = 0; i < directoryCount; i++)
            included[i] = IsSameOrDescendant(Text(directories[i].Identity), root);
        var targets = new List<PeerFileTarget>();
        ReadOnlySpan<FileRecord> files = Records<FileRecord>(Section.Files);
        for (int i = 0; i < fileCount; i++)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            ref readonly FileRecord file = ref files[i];
            if ((file.Flags & LockedFlag) == 0 && included[DirectoryIndex(file.Directory)])
                targets.Add(Target(identity.Username, i));
        }
        return new(identity, targets, isComplete: true);
    }

    public PeerBrowsePage<PeerBrowseDirectoryEntry> ReadDirectories(long? parentId, string? query, bool recursive,
        string? afterKey, long? afterId, int limit, CancellationToken cancellationToken)
    {
        using var scope = Enter();
        byte[]? parentIdentity = null;
        Section source;
        long start = 0, count;
        if (recursive)
        {
            if (parentId is { } id)
                parentIdentity = Text(Directory(RequireDirectory(id)).Identity).ToArray();
            (source, count) = (Section.DirectoryOrder, directoryCount);
        }
        else if (parentId is null)
            (source, count) = (Section.Roots, rootCount);
        else if (Valid(parentId.Value, directoryCount))
        {
            ref readonly DirectoryRecord parent = ref Directory((int)parentId.Value - 1);
            (source, start, count) = (Section.ChildOrder, parent.ChildStart, parent.ChildCount);
        }
        else
            (source, count) = (Section.Roots, 0);
        byte[]? needle = string.IsNullOrEmpty(query) ? null : SearchBytes(Utils.FoldDisplayText(query));
        byte[]? after = afterKey is null ? null : Encoding.UTF8.GetBytes(afterKey);
        var rows = new List<PeerBrowseDirectoryEntry>();
        for (long position = start; position < start + count; position++)
        {
            if ((position & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int index = Index(Item<int>(source, position), directoryCount);
            ref readonly DirectoryRecord entry = ref Directory(index);
            if (parentIdentity is not null && !IsDescendant(Text(entry.Identity), parentIdentity))
                continue;
            if (needle is not null && Text(entry.Search).IndexOf(needle) < 0)
                continue;
            if (after is not null && CompareKey(Text(entry.DisplayPath), index + 1L, after, afterId!.Value) <= 0)
                continue;
            rows.Add(DirectoryEntryAt(index));
            if (rows.Count > limit)
                break;
        }
        return Page(rows, limit, row => row.DisplayPath, row => row.DirectoryId);
    }

    public PeerBrowsePage<PeerBrowseFileEntry> ReadFiles(long directoryId, string? query, string? afterKey,
        long? afterId, int limit, CancellationToken cancellationToken)
    {
        using var scope = Enter();
        var rows = new List<PeerBrowseFileEntry>();
        if (!Valid(directoryId, directoryCount))
            return Page(rows, limit, row => row.Name, row => row.FileId);
        ref readonly DirectoryRecord directory = ref Directory((int)directoryId - 1);
        byte[]? needle = string.IsNullOrEmpty(query) ? null : SearchBytes(Utils.FoldDisplayText(query));
        byte[]? after = afterKey is null ? null : Encoding.UTF8.GetBytes(afterKey);
        for (long position = directory.FileStart; position < (long)directory.FileStart + directory.FileCount; position++)
        {
            if ((position & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int index = Index(Item<int>(Section.FileOrder, position), fileCount);
            if (needle is not null && SearchText(index).IndexOf(needle) < 0)
                continue;
            if (after is not null && CompareKey(Name(index), index + 1L, after, afterId!.Value) <= 0)
                continue;
            rows.Add(FileEntryAt(index));
            if (rows.Count > limit)
                break;
        }
        return Page(rows, limit, row => row.Name, row => row.FileId);
    }

    public string? SearchSortKey(PeerBrowseSearchEntryKind kind, long id)
    {
        using var scope = Enter();
        return kind switch
        {
            PeerBrowseSearchEntryKind.Directory => Valid(id, directoryCount) ? String(Directory((int)id - 1).DisplayPath) : null,
            PeerBrowseSearchEntryKind.File => Valid(id, fileCount) ? KeyOf(new Hit((int)id - 1, kind)).ToString() : null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public PeerBrowseSearchPage Search(string query, string? afterPath, PeerBrowseSearchEntryKind? afterKind,
        long? afterId, int limit, CancellationToken cancellationToken)
    {
        using var scope = Enter();
        query = Utils.FoldDisplayText(query);
        int separator = query.LastIndexOf('\\');
        byte[] needle = SearchBytes(query);
        byte[] directorySuffix = separator < 0 ? [] : SearchBytes(query[..separator]);
        byte[] namePrefix = separator < 0 ? [] : SearchBytes(query[(separator + 1)..]);
        var included = new bool[directoryCount];
        var counts = new Counts[directoryCount];
        var fileMatches = new bool[fileCount];
        ReadOnlySpan<DirectoryRecord> directories = Records<DirectoryRecord>(Section.Directories);
        ReadOnlySpan<FileRecord> files = Records<FileRecord>(Section.Files);
        ReadOnlySpan<int> fileOrder = Records<int>(Section.FileOrder);
        for (int i = 0; i < directoryCount; i++)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            ref readonly DirectoryRecord directory = ref directories[i];
            ReadOnlySpan<byte> text = Text(directory.Search);
            bool direct = included[i] = text.IndexOf(needle) >= 0;
            // A query crossing a separator matches a directory suffix, then a name prefix.
            bool crossing = separator >= 0 && text.EndsWith(directorySuffix);
            if (!direct && !crossing)
                continue;
            foreach (int file in Slice(fileOrder, directory.FileStart, directory.FileCount))
            {
                int index = Index(file, fileCount);
                fileMatches[index] |= direct || SearchText(index).StartsWith(namePrefix);
            }
        }
        if (separator < 0)
            MarkNameMatches(needle, fileMatches, cancellationToken);

        byte[]? after = afterPath is null ? null : Encoding.UTF8.GetBytes(afterPath);
        var order = new HitComparer(this);
        var page = new PriorityQueue<Hit, Hit>(Comparer<Hit>.Create((a, b) => order.Compare(b, a)));
        void Offer(Hit hit)
        {
            if (after is not null && Compare(KeyOf(hit), hit.Kind, hit.Index + 1L, new Key(after), afterKind!.Value, afterId!.Value) <= 0)
                return;
            if (page.Count > limit && page.TryPeek(out Hit worst, out _) && order.Compare(hit, worst) >= 0)
                return;
            page.Enqueue(hit, hit);
            if (page.Count > (long)limit + 1)
                page.Dequeue();
        }
        Counts total = default;
        int visited = 0;
        // Visit only the matches, found by vectorized scans of the match flags.
        for (int i = NextMatch(fileMatches, 0); i >= 0; i = NextMatch(fileMatches, i + 1))
        {
            if ((++visited & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            ref readonly FileRecord file = ref files[i];
            int directory = DirectoryIndex(file.Directory);
            total.Add(file);
            counts[directory].Add(file);
            included[directory] = true;
            Offer(new(i, PeerBrowseSearchEntryKind.File));
        }
        for (int i = directoryCount - 1; i >= 0; i--)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (!included[i])
                continue;
            Offer(new(i, PeerBrowseSearchEntryKind.Directory));
            int parent = Directory(i).Parent - 1;
            if (parent >= 0)
            {
                parent = Index(parent, i); // Parents precede their children.
                included[parent] = true;
                counts[parent].Add(counts[i]);
            }
        }
        var hits = page.UnorderedItems.Select(item => item.Element).ToArray();
        Array.Sort(hits, order);
        bool more = hits.Length > limit;
        if (more)
            Array.Resize(ref hits, limit);
        var items = new List<PeerBrowseSearchEntry>(hits.Length);
        foreach (Hit hit in hits)
        {
            int index = hit.Index;
            if (hit.Kind == PeerBrowseSearchEntryKind.File)
            {
                ref readonly FileRecord file = ref File(index);
                bool locked = (file.Flags & LockedFlag) != 0;
                var (facts, _) = ReadAttributes<object>(index, null);
                items.Add(new(hit.Kind, index + 1L, file.Directory, file.Directory, Utf8(Name(index)), KeyOf(hit).ToString(),
                    Visibility(file), locked ? 0 : 1, locked ? 0 : file.Size, locked ? 1 : 0, locked ? file.Size : 0,
                    file.Size, Extension(file), facts.BitRate, facts.BitDepth, facts.SampleRate, facts.Length));
            }
            else
            {
                var entry = DirectoryEntryAt(index);
                Counts count = counts[index];
                items.Add(new(hit.Kind, index + 1L, index + 1L, entry.ParentId, entry.Name, entry.DisplayPath,
                    entry.Visibility, count.Public, count.PublicBytes, count.Locked, count.LockedBytes,
                    null, null, null, null, null, null, entry.RecursiveFileCount, entry.RecursiveFileBytes,
                    entry.TotalRecursiveFileCount, entry.TotalRecursiveFileBytes));
            }
        }
        Hit? last = more ? hits[^1] : null;
        return new(items, total.Public, total.PublicBytes, total.Locked, total.LockedBytes,
            last is { } final ? KeyOf(final).ToString() : null, last?.Kind, last is { } next ? next.Index + 1L : null);
    }

    internal PeerBrowseDownloadResolution Resolve(string username, IReadOnlyList<long> directoryIds,
        IReadOnlyList<long> fileIds, IReadOnlyList<long> excludedFiles, IReadOnlyList<long> excludedDirectories,
        CancellationToken cancellationToken)
    {
        using var scope = Enter();
        if (directoryIds.Count == 0 && fileIds.Count == 0)
            throw new PeerBrowseSelectionException("At least one directory or file must be selected.");
        foreach (long id in directoryIds.Concat(excludedDirectories))
            if (!Valid(id, directoryCount))
                throw new PeerBrowseSelectionException("One or more selected or excluded directories do not exist in this browse.");
        foreach (long id in fileIds.Concat(excludedFiles))
            if (!Valid(id, fileCount))
                throw new PeerBrowseSelectionException("One or more selected or excluded files do not exist in this browse.");
        var identities = new Dictionary<int, string>();
        string Identity(int index) => identities.TryGetValue(index, out string? value)
            ? value : identities[index] = String(Directory(index).Identity)!;
        int[] selectedDirectories = directoryIds.Distinct().Select(id => (int)id - 1)
            .OrderBy(index => Identity(index).Length).ThenBy(index => Identity(index), Comparer<string>.Create((a, b) => CompareText(a, b))).ToArray();
        int[] selectedFiles = fileIds.Distinct().Select(id => (int)id - 1).Order().ToArray();
        if (selectedDirectories.Any(index => Directory(index).Visibility == (byte)PeerBrowseEntryVisibility.Locked))
            throw new PeerBrowseSelectionException("Locked directories cannot be selected for download.");
        if (selectedFiles.Any(index => (File(index).Flags & LockedFlag) != 0))
            throw new PeerBrowseSelectionException("Locked files cannot be selected for download.");
        int redundant = directoryIds.Count - selectedDirectories.Length + fileIds.Count - selectedFiles.Length;
        var selected = selectedDirectories.ToHashSet();
        var excluded = excludedDirectories.Select(id => (int)id - 1).ToHashSet();
        var excludedFileSet = excludedFiles.Select(id => (int)id - 1).ToHashSet();
        var selectedFileSet = selectedFiles.ToHashSet();
        var canonical = new List<int>();
        var canonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        var includedPaths = new HashSet<string>(StringComparer.Ordinal);
        var excludedPaths = excluded.Select(Identity).ToHashSet(StringComparer.Ordinal);
        foreach (int index in selectedDirectories)
        {
            if (HasSameOrAncestor(canonicalPaths, Identity(index)))
            {
                if (IncludedByRule(Identity(index), includedPaths, excludedPaths))
                    redundant++;
            }
            else
            {
                canonical.Add(index);
                canonicalPaths.Add(Identity(index));
            }
            includedPaths.Add(Identity(index));
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
                ref readonly DirectoryRecord record = ref Directory(directory);
                bool included = !excluded.Contains(directory) && (selected.Contains(directory) || current.Included);
                if (included && directory != owner && record.Visibility == (byte)PeerBrowseEntryVisibility.Locked)
                    skipped[owner] = skipped.GetValueOrDefault(owner) + 1;
                for (long position = record.FileStart; position < (long)record.FileStart + record.FileCount; position++)
                {
                    int index = Index(Item<int>(Section.FileOrder, position), fileCount);
                    if ((File(index).Flags & LockedFlag) != 0 || excludedFileSet.Contains(index))
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
                for (long position = record.ChildStart; position < (long)record.ChildStart + record.ChildCount; position++)
                    pending.Push((Index(Item<int>(Section.ChildOrder, position), directoryCount), included));
            }
            entries[owner].Sort(); // Stable source/file-ID order, as in retained plans.
        }
        var plans = new List<(string Key, DirectoryTransferPlan Plan)>();
        long totalFiles = 0, totalBytes = 0, lockedSkipped = 0;
        foreach (int index in canonical)
        {
            if (entries[index].Count == 0)
                continue;
            string ownerIdentity = Identity(index);
            var relativePaths = new Dictionary<int, string[]>();
            var transfers = entries[index].Select(file =>
            {
                int directory = DirectoryIndex(File(file).Directory);
                if (!relativePaths.TryGetValue(directory, out string[]? relative))
                    relativePaths[directory] = relative = directory == index ? [] : Identity(directory)[(ownerIdentity.Length + 1)..].Split('\\');
                return new DirectoryTransferEntry(Target(username, file), relative);
            }).ToArray();
            ref readonly DirectoryRecord record = ref Directory(index);
            var plan = new DirectoryTransferPlan(PeerBrowsePath.Leaf(ownerIdentity), transfers,
                sourceDirectory: new PeerDirectoryIdentity(username, String(record.WirePath)!));
            plans.Add(("0\0" + String(record.DisplayPath), plan));
            totalFiles += plan.Entries.Count;
            totalBytes = Sum(totalBytes, plan.TotalKnownBytes);
            lockedSkipped += excluded.Count > 0 ? skipped.GetValueOrDefault(index) : record.LockedDescendantCount;
        }
        foreach (var group in standalone.GroupBy(index => DirectoryIndex(File(index).Directory)))
        {
            var targets = group.Select(index => Target(username, index)).OrderBy(target => target.Filename, StringComparer.Ordinal)
                .Select(target => new DirectoryTransferEntry(target, [])).ToArray();
            var plan = new DirectoryTransferPlan(PeerBrowsePath.Leaf(Identity(group.Key)), targets, representsDirectorySelection: false);
            plans.Add(("1\0" + String(Directory(group.Key).DisplayPath), plan));
            totalFiles += targets.Length;
            totalBytes = Sum(totalBytes, plan.TotalKnownBytes);
        }
        return new(plans.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Plan).ToArray(),
            canonical.Count, standalone.Count, totalFiles, totalBytes, redundant, lockedSkipped);
    }

    // Finds every file whose search text contains the needle with vectorized
    // scans over the contiguous text block, in chunks of whole files.
    private void MarkNameMatches(byte[] needle, bool[] matches, CancellationToken cancellationToken)
    {
        const long ChunkBytes = 64L * 1024 * 1024;
        if (needle.Length == 0)
        {
            Array.Fill(matches, true);
            return;
        }
        int first = 0;
        while (first < fileCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long start = first == 0 ? 0 : Item<long>(Section.SearchEnds, first - 1);
            int last = LastFileEndingBy(first, start + ChunkBytes);
            ReadOnlySpan<byte> chunk = Bytes(Section.Search, start, Item<long>(Section.SearchEnds, last) - start);
            int position = 0;
            while (position < chunk.Length)
            {
                int hit = chunk[position..].IndexOf(needle);
                if (hit < 0)
                    break;
                long at = start + position + hit;
                int owner = FileContaining(at, first, last);
                long end = Item<long>(Section.SearchEnds, owner);
                // A hit running into the next name is not a match in this one;
                // either way, scanning resumes at the next name.
                if (at + needle.Length <= end)
                    matches[owner] = true;
                position = checked((int)(end - start));
            }
            first = last + 1;
        }
    }

    private int LastFileEndingBy(int first, long limit)
    {
        int low = first, high = fileCount - 1;
        while (low < high)
        {
            int middle = low + (high - low + 1) / 2;
            if (Item<long>(Section.SearchEnds, middle) <= limit)
                low = middle;
            else
                high = middle - 1;
        }
        return low;
    }

    private int FileContaining(long offset, int low, int high)
    {
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (Item<long>(Section.SearchEnds, middle) > offset)
                high = middle;
            else
                low = middle + 1;
        }
        return low;
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

    private PeerBrowseDirectoryEntry DirectoryEntryAt(int index)
    {
        ref readonly DirectoryRecord directory = ref Directory(index);
        return new(index + 1L, directory.Parent == 0 ? null : directory.Parent, String(directory.Name)!,
            String(directory.DisplayPath)!, (PeerBrowseEntryVisibility)directory.Visibility, directory.Synthetic != 0,
            directory.ChildCount, directory.FileCount, directory.RecursiveFileCount, directory.RecursiveFileBytes,
            directory.LockedDescendantCount, directory.ChildCount > 0 || directory.FileCount > 0,
            directory.TotalRecursiveFileCount, directory.TotalRecursiveFileBytes);
    }

    private PeerBrowseFileEntry FileEntryAt(int index)
    {
        ref readonly FileRecord file = ref File(index);
        var (metadata, attributes) = ReadAttributes(index, static (type, value) => new PeerBrowseFileAttribute(type, value));
        return new(index + 1L, file.Directory, Visibility(file), Utf8(Name(index)), file.Size, Extension(file),
            metadata.BitRate, metadata.BitDepth, metadata.SampleRate, metadata.Length, attributes);
    }

    private PeerFileTarget Target(string username, int index)
    {
        ref readonly FileRecord file = ref File(index);
        var (metadata, attributes) = ReadAttributes(index,
            static (type, value) => new FileAttributeSnapshot(type.ToString(CultureInfo.InvariantCulture), value, type));
        string? rawName = null, wire = null;
        if ((file.Flags & ExceptionFlag) != 0 && FindException(index) is { } exception)
            (rawName, wire) = (String(exception.RawName), String(exception.WireOverride));
        wire ??= String(Directory(DirectoryIndex(file.Directory)).WirePath) + "\\" + (rawName ?? Utf8(Name(index)));
        return new(new PeerFileIdentity(username, wire), file.Size, Extension(file),
            metadata.BitRate, metadata.BitDepth, metadata.SampleRate, metadata.Length, attributes);
    }

    private FileException? FindException(int file)
    {
        int low = 0, high = exceptionCount - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            FileException candidate = Item<FileException>(Section.Exceptions, middle);
            if (candidate.File == file)
                return candidate;
            if (candidate.File < file)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return null;
    }

    private (PeerBrowseFileMetadata Metadata, List<T>? Attributes) ReadAttributes<T>(int index, Func<int, int, T>? create)
    {
        PeerBrowseFileMetadata metadata = default;
        ReadOnlySpan<byte> bytes = Range(Section.Attributes, Section.AttributeEnds, index);
        if (bytes.Length % 8 != 0)
            throw Corrupt();
        List<T>? attributes = create is not null && bytes.Length > 0 ? new(bytes.Length / 8) : null;
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

    private string? Extension(in FileRecord file)
        => file.Extension == 0 ? null : extensions[Index(file.Extension - 1, extensions.Length)];

    // Search results, page continuations and folder listings share one order:
    // UTF-8 byte order of the display path (Unicode scalar order), then kind, then ID.
    private readonly record struct Hit(int Index, PeerBrowseSearchEntryKind Kind);

    private sealed class HitComparer(PeerBrowseArtifact artifact) : IComparer<Hit>
    {
        public int Compare(Hit a, Hit b)
            => PeerBrowseArtifact.Compare(artifact.KeyOf(a), a.Kind, a.Index + 1L, artifact.KeyOf(b), b.Kind, b.Index + 1L);
    }

    /// <summary>A path compared without building it: a directory path, or a directory path, separator and name.</summary>
    private readonly ref struct Key(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail = default, bool separator = false)
    {
        public readonly ReadOnlySpan<byte> Head = head, Tail = tail;
        public readonly bool Separator = separator;
        public int Length => Head.Length + (Separator ? 1 : 0) + Tail.Length;
        public byte this[int index] => index < Head.Length ? Head[index]
            : Separator ? index == Head.Length ? (byte)'\\' : Tail[index - Head.Length - 1]
            : Tail[index - Head.Length];
        public override string ToString()
            => Separator ? Utf8(Head) + "\\" + Utf8(Tail) : Utf8(Head) + Utf8(Tail);
    }

    private Key KeyOf(Hit hit)
    {
        if (hit.Kind == PeerBrowseSearchEntryKind.Directory)
            return new(Text(Directory(hit.Index).DisplayPath));
        return new(Text(Directory(DirectoryIndex(File(hit.Index).Directory)).DisplayPath), Name(hit.Index), separator: true);
    }

    private static int Compare(Key a, PeerBrowseSearchEntryKind kind, long id, Key b, PeerBrowseSearchEntryKind otherKind, long otherId)
    {
        int comparison = Compare(a, b);
        if (comparison == 0)
            comparison = kind.CompareTo(otherKind);
        return comparison == 0 ? id.CompareTo(otherId) : comparison;
    }

    private static int Compare(Key a, Key b)
    {
        int length = Math.Min(a.Length, b.Length);
        int index = a.Head.CommonPrefixLength(b.Head);
        if (index < a.Head.Length && index < b.Head.Length)
            return a.Head[index].CompareTo(b.Head[index]);
        for (; index < length; index++)
        {
            byte x = a[index], y = b[index];
            if (x != y)
                return x.CompareTo(y);
        }
        return a.Length.CompareTo(b.Length);
    }

    private static int CompareKey(ReadOnlySpan<byte> key, long id, ReadOnlySpan<byte> otherKey, long otherId)
    {
        int comparison = key.SequenceCompareTo(otherKey);
        return comparison == 0 ? id.CompareTo(otherId) : Math.Sign(comparison);
    }

    // Preserve the former SQLite BINARY order: UTF-8 scalar order, including
    // supplementary Unicode characters. No culture-dependent ordering of peers.
    internal static int CompareText(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
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

    /// <summary>
    /// The text a filter is matched against, so a plain substring search equals the
    /// former accent-folded <see cref="StringComparison.OrdinalIgnoreCase"/> Contains:
    /// each character becomes its upper case only where ordinal ignore-case treats
    /// the two as equal (it does not, for example, equate long s with s).
    /// </summary>
    internal static byte[] SearchBytes(string folded)
    {
        char[] table = OrdinalUpper.Value;
        string upper = string.Create(folded.Length, (folded, table), static (target, state) =>
        {
            var (source, map) = state;
            for (int i = 0; i < source.Length; i++)
            {
                if (char.IsHighSurrogate(source[i]) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    string pair = source.Substring(i, 2), mapped = pair.ToUpperInvariant();
                    if (mapped.Length != 2 || !string.Equals(pair, mapped, StringComparison.OrdinalIgnoreCase))
                        mapped = pair;
                    target[i] = mapped[0];
                    target[++i] = mapped[1];
                }
                else
                    target[i] = map[source[i]];
            }
        });
        return Encoding.UTF8.GetBytes(upper);
    }

    private static readonly Lazy<char[]> OrdinalUpper = new(static () =>
    {
        var table = new char[char.MaxValue + 1];
        for (int value = 0; value <= char.MaxValue; value++)
        {
            char character = (char)value, upper = char.ToUpperInvariant(character);
            table[value] = upper != character && !char.IsSurrogate(character)
                && string.Equals(character.ToString(), upper.ToString(), StringComparison.OrdinalIgnoreCase)
                ? upper : character;
        }
        return table;
    });

    private static bool IsDescendant(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> root)
        => candidate.Length > root.Length && candidate.StartsWith(root) && candidate[root.Length] == (byte)'\\';

    private static bool IsSameOrDescendant(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> root)
        => candidate.SequenceEqual(root) || IsDescendant(candidate, root);

    private static PeerBrowsePage<T> Page<T>(List<T> rows, int limit, Func<T, string> key, Func<T, long> id) where T : class
    {
        bool more = rows.Count > limit;
        if (more)
            rows.RemoveAt(rows.Count - 1);
        return new(rows, more ? key(rows[^1]) : null, more ? id(rows[^1]) : null);
    }

    private int RequireDirectory(long id) => Valid(id, directoryCount) ? (int)id - 1
        : throw new KeyNotFoundException($"Peer browse directory '{id}' does not exist.");
    private static bool Valid(long id, int count) => id > 0 && id <= count;
    private static PeerBrowseEntryVisibility Visibility(in FileRecord file)
        => (file.Flags & LockedFlag) != 0 ? PeerBrowseEntryVisibility.Locked : PeerBrowseEntryVisibility.Public;
    internal static long Sum(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    private static string Utf8(ReadOnlySpan<byte> text) => Encoding.UTF8.GetString(text);

    // Every access below is bounds-checked against the mapped sections, so a
    // damaged file fails with InvalidDataException rather than reading outside it.
    private ref readonly DirectoryRecord Directory(int index) => ref Item<DirectoryRecord>(Section.Directories, index);
    private ref readonly FileRecord File(int index) => ref Item<FileRecord>(Section.Files, index);
    private int DirectoryIndex(int oneBased) => Index(oneBased - 1, directoryCount);
    private ReadOnlySpan<byte> Name(int file) => Range(Section.Names, Section.NameEnds, file);
    private ReadOnlySpan<byte> SearchText(int file) => Range(Section.Search, Section.SearchEnds, file);
    private ReadOnlySpan<byte> Text(TextRef text) => text.Length < 0 ? default : Bytes(Section.Strings, text.Offset, text.Length);
    private string? String(TextRef text) => text.Length < 0 ? null : Utf8(Text(text));

    private static int NextMatch(bool[] matches, int start)
    {
        int found = matches.AsSpan(start).IndexOf(true);
        return found < 0 ? -1 : start + found;
    }

    private static ReadOnlySpan<int> Slice(ReadOnlySpan<int> items, int start, int count)
        => start >= 0 && count >= 0 && start <= items.Length - count ? items.Slice(start, count) : throw Corrupt();

    /// <summary>A whole fixed-size section; its length was checked against the header at open.</summary>
    private unsafe ReadOnlySpan<T> Records<T>(Section section) where T : unmanaged
        => new(data + offsets[(int)section], checked((int)(lengths[(int)section] / sizeof(T))));

    private ReadOnlySpan<byte> Range(Section block, Section ends, int file)
    {
        long start = file == 0 ? 0 : Item<long>(ends, file - 1);
        return Bytes(block, start, Item<long>(ends, file) - start);
    }

    private unsafe ref readonly T Item<T>(Section section, long index) where T : unmanaged
    {
        if ((ulong)index >= (ulong)(lengths[(int)section] / sizeof(T)))
            throw Corrupt();
        return ref Unsafe.AsRef<T>(data + offsets[(int)section] + index * sizeof(T));
    }

    private unsafe ReadOnlySpan<byte> Bytes(Section section, long offset, long count)
    {
        if (offset < 0 || count < 0 || count > int.MaxValue || offset > lengths[(int)section] - count)
            throw Corrupt();
        return new(data + offsets[(int)section] + offset, (int)count);
    }

    private unsafe void RequireArray<T>(Section section, long count) where T : unmanaged
    {
        if (count < 0 || lengths[(int)section] != count * sizeof(T))
            throw Corrupt();
    }

    private static int Index(int index, int count) => (uint)index < (uint)count ? index : throw Corrupt();
    private static int Count(ReadOnlySpan<byte> bytes)
        => BinaryPrimitives.ReadInt32LittleEndian(bytes) is var value and >= 0 ? value : throw Corrupt();
    private static InvalidDataException Corrupt() => new("The peer browse artifact is damaged.");

    private Scope Enter()
    {
        bool added = false;
        handle.DangerousAddRef(ref added);
        return new(handle);
    }

    /// <summary>Keeps the mapping alive for the duration of one query.</summary>
    private readonly ref struct Scope(SafeMemoryMappedViewHandle handle)
    {
        public void Dispose() => handle.DangerousRelease();
    }

    internal struct Counts
    {
        public long Public, PublicBytes, Locked, LockedBytes;
        public void Add(in FileRecord file) => Add(file.Size, (file.Flags & LockedFlag) != 0);
        public void Add(long size, bool locked)
        {
            if (locked)
            {
                Locked++;
                LockedBytes = Sum(LockedBytes, size);
            }
            else
            {
                Public++;
                PublicBytes = Sum(PublicBytes, size);
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
