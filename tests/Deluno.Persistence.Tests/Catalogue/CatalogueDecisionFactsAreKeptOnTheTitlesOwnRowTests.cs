using Deluno.Media;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// The decision facts a shelf filters and sorts on: the primary copy's reason, target quality, cutoff, last and next search,
/// last result and the moment its file was found gone sit on the title's own row, beside the count of its libraries and of its
/// copies with a file, and the counts are written for the table and key of the kind they are asked for.
/// </summary>
public sealed class CatalogueDecisionFactsAreKeptOnTheTitlesOwnRowTests
{
    [Fact]
    public void The_title_carries_the_primary_decision_and_two_counts()
    {
        var names = CatalogueDecisionFacts.All.Select(fact => fact.Column).ToArray();

        Assert.Equal(
            [
                "primary_wanted_reason", "primary_target_quality", "primary_quality_cutoff_met", "primary_last_search_utc",
                "primary_next_eligible_search_utc", "primary_last_search_result", "primary_missing_detected_utc", "library_count", "file_count"
            ],
            names);
        Assert.All(CatalogueDecisionFacts.All.Where(fact => fact.Column.StartsWith("primary_", StringComparison.Ordinal)), fact => Assert.False(fact.Aggregate));
        Assert.All(CatalogueDecisionFacts.All.Where(fact => !fact.Column.StartsWith("primary_", StringComparison.Ordinal)), fact => Assert.True(fact.Aggregate));
    }

    [Fact]
    public void A_cutoff_that_was_never_decided_reads_as_not_met_and_the_counts_are_integers()
    {
        var cutoff = CatalogueDecisionFacts.All.Single(fact => fact.Column == "primary_quality_cutoff_met");
        Assert.Equal("COALESCE(pick.quality_cutoff_met, 0)", cutoff.Expression);
        Assert.Equal("INTEGER", cutoff.Type);
        Assert.All(CatalogueDecisionFacts.All.Where(fact => fact.Aggregate), fact => Assert.Equal("INTEGER", fact.Type));
    }

    [Fact]
    public void The_counts_are_written_for_the_wanted_table_and_key_asked_for_and_the_rest_is_left_as_it_is()
    {
        var movies = CatalogueDecisionFacts.For("movie_wanted_state", "movie_id");
        var shows = CatalogueDecisionFacts.For("series_wanted_state", "series_id");

        Assert.Equal("SELECT COUNT(*) FROM movie_wanted_state c WHERE c.movie_id = {owner}", movies.Single(fact => fact.Column == "library_count").Expression);
        Assert.Equal("SELECT COUNT(*) FROM movie_wanted_state c WHERE c.movie_id = {owner} AND c.has_file = 1", movies.Single(fact => fact.Column == "file_count").Expression);
        Assert.Equal("SELECT COUNT(*) FROM series_wanted_state c WHERE c.series_id = {owner}", shows.Single(fact => fact.Column == "library_count").Expression);
        Assert.Equal(
            CatalogueDecisionFacts.All.Where(fact => !fact.Aggregate).Select(fact => fact.Expression),
            movies.Where(fact => !fact.Aggregate).Select(fact => fact.Expression));
    }
}
