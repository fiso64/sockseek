using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using SkiaSharp;
using Sockseek.Core.Settings;
using Sockseek.Core.UserProfiles;

namespace Tests.Core;

[TestClass]
public sealed class UserPictureCodecTests
{
    [TestMethod]
    public void DescriptionNormalizationProducesBoundedPlainText()
    {
        Assert.AreEqual(
            "a\nb\tc",
            UserProfileText.NormalizeDescription("a\r\nb\u202e\u0001\tc"));

        string bounded = UserProfileText.NormalizeDescription(new string('\u00e9', 40_000));
        Assert.AreEqual(
            UserProfileText.MaximumDescriptionUtf8Bytes,
            System.Text.Encoding.UTF8.GetByteCount(bounded));
    }

    [TestMethod]
    public async Task LocalPicture_IsNormalizedOnceToBoundedMetadataFreeJpeg()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sockseek-picture-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(path, Encode(1_000, 400, SKColors.Transparent, SKEncodedImageFormat.Png));

            UserPicture picture = await UserPictureCodec.LoadAndNormalizeLocalAsync(path);

            Assert.AreEqual("image/jpeg", picture.MediaType);
            Assert.IsTrue(picture.Bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff }));
            Assert.AreEqual(512, picture.Width);
            Assert.AreEqual(205, picture.Height);
            using var decoded = SKBitmap.Decode(picture.Bytes);
            Assert.AreEqual(SKColors.White, decoded.GetPixel(0, 0));
            Assert.IsTrue(picture.ETag.StartsWith('"') && picture.ETag.EndsWith('"'));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task LocalProfile_MissingPictureDoesNotPreventStartup()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-missing-picture-{Guid.NewGuid():N}.png");
        var settings = new EngineSettings
        {
            UserDescription = "still advertised",
            UserPicturePath = path,
        };

        LocalUserProfile profile = await LocalUserProfile.LoadAsync(settings);

        Assert.AreEqual("still advertised", profile.Description);
        Assert.IsNull(profile.Picture);
    }

    [TestMethod]
    [DataRow("010203")]
    [DataRow("49492B000800000010000000000000000100000000000000")]
    public async Task LocalProfile_CorruptPictureDoesNotPreventStartup(string pictureHex)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-corrupt-picture-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(path, Convert.FromHexString(pictureHex));
            var settings = new EngineSettings
            {
                UserDescription = "still advertised",
                UserPicturePath = path,
            };

            LocalUserProfile profile = await LocalUserProfile.LoadAsync(settings);

            Assert.AreEqual("still advertised", profile.Description);
            Assert.IsNull(profile.Picture);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [DataRow("jpeg", "image/jpeg")]
    [DataRow("png", "image/png")]
    [DataRow("gif", "image/gif")]
    [DataRow("webp", "image/webp")]
    public async Task RemotePicture_ReportsDetectedFormatAndRetainsValidatedBytes(
        string extension, string mediaType)
    {
        // Two-frame GIF: preserving the original bytes also preserves browser animation.
        byte[] bytes = extension == "gif"
            ? Convert.FromHexString("47494638396101000100800000000000FFFFFF21F90400010000002C000000000100010000020244010021F90400010000002C00000000010001000002024C01003B")
            : Encode(32, 24, SKColors.CornflowerBlue, Enum.Parse<SKEncodedImageFormat>(extension, ignoreCase: true));

        UserPicture picture = await UserPictureCodec.ValidateRemoteAsync(bytes);

        Assert.AreEqual(mediaType, picture.MediaType);
        Assert.AreEqual(extension == "gif" ? 1 : 32, picture.Width);
        Assert.AreEqual(extension == "gif" ? 1 : 24, picture.Height);
        CollectionAssert.AreEqual(bytes, picture.Bytes);
    }

    [TestMethod]
    public async Task RemotePicture_RejectsOversizedAndNonAllowlistedInput()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UserPictureCodec.ValidateRemoteAsync(
                new byte[UserPictureCodec.MaximumInputBytes + 1]));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UserPictureCodec.ValidateRemoteAsync(Convert.FromHexString("49492A0008000000000000000000")));

        // Keep the truncated entry count small so a regression cannot hang CI.
        // Format detection must reject it before any TIFF metadata parsing.
        byte[] bigTiff = new byte[24];
        bigTiff[0] = bigTiff[1] = 0x49;
        bigTiff[2] = 0x2b;
        bigTiff[4] = 8;
        BinaryPrimitives.WriteUInt64LittleEndian(bigTiff.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt64LittleEndian(bigTiff.AsSpan(16), 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UserPictureCodec.ValidateRemoteAsync(bigTiff));
    }

    [TestMethod]
    public async Task RemotePicture_RejectsDimensionsBeyondDecoderSafetyBoundary()
    {
        byte[] png = Encode(UserPictureCodec.MaximumDimension + 1, 1, SKColors.Black, SKEncodedImageFormat.Png);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UserPictureCodec.ValidateRemoteAsync(png));
    }

    [TestMethod]
    [DataRow(1, 255, 0, 0)]
    [DataRow(2, 0, 128, 0)]
    [DataRow(3, 255, 255, 0)]
    [DataRow(4, 0, 0, 255)]
    [DataRow(5, 255, 0, 0)]
    [DataRow(6, 0, 0, 255)]
    [DataRow(7, 255, 255, 0)]
    [DataRow(8, 0, 128, 0)]
    public async Task LocalPicture_AppliesExifOrientationAndRemovesMetadata(int orientation, int red, int green, int blue)
    {
        using var bitmap = new SKBitmap(80, 40);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            foreach (var (x, y, color) in new[] { (0, 0, SKColors.Red), (40, 0, SKColors.Green),
                (0, 20, SKColors.Blue), (40, 20, SKColors.Yellow) })
            {
                paint.Color = color;
                canvas.DrawRect(x, y, 40, 20, paint);
            }
        }
        using var source = SKImage.FromBitmap(bitmap);
        using var jpeg = source.Encode(SKEncodedImageFormat.Jpeg, 100);
        byte[] bytes = jpeg.ToArray();
        // A single EXIF orientation entry in an APP1 segment, independent of the decoder.
        byte[] exif = Convert.FromHexString("FFE1002245786966000049492A0008000000010012010300010000000100000000000000");
        exif[28] = (byte)orientation;
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [.. bytes.AsSpan(0, 2), .. exif, .. bytes.AsSpan(2)]);
            var normalized = await UserPictureCodec.LoadAndNormalizeLocalAsync(path);
            Assert.AreEqual(orientation >= 5 ? 40 : 80, normalized.Width);
            Assert.AreEqual(orientation >= 5 ? 80 : 40, normalized.Height);
            var remote = await UserPictureCodec.ValidateRemoteAsync(await File.ReadAllBytesAsync(path));
            using var terminal = UserPictureCodec.DecodeForDisplay(remote, 32, 512);
            Assert.AreEqual(32, terminal.Width);
            Assert.AreEqual(orientation >= 5 ? 64 : 16, terminal.Height);
            Assert.IsFalse(normalized.Bytes.AsSpan().IndexOf("Exif"u8) >= 0);
            using var decoded = SKBitmap.Decode(normalized.Bytes);
            var corner = decoded.GetPixel(5, 5);
            Assert.IsTrue(Math.Abs(corner.Red - red) < 20 && Math.Abs(corner.Green - green) < 20
                && Math.Abs(corner.Blue - blue) < 20);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task RemotePicture_RejectsTruncationAndHonorsCancellation()
    {
        byte[] png = Encode(32, 24, SKColors.Red, SKEncodedImageFormat.Png);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UserPictureCodec.ValidateRemoteAsync(png.AsMemory(0, png.Length / 2)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            UserPictureCodec.ValidateRemoteAsync(png, cancelled.Token));
    }

    private static byte[] Encode(int width, int height, SKColor color, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        return data.ToArray();
    }
}
