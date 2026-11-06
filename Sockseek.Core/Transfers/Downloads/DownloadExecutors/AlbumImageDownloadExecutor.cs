using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Transfers.Downloads.Runtime;

namespace Sockseek.Core;

internal sealed class AlbumImageDownloadExecutor
{
    private readonly SongDownloadExecutor songDownloads;
    private readonly DownloadExecutionContext context;

    public AlbumImageDownloadExecutor(
        DownloadExecutionContext context,
        SongDownloadExecutor songDownloads)
    {
        this.context = context;
        this.songDownloads = songDownloads;
    }

    public async Task<List<SongJob>> DownloadImages(AlbumJob job, JobContext ctx, FileManager fileManager, AlbumFolder? chosenFolder)
    {
        var result = new List<SongJob>();
        var config = job.Config;
        long mSize = 0;
        int mCount = 0;
        var option = config.Output.EffectiveAlbumArtOption;

        if (chosenFolder != null)
        {
            string dir = chosenFolder.FolderPath;
            fileManager.SetDefaultFolderName(Path.GetFileName(Utils.NormalizedPath(dir)));
        }

        if (option == AlbumArtOption.Default) return result;

        int[]? sortedLengths = null;
        if (chosenFolder?.Files.Any(af => !af.IsNotAudio) == true)
            sortedLengths = chosenFolder.Files.Where(af => !af.IsNotAudio)
                .Select(af => af.Query.Length).OrderBy(x => x).ToArray();

        var imageFolders = job.Results
            .Where(f => chosenFolder == null || Searcher.AlbumsAreSimilar(chosenFolder, f, sortedLengths))
            .Select(f => f.Files.Where(af => Utils.IsImageFile(af.Filename)).ToList())
            .Where(ls => ls.Count > 0)
            .ToList();

        if (imageFolders.Count == 0)
        {
            context.Events.RaiseJobMessage(
                job,
                LogLevel.Information,
                null,
                "no images found");
            return result;
        }

        if (option == AlbumArtOption.Largest)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Max(af => af.Candidate.Size) / 1024 / 100)
                .ThenByDescending(ls => (ls[0].Candidate.UploadSpeed ?? -1) / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.Candidate.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mSize = job.TrackJobs
                    .Where(af => af.TerminalOutcome == JobTerminalOutcome.Succeeded && Utils.IsImageFile(af.DownloadPath ?? ""))
                    .Select(af => af.ResolvedTarget!.Size)
                    .DefaultIfEmpty(0).Max();
        }
        else if (option == AlbumArtOption.Most)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Count)
                .ThenByDescending(ls => (ls[0].Candidate.UploadSpeed ?? -1) / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.Candidate.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mCount = job.TrackJobs.Count(af => af.TerminalOutcome == JobTerminalOutcome.Succeeded && Utils.IsImageFile(af.DownloadPath ?? ""));
        }

        bool needsDownload(List<AlbumFile> ls) => option == AlbumArtOption.Most
            ? mCount < ls.Count
            : option == AlbumArtOption.Largest
                ? mSize == 0 || mSize < ls.Max(af => af.Candidate.Size) - 1024 * 50
                : true;

        while (imageFolders.Count > 0)
        {
            var imgs = imageFolders[0];
            imageFolders.RemoveAt(0);
            var imageJobs = config.Output.AlbumArtOnly
                ? job.BeginAlbumArtTransferAttempt(
                    job.Results.First(folder => imgs.All(folder.Files.Contains)),
                    imgs)
                : job.EnsureSupplementalTrackJobs(imgs);

            if (imageJobs.All(af => af.TerminalOutcome == JobTerminalOutcome.Succeeded
                    || (af.TerminalOutcome == JobTerminalOutcome.Skipped && af.SkipReason == JobSkipReason.AlreadyExists))
                || !needsDownload(imgs))
            {
                var imageFolderPath = Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.Filename));
                context.Events.RaiseJobMessage(
                    job,
                    LogLevel.Information,
                    null,
                    "image requirements already satisfied");
                return result;
            }

            fileManager.downloadingAdditionalImages = true;
            fileManager.SetRemoteCommonImagesDir(Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.Filename)));

            bool allSucceeded = true;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);

            foreach (var af in imageJobs)
            {
                if (af.ResolvedTarget != null && af.Candidates == null)
                    af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
                await songDownloads.DownloadEmbeddedSong(
                    af,
                    job,
                    config,
                    fileManager,
                    cts,
                    cancelGroupOnFail: false,
                    finalizePlacement: false);
                if (af.TerminalOutcome == JobTerminalOutcome.Succeeded)
                    result.Add(af);
                else
                    allSucceeded = false;
            }

            if (allSucceeded) break;
        }

        return result;
    }
}
