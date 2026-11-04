namespace Deluno.Tray.Tests;

/// <summary>The time between the tray's background update checks is six hours; only the test hook changes it, and only to a sensible number of seconds.</summary>
public sealed class TheUpdateCheckIntervalTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-60")]
    [InlineData("4")]
    [InlineData("21601")]
    [InlineData("60.5")]
    public void Anything_that_is_not_a_whole_number_of_seconds_from_5_to_21600_is_six_hours(string? value)
    {
        Assert.Equal(TimeSpan.FromHours(6), TrayUpdateBackgroundService.IntervalFrom(value));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("45", 45)]
    [InlineData("21600", 21600)]
    public void A_whole_number_of_seconds_in_range_is_used(string value, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), TrayUpdateBackgroundService.IntervalFrom(value));
    }
}
