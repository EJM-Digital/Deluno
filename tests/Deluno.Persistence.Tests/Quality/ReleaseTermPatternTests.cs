using Deluno.Quality;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #996: Must contain / Must not contain accept /pattern/ like Sonarr's
/// PerlRegexFactory and TermMatcherService (plain terms stay plain contains).
/// </summary>
public sealed class ReleaseTermPatternTests
{
    [Fact]
    public void Plain_words_still_match_anywhere_ignoring_case()
    {
        Assert.True(ReleaseTermPattern.IsMatch("Movie.2024.1080p.WEB-DL.x265-GRP", "web-dl"));
        Assert.True(ReleaseTermPattern.IsMatch("Movie (2020) 1080p", "(2020)"));
        Assert.False(ReleaseTermPattern.IsMatch("Movie.2024.1080p", "remux"));
    }

    [Fact]
    public void A_term_between_slashes_is_a_pattern_and_i_ignores_case()
    {
        Assert.True(ReleaseTermPattern.IsMatch("Movie.2024.2160p.HDR10-GRP", @"/\b(2160p|4k)\b/i"));
        Assert.True(ReleaseTermPattern.IsMatch("Movie.2024.WEB-DL", "/web-dl/i"));
        // No i: case-sensitive, as in Sonarr's regex matcher.
        Assert.False(ReleaseTermPattern.IsMatch("Movie.2024.WEB-DL", "/web-dl/"));
        Assert.True(ReleaseTermPattern.IsMatch("Movie.2024.WEB-DL", "/WEB-DL/"));
    }

    [Fact]
    public void A_comma_inside_a_pattern_does_not_split_it_but_commas_between_terms_do()
    {
        var terms = ReleaseTermPattern.Split("/x{2,3}/i, Remux\n/H[.]?26[45]/i");
        Assert.Equal(["/x{2,3}/i", "Remux", "/H[.]?26[45]/i"], terms);
    }

    [Fact]
    public void Terms_that_only_start_with_a_slash_stay_plain_words()
    {
        Assert.Equal(["/not a pattern", "b"], ReleaseTermPattern.Split("/not a pattern, b"));
        Assert.False(ReleaseTermPattern.IsPattern("/not a pattern"));
    }

    [Fact]
    public void An_unreadable_pattern_is_reported_in_plain_words_and_matches_its_own_text_only()
    {
        Assert.NotNull(ReleaseTermPattern.Validate("/(unclosed/i"));
        Assert.NotNull(ReleaseTermPattern.Validate("/abc/z"));
        Assert.Null(ReleaseTermPattern.Validate("/abc/i"));
        Assert.Null(ReleaseTermPattern.Validate("plain words"));
        Assert.False(ReleaseTermPattern.IsMatch("Movie.2024", "/(unclosed/i"));
    }

    [Fact]
    public void Acquisition_rules_of_a_profile_keep_patterns_whole_when_saved()
    {
        var rules = new ProfileAcquisitionRules(MustContain: "/x{2,3}/i, remux").Normalize();
        Assert.Equal("/x{2,3}/i, remux", rules.MustContain);
    }
}
