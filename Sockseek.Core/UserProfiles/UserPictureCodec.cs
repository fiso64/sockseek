using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Sockseek.Core.UserProfiles;

/// <summary>A structurally validated, bounded profile picture.</summary>
public sealed record UserPicture(
    byte[] Bytes,
    string MediaType,
    int Width,
    int Height,
    string ETag);

/// <summary>
/// Applies the same hostile-input boundary to local and peer-supplied profile
/// pictures. Local images are additionally normalized to a metadata-free JPEG.
/// </summary>
public static class UserPictureCodec
{
    public const int MaximumInputBytes = 8 * 1024 * 1024;
    public const int MaximumDimension = 8_192;
    public const long MaximumPixels = 40_000_000;
    public const int MaximumDecodedDimension = 1_024;
    public const int LocalOutputDimension = 512;
    public static readonly TimeSpan MaximumWorkDuration = TimeSpan.FromSeconds(5);

    // Restrict identification as well as decoding: checking the detected format
    // afterwards would already have parsed unsupported, peer-supplied metadata.
    private static readonly Configuration PictureConfiguration = new(
        new JpegConfigurationModule(),
        new PngConfigurationModule(),
        new GifConfigurationModule(),
        new WebpConfigurationModule());

    public static async Task<UserPicture> ValidateRemoteAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (bytes.IsEmpty)
            throw new InvalidDataException("The profile picture is empty.");
        if (bytes.Length > MaximumInputBytes)
            throw new InvalidDataException(
                $"The profile picture exceeds the {MaximumInputBytes}-byte limit.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaximumWorkDuration);

        byte[] ownedBytes = bytes.ToArray();
        await using var stream = new MemoryStream(ownedBytes, writable: false);
        ImageInfo info = await IdentifyAsync(stream, timeout.Token).ConfigureAwait(false);
        string mediaType = info.Metadata.DecodedImageFormat?.DefaultMimeType
            ?? throw new InvalidDataException("The profile picture contains no image format.");
        ValidateDimensions(info);

        stream.Position = 0;
        using Image decoded = await Image.LoadAsync(
            CreateDecoderOptions(skipMetadata: true, MaximumDecodedDimension),
            stream,
            timeout.Token).ConfigureAwait(false);
        ValidateDecodedDimensions(decoded);

        return Create(ownedBytes, mediaType, info.Width, info.Height);
    }

    public static async Task<UserPicture> LoadAndNormalizeLocalAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaximumWorkDuration);

        byte[] input = await ReadBoundedFileAsync(path, timeout.Token).ConfigureAwait(false);
        await using var stream = new MemoryStream(input, writable: false);
        ImageInfo info = await IdentifyAsync(stream, timeout.Token).ConfigureAwait(false);
        ValidateDimensions(info);

        stream.Position = 0;
        using Image image = await Image.LoadAsync(
            CreateDecoderOptions(skipMetadata: false, LocalOutputDimension),
            stream,
            timeout.Token).ConfigureAwait(false);

        image.Mutate(context =>
        {
            context.AutoOrient();
            if (image.Width > LocalOutputDimension || image.Height > LocalOutputDimension)
            {
                context.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(LocalOutputDimension, LocalOutputDimension),
                    Sampler = KnownResamplers.Bicubic,
                });
            }
            context.BackgroundColor(Color.White);
        });

        await using var output = new MemoryStream();
        await image.SaveAsJpegAsync(
            output,
            new JpegEncoder
            {
                Quality = 85,
                SkipMetadata = true,
            },
            timeout.Token).ConfigureAwait(false);

        if (output.Length > MaximumInputBytes)
            throw new InvalidDataException("The normalized profile picture is unexpectedly large.");

        byte[] normalized = output.ToArray();
        return Create(normalized, "image/jpeg", image.Width, image.Height);
    }

    private static DecoderOptions CreateDecoderOptions(bool skipMetadata, int targetDimension)
        => new()
        {
            Configuration = PictureConfiguration,
            MaxFrames = 1,
            SkipMetadata = skipMetadata,
            TargetSize = new Size(targetDimension, targetDimension),
        };

    private static async Task<ImageInfo> IdentifyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ImageInfo? info;
        try
        {
            info = await Image.IdentifyAsync(
                CreateDecoderOptions(skipMetadata: true, MaximumDecodedDimension),
                stream,
                cancellationToken).ConfigureAwait(false);
        }
        catch (UnknownImageFormatException ex)
        {
            throw new InvalidDataException("The profile picture must be JPEG, PNG, GIF, or WebP.", ex);
        }
        catch (InvalidImageContentException ex)
        {
            throw new InvalidDataException("The profile picture is corrupt or incomplete.", ex);
        }

        return info ?? throw new InvalidDataException("The profile picture contains no image.");
    }

    private static void ValidateDimensions(ImageInfo info)
    {
        if (info.Width <= 0 || info.Height <= 0
            || info.Width > MaximumDimension || info.Height > MaximumDimension
            || (long)info.Width * info.Height > MaximumPixels)
        {
            throw new InvalidDataException(
                $"The profile picture dimensions {info.Width}x{info.Height} exceed the safety limit.");
        }
    }

    private static void ValidateDecodedDimensions(Image image)
    {
        if (image.Width <= 0 || image.Height <= 0
            || image.Width > MaximumDecodedDimension
            || image.Height > MaximumDecodedDimension)
        {
            throw new InvalidDataException("The profile picture decoder exceeded its target bounds.");
        }
    }

    private static UserPicture Create(
        byte[] bytes,
        string mediaType,
        int width,
        int height)
    {
        string etag = $"\"{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}\"";
        return new UserPicture(bytes, mediaType, width, height, etag);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        if (!file.Exists || (file.Attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException($"Profile picture '{path}' is not a readable regular file.");
        if (file.Length <= 0)
            throw new InvalidDataException($"Profile picture '{path}' is empty.");
        if (file.Length > MaximumInputBytes)
            throw new InvalidDataException(
                $"Profile picture '{path}' exceeds the {MaximumInputBytes}-byte limit.");

        int length = checked((int)file.Length);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("The profile picture changed while it was being read.");
        return bytes;
    }
}
