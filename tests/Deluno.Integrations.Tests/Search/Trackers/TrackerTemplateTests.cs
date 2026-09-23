using System.Net;
using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>#606: the Go-template subset definitions are written in, with Cardigann's meanings.</summary>
public sealed class TrackerTemplateTests
{
    private static readonly Dictionary<string, object?> Variables = new()
    {
        [".Keywords"] = "night of the living dead",
        [".Config.sort"] = "time",
        [".Config.disablesort"] = null,
        [".Config.freeleech"] = "True",
        [".True"] = "True",
        [".False"] = null,
        [".Categories"] = new List<string> { "1", "4" },
        [".Result.title_optional"] = "",
        [".Result.title_default"] = "Default Title",
        [".Query.IMDBID"] = null
    };

    [Fact]
    public void Writes_variables_and_leaves_the_text_alone()
        => Assert.Equal("search/night of the living dead/time/1/", TrackerTemplate.Render("search/{{ .Keywords }}/{{ .Config.sort }}/1/", Variables));

    [Fact]
    public void Encodes_only_what_the_actions_write()
        => Assert.Equal("search/night+of+the+living+dead/1/", TrackerTemplate.Render("search/{{ .Keywords }}/1/", Variables, WebUtility.UrlEncode));

    [Fact]
    public void A_checkbox_that_is_off_equals_False()
    {
        // 1337x's path: sort unless sorting is turned off.
        const string path = "{{ if and (.Keywords) (eq .Config.disablesort .False) }}sort-{{ else }}{{ end }}search";
        Assert.Equal("sort-search", TrackerTemplate.Render(path, Variables));
    }

    [Fact]
    public void Forgives_a_stray_closing_bracket_as_Prowlarr_does()
        => Assert.Equal("/time", TrackerTemplate.Render("{{ if and (.Keywords) (eq .Config.disablesort .False)) }}/{{ .Config.sort }}{{ else }}{{ end }}", Variables));

    [Fact]
    public void Or_returns_the_first_value_that_is_set()
        => Assert.Equal("Default Title", TrackerTemplate.Render("{{ or .Result.title_optional .Result.title_default }}", Variables));

    [Fact]
    public void An_empty_result_is_false()
        => Assert.Equal("Default Title", TrackerTemplate.Render("{{ if .Result.title_optional }}{{ .Result.title_optional }}{{ else }}{{ .Result.title_default }}{{ end }}", Variables));

    [Fact]
    public void Ranges_over_the_categories()
        => Assert.Equal("cat1=1&cat4=1&", TrackerTemplate.Render("{{ range .Categories }}cat{{.}}=1&{{end}}", Variables));

    [Fact]
    public void Joins_the_categories()
        => Assert.Equal("1,4", TrackerTemplate.Render("{{ join .Categories \",\" }}", Variables));

    [Fact]
    public void Replaces_with_a_regular_expression()
        => Assert.Equal("timesort", TrackerTemplate.Render("{{ re_replace .Config.sort \"$\" \"sort\" }}", Variables));

    [Fact]
    public void Chooses_between_else_if_branches()
        => Assert.Equal("b", TrackerTemplate.Render("{{ if .Query.IMDBID }}a{{ else if .Config.freeleech }}b{{ else }}c{{ end }}", Variables));

    [Fact]
    public void Eq_compares_against_any_of_several()
        => Assert.Equal("yes", TrackerTemplate.Render("{{ if eq .Config.sort \"size\" \"time\" }}yes{{ else }}no{{ end }}", Variables));

    [Fact]
    public void Refuses_a_function_it_does_not_have()
        => Assert.Throws<FormatException>(() => TrackerTemplate.Validate("{{ printf \"%s\" .Keywords }}"));
}
