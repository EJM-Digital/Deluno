using Deluno.Contracts;

namespace Deluno.Platform.Tests.Properties;

/// <summary>#1307: the one episode, date and year reader every parser shares.</summary>
public sealed class ReleaseNameReaderTests
{
    [Theory]
    [InlineData("Show.S01E01E02E03.720p", 1, 1, 3)]
    [InlineData("Show_S01E05-E07_720p", 1, 5, 7)]
    [InlineData("Show S02E10-12 720p", 2, 10, 12)]
    [InlineData("Show-3x04-WEB", 3, 4, 4)]
    public void An_episode_marker_reads_the_same_with_any_separator_and_any_range_spelling(string name, int season, int first, int last)
    {
        var marker = ReleaseNameReader.ReadEpisode(name);
        Assert.NotNull(marker);
        Assert.Equal((season, first, last), (marker.Season, marker.FirstEpisode, marker.LastEpisode));
    }

    [Theory]
    [InlineData("Night.Living.2000.01.01.720p", 2000, 1, 1)]
    [InlineData("Night_Living_2000_01_01_720p", 2000, 1, 1)]
    [InlineData("Night Living 2100-12-31", 2100, 12, 31)]
    public void A_daily_date_is_read_in_full_with_any_separator(string name, int year, int month, int day)
        => Assert.Equal(new DateOnly(year, month, day), ReleaseNameReader.ReadDailyDate(name)?.Date);

    [Fact]
    public void A_date_that_is_not_on_the_calendar_is_not_a_daily_date()
        => Assert.Null(ReleaseNameReader.ReadDailyDate("Show.2001.02.30.720p"));

    [Theory]
    [InlineData("Title (2100)", 2100)]
    [InlineData("Title_2199_1080p", 2199)]
    [InlineData("Title.1900.1080p", 1900)]
    public void A_year_from_1900_to_2199_is_read(string name, int year)
        => Assert.Equal(year, ReleaseNameReader.ReadYear(name)?.Year);

    [Theory]
    [InlineData("Conclave 2160p UHD")]
    [InlineData("Conclave.2160i.UHD")]
    public void A_resolution_is_not_a_year(string name)
        => Assert.Null(ReleaseNameReader.ReadYear(name));

    [Theory]
    [InlineData("Title 2200")]
    [InlineData("Title 1899")]
    public void A_number_outside_1900_to_2199_is_not_a_year(string name)
        => Assert.Null(ReleaseNameReader.ReadYear(name));

    [Theory]
    [InlineData("Show.S01E01v2.720p", 1, 1, 1)]
    [InlineData("Show.S01E01E02v2.720p", 1, 1, 2)]
    [InlineData("Show_S01E01v3_720p", 1, 1, 1)]
    [InlineData("Show.S01E01.REPACK.720p", 1, 1, 1)]
    [InlineData("Show.S01E01-PROPER.720p", 1, 1, 1)]
    [InlineData("Show.S01E01E02.REPACK.720p", 1, 1, 2)]
    [InlineData("Show.1x01v2.720p", 1, 1, 1)]
    public void A_version_suffix_or_a_separated_tag_after_the_marker_still_reads_that_episode(string name, int season, int first, int last)
    {
        var marker = ReleaseNameReader.ReadEpisode(name);
        Assert.NotNull(marker);
        Assert.Equal((season, first, last), (marker.Season, marker.FirstEpisode, marker.LastEpisode));
    }

    [Theory]
    [InlineData("Show.S01E01vx.720p")]
    [InlineData("Show.S01E01abc.720p")]
    [InlineData("Show.S01E01v22.720p")]
    [InlineData("Show.S01E01v.720p")]
    public void Anything_else_glued_to_the_marker_is_not_an_episode(string name)
        => Assert.Null(ReleaseNameReader.ReadEpisode(name));

    [Theory]
    [InlineData("Show - 01v2 [1080p]", 1)]
    [InlineData("Show - 12v3", 12)]
    public void An_absolute_number_with_a_version_suffix_is_that_number(string name, int number)
        => Assert.Null(ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation("Show", null, IsTv: true, AbsoluteNumber: number)));

    [Theory]
    [InlineData("Show - 01vx [1080p]")]
    [InlineData("Show - 01abc [1080p]")]
    public void An_absolute_number_with_anything_else_glued_on_is_refused(string name)
        => Assert.NotNull(ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation("Show", null, IsTv: true, AbsoluteNumber: 1)));

    [Fact]
    public void The_feed_reads_a_versioned_episode_and_not_a_glued_word()
    {
        var versioned = FeedReleaseNames.Parse("Show.Name.S01E01v2.720p.WEB-DL-GRP");
        Assert.Equal((1, 1), (versioned?.Season, versioned?.Episode));
        Assert.Null(FeedReleaseNames.Parse("Show.Name.S01E01abc.720p.WEB-DL-GRP")?.Episode);
    }
}
