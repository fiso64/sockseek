using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
            using (var source = new Image<Rgba32>(1_000, 400, Color.Transparent))
                await source.SaveAsPngAsync(path);

            UserPicture picture = await UserPictureCodec.LoadAndNormalizeLocalAsync(path);

            Assert.AreEqual("image/jpeg", picture.MediaType);
            Assert.IsTrue(picture.Bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff }));
            Assert.AreEqual(512, picture.Width);
            Assert.AreEqual(205, picture.Height);
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
        byte[] bytes;
        using (var source = new Image<Rgba32>(32, 24, Color.CornflowerBlue))
        await using (var output = new MemoryStream())
        {
            Assert.IsTrue(Configuration.Default.ImageFormatsManager.TryFindFormatByFileExtension(
                extension, out var format));
            Assert.IsNotNull(format);
            await source.SaveAsync(output, format);
            bytes = output.ToArray();
        }

        UserPicture picture = await UserPictureCodec.ValidateRemoteAsync(bytes);

        Assert.AreEqual(mediaType, picture.MediaType);
        Assert.AreEqual(32, picture.Width);
        Assert.AreEqual(24, picture.Height);
        CollectionAssert.AreEqual(bytes, picture.Bytes);
    }

    [TestMethod]
    public async Task RemotePicture_RejectsOversizedAndNonAllowlistedInput()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UserPictureCodec.ValidateRemoteAsync(
                new byte[UserPictureCodec.MaximumInputBytes + 1]));

        byte[] tiff;
        using (var source = new Image<Rgba32>(4, 4, Color.Black))
        await using (var output = new MemoryStream())
        {
            await source.SaveAsTiffAsync(output);
            tiff = output.ToArray();
        }

        var unsupported = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UserPictureCodec.ValidateRemoteAsync(tiff));
        Assert.IsInstanceOfType<UnknownImageFormatException>(unsupported.InnerException);

        // Keep the truncated entry count small so a regression cannot hang CI.
        // Format detection must reject it before any TIFF metadata parsing.
        byte[] bigTiff = new byte[24];
        bigTiff[0] = bigTiff[1] = 0x49;
        bigTiff[2] = 0x2b;
        bigTiff[4] = 8;
        BinaryPrimitives.WriteUInt64LittleEndian(bigTiff.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt64LittleEndian(bigTiff.AsSpan(16), 1);
        unsupported = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UserPictureCodec.ValidateRemoteAsync(bigTiff));
        Assert.IsInstanceOfType<UnknownImageFormatException>(unsupported.InnerException);
    }

    [TestMethod]
    public async Task RemotePicture_RejectsDimensionsBeyondDecoderSafetyBoundary()
    {
        byte[] png;
        using (var source = new Image<Rgba32>(UserPictureCodec.MaximumDimension + 1, 1, Color.Black))
        await using (var output = new MemoryStream())
        {
            await source.SaveAsPngAsync(output);
            png = output.ToArray();
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UserPictureCodec.ValidateRemoteAsync(png));
    }
}
