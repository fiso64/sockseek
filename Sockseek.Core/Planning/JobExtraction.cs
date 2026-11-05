using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Planning;

/// <summary>Shared extraction and Job preparation for Start, Review and local CLI.</summary>
public static class JobExtraction
{
    public static async Task<JobExtractionResult> ExtractAsync(ExtractJob source,
        ExtractorContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Config is null) throw new InvalidOperationException("Resolve settings before extraction.");
        var (inputType, extractor) = ExtractorRegistry.GetMatchingExtractor(
            source.Input, source.InputType ?? InputType.None, source.Config);
        source.InputType = inputType;
        source.Config.Extraction.InputType = inputType;
        source.SourceInfo = extractor.DescribeSource(source, source) ?? source.SourceInfo;

        ExtractionSettings settings = source.Config.Extraction;
        if (source.RequestedModeOverride is { } requested)
        {
            settings = SettingsCloner.Clone(settings);
            settings.RequestedMode = requested;
        }
        cancellationToken.ThrowIfCancellationRequested();
        Job result = await extractor.GetTracks(source.Input, settings, context ?? ExtractorContext.None)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(Normalize(source, result, settings.UpgradeToAlbum, extractor), inputType, extractor);
    }

    public static Job Normalize(ExtractJob source, Job result, bool upgradeToAlbum, IExtractor extractor)
    {
        bool collectionOwnsPositions = result is JobList;
        bool enablesIndex = source.EnablesIndexByDefault || result.EnablesIndexByDefault;
        InputType? collectionType = source.SourceCollectionInputType ?? result.SourceCollectionInputType;
        if (result is IUpgradeable upgradeable)
        {
            List<Job> upgraded = upgradeable.Upgrade(upgradeToAlbum, source.Config.Search.IsAggregate).ToList();
            if (upgraded.Count == 1) result = upgraded[0];
            else
            {
                var list = new JobList(result.ItemName, upgraded);
                list.CopySharedFieldsFrom(result);
                result = list;
            }
        }
        source.Result = result;
        source.SourceInfo = extractor.DescribeSource(source, source) ?? source.SourceInfo;
        if (result.LineNumber == 0) result.LineNumber = source.LineNumber;
        if (!collectionOwnsPositions || result is JobList) result.ItemNumber = source.ItemNumber;
        result.SourceMutation ??= source.SourceMutation;
        result.EnablesIndexByDefault = enablesIndex;
        result.ExtractorCond = FileConditionPatch.Merge(result.ExtractorCond, source.ExtractorCond);
        result.ExtractorPrefCond = FileConditionPatch.Merge(result.ExtractorPrefCond, source.ExtractorPrefCond);
        result.ExtractorFolderCond = FolderConditionPatch.Merge(result.ExtractorFolderCond, source.ExtractorFolderCond);
        result.ExtractorPrefFolderCond = FolderConditionPatch.Merge(result.ExtractorPrefFolderCond, source.ExtractorPrefFolderCond);
        Apply(result);
        SubmissionIdentity.AssignGeneratedResult(source, result);
        return result;

        void Apply(Job item)
        {
            item.WorkflowId = source.WorkflowId;
            item.SourceInputType = source.InputType;
            if (collectionType is not null) item.SourceCollectionInputType = collectionType;
            item.ArtifactId = source.ArtifactId;
            if (source.ArtifactId is not null) item.SourceMutation = null;
            if (source.ResultDownloadBehaviorPolicy is { } behavior)
            {
                item.DownloadBehaviorPolicy = behavior;
                if (item is ExtractJob extract)
                {
                    extract.ResultDownloadBehaviorPolicy = behavior;
                    if (extract.Result is { } nestedResult)
                        JobOrchestrator.ApplyDownloadBehaviorPolicy(nestedResult, behavior);
                }
            }
            item.SourceInfo = extractor.DescribeSource(source, item) ?? item.SourceInfo;
            switch (item)
            {
                case JobList list:
                    foreach (Job child in list.Jobs) Apply(child);
                    break;
                case AggregateJob aggregate:
                    foreach (SongJob child in aggregate.Songs) Apply(child);
                    break;
                case AlbumAggregateJob aggregate:
                    foreach (AlbumJob child in aggregate.Albums) Apply(child);
                    break;
            }
        }
    }
}

public sealed record JobExtractionResult(Job Result, InputType InputType, IExtractor Extractor);
