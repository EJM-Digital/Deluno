using Deluno.Platform;

namespace Deluno.Platform.Tests;

public sealed class NamingTemplateRendererTests
{
    [Fact]
    public void Renders_identifiers_and_keeps_folder_grouping()
    {
        var rendered = NamingTemplateRenderer.RenderFolder(
            "{Genre}\\{Movie Title} ({Release Year}) [{IMDb ID}]",
            "Big Buck Bunny",
            2008,
            imdbId: "tt1254207",
            genre: "Animation");

        Assert.Equal(
            Path.Combine("Animation", "Big Buck Bunny (2008) [tt1254207]"),
            rendered);
    }

    [Fact]
    public void Omits_optional_wrappers_when_an_identifier_is_unknown()
    {
        var rendered = NamingTemplateRenderer.RenderFolder(
            "{Series Title} ({Series Year}) [tvdb-{TVDb ID}]",
            "Severance",
            2022);

        Assert.Equal("Severance (2022)", rendered);
    }

    [Fact]
    public void Sanitizes_values_before_they_can_create_nested_paths()
    {
        var rendered = NamingTemplateRenderer.RenderFolder(
            "{Movie Title}",
            "Title/with\\separators",
            2026);

        Assert.DoesNotContain(Path.DirectorySeparatorChar + "with", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar + "separators", rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#757: the historical default keeps turning an illegal character into a dash.</summary>
    [Fact]
    public void RenderSegment_replaces_illegal_characters_with_a_dash_by_default()
    {
        var rendered = NamingTemplateRenderer.RenderSegment("{Movie Title}", "Who? Me", 2024);

        Assert.Equal("Who- Me", rendered);
    }

    /// <summary>#757: unchecking "Replace illegal characters" removes them instead.</summary>
    [Fact]
    public void RenderSegment_removes_illegal_characters_when_replacement_is_off()
    {
        var rendered = NamingTemplateRenderer.RenderSegment("{Movie Title}", "Who? Me", 2024, replaceIllegalCharacters: false);

        Assert.Equal("Who Me", rendered);
    }

    /// <summary>A folder template's separators must still work regardless of the illegal-character switch.</summary>
    [Fact]
    public void RenderFolder_still_keeps_folder_grouping_when_illegal_characters_are_removed()
    {
        var rendered = NamingTemplateRenderer.RenderFolder(
            "{Genre}\\{Movie Title}",
            "Who? Me",
            2024,
            genre: "Comedy",
            replaceIllegalCharacters: false);

        Assert.Equal(Path.Combine("Comedy", "Who Me"), rendered);
    }
}
