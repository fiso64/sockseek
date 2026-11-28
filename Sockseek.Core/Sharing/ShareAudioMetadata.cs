using System.Buffers.Binary;
using Soulseek;
using TagLib.Mpeg4;

namespace Sockseek.Core.Sharing;

internal static class ShareAudioMetadata
{
    public static IReadOnlyList<ShareFileAttribute> Read(string name, Stream stream)
    {
        string extension = Path.GetExtension(name);
        stream.Position = 0;
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            && ReadRf64(stream) is { } wave) return Attributes(wave, stream, null);
        stream.Position = 0;
        var source = new ReadOnlyFileAbstraction(name, stream);
        if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new Mpeg4PropertyReader(source);
            try
            {
                if (reader.Properties is { } audio) return Attributes(audio, stream, null);
            }
            catch (TagLib.CorruptFileException) { /* Let the normal reader try the file. */ }
            stream.Position = 0;
        }
        using var file = TagLib.File.Create(source, Utils.AudioMetadataReadStyle);
        return Attributes(file.Properties, stream, file);
    }

    private static IReadOnlyList<ShareFileAttribute> Attributes(TagLib.Properties? properties, Stream stream, TagLib.File? file)
    {
        if (properties is null)
            return [];

        int depth = properties.BitsPerSample;
        bool? variable = null;
        bool useStreamAverages = true;
        foreach (var codec in properties.Codecs)
        {
            switch (codec)
            {
                case TagLib.Mpeg.AudioHeader mpeg when file is not null:
                    (variable, useStreamAverages) = MpegBitRate(mpeg, stream, file);
                    break;
                case TagLib.Riff.WaveFormatEx { FormatTag: 1 or 3 }: // PCM or IEEE float
                    variable = false;
                    break;
                case TagLib.Mpeg4.IsoAudioSampleEntry audio when depth == 0 && audio.BoxType == "alac":
                    depth = audio.AudioSampleSize;
                    break;
            }
        }

        var attributes = new List<ShareFileAttribute>(5);
        if (useStreamAverages && properties.AudioBitrate > 0)
            attributes.Add(new((int)FileAttributeType.BitRate, properties.AudioBitrate));
        if (useStreamAverages && properties.Duration > TimeSpan.Zero)
            attributes.Add(new((int)FileAttributeType.Length, checked((int)properties.Duration.TotalSeconds)));
        if (variable.HasValue)
            attributes.Add(new((int)FileAttributeType.VariableBitRate, variable.Value ? 1 : 0));
        if (properties.AudioSampleRate > 0)
            attributes.Add(new((int)FileAttributeType.SampleRate, properties.AudioSampleRate));
        if (depth > 0)
            attributes.Add(new((int)FileAttributeType.BitDepth, depth));
        return attributes;
    }

    private static (bool? Variable, bool UseStreamAverages) MpegBitRate(
        TagLib.Mpeg.AudioHeader mpeg, Stream stream, TagLib.File file)
    {
        if (mpeg.XingHeader.Present || mpeg.VBRIHeader.Present) return (true, true);
        long audioStart = file.InvariantStartPosition;
        if (mpeg.AudioLayer != 3 || audioStart < 0) return (null, true);

        // Info is the constant-rate counterpart of a Xing header.
        int offset = TagLib.Mpeg.XingHeader.XingHeaderOffset(mpeg.Version, mpeg.ChannelMode);
        Span<byte> header = stackalloc byte[4];
        if (audioStart > stream.Length - offset - header.Length) return (null, true);
        stream.Position = audioStart + offset;
        stream.ReadExactly(header);
        if (header.SequenceEqual("Info"u8)) return (false, true);

        // With no stream header TagLib estimates bitrate and duration from frame one.
        // A later frame with a different bitrate disproves both estimates.
        long followingFrame = audioStart + mpeg.AudioFrameLength;
        if (mpeg.AudioFrameLength > 0 && followingFrame < stream.Length
            && TagLib.Mpeg.AudioHeader.Find(out var next, file, followingFrame, 4096)
            && next.Version == mpeg.Version && next.AudioLayer == mpeg.AudioLayer
            && next.AudioSampleRate == mpeg.AudioSampleRate && next.AudioBitrate != mpeg.AudioBitrate)
            return (true, false);
        return (null, true);
    }

    // EBU Tech 3306 puts the 64-bit data size and sample count in the first ds64 chunk.
    // Read headers and seek past ancillary chunks without reading the recording itself.
    private static TagLib.Properties? ReadRf64(Stream stream)
    {
        Span<byte> header = stackalloc byte[48];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length
            || !header[..4].SequenceEqual("RF64"u8) || !header[8..16].SequenceEqual("WAVEds64"u8))
            return null;
        uint descriptorSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        ulong dataSize = BinaryPrimitives.ReadUInt64LittleEndian(header[28..]);
        ulong samples = BinaryPrimitives.ReadUInt64LittleEndian(header[36..]);
        if (descriptorSize < 28) return null;
        stream.Position = 20L + descriptorSize + (descriptorSize & 1);
        TagLib.Riff.WaveFormatEx? format = null;
        while (stream.Position <= stream.Length - 8)
        {
            var chunk = header[..8];
            stream.ReadExactly(chunk);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("data"u8))
            {
                if (format is not { } wave || wave.AudioSampleRate <= 0 || wave.AverageBytesPerSecond <= 0)
                    return null;
                ulong bytes = size == uint.MaxValue ? dataSize : size;
                if (bytes > (ulong)(stream.Length - stream.Position)) return null;
                double seconds = samples > 0 ? (double)samples / wave.AudioSampleRate : (double)bytes / wave.AverageBytesPerSecond;
                return new TagLib.Properties(TimeSpan.FromSeconds(seconds), wave);
            }
            if (size == uint.MaxValue || size > stream.Length - stream.Position) return null;
            long next = stream.Position + size + (size & 1);
            if (chunk[..4].SequenceEqual("fmt "u8) && size >= 16)
            {
                var waveFormat = new byte[16];
                stream.ReadExactly(waveFormat);
                format = new TagLib.Riff.WaveFormatEx(waveFormat, 0);
            }
            stream.Position = next;
        }
        return null;
    }

    // The ordinary MPEG-4 tag reader loads the entire cover image even with PictureLazy.
    // Shares needs only the movie duration and the first audio sample description.
    private sealed class Mpeg4PropertyReader(TagLib.File.IFileAbstraction source) : TagLib.File(source)
    {
        public override TagLib.Properties? Properties => ReadProperties();

        private TagLib.Properties? ReadProperties()
        {
            Mode = AccessMode.Read;
            var movie = Boxes(0, Length).FirstOrDefault(box => box.BoxType == "moov");
            IsoMovieHeaderBox? header = null;
            IsoAudioSampleEntry? audio = null;
            foreach (var box in Children(movie))
            {
                if (box.BoxType == "mvhd" && header == null)
                    header = new IsoMovieHeaderBox(box, this, null);
                else if (box.BoxType == "trak" && audio == null)
                    audio = ReadAudioTrack(box);
                if (header != null && audio != null) return new(header.Duration, audio);
            }
            return null;
        }

        private IsoAudioSampleEntry? ReadAudioTrack(BoxHeader track)
        {
            var media = Child(track, "mdia");
            var handler = Child(media, "hdlr");
            if (handler.TotalBoxSize == 0) return null;
            var kind = new IsoHandlerBox(handler, this, null);
            if (kind.HandlerType != "soun") return null;
            var table = Child(Child(media, "minf"), "stbl");
            var description = Child(table, "stsd");
            return description.TotalBoxSize == 0 ? null : new IsoSampleDescriptionBox(description, this, kind)
                .Children.OfType<IsoAudioSampleEntry>().FirstOrDefault();
        }

        private BoxHeader Child(BoxHeader parent, string type)
            => Children(parent).FirstOrDefault(box => box.BoxType == type);

        private IEnumerable<BoxHeader> Children(BoxHeader parent)
            => Boxes(parent.Position + parent.HeaderSize, parent.Position + parent.TotalBoxSize);

        private IEnumerable<BoxHeader> Boxes(long start, long end)
        {
            for (long position = start; position <= end - 8;)
            {
                var box = new BoxHeader(this, position);
                if (box.TotalBoxSize < box.HeaderSize || box.TotalBoxSize > end - position)
                    throw new TagLib.CorruptFileException("Invalid MPEG-4 box size.");
                yield return box;
                position += box.TotalBoxSize;
            }
        }

        public override TagLib.Tag Tag => throw new NotSupportedException();
        public override TagLib.Tag GetTag(TagLib.TagTypes type, bool create) => throw new NotSupportedException();
        public override void RemoveTags(TagLib.TagTypes types) => throw new NotSupportedException();
        public override void Save() => throw new NotSupportedException();
    }

    private sealed class ReadOnlyFileAbstraction(
        string name,
        Stream stream) : TagLib.File.IFileAbstraction
    {
        public string Name { get; } = name;
        public Stream ReadStream => stream;
        public Stream WriteStream => throw new NotSupportedException();
        public void CloseStream(Stream _) { }
    }
}
