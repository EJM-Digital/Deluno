using Deluno.Series.Contracts;

namespace Deluno.Series.Tests;

/// <summary>#763: each style's own combined season/episode token, checked against a 2-episode and a 3-episode file.</summary>
public sealed class MultiEpisodeStylesTests
{
    [Theory]
    [InlineData(MultiEpisodeStyles.Extend, "S01E01E02")]
    [InlineData(MultiEpisodeStyles.Duplicate, "S01E01S01E02")]
    [InlineData(MultiEpisodeStyles.Repeat, "S01E01-S01E02")]
    [InlineData(MultiEpisodeStyles.Scene, "S01E01-E02")]
    [InlineData(MultiEpisodeStyles.Range, "S01E01-E02")]
    [InlineData(MultiEpisodeStyles.PrefixedRange, "S01E01-02")]
    public void Two_episode_file_renders_each_style(string style, string expected)
    {
        Assert.Equal(expected, MultiEpisodeStyles.FormatToken(style, seasonNumber: 1, episodeNumbers: [1, 2]));
    }

    [Theory]
    [InlineData(MultiEpisodeStyles.Extend, "S01E01E02E03")]
    [InlineData(MultiEpisodeStyles.Duplicate, "S01E01S01E02S01E03")]
    [InlineData(MultiEpisodeStyles.Repeat, "S01E01-S01E02-S01E03")]
    [InlineData(MultiEpisodeStyles.Scene, "S01E01-E02-E03")]
    [InlineData(MultiEpisodeStyles.Range, "S01E01-E03")]
    [InlineData(MultiEpisodeStyles.PrefixedRange, "S01E01-03")]
    public void Three_episode_file_renders_each_style(string style, string expected)
    {
        Assert.Equal(expected, MultiEpisodeStyles.FormatToken(style, seasonNumber: 1, episodeNumbers: [1, 2, 3]));
    }

    [Fact]
    public void A_single_episode_always_renders_the_same_token_regardless_of_style()
    {
        foreach (var style in MultiEpisodeStyles.All)
        {
            Assert.Equal("S01E01", MultiEpisodeStyles.FormatToken(style, seasonNumber: 1, episodeNumbers: [1]));
        }
    }

    [Fact]
    public void Unordered_episode_numbers_are_sorted_before_rendering()
    {
        Assert.Equal("S02E01-E03", MultiEpisodeStyles.FormatToken(MultiEpisodeStyles.Range, seasonNumber: 2, episodeNumbers: [3, 1, 2]));
    }

    [Fact]
    public void An_unrecognised_style_normalizes_to_range()
    {
        Assert.Equal(MultiEpisodeStyles.Range, MultiEpisodeStyles.Normalize("not-a-real-style"));
        Assert.Equal(MultiEpisodeStyles.Range, MultiEpisodeStyles.Normalize(null));
    }
}
