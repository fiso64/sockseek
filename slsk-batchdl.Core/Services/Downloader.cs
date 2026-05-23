using Soulseek;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core;

using File = System.IO.File;
using Directory = System.IO.Directory;
using Sldl.Core.Settings;

namespace Sldl.Core.Services;


public class Downloader
{
    private readonly ISoulseekClient client;
    private readonly SoulseekClientManager clientManager;
    private readonly IDownloadRegistry downloadRegistry;
    private readonly EngineEvents events;

    public Downloader(ISoulseekClient client,
                      SoulseekClientManager clientManager,
                      IDownloadRegistry downloadRegistry,
                      EngineEvents events)
    {
        this.client = client;
        this.clientManager = clientManager;
        this.downloadRegistry = downloadRegistry;
        this.events = events;
    }

    public async Task DownloadFile(FileCandidate candidate, string outputPath, SongJob song,
        TransferSettings transfer, string? parentDir, CancellationToken? ct = null)
    {
        string fileKey = candidate.Username + '\\' + candidate.Filename;

        if (downloadRegistry.DownloadedFiles.TryGetValue(fileKey, out var existingSong))
        {
            lock (downloadRegistry.DownloadedFiles)
            {
                var existingPath     = existingSong.DownloadPath;
                var outputFileInfo   = new FileInfo(outputPath);
                var existingFileInfo = new FileInfo(existingPath ?? "");

                if (existingFileInfo.Exists && existingFileInfo.Length == candidate.File.Size)
                {
                    SldlLog.Debug($"File \"{candidate.Filename}\" already downloaded at {existingPath}");

                    if (!outputFileInfo.Exists || outputFileInfo.Length != existingFileInfo.Length)
                    {
                        SldlLog.Debug("Copying to new output path");
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        File.Copy(existingPath!, outputPath, true);
                    }

                    song.SetDone(outputPath, candidate);
                    return;
                }
                else
                {
                    downloadRegistry.DownloadedFiles.TryRemove(fileKey, out _);
                }
            }
        }

        await clientManager.WaitUntilReadyAsync(ct ?? CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string incompleteOutputPath = transfer.NoIncompleteExt ? outputPath : outputPath + ".incomplete";

        SldlLog.Debug($"Downloading: {song} from '{candidate.Username}\\{candidate.Filename}' to '{incompleteOutputPath}'");

        var transferOptions = new TransferOptions(
            disposeOutputStreamOnCompletion: false,
            stateChanged: (state) =>
            {
                if (downloadRegistry.Downloads.TryGetValue(candidate.Filename, out var x))
                    x.Transfer = state.Transfer;
                events.RaiseDownloadStateChanged(song, state.Transfer.State);
            },
            progressUpdated: (progress) =>
            {
                if (downloadRegistry.Downloads.TryGetValue(candidate.Filename, out var x))
                    x.Song.BytesTransferred = progress.PreviousBytesTransferred;
                events.RaiseDownloadProgress(song, progress.PreviousBytesTransferred, candidate.File.Size > 0 ? candidate.File.Size : 0);
            }
        );

        try
        {
            using var downloadCts = ct != null
                ? CancellationTokenSource.CreateLinkedTokenSource((CancellationToken)ct)
                : new CancellationTokenSource();

            using var outputStream = new FileStream(incompleteOutputPath, FileMode.Create);

            song.FileSize = candidate.File.Size;
            var activeDownload = new ActiveDownload(song, candidate, downloadCts);
            downloadRegistry.Downloads.TryAdd(candidate.Filename, activeDownload);

            events.RaiseDownloadStarted(song, candidate);

            int maxRetries = 3;
            int retryCount = 0;
            while (true)
            {
                try
                {
                    await client.DownloadAsync(candidate.Username, candidate.Filename,
                        () => Task.FromResult((Stream)outputStream),
                        candidate.File.Size == -1 ? null : candidate.File.Size,
                        startOffset: outputStream.Position,
                        options: transferOptions,
                        cancellationToken: downloadCts.Token);
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    retryCount++;
                    bool canRetry = e is SoulseekClientException
                        && retryCount < maxRetries
                        && !clientManager.IsConnectedAndLoggedIn;
                    int reportedMaxRetries = canRetry || (e is SoulseekClientException && !clientManager.IsConnectedAndLoggedIn)
                        ? maxRetries
                        : retryCount;

                    SldlLog.Debug($"Error while downloading '{candidate.Username}\\{candidate.Filename}' to '{incompleteOutputPath}' (attempt {retryCount}/{maxRetries}): {e}");
                    events.RaiseDownloadAttemptFailed(song, candidate, incompleteOutputPath, retryCount, reportedMaxRetries, e);

                    if (!canRetry)
                        throw;

                    await clientManager.WaitUntilReadyAsync(downloadCts.Token);
                }
            }
        }
        catch
        {
            if (File.Exists(incompleteOutputPath))
                try { Utils.DeleteFileAndParentsIfEmpty(incompleteOutputPath, parentDir ?? ""); } catch { }
            
            if (downloadRegistry.Downloads.TryRemove(candidate.Filename, out var ad) && ad.IsManuallySkipped)
                throw new ManuallySkippedException();

            throw;
        }


        if (!transfer.NoIncompleteExt)
        {
            try { Utils.Move(incompleteOutputPath, outputPath); }
            catch (IOException e) { SldlLog.Error($"Failed to rename .incomplete file. Error: {e}"); }
        }

        downloadRegistry.DownloadedFiles[fileKey] = song;
        downloadRegistry.Downloads.TryRemove(candidate.Filename, out _);

        if (candidate.File.Size > 0)
            song.BytesTransferred = candidate.File.Size;

        song.SetDone(outputPath, candidate);
    }

    static string GetStateLabel(TransferStates s)
    {
        if (s.HasFlag(TransferStates.InProgress))   return "InProgress";
        if (s.HasFlag(TransferStates.Queued))
            return s.HasFlag(TransferStates.Remotely) ? "Queued (R)" :
                   s.HasFlag(TransferStates.Locally)  ? "Queued (L)" : "Queued";
        if (s.HasFlag(TransferStates.Initializing)) return "Initialising";
        return "Requested";
    }
}
