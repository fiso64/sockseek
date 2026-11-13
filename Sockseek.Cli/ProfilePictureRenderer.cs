using System.Diagnostics;
using System.Text;
using SkiaSharp;
using KGySoft.Drawing.Imaging;
using Sockseek.Api;
using Sockseek.Core.UserProfiles;

namespace Sockseek.Cli;

/// <summary>
/// Bounded terminal rendering for already validated daemon profile pictures.
/// All escape sequences are generated locally; peer bytes are only image input.
/// </summary>
internal static class ProfilePictureRenderer
{
    private const int CellWidth = 32;
    private const int EstimatedSixelPixelsPerCell = 8;
    private const int MaximumSixelColors = 64;
    private static readonly TimeSpan SixelProbeTimeout = TimeSpan.FromMilliseconds(200);

    public static void ValidateMode(string mode)
    {
        if (mode is not ("auto" or "sixel" or "pixels" or "none"))
            throw new ArgumentException("--picture must be auto, sixel, pixels, or none.");
    }

    public static async Task<string> RenderAsync(
        UserPictureResponse response,
        string mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ValidateMode(mode);
        if (mode == "none" || response.NotModified)
            return "";
        if (response.ContentLength is > UserPictureCodec.MaximumInputBytes)
            throw new InvalidDataException("The profile picture exceeds the terminal decoder limit.");

        await using Stream body = await response.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(body, cancellationToken).ConfigureAwait(false);
        UserPicture validated = await UserPictureCodec.ValidateRemoteAsync(bytes, cancellationToken)
            .ConfigureAwait(false);

        string selectedMode = mode == "auto"
            ? TerminalSupportsSixel() ? "sixel" : "pixels"
            : mode;
        int requestedWidth = selectedMode == "sixel"
            ? Math.Min(UserPictureCodec.LocalOutputDimension, CellWidth * EstimatedSixelPixelsPerCell)
            : CellWidth;
        using var image = UserPictureCodec.DecodeForDisplay(
            validated, requestedWidth, UserPictureCodec.LocalOutputDimension, cancellationToken);

        return selectedMode == "sixel" ? RenderSixel(image) : RenderPixels(image);
    }

    internal static string RenderPixels(SKBitmap image)
    {
        var output = new StringBuilder((image.Width * 32 + 5) * ((image.Height + 1) / 2));
        SKColor[] pixels = image.Pixels;
        for (int y = 0; y < image.Height; y += 2)
        {
            Span<SKColor> top = pixels.AsSpan(y * image.Width, image.Width);
            Span<SKColor> bottom = y + 1 < image.Height
                ? pixels.AsSpan((y + 1) * image.Width, image.Width)
                : top;
            for (int x = 0; x < image.Width; x++)
            {
                AppendColor(output, top[x], foreground: true);
                AppendColor(output, y + 1 < image.Height ? bottom[x] : default, foreground: false);
                output.Append('▀');
            }
            output.Append("\u001b[0m\n");
        }
        return output.ToString();
    }

    internal static string RenderSixel(SKBitmap source)
    {
        using SKBitmap image = source.Copy();
        using var bitmapData = BitmapDataFactory.CreateBitmapData(image.GetPixels(),
            new System.Drawing.Size(image.Width, image.Height), image.RowBytes, KnownPixelFormat.Format32bppArgb);
        bitmapData.Quantize(OptimizedPaletteQuantizer.Wu(MaximumSixelColors, new Color32(255, 255, 255)));
        SKColor[] pixels = image.Pixels;

        var palette = new Dictionary<SKColor, int>();
        for (int y = 0; y < image.Height; y++)
        {
            Span<SKColor> row = pixels.AsSpan(y * image.Width, image.Width);
            for (int x = 0; x < row.Length; x++)
            {
                SKColor color = row[x].Alpha < 128 ? default : Opaque(row[x]);
                row[x] = color;
                if (color.Alpha == 0)
                    continue;
                if (!palette.ContainsKey(color))
                    palette[color] = palette.Count;
            }
        }

        var output = new StringBuilder(Math.Max(256, image.Width * image.Height * 2));
        output.Append("\u001bPq\"").Append("1;1;").Append(image.Width).Append(';').Append(image.Height);
        foreach ((SKColor color, int index) in palette.OrderBy(pair => pair.Value))
        {
            output.Append('#').Append(index).Append(";2;")
                .Append(Percent(color.Red)).Append(';')
                .Append(Percent(color.Green)).Append(';')
                .Append(Percent(color.Blue));
        }

        int bandCount = (image.Height + 5) / 6;
        for (int band = 0; band < bandCount; band++)
        {
            bool wroteColor = false;
            foreach ((SKColor color, int index) in palette.OrderBy(pair => pair.Value))
            {
                char[] columns = new char[image.Width];
                int lastNonEmpty = -1;
                for (int x = 0; x < image.Width; x++)
                {
                    int bits = 0;
                    for (int bit = 0; bit < 6; bit++)
                    {
                        int y = band * 6 + bit;
                        if (y < image.Height && pixels[y * image.Width + x].Equals(color))
                            bits |= 1 << bit;
                    }
                    columns[x] = (char)(63 + bits);
                    if (bits != 0) lastNonEmpty = x;
                }
                if (lastNonEmpty < 0)
                    continue;
                if (wroteColor) output.Append('$');
                output.Append('#').Append(index);
                AppendRunLengthEncoded(output, columns.AsSpan(0, lastNonEmpty + 1));
                wroteColor = true;
            }
            if (band + 1 < bandCount) output.Append('-');
        }
        output.Append("\u001b\\");
        return output.ToString();
    }

    private static void AppendRunLengthEncoded(StringBuilder output, ReadOnlySpan<char> data)
    {
        for (int index = 0; index < data.Length;)
        {
            char value = data[index];
            int count = 1;
            while (index + count < data.Length && data[index + count] == value)
                count++;
            if (count >= 4)
                output.Append('!').Append(count).Append(value);
            else
                output.Append(value, count);
            index += count;
        }
    }

    private static int Percent(byte value) => (int)Math.Round(value * (100d / 255d));

    private static void AppendColor(StringBuilder output, SKColor color, bool foreground)
    {
        if (color.Alpha < 128)
        {
            output.Append(foreground ? "\u001b[39m" : "\u001b[49m");
            return;
        }
        output.Append(foreground ? "\u001b[38;2;" : "\u001b[48;2;")
            .Append(color.Red).Append(';').Append(color.Green).Append(';').Append(color.Blue).Append('m');
    }

    private static SKColor Opaque(SKColor value)
    {
        if (value.Alpha == byte.MaxValue)
            return value;
        int inverse = byte.MaxValue - value.Alpha;
        return new SKColor(
            (byte)((value.Red * value.Alpha + 255 * inverse) / 255),
            (byte)((value.Green * value.Alpha + 255 * inverse) / 255),
            (byte)((value.Blue * value.Alpha + 255 * inverse) / 255),
            byte.MaxValue);
    }

    private static bool TerminalSupportsSixel()
    {
        string capabilities = string.Join(' ',
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("TERM_PROGRAM"),
            Environment.GetEnvironmentVariable("DEC_TERMINAL_ID"));
        if (capabilities.Contains("sixel", StringComparison.OrdinalIgnoreCase)
            || capabilities.Contains("mlterm", StringComparison.OrdinalIgnoreCase)
            || capabilities.Contains("yaft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ProbePrimaryDeviceAttributes();
    }

    private static bool ProbePrimaryDeviceAttributes()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;

        try
        {
            // Do not consume input the user typed before the probe. A false negative
            // is preferable; --picture sixel remains the explicit override.
            if (Console.KeyAvailable)
                return false;

            Console.Out.Write("\u001b[c");
            Console.Out.Flush();

            var response = new StringBuilder();
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < SixelProbeTimeout)
            {
                while (Console.KeyAvailable)
                {
                    char value = Console.ReadKey(intercept: true).KeyChar;
                    response.Append(value);
                    if (value == 'c')
                        return PrimaryDeviceAttributesAdvertiseSixel(response.ToString());
                }
                Thread.Sleep(2);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Capability probing is best effort. The pixel renderer is always safe.
        }
        return false;
    }

    internal static bool PrimaryDeviceAttributesAdvertiseSixel(string response)
    {
        int start = response.IndexOf("\u001b[?", StringComparison.Ordinal);
        if (start < 0)
            return false;
        start += 3;
        int end = response.IndexOf('c', start);
        if (end < 0)
            return false;

        foreach (string attribute in response[start..end].Split(';'))
        {
            if (int.TryParse(attribute, out int value) && value == 4)
                return true;
        }
        return false;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > UserPictureCodec.MaximumInputBytes)
                throw new InvalidDataException("The profile picture exceeds the terminal decoder limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }
}
