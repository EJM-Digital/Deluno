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
}
