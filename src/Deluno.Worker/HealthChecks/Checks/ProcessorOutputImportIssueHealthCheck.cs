using Deluno.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// A file a processor named that is not on disk, or a season-pack file that
/// does not resolve to a canonical episode, shown on System &#8594; Health as
/// well as on the title/season page (#930). Both surfaces read the same
/// recovery cases <see cref="Services.ProcessorFailureReportHandler.HandleOutputsMissingAsync"/>
/// already raises -- Health is not a second record of the same problem, only
/// a second place it is seen until it is dealt with.
/// </summary>
public sealed class ProcessorOutputImportIssueHealthCheck(
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series) : IHealthCheck
{
    public string Category => HealthCheckCategories.Processor;

    private const string FailureKind = "processor-output-missing";

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<HealthCheckOutcome>();

        var movieSummary = await movies.GetImportRecoverySummaryAsync(cancellationToken);
        foreach (var item in movieSummary.RecentCases.Where(item => item.FailureKind == FailureKind))
        {
            outcomes.Add(BuildOutcome(item.Id, item.Title, item.Summary, item.RecommendedAction, item.DetailsJson));
        }

        var seriesSummary = await series.GetImportRecoverySummaryAsync(cancellationToken);
        foreach (var item in seriesSummary.RecentCases.Where(item => item.FailureKind == FailureKind))
        {
            outcomes.Add(BuildOutcome(item.Id, item.Title, item.Summary, item.RecommendedAction, item.DetailsJson));
        }

        return outcomes;
    }

    private HealthCheckOutcome BuildOutcome(string caseId, string title, string summary, string recommendedAction, string? detailsJson)
        => HealthCheckOutcome.Problem(
            $"processor-output-issue:{caseId}",
            Category,
            $"{title}: missing from a processor's output",
            summary,
            cause: "A processor named a file it made that Deluno cannot find, or a season-pack file that does not resolve to a canonical episode.",
            fix: recommendedAction,
            detailsJson: detailsJson);
}
