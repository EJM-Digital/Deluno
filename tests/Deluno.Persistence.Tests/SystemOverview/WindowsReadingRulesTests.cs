using Deluno.Infrastructure.SystemReadings;

namespace Deluno.Persistence.Tests.SystemReadings;

/// <summary>
/// #1098: how Windows' own answers (registry keys, WMI numbers) become Deluno's words. The WMI and
/// registry calls themselves cannot run on every machine, so what they return is tested as pure rules.
/// </summary>
public sealed class WindowsReadingRulesTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void A_reboot_is_pending_when_any_of_the_three_places_says_so(bool servicing, bool update, bool renames, bool expected)
    {
        Assert.Equal(expected, RebootPendingRules.Evaluate(servicing, update, renames));
    }

    [Fact]
    public void A_thermal_zone_in_tenths_of_a_kelvin_becomes_degrees_celsius()
    {
        // 3331 tenths of a kelvin is 333.1 K, which is 59.95 C.
        Assert.Equal(59.95, ThermalZoneRules.HottestCelsius([3331])!.Value, 0.06);
    }

    [Fact]
    public void The_hottest_zone_wins_and_implausible_readings_are_ignored()
    {
        Assert.Equal(70.85, ThermalZoneRules.HottestCelsius([3331, 3440, 0, 9999])!.Value, 0.06);
    }

    [Fact]
    public void No_zones_means_no_temperature()
    {
        Assert.Null(ThermalZoneRules.HottestCelsius([]));
        Assert.Null(ThermalZoneRules.HottestCelsius([0]));
    }

    [Fact]
    public void Healthy_is_good_with_no_note()
    {
        var health = DriveHealthRules.Map(0, [2]);

        Assert.Equal("good", health.Health);
        Assert.Null(health.Note);
    }

    [Fact]
    public void A_warning_says_what_the_drive_reported_in_plain_words()
    {
        var health = DriveHealthRules.Map(1, [3]);

        Assert.Equal("warning", health.Health);
        Assert.Equal("The drive is running in a reduced state.", health.Note);
    }

    [Fact]
    public void A_predicted_failure_is_failing()
    {
        var health = DriveHealthRules.Map(2, [5]);

        Assert.Equal("failing", health.Health);
        Assert.Equal("The drive expects to fail soon.", health.Note);
    }

    [Fact]
    public void A_warning_with_no_known_status_still_says_something()
    {
        Assert.Equal("The drive reports a warning.", DriveHealthRules.Map(1, [99]).Note);
        Assert.Equal("The drive reports that it is failing.", DriveHealthRules.Map(2, []).Note);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    public void A_status_Windows_does_not_know_is_unknown(int healthStatus)
    {
        Assert.Equal("unknown", DriveHealthRules.Map(healthStatus, []).Health);
    }
}
