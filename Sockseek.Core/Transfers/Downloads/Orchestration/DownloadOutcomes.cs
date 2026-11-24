using Sockseek.Api;
using Sockseek.Core.Jobs;

namespace Sockseek.Core;

internal static class DownloadOutcomes
{
    internal static string FailureMessage(Exception exception)
    {
        // Soulseek may wrap an output-stream failure. Keep the operation and path
        // captured where it failed, even when the transport supplies another message.
        for (Exception? cause = exception; cause != null; cause = cause.InnerException)
            if (cause is Services.DownloadDestinationException or Soulseek.TransferRejectedException or Services.FileOrganizationException)
                return cause.Message;
        return Diagnostics.ExceptionText.Summary(exception);
    }

    public static JobOutcome ExtractionFailed(Exception exception)
        => ExceptionFailure(JobFailureReason.ExtractionFailed, exception);

    public static JobOutcome ExceptionFailure(JobFailureReason reason, Exception exception)
        => JobOutcome.Failed(
            reason,
            FailureMessage(exception),
            Diagnostics.ExceptionText.Detail(exception));

    public static JobOutcome NoMatchingDiscovery(Job job, string candidatePlural)
    {
        // A track search filters at admission, so its raw count holds only the
        // files that passed; an album search keeps every file it was offered.
        int files = (job.Discovery?.RawResultCount ?? 0)
            + (job is SongJob or SongGroupsJob ? job.Discovery?.Rejections.Total ?? 0 : 0);
        if (files == 0)
            return JobOutcome.Failed(JobFailureReason.NoSearchResults, "No Soulseek file results found.");

        string noun = files == 1 ? "file result" : "file results";
        return JobOutcome.Failed(JobFailureReason.NoMatchingResults,
            $"{files} Soulseek {noun} found, but no matching {candidatePlural} satisfied the required conditions.");
    }

    public static JobOutcome NoMatchingCandidates()
        => JobOutcome.Failed(JobFailureReason.NoMatchingResults);
}
