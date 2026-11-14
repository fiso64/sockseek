using System.Security.Cryptography;
using SkiaSharp;

namespace Sockseek.Core.UserProfiles;

/// <summary>A structurally validated, bounded profile picture.</summary>
public sealed record UserPicture(byte[] Bytes, string MediaType, int Width, int Height, string ETag);

/// <summary>Shared picture validation for peer profiles, local configuration and terminal rendering.</summary>
public static class UserPictureCodec
{
    public const int MaximumInputBytes = 8 * 1024 * 1024;
    public const int MaximumDimension = 8_192;
    public const long MaximumPixels = 40_000_000;
    public const int MaximumDecodedDimension = 1_024;
    public const int LocalOutputDimension = 512;
    public static readonly TimeSpan MaximumWorkDuration = TimeSpan.FromSeconds(5);

    public static Task<UserPicture> ValidateRemoteAsync(
        ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaximumWorkDuration);
        timeout.Token.ThrowIfCancellationRequested();
        string mediaType = DetectFormat(bytes.Span);
        byte[] owned = bytes.ToArray();
        using var data = SKData.CreateCopy(owned);
        using var codec = OpenCodec(data);
        using var decoded = Decode(codec, MaximumDecodedDimension, MaximumDecodedDimension,
            orient: false, opaque: false, timeout.Token);
        return Task.FromResult(Create(owned, mediaType, codec.Info.Width, codec.Info.Height));
    }

    public static async Task<UserPicture> LoadAndNormalizeLocalAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaximumWorkDuration);
        byte[] input = await ReadBoundedFileAsync(path, timeout.Token).ConfigureAwait(false);
        DetectFormat(input);
        using var data = SKData.CreateCopy(input);
        using var codec = OpenCodec(data);
        using var bitmap = Decode(codec, LocalOutputDimension, LocalOutputDimension,
            orient: true, opaque: true, timeout.Token);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85)
            ?? throw new InvalidDataException("The profile picture could not be encoded.");
        timeout.Token.ThrowIfCancellationRequested();
        if (encoded.Size > MaximumInputBytes)
            throw new InvalidDataException("The normalized profile picture is unexpectedly large.");
        // Encoding from pixels retains neither EXIF nor other input metadata.
        return Create(encoded.ToArray(), "image/jpeg", bitmap.Width, bitmap.Height);
    }

    /// <summary>Returns an owned, bounded BGRA bitmap for terminal rendering.</summary>
    public static SKBitmap DecodeForDisplay(
        UserPicture picture, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaximumWorkDuration);
        timeout.Token.ThrowIfCancellationRequested();
        DetectFormat(picture.Bytes);
        using var data = SKData.CreateCopy(picture.Bytes);
        using var codec = OpenCodec(data);
        return Decode(codec, Math.Min(width, MaximumDecodedDimension), Math.Min(height, MaximumDecodedDimension),
            orient: true, opaque: false, timeout.Token);
    }

    private static string DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumInputBytes)
            throw new InvalidDataException("The profile picture is empty or exceeds the byte limit.");
        // Check the allowlist before invoking native format identification. In particular,
        // TIFF, SVG and other unsupported inputs must never reach their parsers.
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        throw new InvalidDataException("The profile picture must be JPEG, PNG, GIF, or WebP.");
    }

    private static SKCodec OpenCodec(SKData data)
    {
        var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("The profile picture is corrupt or incomplete.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaximumDimension
            || info.Height > MaximumDimension || (long)info.Width * info.Height > MaximumPixels)
        {
            codec.Dispose();
            throw new InvalidDataException("The profile picture dimensions exceed the safety limit.");
        }
        return codec;
    }

    private static SKBitmap Decode(SKCodec codec, int maxWidth, int maxHeight,
        bool orient, bool opaque, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var origin = orient ? codec.EncodedOrigin : SKEncodedOrigin.TopLeft;
        bool rotated = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int width = rotated ? codec.Info.Height : codec.Info.Width;
        int height = rotated ? codec.Info.Width : codec.Info.Height;
        double scale = Math.Min(1, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        var size = codec.GetScaledDimensions((float)scale);
        // JPEG/WebP can decode smaller directly. Other codecs may require the original
        // raster, already bounded by MaximumPixels, before resizing. Only frame zero is decoded.
        using var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height,
            SKColorType.Bgra8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("The profile picture is corrupt or incomplete.");
        // Native decoding is synchronous; cancellation is checked between operations,
        // not implemented by abandoning a worker that would keep using native memory.
        cancellationToken.ThrowIfCancellationRequested();
        var result = new SKBitmap(new SKImageInfo(Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)), SKColorType.Bgra8888, SKAlphaType.Unpremul));
        try
        {
            using var canvas = new SKCanvas(result);
            canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);
            canvas.Scale((float)result.Width / (rotated ? bitmap.Height : bitmap.Width),
                (float)result.Height / (rotated ? bitmap.Width : bitmap.Height));
            var transform = Orientation(origin, bitmap.Width, bitmap.Height);
            canvas.Concat(in transform);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKCubicResampler.CatmullRom));
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static SKMatrix Orientation(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

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
