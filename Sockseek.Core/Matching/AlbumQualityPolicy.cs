using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

internal static class AlbumQualityPolicy
{
    // In album mode, Format means the acceptable format of primary audio tracks.
    // Selecting ancillary album files by extension is a separate product concern and
    // must not be implemented by broadening this policy to PDFs, logs, or images.
    public static ActiveAlbumTrackConditions ActiveConditions(FileConditions conditions)
        => new(
            conditions.HasActiveFormatCondition(),
            conditions.MinBitrate != null || conditions.MaxBitrate != null,
            conditions.MinSampleRate != null || conditions.MaxSampleRate != null,
            conditions.MinBitDepth != null || conditions.MaxBitDepth != null);

    public static AlbumAudioQualityCoverage Evaluate(
        IEnumerable<Soulseek.File> audioFiles,
        FileConditions conditions,
        ActiveAlbumTrackConditions activeQuality)
        => Evaluate(
            audioFiles.Select(ConditionFile.From),
            conditions,
            activeQuality);

    public static AlbumAudioQualityCoverage Evaluate(
        IEnumerable<ConditionFile> audioFiles,
        FileConditions conditions,
        ActiveAlbumTrackConditions activeQuality)
    {
        int audioFileCount = 0;
        int formatMatchingFileCount = 0;
        int bitrateMatchingFileCount = 0;
        int sampleRateMatchingFileCount = 0;
        int bitDepthMatchingFileCount = 0;

        foreach (var file in audioFiles)
        {
            audioFileCount++;
            if (activeQuality.Format && conditions.FormatSatisfies(file.Path))
                formatMatchingFileCount++;
            if (activeQuality.Bitrate && conditions.BitrateSatisfies(file.Bitrate))
                bitrateMatchingFileCount++;
            if (activeQuality.SampleRate && conditions.SampleRateSatisfies(file.SampleRate))
                sampleRateMatchingFileCount++;
            if (activeQuality.BitDepth && conditions.BitDepthSatisfies(file.BitDepth))
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
        ActiveAlbumTrackConditions activeQuality)
        => Evaluate(
            folder.Files
                .Where(file => !file.IsNotAudio)
                .Select(file => ConditionFile.From(file.Candidate)),
            conditions,
            activeQuality);

    private static AlbumQualityCoverageBucket CoverageBucket(bool isActive, int audioFileCount, int matchingFileCount)
        => isActive
            ? new AlbumQualityCoverageBucket(true, audioFileCount, matchingFileCount)
            : AlbumQualityCoverageBucket.Inactive(audioFileCount);
}

internal readonly record struct ActiveAlbumTrackConditions(
    bool Format,
    bool Bitrate,
    bool SampleRate,
    bool BitDepth)
{
    public bool IsActive => Format || Bitrate || SampleRate || BitDepth;
}
