using Deluno.Api.Discover;
using Deluno.Integrations.Metadata;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1429: the Discover page's filters become TMDb's discover filters, for films and for shows.</summary>
public sealed class DiscoverFiltersTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static DiscoverListQuery Ask(DiscoverFilterQuery query, string mediaType = "movies")
        => DiscoverFilters.ToListQuery(query, mediaType, 1, Today);

    [Fact]
    public void With_nothing_set_it_is_the_popular_list_and_the_list_asked_for_is_kept_when_TMDb_offers_it()
    {
        Assert.Equal("popular", Ask(new DiscoverFilterQuery()).List);
        Assert.Equal("trending", Ask(new DiscoverFilterQuery(List: "Trending")).List);
        Assert.Equal("day", Ask(new DiscoverFilterQuery(List: "trending", Window: "day")).Window);
        Assert.Equal("week", Ask(new DiscoverFilterQuery(List: "trending", Window: "hour")).Window);
        Assert.Equal("popular", Ask(new DiscoverFilterQuery(List: "upcoming"), "tv").List);
        Assert.Equal("upcoming", Ask(new DiscoverFilterQuery(List: "upcoming")).List);
        Assert.Equal("popular", Ask(new DiscoverFilterQuery(List: "discover")).List);
        Assert.Equal("popular", Ask(new DiscoverFilterQuery(List: "bogus")).List);
        Assert.Null(Ask(new DiscoverFilterQuery()).Filters);
    }

    [Fact]
    public void Released_means_up_to_today_and_upcoming_means_from_tomorrow_and_a_year_range_narrows_either()
    {
        Assert.Equal("2026-10-29", Ask(new DiscoverFilterQuery(Release: "released")).Filters!["primary_release_date.lte"]);
        Assert.Equal("2020-12-31", Ask(new DiscoverFilterQuery(Release: "released", YearTo: 2020)).Filters!["primary_release_date.lte"]);
        Assert.Equal("2026-10-29", Ask(new DiscoverFilterQuery(Release: "released", YearTo: 2030)).Filters!["primary_release_date.lte"]);
        Assert.Equal("2026-10-30", Ask(new DiscoverFilterQuery(Release: "upcoming")).Filters!["primary_release_date.gte"]);
        Assert.Equal("2028-01-01", Ask(new DiscoverFilterQuery(Release: "UPCOMING", YearFrom: 2028)).Filters!["primary_release_date.gte"]);
        Assert.Equal("2026-10-30", Ask(new DiscoverFilterQuery(Release: "upcoming", YearFrom: 2024)).Filters!["primary_release_date.gte"]);
        var range = Ask(new DiscoverFilterQuery(YearFrom: 1999, YearTo: 2005)).Filters!;
        Assert.Equal("1999-01-01", range["primary_release_date.gte"]);
        Assert.Equal("2005-12-31", range["primary_release_date.lte"]);
        Assert.Equal("discover", Ask(new DiscoverFilterQuery(YearFrom: 1999)).List);
    }

    [Fact]
    public void A_show_is_filtered_on_its_first_air_date_and_a_film_on_its_release_date()
    {
        var show = Ask(new DiscoverFilterQuery(Release: "upcoming", Sort: "oldest"), "tv").Filters!;
        Assert.Equal("2026-10-30", show["first_air_date.gte"]);
        Assert.Equal("first_air_date.asc", show["sort_by"]);
        Assert.DoesNotContain("primary_release_date.gte", show.Keys);
        Assert.Equal("primary_release_date.desc", Ask(new DiscoverFilterQuery(Sort: "newest")).Filters!["sort_by"]);
    }

    [Fact]
    public void Genre_rating_votes_language_and_runtime_are_passed_on_and_what_is_not_understood_is_dropped()
    {
        var filters = Ask(new DiscoverFilterQuery(
            Genres: "28, 12,28,abc,123456789", RatingMin: 7.25, VotesMin: 50, Language: " EN ", RuntimeMin: 80, RuntimeMax: 120, Sort: "popular")).Filters!;

        Assert.Equal("28,12", filters["with_genres"]);
        Assert.Equal("7.3", filters["vote_average.gte"]);
        Assert.Equal("50", filters["vote_count.gte"]);
        Assert.Equal("en", filters["with_original_language"]);
        Assert.Equal("80", filters["with_runtime.gte"]);
        Assert.Equal("120", filters["with_runtime.lte"]);
        Assert.DoesNotContain("sort_by", filters.Keys);

        Assert.Null(Ask(new DiscoverFilterQuery(Genres: "abc", RatingMin: 0, VotesMin: 0, Language: "e", RuntimeMin: 0, RuntimeMax: -1, YearFrom: 1500, YearTo: 3000, Release: "never")).Filters);
        Assert.Null(Ask(new DiscoverFilterQuery(RatingMin: 11, Language: "e1")).Filters);
        Assert.Equal("10", Ask(new DiscoverFilterQuery(RatingMin: 10)).Filters!["vote_average.gte"]);
        Assert.Equal("1888-01-01", Ask(new DiscoverFilterQuery(YearFrom: 1888)).Filters!["primary_release_date.gte"]);
        Assert.Equal("2100-12-31", Ask(new DiscoverFilterQuery(YearTo: 2100)).Filters!["primary_release_date.lte"]);
        Assert.Null(Ask(new DiscoverFilterQuery(YearFrom: 1887, YearTo: 2101)).Filters);
    }

    [Fact]
    public void Sorting_by_rating_asks_for_a_vote_floor_unless_one_was_set()
    {
        Assert.Equal("200", Ask(new DiscoverFilterQuery(Sort: "rating")).Filters!["vote_count.gte"]);
        Assert.Equal("30", Ask(new DiscoverFilterQuery(Sort: "rating", VotesMin: 30)).Filters!["vote_count.gte"]);
    }
}
