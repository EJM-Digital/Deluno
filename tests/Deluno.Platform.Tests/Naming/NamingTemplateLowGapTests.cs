using Deluno.Platform;

namespace Deluno.Platform.Tests.Naming;

/// <summary>
/// Low-severity gap rows (#1329) for the naming renderer: what a name looks like when a value is missing, and
/// what a token Deluno does not know does to the name.
/// </summary>
public sealed class NamingTemplateLowGapTests
{
    [Fact]
    public void Empty_wrappers_left_by_a_missing_value_are_removed_a_missing_year_reads_Unknown_Year_and_an_empty_result_falls_back_to_the_title_then_Untitled()
    {
        // () and [] left by an empty token vanish from a folder name.
        var folder = NamingTemplateRenderer.RenderFolder("{Movie Title} ({Edition}) [{IMDb ID}]", "Arrival", 2016);
        Assert.Equal("Arrival", folder);

        // The same for a file-name segment.
        var segment = NamingTemplateRenderer.RenderSegment("{Movie Title} ({Edition}) [{Quality}]", "Arrival", 2016);
        Assert.Equal("Arrival", segment);

        // A missing year renders "Unknown Year", never a blank or a zero.
        Assert.Equal("Arrival (Unknown Year)", NamingTemplateRenderer.RenderFolder("{Movie Title} ({Release Year})", "Arrival", null));

        // A template that renders to nothing falls back to the title.
        Assert.Equal("Arrival", NamingTemplateRenderer.RenderFolder("{Tag}", "Arrival", 2016));

        // And with no title either, to "Untitled".
        Assert.Equal("Untitled", NamingTemplateRenderer.RenderFolder("{Tag}", null, 2016));
    }

    [Fact]
    public void A_token_Deluno_does_not_know_stays_visible_in_the_name_beside_the_tokens_it_does()
    {
        var segment = NamingTemplateRenderer.RenderSegment("{Movie Title} {My Own Token} ({Release Year})", "Arrival", 2016);

        Assert.Equal("Arrival {My Own Token} (2016)", segment);
    }

    [Fact]
    public void Quality_Profile_Tag_and_Network_render_from_what_they_are_given_and_render_empty_when_absent()
    {
        var given = NamingTemplateRenderer.RenderSegment(
            "{Movie Title} - {Quality Profile} - {Tag} - {Network} - {Genre}",
            "Arrival", 2016, qualityProfile: "HD-1080p", genre: "Drama", tag: "kids", network: "HBO");
        Assert.Equal("Arrival - HD-1080p - kids - HBO - Drama", given);

        var absent = NamingTemplateRenderer.RenderSegment(
            "{Movie Title} [{Quality Profile}] [{Tag}] ({Network})",
            "Arrival", 2016);
        Assert.Equal("Arrival", absent);
    }
}
