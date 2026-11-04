using Deluno.Contracts;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// The corners of reading a release name: a name that is nothing but a separator before its quality gives no title, a show searched
/// by season or by absolute number that a name does not number is judged by what it says, and an episode whose own title cleans to
/// nothing ("The") never counts as the title a name carries. Refs #1268.
/// </summary>
public sealed class AReleaseNameWithNoTitleOrNoEpisodeIsReadAsItSaysTests
{
    [Fact]
    public void A_name_that_is_only_a_separator_before_its_quality_gives_back_the_whole_name_as_its_title()
    {
        var (title, year) = ReleaseTitleMatch.ParseTitleAndYear("-.1080p.WEB-DL-GRP");

        Assert.Equal("-.1080p.WEB-DL-GRP", title);
        Assert.Null(year);
    }

    [Fact]
    public void A_name_that_numbers_no_episode_cannot_be_the_one_searched_for_by_absolute_number()
    {
        var expected = new ReleaseTitleExpectation("Some Show", null, IsTv: true, AbsoluteNumber: 5);

        Assert.Equal("The name does not say which episode this is.", ReleaseTitleMatch.Mismatch("Some.Show.720p.WEB-DL-GRP", expected));
    }

    [Fact]
    public void A_name_that_numbers_no_episode_is_not_refused_for_a_search_of_a_whole_season()
    {
        var expected = new ReleaseTitleExpectation("Some Show", null, IsTv: true, Season: 1);

        Assert.Null(ReleaseTitleMatch.Mismatch("Some.Show.720p.WEB-DL-GRP", expected));
    }

    [Fact]
    public void An_episode_whose_own_title_cleans_to_nothing_is_never_the_title_a_name_carries()
    {
        var titles = new Dictionary<int, string> { [1] = "The", [2] = "Other Thing" };
        var expected = new ReleaseTitleExpectation("Some Show", null, IsTv: true, Season: 1, Episode: 1, EpisodeTitles: titles);

        var refused = ReleaseTitleMatch.Mismatch("Some.Show.S01E01.Other.Thing.720p.WEB-DL-GRP", expected);

        Assert.NotNull(refused);
        Assert.Contains("but its title is S01E02's \"Other Thing\"", refused, StringComparison.Ordinal);
        Assert.Equal((2, "Other Thing"), ReleaseTitleMatch.MislabelledAs("Some.Show.S01E01.Other.Thing.720p.WEB-DL-GRP", expected));
    }
}
