namespace Sockseek.Api;

public static class ServerFailureReasonDisplay
{
    public static string Label(JobFailureReason? reason)
        => reason switch
        {
            null or JobFailureReason.None => "",
            JobFailureReason.InvalidSearchString => "Invalid search string",
            JobFailureReason.OutOfDownloadRetries => "Out of download retries",
            JobFailureReason.AllDownloadsFailed => "All downloads failed",
            JobFailureReason.ExtractionFailed => "Extraction failed",
            JobFailureReason.Cancelled => "Cancelled",
            JobFailureReason.ChildJobsFailed => "Child jobs failed",
            JobFailureReason.NoSearchResults => "No search results",
            JobFailureReason.NoMatchingResults => "No matching results",
            JobFailureReason.Other => "Unknown error",
            _ => "",
        };

    public static string FailedLabel(JobFailureReason? reason)
    {
        var label = Label(reason);
        return label.Length > 0 ? $"failed [{label}]" : "failed";
    }
}
