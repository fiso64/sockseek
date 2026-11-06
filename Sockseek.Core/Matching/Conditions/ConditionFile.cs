namespace Sockseek.Core.Models;

internal readonly record struct ConditionFile(
    string Path,
    int? Length,
    int? Bitrate,
    int? SampleRate,
    int? BitDepth)
{
    public static ConditionFile From(Soulseek.File file)
        => new(file.Filename, file.Length, file.BitRate, file.SampleRate, file.BitDepth);

    public static ConditionFile From(FileCandidate file)
        => new(file.Filename, file.Length, file.BitRate, file.SampleRate, file.BitDepth);

    public static ConditionFile From(TagLib.File file)
        => new(
            file.Name,
            (int)file.Properties.Duration.TotalSeconds,
            file.Properties.AudioBitrate,
            file.Properties.AudioSampleRate,
            file.Properties.BitsPerSample);

    public static ConditionFile From(SimpleFile file)
        => new(file.Path, file.Length, file.Bitrate, file.Samplerate, file.Bitdepth);
}
