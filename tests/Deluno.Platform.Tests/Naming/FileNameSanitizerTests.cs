using Deluno.Platform;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Tests.Naming;

/// <summary>
/// Every branch of the name sanitizer, by example. The property tests over generated names reach most of them by
/// chance; these make each one certain, so the coverage of the file no longer depends on what the generator drew.
/// </summary>
public sealed class FileNameSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_empty(string? value) => Assert.Equal(string.Empty, FileNameSanitizer.Sanitize(value));

    [Theory]
    [InlineData(ColonReplacementModes.Delete, "Mission Impossible", "MissionImpossible")]
    [InlineData(ColonReplacementModes.Dash, "Mission- Impossible", "Mission-Impossible")]
    [InlineData(ColonReplacementModes.SpaceDash, "Mission - Impossible", "Mission -Impossible")]
    [InlineData(ColonReplacementModes.SpaceDashSpace, "Mission -  Impossible", "Mission - Impossible")]
    [InlineData(ColonReplacementModes.SmartReplace, "Mission - Impossible", "Mission-Impossible")]
    public void Each_colon_mode_replaces_a_colon_its_own_way(string mode, string withSpaceAfter, string withNoSpaceAfter)
    {
        Assert.Equal(withSpaceAfter, FileNameSanitizer.Sanitize("Mission: Impossible", colonReplacementMode: mode));
        Assert.Equal(withNoSpaceAfter, FileNameSanitizer.Sanitize("Mission:Impossible", colonReplacementMode: mode));
    }

    [Fact]
    public void With_replacing_off_illegal_characters_and_colons_are_dropped_not_replaced()
    {
        Assert.Equal("Mission Impossible", FileNameSanitizer.Sanitize("Mission: Impossible", replaceIllegalCharacters: false));
        Assert.Equal("ab", FileNameSanitizer.Sanitize("a?b", replaceIllegalCharacters: false));
        Assert.Equal("ab", FileNameSanitizer.Sanitize("a\nb", replaceIllegalCharacters: false));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    public void Every_character_illegal_on_any_platform_becomes_a_dash_on_every_platform(string value) =>
        Assert.Equal("a-b", FileNameSanitizer.Sanitize(value));

    [Fact]
    public void A_control_character_becomes_a_space_and_the_ends_are_trimmed() =>
        Assert.Equal("line one line two", FileNameSanitizer.Sanitize(" line one\nline two\t"));

    [Fact]
    public void A_trailing_dot_or_space_is_trimmed_but_not_one_inside_the_name()
    {
        Assert.Equal("Mr. (2001)", FileNameSanitizer.TrimEnd("Mr. (2001). ."));
        Assert.Equal(5, FileNameSanitizer.ByteCount("héll"));
    }

    [Fact]
    public void A_cut_keeps_whole_characters_never_half_a_surrogate_pair_and_drops_the_space_it_leaves()
    {
        Assert.Equal(string.Empty, FileNameSanitizer.LimitBytes("anything", 0));
        Assert.Equal("short", FileNameSanitizer.LimitBytes("short", 255));
        Assert.Equal("ab", FileNameSanitizer.LimitBytes("ab é", 4));
        Assert.Equal("a", FileNameSanitizer.LimitBytes("a\U0001F600b", 4));
        Assert.Equal("a\U0001F600", FileNameSanitizer.LimitBytes("a\U0001F600b", 5));
        // A lone high surrogate (a broken pair at the very end, or one followed by an ordinary letter) is one unit wide.
        Assert.Equal("ab", FileNameSanitizer.LimitBytes("ab\uD83D", 2));
        Assert.Equal("ab", FileNameSanitizer.LimitBytes("ab\uD83Dc", 3));
    }

    [Fact]
    public void An_unknown_colon_mode_is_read_as_smart_replace_and_a_nul_is_dropped_as_a_control_character()
    {
        Assert.Equal("Mission - Impossible", FileNameSanitizer.Sanitize("Mission: Impossible", colonReplacementMode: "something-else"));
        Assert.Equal("Mission-Impossible", FileNameSanitizer.Sanitize("Mission:Impossible", colonReplacementMode: null!));
        Assert.Equal("a b", FileNameSanitizer.Sanitize("a\0b"));
    }
}
