using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Sockseek.Core;
using Sockseek.Core.IO;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;
using static Sockseek.Persistence.PeerBrowsing.PeerBrowseArtifact;

namespace Sockseek.Persistence.PeerBrowsing;

/// <summary>
/// Writes one browse generation in the layout <see cref="PeerBrowseArtifact"/>
/// maps. Files arrive grouped by directory; their records and text stream to
/// staging files, so memory holds only the directories and the directory
/// currently arriving, however large the share.
/// </summary>
internal sealed class PeerBrowseArtifactBuilder : IDisposable
{
    internal sealed record Directory(int Parent, string WirePath, PeerBrowseEntryVisibility Visibility, bool Synthetic);

    /// <summary>One-based directory IDs are positions in this list; parents precede children.</summary>
    public List<Directory> Directories { get; } = [];

    private readonly string path;
    private readonly Spill files, nameEnds, searchEnds, attributeEnds, fileOrder, names, search, attributes;
    private readonly Dictionary<string, int> extensionIds = new(StringComparer.Ordinal);
    private readonly List<string> extensions = [];
    private readonly List<(int File, string? RawName, string? WireOverride)> exceptions = [];
    private readonly List<Counts> directCounts = [];
    private readonly Dictionary<int, (int Start, int Count)> fileRanges = [];
    private readonly List<string> runNames = [];
    private readonly HashSet<string> runRawNames = new(StringComparer.Ordinal);
    private readonly ArrayBufferWriter<byte> text = new();
    private int runDirectory, runStart, fileCount;
    private bool duplicate;

    public PeerBrowseArtifactBuilder(string path)
    {
        this.path = path;
        var spills = new List<Spill>();
        try
        {
            Spill Open(string name)
            {
                var spill = new Spill($"{path}.{name}.staging");
                spills.Add(spill);
                return spill;
            }
            files = Open("files");
            nameEnds = Open("name-ends");
            searchEnds = Open("search-ends");
            attributeEnds = Open("attribute-ends");
            fileOrder = Open("file-order");
            names = Open("names");
            search = Open("search");
            attributes = Open("attributes");
        }
        catch
        {
            spills.ForEach(spill => spill.Dispose());
            throw;
        }
    }

    public void AddFile(int directory, string rawName, string? wireOverride, long size, string? extension,
        int code, ReadOnlySpan<byte> attributeBytes, bool locked)
    {
        if (directory != runDirectory)
        {
            CloseRun();
            if (directory <= 0 || directory > Directories.Count || fileRanges.ContainsKey(directory))
                throw PeerBrowseArtifactWriter.Invalid("files of one directory were not listed together");
            runDirectory = directory;
            runStart = fileCount;
        }
        // Duplicates are reported when the response completes, as before.
        duplicate |= !runRawNames.Add(rawName);
        string display = PeerIdentityValidator.ToDisplayText(rawName);
        runNames.Add(display);
        Write(names, nameEnds, display);
        search.Write(SearchBytes(Utils.FoldDisplayText(display)));
        searchEnds.Write(search.Length);
        attributes.Write(attributeBytes);
        attributeEnds.Write(attributes.Length);
        int extensionId = 0;
        if (extension is not null && !extensionIds.TryGetValue(extension, out extensionId))
        {
            extensions.Add(extension);
            extensionIds.Add(extension, extensionId = extensions.Count);
        }
        bool exception = !StringComparer.Ordinal.Equals(display, rawName) || wireOverride is not null;
        if (exception)
            exceptions.Add((fileCount, StringComparer.Ordinal.Equals(display, rawName) ? null : rawName, wireOverride));
        files.Write(new FileRecord
        {
            Size = size,
            Directory = directory,
            Extension = extensionId,
            Code = checked((byte)code),
            Flags = (byte)((locked ? LockedFlag : 0) | (exception ? ExceptionFlag : 0)),
        });
        while (directCounts.Count < directory)
            directCounts.Add(default);
        var counts = directCounts[directory - 1];
        counts.Add(size, locked);
        directCounts[directory - 1] = counts;
        fileCount++;
    }

    public void Complete(CancellationToken cancellationToken)
    {
        CloseRun();
        if (duplicate)
            throw PeerBrowseArtifactWriter.Invalid("the response contains a duplicate path identity");
        int count = Directories.Count;
        while (directCounts.Count < count)
            directCounts.Add(default);
        var identities = new string[count];
        var displays = new string[count];
        var totals = new Counts[count];
        var lockedDescendants = new long[count];
        var hasPublic = new bool[count];
        var hasLocked = new bool[count];
        var childCounts = new int[count];
        int rootCount = 0;
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Directories[i];
            if (directory.Parent < 0 || directory.Parent > i)
                throw new InvalidDataException("A browse directory has an invalid parent.");
            identities[i] = PeerBrowsePath.NormalizeDirectoryIdentity(directory.WirePath);
            string display = PeerBrowsePath.ToDisplayPath(identities[i]);
            displays[i] = display == identities[i] ? identities[i] : display;
            totals[i] = directCounts[i];
            hasPublic[i] = directory.Visibility != PeerBrowseEntryVisibility.Locked;
            hasLocked[i] = directory.Visibility != PeerBrowseEntryVisibility.Public;
            if (directory.Parent > 0)
                childCounts[directory.Parent - 1]++;
            else
                rootCount++;
        }
        for (int i = count - 1; i >= 0; i--)
        {
            var directory = Directories[i];
            if (directory.Parent <= 0)
                continue;
            int parent = directory.Parent - 1;
            totals[parent].Add(totals[i]);
            lockedDescendants[parent] += lockedDescendants[i] + (directory.Visibility == PeerBrowseEntryVisibility.Locked ? 1 : 0);
            hasPublic[parent] |= hasPublic[i];
            hasLocked[parent] |= hasLocked[i];
        }

        int Order(int a, int b)
        {
            int comparison = CompareText(displays[a], displays[b]);
            return comparison == 0 ? a.CompareTo(b) : comparison;
        }
        var childStarts = new int[count];
        for (int i = 0, position = 0; i < count; i++)
        {
            childStarts[i] = position;
            position += childCounts[i];
        }
        var childOrder = new int[count - rootCount];
        var roots = new int[rootCount];
        var filled = new int[count];
        for (int i = 0, root = 0; i < count; i++)
        {
            int parent = Directories[i].Parent - 1;
            if (parent < 0)
                roots[root++] = i;
            else
                childOrder[childStarts[parent] + filled[parent]++] = i;
        }
        for (int i = 0; i < count; i++)
            childOrder.AsSpan(childStarts[i], childCounts[i]).Sort(Order);
        roots.AsSpan().Sort(Order);
        var directoryOrder = Enumerable.Range(0, count).ToArray();
        directoryOrder.AsSpan().Sort(Order);

        var strings = new ArrayBufferWriter<byte>();
        TextRef Add(string? value)
        {
            if (value is null)
                return TextRef.Null;
            long offset = strings.WrittenCount;
            int length = checked((int)Encoding.UTF8.GetBytes(value, strings));
            return new(offset, length);
        }
        TextRef AddBytes(ReadOnlySpan<byte> value)
        {
            long offset = strings.WrittenCount;
            strings.Write(value);
            return new(offset, value.Length);
        }
        var records = new DirectoryRecord[count];
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Directories[i];
            TextRef wire = Add(directory.WirePath);
            TextRef identity = StringComparer.Ordinal.Equals(identities[i], directory.WirePath) ? wire : Add(identities[i]);
            TextRef display = ReferenceEquals(displays[i], identities[i]) ? identity : Add(displays[i]);
            Counts total = totals[i];
            var range = fileRanges.GetValueOrDefault(i + 1);
            records[i] = new DirectoryRecord
            {
                WirePath = wire,
                Identity = identity,
                DisplayPath = display,
                Name = Add(PeerIdentityValidator.ToDisplayText(PeerBrowsePath.Leaf(identities[i]))),
                Search = AddBytes(SearchBytes(Utils.FoldDisplayText(displays[i]))),
                RecursiveFileCount = total.Public,
                RecursiveFileBytes = total.PublicBytes,
                TotalRecursiveFileCount = total.Public + total.Locked,
                TotalRecursiveFileBytes = Sum(total.PublicBytes, total.LockedBytes),
                LockedDescendantCount = lockedDescendants[i],
                Parent = directory.Parent,
                ChildStart = childStarts[i],
                ChildCount = childCounts[i],
                FileStart = range.Start,
                FileCount = range.Count,
                Visibility = (byte)(hasLocked[i] ? hasPublic[i] ? PeerBrowseEntryVisibility.Mixed : PeerBrowseEntryVisibility.Locked : PeerBrowseEntryVisibility.Public),
                Synthetic = directory.Synthetic ? (byte)1 : (byte)0,
            };
        }
        var extensionRefs = extensions.Select(Add).ToArray();
        var exceptionRecords = exceptions.Select(item => new FileException
        {
            File = item.File,
            RawName = Add(item.RawName),
            WireOverride = Add(item.WireOverride),
        }).ToArray();

        var sections = new (long Offset, long Length)[SectionCount];
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            OwnerOnlyFilePermissions.EnsureFile(path);
            output.Write(new byte[HeaderSize]);
            void Put(Section section, Action<Stream> write)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(new byte[(8 - output.Position % 8) % 8]);
                long offset = output.Position;
                write(output);
                sections[(int)section] = (offset, output.Position - offset);
            }
            void PutArray<T>(Section section, T[] items) where T : unmanaged
                => Put(section, stream => stream.Write(MemoryMarshal.AsBytes(items.AsSpan())));
            PutArray(Section.Directories, records);
            Put(Section.Files, files.CopyTo);
            Put(Section.NameEnds, nameEnds.CopyTo);
            Put(Section.SearchEnds, searchEnds.CopyTo);
            Put(Section.AttributeEnds, attributeEnds.CopyTo);
            Put(Section.FileOrder, fileOrder.CopyTo);
            PutArray(Section.ChildOrder, childOrder);
            PutArray(Section.DirectoryOrder, directoryOrder);
            PutArray(Section.Roots, roots);
            PutArray(Section.Extensions, extensionRefs);
            PutArray(Section.Exceptions, exceptionRecords);
            Put(Section.Names, names.CopyTo);
            Put(Section.Search, search.CopyTo);
            Put(Section.Attributes, attributes.CopyTo);
            Put(Section.Strings, stream => stream.Write(strings.WrittenSpan));

            var header = new byte[HeaderSize];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), PeerBrowseArtifactStore.ArtifactFormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), count);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), fileCount);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), rootCount);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), extensions.Count);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), exceptionRecords.Length);
            for (int section = 0; section < SectionCount; section++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32 + section * 16), sections[section].Offset);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40 + section * 16), sections[section].Length);
            }
            output.Position = 0;
            output.Write(header);
            // Not forced to disk: a browse can be fetched again, and a file damaged
            // by a power loss is rejected when opened rather than misread.
            output.Flush();
        }
    }

    public void Dispose()
    {
        foreach (var spill in new[] { files, nameEnds, searchEnds, attributeEnds, fileOrder, names, search, attributes })
            spill.Dispose();
        // The writer may outlive its build; it must not keep the directory list alive.
        Directories.Clear();
        directCounts.Clear();
        fileRanges.Clear();
        exceptions.Clear();
        extensionIds.Clear();
        extensions.Clear();
    }

    // A directory's files are contiguous; sort them by display name once it ends.
    private void CloseRun()
    {
        if (runDirectory == 0)
            return;
        var order = Enumerable.Range(0, runNames.Count).ToArray();
        order.AsSpan().Sort((a, b) =>
        {
            int comparison = CompareText(runNames[a], runNames[b]);
            return comparison == 0 ? a.CompareTo(b) : comparison;
        });
        foreach (int index in order)
            fileOrder.Write(runStart + index);
        fileRanges[runDirectory] = (runStart, runNames.Count);
        runDirectory = 0;
        runNames.Clear();
        runRawNames.Clear();
    }

    private void Write(Spill block, Spill ends, string value)
    {
        text.Clear();
        Encoding.UTF8.GetBytes(value, text);
        block.Write(text.WrittenSpan);
        ends.Write(block.Length);
    }

    /// <summary>An append-only staging file, deleted when closed.</summary>
    private sealed class Spill(string path) : IDisposable
    {
        private readonly FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1 << 16, FileOptions.DeleteOnClose);

        public long Length { get; private set; }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            stream.Write(bytes);
            Length += bytes.Length;
        }

        public void Write<T>(T value) where T : unmanaged
            => Write(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

        public void CopyTo(Stream output)
        {
            stream.Flush();
            stream.Position = 0;
            stream.CopyTo(output, 1 << 20);
        }

        public void Dispose() => stream.Dispose();
    }
}
