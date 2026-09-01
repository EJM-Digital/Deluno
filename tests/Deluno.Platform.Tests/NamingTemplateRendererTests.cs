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
}
