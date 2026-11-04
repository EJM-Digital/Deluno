using Deluno.Quality;

namespace Deluno.Platform.Tests.Quality;

public sealed class ReleaseTermPatternEdgeTests
{
    [Fact]
    public void No_text_has_no_terms()
    {
        Assert.Empty(ReleaseTermPattern.Split(null));
        Assert.Empty(ReleaseTermPattern.Split(" , \n ,"));
    }

    [Fact]
    public void A_term_is_kept_once_whatever_its_case_and_too_long_terms_are_dropped()
    {
        Assert.Equal(["Remux", "web"], ReleaseTermPattern.Split("Remux, REMUX\nweb"));
        Assert.Equal(["abc"], ReleaseTermPattern.Split("abc, abcdef", maxLength: 3));
        Assert.Equal(["abc"], ReleaseTermPattern.Split("abc, abcd", maxLength: 3));
    }

    [Fact]
    public void A_slash_pattern_runs_to_its_closing_slash_even_with_spaces_before_the_next_comma()
        => Assert.Equal(["/a b/i", "x"], ReleaseTermPattern.Split("/a b/i \t, x"));

    [Fact]
    public void A_slash_pattern_cut_by_a_new_line_is_plain_words_up_to_the_new_line()
        => Assert.Equal(["/abc", "def/"], ReleaseTermPattern.Split("/abc\ndef/"));

    [Fact]
    public void A_slash_with_text_after_the_modifiers_is_not_the_end_of_a_pattern()
        => Assert.Equal(["/a/ b"], ReleaseTermPattern.Split("/a/ b"));

    [Theory]
    [InlineData("//")]
    [InlineData("/a")]
    [InlineData("/ab")]
    [InlineData("/a/1")]
    [InlineData("a/b/")]
    public void Text_that_is_not_slash_pattern_shaped_is_not_a_pattern(string term)
        => Assert.False(ReleaseTermPattern.IsPattern(term));

    [Fact]
    public void A_pattern_is_found_after_spaces_are_trimmed()
        => Assert.True(ReleaseTermPattern.IsPattern("  /ab/i  "));

    [Theory]
    [InlineData("/a/m")]
    [InlineData("/a/s")]
    [InlineData("/a b/x")]
    [InlineData("/(a)/n")]
    [InlineData("/a/imsxn")]
    public void Every_letter_a_pattern_may_end_in_is_accepted(string term)
        => Assert.Null(ReleaseTermPattern.Validate(term));

    [Theory]
    [InlineData("plain words")]
    [InlineData("/not closed")]
    public void Plain_words_need_no_checking(string term)
        => Assert.Null(ReleaseTermPattern.Validate(term));

    [Fact]
    public void A_letter_a_pattern_cannot_end_in_is_named()
    {
        var message = ReleaseTermPattern.Validate(" /abc/iz ");

        Assert.Equal("'/abc/iz' ends with the letter 'z', which a pattern cannot use. Patterns can end in i, m, s, x or n.", message);
    }

    [Fact]
    public void A_pattern_that_does_not_compile_is_described()
        => Assert.Equal("'/(/' is not a valid pattern. Check the brackets and slashes, or write it as plain words.", ReleaseTermPattern.Validate("/(/"));

    [Fact]
    public void The_modifiers_change_how_a_pattern_reads_the_text()
    {
        Assert.True(ReleaseTermPattern.IsMatch("one\ntwo", "/^two$/m"));
        Assert.False(ReleaseTermPattern.IsMatch("one\ntwo", "/^two$/"));
        Assert.True(ReleaseTermPattern.IsMatch("one\ntwo", "/one.two/s"));
        Assert.False(ReleaseTermPattern.IsMatch("one\ntwo", "/one.two/"));
        Assert.True(ReleaseTermPattern.IsMatch("onetwo", "/one two/x"));
        Assert.False(ReleaseTermPattern.IsMatch("onetwo", "/one two/"));
        Assert.True(ReleaseTermPattern.IsMatch("ab", "/(a)(b)/n"));
    }

    [Fact]
    public void A_capture_group_is_not_numbered_when_the_pattern_ends_in_n()
        => Assert.False(ReleaseTermPattern.IsMatch("aa", "/(a)\\1/n"));

    [Fact]
    public void A_pattern_that_needs_a_look_ahead_still_works()
    {
        Assert.True(ReleaseTermPattern.IsMatch("Movie.DV.HDR", "/DV(?=.HDR)/"));
        Assert.False(ReleaseTermPattern.IsMatch("Movie.DV.SDR", "/DV(?=.HDR)/"));
        Assert.True(ReleaseTermPattern.IsMatch("movie.dv.hdr", "/DV(?=.HDR)/i"));
    }

    [Fact]
    public void A_pattern_with_an_unusable_letter_is_matched_as_the_words_it_is_written_in()
    {
        Assert.True(ReleaseTermPattern.IsMatch("a /abc/z b", "/abc/z"));
        Assert.False(ReleaseTermPattern.IsMatch("abc", "/abc/z"));
    }

    [Fact]
    public void A_pattern_that_runs_out_of_time_does_not_match()
    {
        // The look-ahead sends this to the backtracking engine, where nested repeats on a near-match take
        // far longer than its limit, so the answer is no match.
        var text = new string('a', 60) + "!";

        Assert.False(ReleaseTermPattern.IsMatch(text, "/(?=a)(a+)+$/"));
    }
}
