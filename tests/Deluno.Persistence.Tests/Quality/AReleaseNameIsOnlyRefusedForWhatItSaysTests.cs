using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// The corners of the release-name check: a name is refused only for what it actually says, and every guard that
/// has nothing to compare (no year, no episode titles, no marker, a range of episodes) lets it through.
/// </summary>
public sealed class AReleaseNameIsOnlyRefusedForWhatItSaysTests
{
    private static readonly IReadOnlyDictionary<int, string> Titles = new Dictionary<int, string>
    {
        [1] = "Pilot",
        [2] = "The Childrens Book Job",
        [3] = "  ",
        [4] = "Ox",
    };

    private static ReleaseTitleExpectation Show(
        int? season = 1,
        int? episode = null,
        int? year = 2010,
        IReadOnlyDictionary<int, string>? titles = null,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>? bySeason = null,
        int? absolute = null,
        DateOnly? airDate = null)
        => new("Some Show", year, IsTv: true, Season: season, Episode: episode, AbsoluteNumber: absolute, AirDate: airDate, EpisodeTitles: titles, SeasonEpisodeTitles: bySeason);

    [Fact]
    public void A_film_with_no_year_asked_for_accepts_any_year_and_a_name_with_no_year_is_accepted_for_a_film_with_one()
    {
        var noYear = new ReleaseTitleExpectation("Some Film", null, IsTv: false);
        var withYear = new ReleaseTitleExpectation("Some Film", 2001, IsTv: false);

        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Film.1999.1080p", noYear));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Film.1080p.BluRay", withYear));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Film.2002.1080p", withYear));
        Assert.Equal("This release is from 1999; Some Film is from 2001.", ReleaseTitleMatch.Mismatch("Some.Film.1999.1080p", withYear));
    }

    [Fact]
    public void A_series_year_in_the_name_is_compared_only_when_there_is_a_year_to_compare()
    {
        Assert.False(ReleaseTitleMatch.CarriesSeriesYear("Some.Show.2010.S01E01.720p", Show(year: null)));
        Assert.False(ReleaseTitleMatch.CarriesSeriesYear("Some.Show.S01E01.720p", Show()));
        Assert.False(ReleaseTitleMatch.CarriesSeriesYear("Some.Show.2019.S01E01.720p", Show()));
        Assert.True(ReleaseTitleMatch.CarriesSeriesYear("Some.Show.2011.S01E01.720p", Show()));
        Assert.True(ReleaseTitleMatch.CarriesSeriesYear("Some.Show.2010.S01.720p", Show()));
        // A date-named daily release is an air date, not a year.
        Assert.False(ReleaseTitleMatch.CarriesSeriesYear("Some.Show.2010.05.01.S01E01.720p", Show()));
    }

    [Fact]
    public void A_name_from_another_year_of_the_show_is_refused_unless_the_search_is_for_a_dated_episode()
    {
        Assert.StartsWith("This release is for another show", ReleaseTitleMatch.Mismatch("Some.Show.2019.S01E01.720p", Show(episode: 1)));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.2019.S01E01.720p", Show(episode: 1, year: null)));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.2019.S01E01.720p", Show(episode: 1, airDate: new DateOnly(2019, 5, 1))));
    }

    [Fact]
    public void An_episode_title_is_compared_with_the_other_episodes_titles_only_when_it_names_one_of_them()
    {
        var expected = Show(episode: 1, titles: Titles);

        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S01E01.Pilot.720p", expected));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S01E01.720p", expected));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S01E01.Something.Unlisted.720p", expected));
        // A title of fewer than three letters, or a blank one, is not enough to say whose it is.
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S01E01.Ox.720p", expected));
        Assert.Equal(
            "The name says S01E01, but its title is S01E02's \"The Childrens Book Job\".",
            ReleaseTitleMatch.Mismatch("Some.Show.S01E01.The.Childrens.Book.Job.720p", expected));
        Assert.Null(ReleaseTitleMatch.EpisodeTitleMismatch("Pilot", 1, 1, Show()));
    }

    [Fact]
    public void A_name_carrying_another_episodes_title_is_returned_as_the_episode_it_really_is()
    {
        var expected = Show(episode: 1, titles: Titles);

        Assert.Equal((2, "The Childrens Book Job"), ReleaseTitleMatch.MislabelledAs("Some.Show.S01E01.The.Childrens.Book.Job.720p", expected));
        Assert.Null(ReleaseTitleMatch.MislabelledAs("Some.Show.S01E01.Pilot.720p", expected));
        Assert.Null(ReleaseTitleMatch.MislabelledAs("Some.Show.S01E01.720p", expected));
        Assert.Null(ReleaseTitleMatch.MislabelledAs("Some.Show.S01E02.The.Childrens.Book.Job.720p", expected));
        Assert.Null(ReleaseTitleMatch.MislabelledAs("Some.Show.S01E01.The.Childrens.Book.Job.720p", expected with { Episode = null }));
        Assert.Null(ReleaseTitleMatch.MislabelledAs("Some.Show.S01E01.The.Childrens.Book.Job.720p", expected with { IsTv = false }));
        Assert.Null(ReleaseTitleMatch.MislabelledAs("Some.Show.S01E01.The.Childrens.Book.Job.720p", expected with { EpisodeTitles = null }));
        Assert.Null(ReleaseTitleMatch.MislabelledAs("Some.Show.S01E01.The.Childrens.Book.Job.720p", expected with { EpisodeTitles = new Dictionary<int, string>() }));
        // A name for another season is refused for that, not for a title.
        Assert.Null(ReleaseTitleMatch.MislabelledAs("Some.Show.S02E01.The.Childrens.Book.Job.720p", expected));
    }

    [Fact]
    public void A_search_of_the_whole_show_checks_a_single_episode_name_against_its_own_seasons_titles()
    {
        var bySeason = new Dictionary<int, IReadOnlyDictionary<int, string>> { [1] = Titles };
        var wholeShow = Show(season: null, bySeason: bySeason);

        Assert.Equal(
            "The name says S01E01, but its title is S01E02's \"The Childrens Book Job\".",
            ReleaseTitleMatch.Mismatch("Some.Show.S01E01.The.Childrens.Book.Job.720p", wholeShow));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S01E01.Pilot.720p", wholeShow));
        // A season the provider lists nothing for, a range of episodes, a name with no marker, and no titles at all.
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S05E01.The.Childrens.Book.Job.720p", wholeShow));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S01E01E02.The.Childrens.Book.Job.720p", wholeShow));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.Complete.720p", wholeShow));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S01E01.The.Childrens.Book.Job.720p", Show(season: null)));
    }

    [Fact]
    public void An_absolute_number_or_a_missing_episode_is_judged_by_what_the_name_says()
    {
        Assert.Null(ReleaseTitleMatch.Mismatch("Some Show - 105 [720p]", Show(season: null, absolute: 105)));
        Assert.Equal(
            "This release is episode 106; the search was for episode 105.",
            ReleaseTitleMatch.Mismatch("Some Show - 106 [720p]", Show(season: null, absolute: 105)));
        Assert.Equal(
            "The name does not say which episode this is.",
            ReleaseTitleMatch.Mismatch("Some Show 720p WEB", Show(season: null, absolute: 105)));
        Assert.Equal(
            "The name does not say which episode this is.",
            ReleaseTitleMatch.Mismatch("Some Show 720p WEB", Show(season: null, episode: 3)));
    }

    [Fact]
    public void A_name_is_not_taken_for_a_different_air_date_or_a_season_pack_when_one_episode_is_wanted()
    {
        Assert.Equal(
            "This release is dated 2024-05-02; the search was for 2024-05-01.",
            ReleaseTitleMatch.Mismatch("Some.Show.2024.05.02.720p", Show(season: null, year: null, airDate: new DateOnly(2024, 5, 1))));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.2024.05.01.720p", Show(season: null, year: null, airDate: new DateOnly(2024, 5, 1))));
        Assert.Equal(
            "This is a whole-season pack; the search was for one episode.",
            ReleaseTitleMatch.Mismatch("Some.Show.S01.720p.WEB", Show(episode: 4)));
        Assert.Equal(
            "This release is season 2; the search was for season 1.",
            ReleaseTitleMatch.Mismatch("Some.Show.S02.720p.WEB", Show()));
        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.S01.720p.WEB", Show()));
    }
}
