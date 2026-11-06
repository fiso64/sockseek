namespace Sockseek.Core.Jobs;

/// <summary>
/// Observable state shared by jobs that produce at most one downloaded file.
/// Discovery, targets, naming, and fallback policy belong to semantic subtypes
/// and their orchestrators.
/// </summary>
public abstract class FileDownloadJob : Job
{
    private string? downloadPath;
    private long bytesTransferred;
    private long? fileSize;

    public string? DownloadPath
    {
        get => downloadPath;
        set
        {
            if (downloadPath == value)
                return;
            downloadPath = value;
            OnPropertyChanged();
        }
    }

    public long BytesTransferred
    {
        get => bytesTransferred;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (bytesTransferred == value)
                return;
            bytesTransferred = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Progress));
        }
    }

    public long? FileSize
    {
        get => fileSize;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (fileSize == value)
                return;
            fileSize = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Progress));
        }
    }

    public double Progress => FileSize is > 0
        ? Math.Clamp((double)BytesTransferred / FileSize.Value, 0, 1)
        : 0;

    public override void SetDone()
        => SetDone(downloadPath: null);

    public void SetDone(string? downloadPath)
    {
        if (downloadPath != null)
            DownloadPath = downloadPath;
        base.SetDone();
    }

    public override void SetAlreadyExists()
        => SetAlreadyExists(path: null);

    public void SetAlreadyExists(string? path)
    {
        if (path != null)
            DownloadPath = path;
        base.SetAlreadyExists();
    }
}
