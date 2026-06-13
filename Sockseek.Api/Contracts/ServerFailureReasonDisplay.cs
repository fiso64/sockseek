namespace Sockseek.Api;

public static class ServerFailureReasonDisplay
{
    public static string Label(ServerJobFailureReason? reason)
        => reason switch
        {
            null or ServerJobFailureReason.None => "",
            ServerJobFailureReason.InvalidSearchString => "Invalid search string",
            ServerJobFailureReason.OutOfDownloadRetries => "Out of download retries",
            ServerJobFailureReason.NoSuitableFileFound => "No suitable file found",
            ServerJobFailureReason.AllDownloadsFailed => "All downloads failed",
            ServerJobFailureReason.ExtractionFailed => "Extraction failed",
            ServerJobFailureReason.Cancelled => "Cancelled",
            ServerJobFailureReason.ChildJobsFailed => "Child jobs failed",
            ServerJobFailureReason.Other => "Unknown error",
            _ => "",
        };

    public static string LabelOrUnknown(ServerJobFailureReason? reason)
    {
        var label = Label(reason);
        return label.Length > 0 ? label : Label(ServerJobFailureReason.Other);
    }

    public static string FailedLabel(ServerJobFailureReason? reason)
    {
        var label = Label(reason);
        return label.Length > 0 ? $"failed [{label}]" : "failed";
    }
}
