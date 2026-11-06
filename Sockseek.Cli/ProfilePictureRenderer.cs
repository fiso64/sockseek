using System.Diagnostics;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
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
        int pixelWidth = Math.Min(validated.Width, requestedWidth);
        int pixelHeight = Math.Max(
            1,
            (int)Math.Round(validated.Height * (pixelWidth / (double)validated.Width)));
        pixelHeight = Math.Min(UserPictureCodec.LocalOutputDimension, pixelHeight);

        var decoderOptions = new DecoderOptions
        {
            MaxFrames = 1,
            SkipMetadata = true,
            TargetSize = new Size(pixelWidth, pixelHeight),
        };
        using var decodeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        decodeTimeout.CancelAfter(UserPictureCodec.MaximumWorkDuration);
        await using var input = new MemoryStream(validated.Bytes, writable: false);
        using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(
            decoderOptions,
            input,
            decodeTimeout.Token).ConfigureAwait(false);
        image.Mutate(context =>
        {
            context.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(pixelWidth, pixelHeight),
                Sampler = KnownResamplers.Bicubic,
            });
        });

        return selectedMode == "sixel" ? RenderSixel(image) : RenderPixels(image);
    }

    internal static string RenderPixels(Image<Rgba32> image)
    {
        var output = new StringBuilder((image.Width * 32 + 5) * ((image.Height + 1) / 2));
        for (int y = 0; y < image.Height; y += 2)
        {
            Span<Rgba32> top = image.DangerousGetPixelRowMemory(y).Span;
            Span<Rgba32> bottom = y + 1 < image.Height
                ? image.DangerousGetPixelRowMemory(y + 1).Span
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

    internal static string RenderSixel(Image<Rgba32> source)
    {
        using Image<Rgba32> image = source.Clone();
        image.Mutate(context => context.Quantize(new WuQuantizer(new QuantizerOptions
        {
            MaxColors = MaximumSixelColors,
            Dither = null,
        })));

        var palette = new Dictionary<Rgba32, int>();
        for (int y = 0; y < image.Height; y++)
        {
            Span<Rgba32> row = image.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < row.Length; x++)
            {
                Rgba32 color = row[x].A < 128 ? default : Opaque(row[x]);
                row[x] = color;
                if (color.A == 0)
                    continue;
                if (!palette.ContainsKey(color))
                    palette[color] = palette.Count;
            }
        }

        var output = new StringBuilder(Math.Max(256, image.Width * image.Height * 2));
        output.Append("\u001bPq\"").Append("1;1;").Append(image.Width).Append(';').Append(image.Height);
        foreach ((Rgba32 color, int index) in palette.OrderBy(pair => pair.Value))
        {
            output.Append('#').Append(index).Append(";2;")
                .Append(Percent(color.R)).Append(';')
                .Append(Percent(color.G)).Append(';')
                .Append(Percent(color.B));
        }

        int bandCount = (image.Height + 5) / 6;
        for (int band = 0; band < bandCount; band++)
        {
            bool wroteColor = false;
            foreach ((Rgba32 color, int index) in palette.OrderBy(pair => pair.Value))
            {
                char[] columns = new char[image.Width];
                int lastNonEmpty = -1;
                for (int x = 0; x < image.Width; x++)
                {
                    int bits = 0;
                    for (int bit = 0; bit < 6; bit++)
                    {
                        int y = band * 6 + bit;
                        if (y < image.Height && image[x, y].Equals(color))
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

    private static void AppendColor(StringBuilder output, Rgba32 color, bool foreground)
    {
        if (color.A < 128)
        {
            output.Append(foreground ? "\u001b[39m" : "\u001b[49m");
            return;
        }
        output.Append(foreground ? "\u001b[38;2;" : "\u001b[48;2;")
            .Append(color.R).Append(';').Append(color.G).Append(';').Append(color.B).Append('m');
    }

    private static Rgba32 Opaque(Rgba32 value)
    {
        if (value.A == byte.MaxValue)
            return value;
        int inverse = byte.MaxValue - value.A;
        return new Rgba32(
            (byte)((value.R * value.A + 255 * inverse) / 255),
            (byte)((value.G * value.A + 255 * inverse) / 255),
            (byte)((value.B * value.A + 255 * inverse) / 255),
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
