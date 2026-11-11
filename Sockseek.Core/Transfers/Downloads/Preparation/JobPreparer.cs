using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

/// <summary>
/// Tree-walks the job tree and sets up per-job Config, DownloadOutputFiles, and TrackSkippers.
/// Config flows top-down: each job inherits its parent's config independently (siblings
/// do not accumulate — a profile applied to one job does not affect its siblings).
/// Returns a JobContext keyed by job ID for every job in the tree.
/// </summary>
public static class JobPreparer
{
    public static Dictionary<Guid, JobContext> PrepareJobs(
        JobList queue,
        DownloadSettings startConfig,
        IJobSettingsResolver? resolver = null)
    {
        var contexts = new Dictionary<Guid, JobContext>();
        var editors  = new Dictionary<(string path, OutputFileKind kind, bool readOnly), DownloadOutputFile>();
        var skippers = new Dictionary<(string dir, SkipMode mode, bool checkCond), TrackSkipper>();
        resolver ??= DefaultJobSettingsResolver.Instance;

        bool indexByDefault = HasDefaultIndex(queue);
        foreach (var job in queue.Jobs)
            PrepareJob(job, queue, indexByDefault, OutputScope.Empty, startConfig, contexts, editors, skippers, resolver,
                JobSettingsInheritance.None);

        return contexts;
    }

    // Prepares a single subtree rooted at `root`, using `parentConfig` as the starting config.
    // Returns only the newly created contexts (caller merges them into the main dict).
    public static Dictionary<Guid, JobContext> PrepareSubtree(
        Job root,
        DownloadSettings parentConfig,
        IJobSettingsResolver? resolver = null,
        JobList? explicitOwnerList = null,
        JobContext? parentCtx = null,
        bool rootSettingsAreFinal = false)
    {
        var newContexts = new Dictionary<Guid, JobContext>();
        var editors     = new Dictionary<(string path, OutputFileKind kind, bool readOnly), DownloadOutputFile>();
        var skippers    = new Dictionary<(string dir, SkipMode mode, bool checkCond), TrackSkipper>();
        resolver ??= DefaultJobSettingsResolver.Instance;

        if (parentCtx != null)
        {
            if (parentCtx.IndexEditor != null)
                editors[(parentCtx.IndexEditor.Path, parentCtx.IndexEditor.Kind, !parentCtx.IndexEditor.IsWritable)] = parentCtx.IndexEditor;
            if (parentCtx.PlaylistEditor != null)
                editors[(parentCtx.PlaylistEditor.Path, parentCtx.PlaylistEditor.Kind, !parentCtx.PlaylistEditor.IsWritable)] = parentCtx.PlaylistEditor;

            bool checkCond = parentConfig.Skip.SkipCheckCond || parentConfig.Skip.SkipCheckPrefCond;
            if (parentCtx.OutputDirSkipper != null && parentConfig.Output.ParentDir != null)
                skippers[(parentConfig.Output.ParentDir, parentConfig.Skip.SkipMode, checkCond)] = parentCtx.OutputDirSkipper;
            if (parentCtx.MusicDirSkipper != null && parentConfig.Skip.SkipMusicDir != null)
                skippers[(parentConfig.Skip.SkipMusicDir, parentConfig.Skip.SkipModeMusicDir, checkCond)] = parentCtx.MusicDirSkipper;
        }

        var inheritedScope = parentCtx?.OutputScope ?? OutputScope.Empty;

        // Use a synthetic owner list so index/playlist paths are scoped correctly when
        // root is a bare leaf job (not a JobList).
        var ownerList = root as JobList ?? explicitOwnerList ?? new JobList(null, [root]);
        bool useInheritedScopeForRootEditors = explicitOwnerList != null && root is JobList;
        // A newly accepted root is composed from the normal baseline/profile/request
        // layers. Only a subtree produced by an already-prepared parent inherits that
        // parent's search constraints. This matches JobPlanner's root/child boundary
        // and avoids applying root defaults again after a matching auto-profile.
        var rootInheritance = parentCtx != null || explicitOwnerList != null
            ? JobSettingsInheritance.SearchConstraints
            : JobSettingsInheritance.None;
        PrepareJob(root, ownerList, HasDefaultIndex(ownerList), inheritedScope, parentConfig, newContexts, editors, skippers, resolver,
            rootInheritance, useInheritedScopeForRootEditors, rootSettingsAreFinal,
            syntheticOwner: root is not JobList && explicitOwnerList is null);
        return newContexts;
    }

    public static void ApplySearchSettings(Job job, SearchSettings search)
    {
        switch (job)
        {
            case JobList jl:
                foreach (var s in jl.Jobs.OfType<SongJob>())  ApplySearchSettings(s, search);
                foreach (var a in jl.Jobs.OfType<AlbumJob>()) ApplySearchSettings(a, search);
                break;

            case SongJob sj:
                if (search.ArtistMaybeWrong && !sj.Query.ArtistMaybeWrong)
                    sj.Query = new SongQuery(sj.Query) { ArtistMaybeWrong = true };
                break;

            case AlbumJob aj:
                ApplySearchSettingsToAlbumQuery(aj, search);
                break;

            case SongGroupsJob ag:
                foreach (var s in ag.Songs) ApplySearchSettings(s, search);
                break;

            case AlbumGroupsJob aaj:
                ApplySearchSettingsToAlbumGroupsQuery(aaj, search);
                break;
        }
    }

    static void ApplySearchSettingsToAlbumQuery(AlbumJob aj, SearchSettings search)
    {
        var q   = aj.Query;
        bool amw = q.ArtistMaybeWrong;

        if (search.ArtistMaybeWrong)                               amw = true;

        if (amw != q.ArtistMaybeWrong)
            aj.Query = new AlbumQuery(q) { ArtistMaybeWrong = amw };
    }

    static void ApplySearchSettingsToAlbumGroupsQuery(AlbumGroupsJob aaj, SearchSettings search)
    {
        var q   = aaj.Query;
        bool amw = q.ArtistMaybeWrong;

        if (search.ArtistMaybeWrong)                               amw = true;

        if (amw != q.ArtistMaybeWrong)
            aaj.Query = new AlbumQuery(q) { ArtistMaybeWrong = amw };
    }

    private static void PrepareJob(
        Job job,
        JobList ownerList,
        bool indexByDefault,
        OutputScope inheritedScope,
        DownloadSettings parentConfig,
        Dictionary<Guid, JobContext> contexts,
        Dictionary<(string, OutputFileKind, bool), DownloadOutputFile> editors,
        Dictionary<(string, SkipMode, bool), TrackSkipper> skippers,
        IJobSettingsResolver resolver,
        JobSettingsInheritance inheritance,
        bool useInheritedScopeForCurrentJob = false,
        bool settingsAreFinal = false,
        bool syntheticOwner = false)
    {
        var ctx = new JobContext { PreparationOwner = ownerList };

        job.Config = job.PlannedSettings is { } planned
            ? UsePlannedSettings(job, planned)
            : settingsAreFinal
                ? ApplySemanticSettings(
                job,
                SettingsCloner.Clone(parentConfig),
                consumeExtractorHints: true)
                : ResolveSemanticSettings(
                    job,
                    parentConfig,
                    resolver,
                    inheritance,
                    consumeExtractorHints: true);
        var childScope = OutputScope.ForPreparedJob(job, inheritedScope, job.Config.Output);
        ctx.OutputScope = useInheritedScopeForCurrentJob ? inheritedScope : childScope;

        ctx.EnablesIndexByDefault = job.EnablesIndexByDefault;

        SetupIndexEditor(job, ctx, ownerList, indexByDefault, editors, syntheticOwner);
        SetupPlaylistEditor(job, ctx, ownerList, editors, syntheticOwner);
        SetupSkippers(job, ctx, skippers);

        contexts[job.Id] = ctx;

        // Recurse into JobList children — they inherit this list's config and
        // use this list as their ownerList for editor/index scoping.
        if (job is JobList childList)
        {
            bool childIndexByDefault = ReferenceEquals(childList, ownerList)
                ? indexByDefault : HasDefaultIndex(childList);
            foreach (var child in childList.Jobs)
            {
                // A prepared list is one workflow, including directly supplied
                // Core trees that did not pass through an extractor or daemon.
                child.WorkflowId = childList.WorkflowId;
                PrepareJob(child, childList, childIndexByDefault, childScope, job.Config, contexts, editors, skippers, resolver,
                    JobSettingsInheritance.SearchConstraints,
                    settingsAreFinal: child.PlannedSettings != null);
            }
        }
    }

    /// <summary>
    /// Resolves the semantic settings of one planned/runtime node without
    /// creating filesystem editors or skip indexes. Planning can therefore use
    /// exactly the runtime composition and extractor-condition semantics.
    /// </summary>
    public static DownloadSettings ResolveSemanticSettings(
        Job job,
        DownloadSettings parentConfig,
        IJobSettingsResolver resolver,
        JobSettingsInheritance inheritance,
        bool consumeExtractorHints = false)
    {
        DownloadSettings settings = resolver.Resolve(parentConfig, job, inheritance);
        return ApplySemanticSettings(job, settings, consumeExtractorHints);
    }

    internal static DownloadSettings ApplySemanticSettings(
        Job job,
        DownloadSettings settings,
        bool consumeExtractorHints)
    {
        if (job is RemoteFileJob or RemoteDirectoryJob)
            RemoteTransferNameFormatPolicy.ApplyInherited(settings.Output);

        if (job.ExtractorCond != null)
        {
            settings.Search.NecessaryCond.AddConditions(job.ExtractorCond);
            if (consumeExtractorHints && job is not ExtractJob)
                job.ExtractorCond = null;
        }
        if (job.ExtractorPrefCond != null)
        {
            settings.Search.PreferredCond.AddConditions(job.ExtractorPrefCond);
            if (consumeExtractorHints && job is not ExtractJob)
                job.ExtractorPrefCond = null;
        }
        if (job.ExtractorFolderCond != null)
        {
            settings.Search.NecessaryFolderCond.AddConditions(job.ExtractorFolderCond);
            if (consumeExtractorHints && job is not ExtractJob)
                job.ExtractorFolderCond = null;
        }
        if (job.ExtractorPrefFolderCond != null)
        {
            settings.Search.PreferredFolderCond.AddConditions(job.ExtractorPrefFolderCond);
            if (consumeExtractorHints && job is not ExtractJob)
                job.ExtractorPrefFolderCond = null;
        }

        if (job is SearchJob { Definition: null } searchJob)
            searchJob.Definition = SearchDefinition.Create(searchJob, settings.Search);
        return settings;
    }

    private static DownloadSettings UsePlannedSettings(
        Job job,
        JobSettingsSnapshot planned)
    {
        var settings = planned.ReadSettings();
        // The planner already applied these patches to the retained settings
        // and propagated extractor hints to a resolved Extract result. Clearing
        // leaf hints prevents any later subtree preparation from applying them
        // a second time.
        if (job is not ExtractJob)
        {
            job.ExtractorCond = null;
            job.ExtractorPrefCond = null;
            job.ExtractorFolderCond = null;
            job.ExtractorPrefFolderCond = null;
        }
        return settings;
    }

    private static void SetupIndexEditor(Job job, JobContext ctx, JobList ownerList,
        bool indexByDefault,
        Dictionary<(string, OutputFileKind, bool), DownloadOutputFile> editors, bool syntheticOwner)
    {
        if (job is RemoteFileJob or RemoteDirectoryJob)
            return;
        var config = job.Config;
        bool writeIndex = WillWriteIndex(config, indexByDefault);

        bool needIndex = writeIndex
            || (config.Skip.SkipExisting && config.Skip.SkipMode == SkipMode.Index)
            || config.Skip.SkipNotFound;


        if (!needIndex) return;

        string indexPath;
        if (config.Output.IndexFilePath != null)
        {
            string playlistName = (ownerList.ItemName ?? job.ItemNameOrSource()).ReplaceInvalidChars(" ").Trim();
            indexPath = config.Output.IndexFilePath.Replace("{playlist-name}", playlistName);
        }
        else
        {
            indexPath = ctx.OutputScope.ScopedPath(config.Output, "_index.csv");
        }

        var key = (indexPath, OutputFileKind.Index, !writeIndex);
        if (editors.TryGetValue(key, out var existing))
        {
            ctx.IndexEditor = existing;
            existing.AttachOwner(ownerList, syntheticOwner);
        }
        else
        {
            ctx.IndexEditor = new DownloadOutputFile(indexPath, ownerList, OutputFileKind.Index, true, readOnly: !writeIndex);
            editors.Add(key, ctx.IndexEditor);
        }
    }

    private static void SetupPlaylistEditor(Job job, JobContext ctx, JobList ownerList,
        Dictionary<(string, OutputFileKind, bool), DownloadOutputFile> editors, bool syntheticOwner)
    {
        if (job is RemoteFileJob or RemoteDirectoryJob)
            return;
        var config = job.Config;
        if (!config.Output.WritePlaylist) return;

        string m3uPath;
        if (config.Output.M3uFilePath != null)
        {
            string playlistName = (ownerList.ItemName ?? job.ItemNameOrSource()).ReplaceInvalidChars(" ").Trim();
            m3uPath = config.Output.M3uFilePath.Replace("{playlist-name}", playlistName);
        }
        else
        {
            string playlistFileName = ownerList.ItemName != null ? ownerList.DefaultPlaylistName() : job.DefaultPlaylistName();
            m3uPath = ctx.OutputScope.ScopedPath(config.Output, playlistFileName);
        }

        var key = (m3uPath, OutputFileKind.Playlist, false);
        if (editors.TryGetValue(key, out var existing))
        {
            ctx.PlaylistEditor = existing;
            existing.AttachOwner(ownerList, syntheticOwner);
        }
        else
        {
            ctx.PlaylistEditor = new DownloadOutputFile(m3uPath, ownerList, OutputFileKind.Playlist, false);
            editors.Add(key, ctx.PlaylistEditor);
        }
    }

    private static void SetupSkippers(Job job, JobContext ctx,
        Dictionary<(string, SkipMode, bool), TrackSkipper> skippers)
    {
        if (job is RemoteFileJob or RemoteDirectoryJob)
            return;

        var config = job.Config;
        if (!config.Skip.SkipExisting) return;

        bool checkCond = config.Skip.SkipCheckCond || config.Skip.SkipCheckPrefCond;

        var outputKey = (config.Output.ParentDir!, config.Skip.SkipMode, checkCond);
        if (skippers.TryGetValue(outputKey, out var outputSkipper))
        {
            ctx.OutputDirSkipper = outputSkipper;
        }
        else
        {
            ctx.OutputDirSkipper = TrackSkipperRegistry.GetSkipper(config.Skip.SkipMode, config.Output.ParentDir!, checkCond);
            skippers.Add(outputKey, ctx.OutputDirSkipper);
        }

        if (config.Skip.SkipMusicDir != null)
        {
            var musicKey = (config.Skip.SkipMusicDir, config.Skip.SkipModeMusicDir, checkCond);
            if (skippers.TryGetValue(musicKey, out var musicSkipper))
            {
                ctx.MusicDirSkipper = musicSkipper;
            }
            else
            {
                ctx.MusicDirSkipper = TrackSkipperRegistry.GetSkipper(config.Skip.SkipModeMusicDir, config.Skip.SkipMusicDir, checkCond);
                skippers.Add(musicKey, ctx.MusicDirSkipper);
            }
        }
    }

    // This is an owner-list property. Compute it at each traversal boundary,
    // rather than scanning every sibling for each leaf (quadratic on large lists).
    // It is deliberately not cached on mutable Jobs between preparations.
    private static bool HasDefaultIndex(JobList owner)
        => owner.EnablesIndexByDefault || owner.Jobs.Any(job => job.EnablesIndexByDefault);

    internal static bool WillWriteIndex(DownloadSettings settings, bool indexByDefault)
        => !settings.DoNotDownload && (settings.Output.WriteIndex ?? (settings.Output.IndexFilePath != null || indexByDefault));
}
