namespace Sockseek.Core.Models;

public readonly record struct AlbumAudioQualityCoverage(
    int AudioFileCount,
    AlbumQualityCoverageBucket Format,
    AlbumQualityCoverageBucket Bitrate,
    AlbumQualityCoverageBucket SampleRate,
    AlbumQualityCoverageBucket BitDepth)
{
    public static AlbumAudioQualityCoverage Inactive(int audioFileCount)
        => new(
            audioFileCount,
            AlbumQualityCoverageBucket.Inactive(audioFileCount),
            AlbumQualityCoverageBucket.Inactive(audioFileCount),
            AlbumQualityCoverageBucket.Inactive(audioFileCount),
            AlbumQualityCoverageBucket.Inactive(audioFileCount));

    public int MatchingFileCount => new[] { Format, Bitrate, SampleRate, BitDepth }
        .Where(bucket => bucket.IsActive)
        .Select(bucket => bucket.MatchingFileCount)
        .DefaultIfEmpty(AudioFileCount)
        .Min();

    public bool IsActive => Format.IsActive || Bitrate.IsActive || SampleRate.IsActive || BitDepth.IsActive;

    public bool IsAcceptable(bool strict)
    {
        if (!IsActive)
            return true;

        return Format.IsAcceptable(strict)
            && Bitrate.IsAcceptable(strict)
            && SampleRate.IsAcceptable(strict)
            && BitDepth.IsAcceptable(strict);
    }
}

public readonly record struct AlbumQualityCoverageBucket(
    bool IsActive,
    int AudioFileCount,
    int MatchingFileCount)
{
    public static AlbumQualityCoverageBucket Inactive(int audioFileCount)
        => new(false, audioFileCount, audioFileCount);

    public double Ratio => AudioFileCount == 0 ? 0 : MatchingFileCount / (double)AudioFileCount;

    public int Bucket
    {
        get
        {
            if (!IsActive)
                return 4;
            if (AudioFileCount == 0 || MatchingFileCount == 0)
                return 0;
            if (MatchingFileCount == AudioFileCount)
                return 4;
            double ratio = Ratio;
            if (ratio >= 2.0 / 3.0)
                return 3;
            return ratio >= 1.0 / 3.0 ? 2 : 1;
        }
    }

    public bool IsAcceptable(bool strict)
        => !IsActive
            || (strict ? MatchingFileCount == AudioFileCount && AudioFileCount > 0 : MatchingFileCount > 0);
}
