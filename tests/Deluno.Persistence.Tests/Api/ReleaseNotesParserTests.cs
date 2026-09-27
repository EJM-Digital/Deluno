using Deluno.Api.Updates;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The parser behind RELEASE_NOTES.md (#870): splits "## &lt;version&gt;"
/// sections so the Updates card can show each applied version its own notes.
/// </summary>
public sealed class ReleaseNotesParserTests
{
    [Fact]
    public void Splits_the_document_into_one_body_per_version_heading()
    {
        const string markdown = """
            # Release notes

            Intro text nobody looks up by version.

            ## 1.0.0-rc.2

            - Second thing.
            - Another second thing.

            ## 1.0.0-rc.1

            - First thing.
            """;

        var sections = ReleaseNotesParser.Parse(markdown);

        Assert.Equal(2, sections.Count);
        Assert.Equal("- Second thing.\n- Another second thing.", sections["1.0.0-rc.2"]);
        Assert.Equal("- First thing.", sections["1.0.0-rc.1"]);
    }

    [Fact]
    public void A_version_with_no_section_is_simply_missing_from_the_lookup()
    {
        const string markdown = """
            ## 1.0.0-rc.1

            - First thing.
            """;

        var sections = ReleaseNotesParser.Parse(markdown);

        Assert.False(sections.ContainsKey("1.0.0-rc.2"));
        Assert.True(sections.ContainsKey("1.0.0-rc.1"));
    }

    [Fact]
    public void Blank_or_missing_input_parses_to_no_sections_at_all()
    {
        Assert.Empty(ReleaseNotesParser.Parse(""));
        Assert.Empty(ReleaseNotesParser.Parse("   "));
        Assert.Empty(ReleaseNotesParser.Parse("# Just a title, no version headings"));
    }

    [Fact]
    public void EmbeddedReleaseNotesProvider_reads_the_repos_own_notes_file()
    {
        // The repo's real RELEASE_NOTES.md is embedded into Deluno.Api; this
        // guards against the resource name or the file going missing without
        // any test noticing (a silently-empty Updates card is worse than a
        // build failure).
        var provider = new EmbeddedReleaseNotesProvider();

        Assert.Null(provider.GetNotes("a-version-that-will-never-exist"));
        Assert.NotNull(provider.GetNotes("1.0.0-rc.22"));
    }
}
