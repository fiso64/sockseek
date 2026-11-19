using Sockseek.Core.Jobs;

namespace Sockseek.Core;

internal static class DownloadOutcomes
{
    public static JobOutcome ExtractionFailed(Exception exception)
        => ExceptionFailure(JobFailureReason.ExtractionFailed, exception);

    public static JobOutcome ExceptionFailure(JobFailureReason reason, Exception exception)
        => JobOutcome.Failed(
            reason,
            Diagnostics.ExceptionText.Summary(exception),
            Diagnostics.ExceptionText.Detail(exception));

    public static JobOutcome NoMatchingDiscovery(Job job, string candidatePlural)
    {
        int files = job.Discovery?.RawResultCount ?? 0;
        if (files == 0)
            return JobOutcome.Failed(JobFailureReason.NoSearchResults, "No Soulseek file results found.");

        string noun = files == 1 ? "file result" : "file results";
        return JobOutcome.Failed(JobFailureReason.NoMatchingResults,
            $"{files} Soulseek {noun} found, but no matching {candidatePlural} satisfied the required conditions.");
    }

    public static JobOutcome NoMatchingCandidates()
        => JobOutcome.Failed(JobFailureReason.NoMatchingResults);
}
