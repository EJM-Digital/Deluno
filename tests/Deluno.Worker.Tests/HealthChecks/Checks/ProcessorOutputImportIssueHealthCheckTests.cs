using Deluno.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #930: a file missing or unmatched in a processor's output shows on Health
/// too, not only on the title/season page -- the same recovery case, read
/// from the same summary Queue already reads (#558).
/// </summary>
public sealed class ProcessorOutputImportIssueHealthCheckTests
{
    [Fact]
    public async Task An_open_missing_output_case_is_reported_and_others_are_not()
    {
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repo => repo.GetImportRecoverySummaryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new SeriesImportRecoverySummary(2, 0, 1, 0, 0, 0,
            [
                new SeriesImportRecoveryCase("case-1", "Lioness", "processor-output-missing", "open",
                    "Lioness S02: 9 of 10 imported, E07 missing from Weir's output.",
                    "Search for this episode, or import it manually.",
                    "{\"episodeId\":\"episode-7\"}", DateTimeOffset.UnixEpoch, null),
                new SeriesImportRecoveryCase("case-2", "Some Show", "unmatched", "open",
                    "Wrong title.", "Review manually.", null, DateTimeOffset.UnixEpoch, null)
            ]));
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.GetImportRecoverySummaryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieImportRecoverySummary(0, 0, 0, 0, 0, 0, []));

        var check = new ProcessorOutputImportIssueHealthCheck(movies.Object, series.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckCategories.Processor, check.Category);
        Assert.Equal("processor-output-issue:case-1", outcome.Id);
        Assert.Contains("E07 missing", outcome.Message);
        Assert.Equal("Search for this episode, or import it manually.", outcome.Fix);
    }
}
