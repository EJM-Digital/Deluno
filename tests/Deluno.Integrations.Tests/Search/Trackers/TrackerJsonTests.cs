using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>Reading a tracker's JSON reply by the selectors its definition names. #1268.</summary>
public sealed class TrackerJsonTests
{
    private const string Reply = """
        {
          "data": {
            "items": [
              { "name": "Dune", "size": 12, "free": true, "dead": false, "gone": null, "tags": ["a", "b"], "meta": { "year": 2021 } },
              { "name": "Arrival", "size": 7.5 },
              { "name": "Sicario", "free": true }
            ],
            "byId": { "x": { "name": "First" }, "y": { "name": "Second" } },
            "odd key": "spaced"
          },
          "total": 3, // a comment, and a trailing comma follows
        }
        """;

    private static readonly System.Text.Json.Nodes.JsonNode? Root = TrackerJson.Parse(Reply);

    [Fact]
    public void A_reply_with_comments_and_a_trailing_comma_is_still_read()
    {
        Assert.Equal("3", TrackerJson.Value(Root, "total"));
    }

    [Fact]
    public void Rows_are_the_elements_of_an_array_or_the_values_of_an_object_and_nothing_otherwise()
    {
        Assert.Equal(3, TrackerJson.Rows(Root, "data.items").Count);
        Assert.Equal(3, TrackerJson.Rows(Root, "$.data.items").Count);
        Assert.Equal(["First", "Second"], TrackerJson.Rows(Root, "data.byId").Select(row => TrackerJson.Value(row, "name")));
        Assert.Empty(TrackerJson.Rows(Root, "total"));
        Assert.Empty(TrackerJson.Rows(Root, "data.missing"));
    }

    [Fact]
    public void Rows_can_be_kept_by_what_they_have_lack_or_contain()
    {
        Assert.Equal(["Dune", "Sicario"], TrackerJson.Rows(Root, "data.items:has(free)").Select(row => TrackerJson.Value(row, "name")));
        Assert.Equal(["Arrival"], TrackerJson.Rows(Root, "data.items:not(free)").Select(row => TrackerJson.Value(row, "name")));
        Assert.Equal(["Arrival"], TrackerJson.Rows(Root, "data.items:contains(Arr)").Select(row => TrackerJson.Value(row, "name")));
        Assert.Equal(["Dune"], TrackerJson.Rows(Root, "data.items:has(free):has(size):not(dead2)").Select(row => TrackerJson.Value(row, "name")));
        Assert.Equal(3, TrackerJson.Rows(Root, "data.items:unknown(x)").Count);
        Assert.Equal(3, TrackerJson.Rows(Root, "data.items:nopars").Count);
        Assert.Equal(3, TrackerJson.Rows(Root, "data.items:has(free").Count);
        Assert.Empty(TrackerJson.Rows(Root, "data.items:contains(Du(n)e)"));
        Assert.Equal(["Dune", "Sicario"], TrackerJson.Rows(Root, "data.items:has(free)x:not(free)").Select(row => TrackerJson.Value(row, "name")));
        var quoted = TrackerJson.Parse("""{"rows":["say \"hi\"","other"]}""");
        Assert.Single(TrackerJson.Rows(quoted, "rows:contains(say \"hi\")"));
    }

    [Fact]
    public void A_path_reaches_names_quoted_names_and_positions_and_gives_nothing_for_a_miss()
    {
        Assert.Same(Root, TrackerJson.Select(Root, "$"));
        Assert.Same(Root, TrackerJson.Select(Root, "  "));
        Assert.Equal("Dune", TrackerJson.Value(Root, "data.items[0].name"));
        Assert.Equal("Sicario", TrackerJson.Value(Root, "data.items[2].name"));
        Assert.Equal("spaced", TrackerJson.Value(Root, "data['odd key']"));
        Assert.Equal("spaced", TrackerJson.Value(Root, "data[\"odd key\"]"));
        Assert.Equal("2021", TrackerJson.Value(Root, "data.items[0].meta.year"));
        Assert.Null(TrackerJson.Select(Root, "data.items[9]"));
        Assert.Null(TrackerJson.Select(Root, "data.items.name"));
        Assert.Null(TrackerJson.Select(Root, "data[0]"));
        Assert.Null(TrackerJson.Select(Root, "data['missing']"));
        Assert.Null(TrackerJson.Select(Root, "nope.deeper.still"));
        Assert.Null(TrackerJson.Select(null, "a"));
    }

    [Fact]
    public void A_value_is_text_whatever_the_kind_and_a_failed_test_is_no_value()
    {
        Assert.Equal("Dune", TrackerJson.Value(Root, "data.items[0].name"));
        Assert.Equal("12", TrackerJson.Value(Root, "data.items[0].size"));
        Assert.Equal("7.5", TrackerJson.Value(Root, "data.items[1].size"));
        Assert.Equal("True", TrackerJson.Value(Root, "data.items[0].free"));
        Assert.Equal("False", TrackerJson.Value(Root, "data.items[0].dead"));
        Assert.Null(TrackerJson.Value(Root, "data.items[0].gone"));
        Assert.Null(TrackerJson.Value(Root, "data.items[0].absent"));
        Assert.Equal("""["a","b"]""", TrackerJson.Value(Root, "data.items[0].tags"));
        Assert.Equal("""{"year":2021}""", TrackerJson.Value(Root, "data.items[0].meta"));
        Assert.Null(TrackerJson.Value(Root, "data.items[0].name:has(x)"));
        Assert.Null(TrackerJson.Value(Root, "data.items[0].name:contains(zzz)"));
        Assert.Equal("Dune", TrackerJson.Value(Root, "data.items[0].name:contains(Dun)"));
        Assert.Null(TrackerJson.Text(null));
    }

    [Fact]
    public void An_array_is_joined_and_anything_else_is_its_text()
    {
        const string json = """{"tags":["a","b",null],"one":"solo","num":4}""";

        Assert.Equal("a|b|", TrackerJson.JoinArray(json, "tags", "|"));
        Assert.Equal("solo", TrackerJson.JoinArray(json, "one", "|"));
        Assert.Equal("4", TrackerJson.JoinArray(json, "num", "|"));
        Assert.Equal(string.Empty, TrackerJson.JoinArray(json, "absent", "|"));
    }
}
