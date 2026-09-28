using Deluno.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// A file a processor named that is not on disk, a season-pack file that does
/// not resolve to a canonical episode or that matched an episode but was not
/// imported (#957), or a processed file Checkpoint B held because the
/// processor lost something the original had (#919) -- shown on System
/// &#8594; Health as well as on the title/season page. All these surfaces
/// read the same recovery cases
/// <see cref="Services.ProcessorFailureReportHandler.HandleOutputsMissingAsync"/>,
/// <c>ImportPipelineService</c>'s own <c>processor-unmatched-output</c> case
/// for a season pack's skipped files, and its <c>processedFileIsLess</c> case
/// already raise -- Health is not a second record of the same problem, only a
/// second place it is seen until it is dealt with.
/// </summary>
public sealed class ProcessorOutputImportIssueHealthCheck(
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series) : IHealthCheck
{
    public string Category => HealthCheckCategories.Processor;

    private static readonly IReadOnlySet<string> FailureKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "processor-output-missing",
        "processor-unmatched-output",
        "processedFileIsLess"
    };

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<HealthCheckOutcome>();

        var movieSummary = await movies.GetImportRecoverySummaryAsync(cancellationToken);
        foreach (var item in movieSummary.RecentCases.Where(item => FailureKinds.Contains(item.FailureKind)))
        {
            outcomes.Add(BuildOutcome(item.Id, item.Title, item.FailureKind, item.Summary, item.RecommendedAction, item.DetailsJson));
        }

        var seriesSummary = await series.GetImportRecoverySummaryAsync(cancellationToken);
        foreach (var item in seriesSummary.RecentCases.Where(item => FailureKinds.Contains(item.FailureKind)))
        {
            outcomes.Add(BuildOutcome(item.Id, item.Title, item.FailureKind, item.Summary, item.RecommendedAction, item.DetailsJson));
        }

        return outcomes;
    }

    private HealthCheckOutcome BuildOutcome(string caseId, string title, string failureKind, string summary, string recommendedAction, string? detailsJson)
    {
        var isProcessorFault = failureKind == "processedFileIsLess";
        var isUnmatched = failureKind == "processor-unmatched-output";
        return HealthCheckOutcome.Problem(
            $"processor-output-issue:{caseId}",
            Category,
            isProcessorFault ? $"{title}: held, the processor lost something"
                : isUnmatched ? $"{title}: unmatched in a processor's output"
                : $"{title}: missing from a processor's output",
            summary,
            cause: isProcessorFault
                ? "Checkpoint A already showed the original download is good; the processor's own copy lost something the original had."
                : isUnmatched
                    ? "A season-pack file did not resolve to a canonical episode, matched one but was not imported, or a processor's cleaned output could not be matched to a download hand-off."
                    : "A processor named a file it made that Deluno cannot find.",
            fix: recommendedAction,
            detailsJson: detailsJson);
    }
}
