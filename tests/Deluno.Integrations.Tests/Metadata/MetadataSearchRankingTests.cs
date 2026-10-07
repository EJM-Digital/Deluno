using Deluno.Integrations.Metadata;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// #1163: a typed TV search ranks by how well each title matches what was typed
/// - the exact or whole-word title first, the year if one was typed, then how well
/// rated - across both sources, and lists a show found in both once.
/// </summary>
public sealed class MetadataSearchRankingTests
{
    [Fact]
    public void Frieren_ranks_Frieren_Beyond_Journeys_End_above_the_loose_matches()
    {
        var results = Rank(
            "Frieren",
            null,
            Show("tvdb-1", "Frieden", 2020, rating: 6.1, tvdb: "1"),
            Show("tvdb-2", "Friheden", 2018, rating: 7.0, tvdb: "2"),
            Show("tvdb-3", "Gooische Frieten", 2010, rating: 5.0, tvdb: "3"),
            Show("tvdb-4", "Brieven aan Samson", 2026, rating: 4.0, tvdb: "4"),
            Show("209867", "Frieren: Beyond Journey's End", 2023, rating: 8.8));

        Assert.Equal("Frieren: Beyond Journey's End", results[0].Title);
        Assert.Equal(["Frieden", "Friheden", "Gooische Frieten", "Brieven aan Samson"], results.Skip(1).Select(result => result.Title).OrderBy(title => title == "Frieden" ? 0 : title == "Friheden" ? 1 : title == "Gooische Frieten" ? 2 : 3));
    }

    [Fact]
    public void The_exact_title_beats_a_title_that_only_contains_the_word_and_a_typed_year_picks_the_remake()
    {
        var older = Show("10", "The Twilight Zone", 1959, rating: 8.5);
        var newer = Show("11", "The Twilight Zone", 2019, rating: 6.0);
        var spinOff = Show("12", "Twilight Zone: The Movie Show", 1983, rating: 9.9);

        Assert.Equal("10", Rank("The Twilight Zone", 1959, newer, spinOff, older)[0].ProviderId);
        Assert.Equal("11", Rank("The Twilight Zone", 2019, older, spinOff, newer)[0].ProviderId);
        // With no year typed, the better rated of the two exact titles comes first.
        Assert.Equal("10", Rank("The Twilight Zone", null, newer, spinOff, older)[0].ProviderId);
    }

    [Fact]
    public void A_title_found_in_both_sources_is_listed_once_keeping_the_copy_with_artwork_and_TheTVDBs_id()
    {
        var tmdbOnly = Show("73001", "The Twilight Zone", 1959, poster: null);
        var withArtwork = Show("73002", "The Twilight Zone", 1959, poster: "https://img/zone.jpg", tvdb: "73");

        var results = Rank("The Twilight Zone 1959".Replace(" 1959", string.Empty), 1959, tmdbOnly, withArtwork);

        var only = Assert.Single(results);
        Assert.Equal("73", only.TvDbId);
        Assert.Equal("https://img/zone.jpg", only.PosterUrl);
    }

    [Fact]
    public void Two_shows_with_the_same_title_in_different_years_are_both_kept()
    {
        var results = MetadataSearchRanking.Deduplicate([Show("1", "Dune", 1984), Show("2", "Dune", 2021)]);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Accents_and_punctuation_do_not_stop_a_title_matching()
    {
        var results = Rank("amelie", null, Show("1", "Something Else", 2001), Show("2", "Amélie", 2001));

        Assert.Equal("2", results[0].ProviderId);
    }

    private static IReadOnlyList<MetadataSearchResult> Rank(string query, int? year, params MetadataSearchResult[] results)
        => MetadataSearchRanking.Rank(results, query, year);

    private static MetadataSearchResult Show(
        string id, string title, int? year, double? rating = null, string? poster = null, string? tvdb = null)
        => new(
            "tmdb", id, "tv", title, null, year, null, poster, null, rating, [], [], null, null,
            TvDbId: tvdb);
}
