using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#930, #1228: the three kinds of processor-output issue read differently on Health, movies as well as shows, and each row says which issue it mirrors.</summary>
public sealed class ProcessorOutputImportIssueHealthCheckWordsTests
{
    private static MovieImportRecoveryCase MovieCase(string id, string kind, string? details = null)
        => new(id, "Alien", kind, "open", "Alien: the summary.", "Do the action.", details, DateTimeOffset.UnixEpoch, null);

    private static SeriesImportRecoveryCase ShowCase(string id, string kind, string? details = null)
        => new(id, "Lioness", kind, "open", "Lioness: the summary.", "Do the show action.", details, DateTimeOffset.UnixEpoch, null);

    private static ProcessorOutputImportIssueHealthCheck Check(MovieImportRecoveryCase[] movieCases, SeriesImportRecoveryCase[] showCases)
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.GetImportRecoverySummaryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieImportRecoverySummary(movieCases.Length, 0, 0, 0, 0, 0, movieCases));
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repo => repo.GetImportRecoverySummaryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImportRecoverySummary(showCases.Length, 0, 0, 0, 0, 0, showCases));
        return new ProcessorOutputImportIssueHealthCheck(movies.Object, series.Object);
    }

    [Fact]
    public async Task A_file_the_processor_lost_something_from_is_held_and_says_checkpoint_A_found_the_original_good()
    {
        var outcome = Assert.Single(await Check([MovieCase("c1", "processedFileIsLess")], []).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Alien: held, the processor lost something", outcome.Name);
        Assert.Equal("Alien: the summary.", outcome.Message);
        Assert.Equal("Checkpoint A already showed the original download is good; the processor's own copy lost something the original had.", outcome.Cause);
        Assert.Equal("Do the action.", outcome.Fix);
    }

    [Fact]
    public async Task A_file_no_title_could_be_found_for_is_unmatched_and_says_why()
    {
        var outcome = Assert.Single(await Check([], [ShowCase("c2", "processor-unmatched-output")]).RunAsync(CancellationToken.None));

        Assert.Equal("Lioness: unmatched in a processor's output", outcome.Name);
        Assert.Equal("A season-pack file did not resolve to a canonical episode, matched one but was not imported, or a processor's cleaned output could not be matched to a download hand-off.", outcome.Cause);
        Assert.Equal("Do the show action.", outcome.Fix);
    }

    [Fact]
    public async Task A_file_the_processor_named_but_that_is_not_there_is_missing_and_says_why()
    {
        var outcome = Assert.Single(await Check([MovieCase("c3", "processor-output-missing")], []).RunAsync(CancellationToken.None));

        Assert.Equal("Alien: missing from a processor's output", outcome.Name);
        Assert.Equal("A processor named a file it made that Deluno cannot find.", outcome.Cause);
    }

    [Fact]
    public async Task Movies_come_before_shows_and_each_row_says_which_issue_and_which_kind_of_title_it_mirrors()
    {
        var outcomes = await Check([MovieCase("m1", "processor-output-missing")], [ShowCase("s1", "processor-output-missing")]).RunAsync(CancellationToken.None);

        Assert.Equal(["processor-output-issue:m1", "processor-output-issue:s1"], outcomes.Select(item => item.Id));
        var movie = JsonNode.Parse(outcomes[0].DetailsJson!)!.AsObject();
        Assert.Equal("m1", (string?)movie["recoveryCaseId"]);
        Assert.Equal("movie", (string?)movie["recoveryMediaType"]);
        var show = JsonNode.Parse(outcomes[1].DetailsJson!)!.AsObject();
        Assert.Equal("s1", (string?)show["recoveryCaseId"]);
        Assert.Equal("series", (string?)show["recoveryMediaType"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public async Task Details_that_say_nothing_usable_are_replaced_by_just_the_issue_they_mirror(string? details)
    {
        var outcome = Assert.Single(await Check([MovieCase("m1", "processor-output-missing", details)], []).RunAsync(CancellationToken.None));

        Assert.Equal("""{"recoveryCaseId":"m1","recoveryMediaType":"movie"}""", outcome.DetailsJson);
    }

    [Fact]
    public async Task Details_the_case_carried_are_kept_beside_the_issue_they_mirror()
    {
        var outcome = Assert.Single(await Check([], [ShowCase("s1", "processor-unmatched-output", """{"sourcePath":"/downloads/x.mkv"}""")]).RunAsync(CancellationToken.None));

        Assert.Equal("""{"sourcePath":"/downloads/x.mkv","recoveryCaseId":"s1","recoveryMediaType":"series"}""", outcome.DetailsJson);
    }

    [Fact]
    public async Task Cases_of_any_other_kind_are_left_to_their_own_screens()
    {
        Assert.Empty(await Check([MovieCase("m1", "processor-output-MISSING")], [ShowCase("s1", "unmatched")]).RunAsync(CancellationToken.None));
    }
}
