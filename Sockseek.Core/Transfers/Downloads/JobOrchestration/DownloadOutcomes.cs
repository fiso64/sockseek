using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

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

    public static JobOutcome NoMatchingDiscovery(
        ResponseData responseData,
        string rawResultSingular,
        string rawResultPlural,
        string candidatePlural)
    {
        if (responseData.resultCount <= 0)
            return JobOutcome.Failed(JobFailureReason.NoSearchResults, $"No Soulseek {rawResultPlural} found.");

        var rawResultNoun = responseData.resultCount == 1 ? rawResultSingular : rawResultPlural;
        return JobOutcome.Failed(
            JobFailureReason.NoMatchingResults,
            $"{responseData.resultCount} Soulseek {rawResultNoun} found, but no matching {candidatePlural} satisfied the required conditions.");
    }

    public static JobOutcome NoMatchingCandidates()
        => JobOutcome.Failed(JobFailureReason.NoMatchingResults);
}
