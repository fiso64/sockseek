using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Sockseek.Core.IO;
using Sockseek.Core.Sharing;

namespace Sockseek.Core.Services;

/// <summary>
/// Writes the Soulseek browse response directly to a framed, compressed artifact
/// without retaining the complete directory/file graph or payload in memory.
/// </summary>
public sealed class SoulseekBrowseArtifactBuilder : ISoulseekBrowseArtifactBuilder
{
    private const int BrowseResponsePeerMessageCode = 5;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly long maximumArtifactLength;

    public SoulseekBrowseArtifactBuilder(long maximumArtifactLength = int.MaxValue)
    {
        if (maximumArtifactLength <= 8 || maximumArtifactLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumArtifactLength));
        this.maximumArtifactLength = maximumArtifactLength;
    }

    public async ValueTask<ShareBrowseArtifact> BuildAsync(
        IShareCatalogReader catalog,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using (new FileStream(
                   fullPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
        }
        OwnerOnlyFilePermissions.EnsureFile(fullPath);

        await using (var output = new FileStream(
                         fullPath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         128 * 1_024,
                         FileOptions.SequentialScan))
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], BrowseResponsePeerMessageCode);
            output.Write(header);

            await using (var compressed = new ZLibStream(
                             output,
                             CompressionLevel.Optimal,
                             leaveOpen: true))
            {
                var writer = new BrowseWireWriter(compressed);
                writer.WriteInt32(checked((int)catalog.Metadata.DirectoryCount));

                long directoryCount = 0;
                int remainingFiles = 0;
                await foreach (ShareCatalogBrowseRow row in catalog
                                   .EnumerateBrowseRowsAsync(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (row)
                    {
                        case ShareCatalogBrowseDirectoryRow directory:
                            if (remainingFiles != 0)
                            {
                                throw new InvalidDataException(
                                    "Browse row stream ended a directory before all declared files.");
                            }
                            WriteDirectory(writer, directory);
                            remainingFiles = directory.FileCount;
                            directoryCount++;
                            break;
                        case ShareCatalogBrowseFileRow file:
                            if (remainingFiles <= 0)
                            {
                                throw new InvalidDataException(
                                    "Browse row stream emitted a file without a directory count.");
                            }
                            WriteFile(writer, file.File);
                            remainingFiles--;
                            break;
                        default:
                            throw new InvalidDataException("Unknown browse row type.");
                    }
                }
                if (remainingFiles != 0)
                    throw new InvalidDataException("Browse row stream ended before all declared files.");

                if (directoryCount != catalog.Metadata.DirectoryCount)
                {
                    throw new InvalidDataException(
                        $"Catalog declared {catalog.Metadata.DirectoryCount} directories " +
                        $"but enumerated {directoryCount}.");
                }

                writer.WriteInt32(0); // legacy/private directory marker
                writer.WriteInt32(0); // locked directory count
            }

            long length = output.Length;
            if (length <= 8)
                throw new InvalidDataException("Browse artifact is incomplete.");
            if (length > maximumArtifactLength)
            {
                throw new BrowseArtifactOversizeException(
                    length,
                    maximumArtifactLength);
            }

            output.Position = 0;
            Span<byte> frameLength = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(frameLength, checked((int)length - 4));
            output.Write(frameLength);
            output.Flush();
        }

        string sha256;
        await using (var input = new FileStream(
                         fullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1_024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] hash = await SHA256.HashDataAsync(input, cancellationToken)
                .ConfigureAwait(false);
            sha256 = Convert.ToHexString(hash);
        }

        return new ShareBrowseArtifact(
            fullPath,
            new FileInfo(fullPath).Length,
            sha256,
            ShareCatalogVersions.BrowseWire);
    }

    private static void WriteDirectory(
        BrowseWireWriter writer,
        ShareCatalogBrowseDirectoryRow directory)
    {
        writer.WriteString(directory.Directory.RemotePath);
        writer.WriteInt32(directory.FileCount);
    }

    private static void WriteFile(BrowseWireWriter writer, ShareCatalogFile file)
    {
        writer.WriteByte(checked((byte)file.ProtocolCode));
        writer.WriteString(RemoteFileName(file.RemotePath));
        writer.WriteInt64(file.SizeBytes);
        writer.WriteString(file.Extension);
        writer.WriteInt32(file.Attributes.Count);

        foreach (var attribute in file.Attributes)
        {
            writer.WriteInt32(attribute.Type);
            writer.WriteInt32(attribute.Value);
        }
    }

    private static string RemoteFileName(string remotePath)
    {
        int separator = remotePath.LastIndexOf('\\');
        return separator < 0 ? remotePath : remotePath[(separator + 1)..];
    }

    private sealed class BrowseWireWriter(Stream output)
    {
        public void WriteByte(byte value) => output.WriteByte(value);

        public void WriteInt32(int value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            output.Write(buffer);
        }

        public void WriteInt64(long value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            output.Write(buffer);
        }

        public void WriteString(string value)
        {
            int length = StrictUtf8.GetByteCount(value);
            WriteInt32(length);
            if (length == 0)
                return;

            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                int written = StrictUtf8.GetBytes(value, rented);
                output.Write(rented, 0, written);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
