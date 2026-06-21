using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

internal static class AlbumQualityPolicy
{
    public static ActiveAudioQualityConditions ActiveConditions(FileConditions conditions)
        => new(
            conditions.HasActiveFormatCondition(),
            conditions.MinBitrate != null || conditions.MaxBitrate != null,
            conditions.MinSampleRate != null || conditions.MaxSampleRate != null,
            conditions.MinBitDepth != null || conditions.MaxBitDepth != null);

    public static AlbumAudioQualityCoverage Evaluate(
        IEnumerable<Soulseek.File> audioFiles,
        FileConditions conditions,
        ActiveAudioQualityConditions activeQuality)
    {
        int audioFileCount = 0;
        int formatMatchingFileCount = 0;
        int bitrateMatchingFileCount = 0;
        int sampleRateMatchingFileCount = 0;
        int bitDepthMatchingFileCount = 0;

        foreach (var file in audioFiles)
        {
            audioFileCount++;
            if (activeQuality.Format && conditions.FormatSatisfies(file.Filename))
                formatMatchingFileCount++;
            if (activeQuality.Bitrate && conditions.BitrateSatisfies(file))
                bitrateMatchingFileCount++;
            if (activeQuality.SampleRate && conditions.SampleRateSatisfies(file))
                sampleRateMatchingFileCount++;
            if (activeQuality.BitDepth && conditions.BitDepthSatisfies(file))
                bitDepthMatchingFileCount++;
        }

        if (!activeQuality.IsActive)
            return AlbumAudioQualityCoverage.Inactive(audioFileCount);

        return new AlbumAudioQualityCoverage(
            audioFileCount,
            CoverageBucket(activeQuality.Format, audioFileCount, formatMatchingFileCount),
            CoverageBucket(activeQuality.Bitrate, audioFileCount, bitrateMatchingFileCount),
            CoverageBucket(activeQuality.SampleRate, audioFileCount, sampleRateMatchingFileCount),
            CoverageBucket(activeQuality.BitDepth, audioFileCount, bitDepthMatchingFileCount));
    }

    public static AlbumAudioQualityCoverage Evaluate(
        AlbumFolder folder,
        FileConditions conditions,
        ActiveAudioQualityConditions activeQuality)
        => Evaluate(
            folder.Files
                .Where(song => !song.IsNotAudio && song.ResolvedTarget != null)
                .Select(song => song.ResolvedTarget!.File),
            conditions,
            activeQuality);

    private static AlbumQualityCoverageBucket CoverageBucket(bool isActive, int audioFileCount, int matchingFileCount)
        => isActive
            ? new AlbumQualityCoverageBucket(true, audioFileCount, matchingFileCount)
            : AlbumQualityCoverageBucket.Inactive(audioFileCount);
}

internal readonly record struct ActiveAudioQualityConditions(
    bool Format,
    bool Bitrate,
    bool SampleRate,
    bool BitDepth)
{
    public bool IsActive => Format || Bitrate || SampleRate || BitDepth;
}
