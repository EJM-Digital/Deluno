using Deluno.Contracts;

namespace Deluno.Platform.Tests.Contracts;

public sealed class CatalogueFiltersTests
{
    [Fact]
    public void A_condition_with_values_is_written_field_operator_then_the_values()
        => Assert.Equal("quality:in:1080p|2160p", CatalogueFilters.Where("quality", CatalogueFilterOperator.Includes, "1080p", "2160p").Encode());

    [Fact]
    public void A_condition_that_takes_no_value_is_written_without_a_third_part()
        => Assert.Equal("path:set", CatalogueFilters.Where("path", CatalogueFilterOperator.IsSet).Encode());

    [Fact]
    public void No_conditions_at_all_is_empty_and_so_is_an_empty_list()
    {
        Assert.True(CatalogueFilters.None.IsEmpty);
        Assert.True(new CatalogueFilters([]).IsEmpty);
        Assert.False(CatalogueFilters.Of(CatalogueFilters.Where("year", CatalogueFilterOperator.AtLeast, "2000")).IsEmpty);
        Assert.Same(CatalogueFilters.None, CatalogueFilters.Of());
    }

    [Fact]
    public void Narrowing_further_keeps_what_was_asked_before()
    {
        var one = CatalogueFilters.None.And("year", CatalogueFilterOperator.AtLeast, "2000");
        var two = one.And("year", CatalogueFilterOperator.AtMost, "2010");

        Assert.Single(one.Conditions!);
        Assert.Equal(["year:min:2000", "year:max:2010"], two.Conditions!.Select(c => c.Encode()));
    }

    [Fact]
    public void Filters_written_on_a_query_string_are_read_back_as_conditions()
    {
        var parsed = CatalogueFilters.Parse(MediaKind.Movie, ["quality:in:1080p| 2160p |", "path:set", "year:min:2000"], out var errors);

        Assert.Empty(errors);
        Assert.Equal(["quality:in:1080p|2160p", "path:set", "year:min:2000"], parsed.Conditions!.Select(c => c.Encode()));
    }

    [Fact]
    public void A_value_with_a_colon_in_it_survives_because_only_two_colons_split()
    {
        var parsed = CatalogueFilters.Parse(MediaKind.Movie, ["path:has:D:\\Films"], out var errors);

        Assert.Empty(errors);
        Assert.Equal(["D:\\Films"], parsed.Conditions!.Single().Values);
    }

    [Fact]
    public void Blank_entries_and_no_list_ask_for_nothing()
    {
        Assert.Same(CatalogueFilters.None, CatalogueFilters.Parse(MediaKind.Movie, [null, "", "  "], out var blank));
        Assert.Empty(blank);
        Assert.Same(CatalogueFilters.None, CatalogueFilters.Parse(MediaKind.Movie, null, out var none));
        Assert.Empty(none);
    }

    [Theory]
    [InlineData(MediaKind.Movie, "oops", "“oops” is not a filter. Write it as field:operator:value.")]
    [InlineData(MediaKind.Movie, "nextAiring:before:2020-01-01", "There is no “nextAiring” to filter movies by.")]
    [InlineData(MediaKind.Series, "bogus:is:1", "There is no “bogus” to filter TV by.")]
    [InlineData(MediaKind.Movie, "quality:starts:Rem", "“Quality” cannot be compared with “starts”.")]
    [InlineData(MediaKind.Movie, "quality:wobble:Rem", "“Quality” cannot be compared with “wobble”.")]
    [InlineData(MediaKind.Movie, "quality:in", "“Quality” needs a value.")]
    [InlineData(MediaKind.Movie, "quality:in:||", "“Quality” needs a value.")]
    public void Something_the_catalogue_cannot_answer_is_refused_in_plain_words(MediaKind kind, string entry, string expected)
    {
        var parsed = CatalogueFilters.Parse(kind, [entry], out var errors);

        Assert.Equal(expected, Assert.Single(errors));
        Assert.True(parsed.IsEmpty);
    }

    [Fact]
    public void One_bad_filter_does_not_hide_the_good_ones_beside_it()
    {
        var parsed = CatalogueFilters.Parse(MediaKind.Movie, ["bogus:is:1", "year:min:2000"], out var errors);

        Assert.Single(errors);
        Assert.Equal("year:min:2000", Assert.Single(parsed.Conditions!).Encode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void A_saved_view_with_nothing_in_it_is_an_empty_filter(string? raw)
    {
        Assert.True(CatalogueFilters.TryParseJson(MediaKind.Movie, raw, out var filters));
        Assert.Same(CatalogueFilters.None, filters);
    }

    [Fact]
    public void A_saved_view_is_read_by_operator_token_name_or_number_and_by_any_letter_case()
    {
        const string json = """
            [
              {"Field": " year ", "OPERATOR": "min", "values": ["2000"]},
              {"field": "year", "operator": "AtMost", "values": ["2010"]},
              {"field": "year", "operator": 5, "values": ["1990"]},
              {"field": "path", "operator": "set", "values": []}
            ]
            """;

        Assert.True(CatalogueFilters.TryParseJson(MediaKind.Movie, json, out var filters));
        Assert.Equal(["year:min:2000", "year:max:2010", "year:min:1990", "path:set"], filters.Conditions!.Select(c => c.Encode()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"text\"")]
    [InlineData("[1]")]
    [InlineData("[{\"operator\":\"min\",\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":3,\"operator\":\"min\",\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"  \",\"operator\":\"min\",\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":null,\"operator\":\"min\",\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"year\",\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":\"sideways\",\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":\"99\",\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":99,\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":1.5,\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":true,\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":\"min\"}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":\"min\",\"values\":\"1\"}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":\"min\",\"values\":[1]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":\"min\",\"values\":[]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":\"min\",\"values\":[\" \"]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":\"min\",\"values\":[null]}]")]
    [InlineData("[{\"field\":\"path\",\"operator\":\"set\",\"values\":[\"x\"]}]")]
    [InlineData("[{\"field\":\"nextAiring\",\"operator\":\"min\",\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"quality\",\"operator\":\"starts\",\"values\":[\"1\"]}]")]
    [InlineData("[{\"field\":\"year\",\"operator\":\"min\",\"values\":[\"1\"]},3]")]
    [InlineData("not json")]
    public void An_unreadable_saved_view_is_refused_rather_than_becoming_an_unfiltered_search(string json)
    {
        Assert.False(CatalogueFilters.TryParseJson(MediaKind.Movie, json, out var filters));
        Assert.Same(CatalogueFilters.None, filters);
    }

    [Fact]
    public void The_old_flat_parameters_become_conditions_in_a_fixed_order()
    {
        var conditions = CatalogueFilters.FromLegacyParameters("1080p, 2160p,1080P", "Drama", 1.5, 40, 1990, 2020, 80, 180, 7.5);

        Assert.Equal(
            ["quality:in:1080p|2160p", "genre:all:Drama", "size:min:1.5", "size:max:40", "year:min:1990", "year:max:2020", "runtime:min:80", "runtime:max:180", "rating:min:7.5"],
            conditions.Select(c => c.Encode()));
    }

    [Fact]
    public void Blank_old_parameters_ask_for_nothing()
        => Assert.Empty(CatalogueFilters.FromLegacyParameters(" ", ",, ,", null, null, null, null, null, null, null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" , ,")]
    public void A_comma_list_with_nothing_in_it_is_no_list(string? value)
        => Assert.Null(CatalogueFilters.ParseList(value));

    [Fact]
    public void A_comma_list_drops_blanks_and_repeats()
        => Assert.Equal(["a", "b"], CatalogueFilters.ParseList("a,, A , b,"));

    [Fact]
    public void Old_and_new_parameters_are_added_together_into_one_filter()
    {
        var built = CatalogueFilters.TryBuild(MediaKind.Movie, ["path:set"], "1080p", null, null, null, 2000, null, null, null, null, out var filters, out var errors);

        Assert.True(built);
        Assert.Empty(errors);
        Assert.Equal(["path:set", "quality:in:1080p", "year:min:2000"], filters.Conditions!.Select(c => c.Encode()));
    }

    [Fact]
    public void With_only_new_parameters_the_filter_is_just_those()
    {
        var built = CatalogueFilters.TryBuild(MediaKind.Movie, ["path:set"], null, null, null, null, null, null, null, null, null, out var filters, out _);

        Assert.True(built);
        Assert.Equal("path:set", Assert.Single(filters.Conditions!).Encode());
    }

    [Fact]
    public void With_only_old_parameters_the_filter_is_just_those()
    {
        var built = CatalogueFilters.TryBuild(MediaKind.Movie, null, null, "Drama", null, null, null, null, null, null, null, out var filters, out _);

        Assert.True(built);
        Assert.Equal("genre:all:Drama", Assert.Single(filters.Conditions!).Encode());
    }

    [Fact]
    public void A_request_with_a_bad_condition_is_refused_whatever_else_it_says()
    {
        var built = CatalogueFilters.TryBuild(MediaKind.Movie, ["bogus:is:1"], "1080p", null, null, null, null, null, null, null, null, out var filters, out var errors);

        Assert.False(built);
        Assert.Single(errors);
        Assert.Same(CatalogueFilters.None, filters);
    }

    [Fact]
    public void A_request_with_no_conditions_and_no_old_parameters_asks_for_nothing()
    {
        Assert.True(CatalogueFilters.TryBuild(MediaKind.Series, null, null, null, null, null, null, null, null, null, null, out var filters, out _));
        Assert.True(filters.IsEmpty);
    }
}
