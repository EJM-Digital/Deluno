using Deluno.Api.Monitoring;
using Deluno.Jobs.Data;
using Deluno.Worker.SystemReadings;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.SystemReadings;

/// <summary>#1098: the plain rules behind the System screen's words and forecasts.</summary>
public sealed class OverviewRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static DriveSpaceDay Day(int daysAgo, long free) => new("D:", Today.AddDays(-daysAgo), free, 1_000);

    [Fact]
    public void A_drive_losing_ten_gigabytes_a_day_is_full_in_free_over_ten_days()
    {
        // 7 days ago 170 free, today 100 free: 10 a day, so 10 more days.
        var days = FullInEstimator.Estimate([Day(7, 170), Day(4, 140), Day(1, 110)], freeNowBytes: 100, Today);

        Assert.Equal(10, days);
    }

    [Fact]
    public void Todays_stored_reading_never_beats_the_reading_from_right_now()
    {
        var days = FullInEstimator.Estimate([Day(2, 120), Day(0, 999)], freeNowBytes: 100, Today);

        // 120 -> 100 over 2 days is 10 a day: 10 days left. The stale 999 for today is ignored.
        Assert.Equal(10, days);
    }

    [Fact]
    public void A_drive_that_is_not_filling_has_no_forecast()
    {
        Assert.Null(FullInEstimator.Estimate([Day(5, 100), Day(2, 100)], freeNowBytes: 100, Today));
        Assert.Null(FullInEstimator.Estimate([Day(5, 80), Day(2, 90)], freeNowBytes: 100, Today));
    }

    [Fact]
    public void With_only_one_days_reading_there_is_nothing_to_measure()
    {
        Assert.Null(FullInEstimator.Estimate([], freeNowBytes: 100, Today));
        Assert.Null(FullInEstimator.Estimate([Day(0, 100)], freeNowBytes: 100, Today));
    }

    [Fact]
    public void A_forecast_beyond_ten_years_is_not_shown()
    {
        var days = FullInEstimator.Estimate([Day(7, 1_000_000_000_001), Day(0, 1_000_000_000_000)], freeNowBytes: 1_000_000_000_000, Today);

        Assert.Null(days);
    }

    [Theory]
    [InlineData(true, true, true, false, "docker")]
    [InlineData(false, true, true, false, "app")]
    [InlineData(false, true, false, false, "service")]
    [InlineData(false, false, false, true, "service")]
    [InlineData(false, false, true, false, "app")]
    public void How_Deluno_runs_follows_where_it_runs(bool docker, bool windows, bool interactive, bool systemd, string expected)
    {
        Assert.Equal(expected, RunsAsRules.Describe(docker, windows, interactive, systemd));
    }

    [Theory]
    [InlineData("http://[::]:5099", "192.168.1.51", "http://192.168.1.51:5099")]
    [InlineData("http://0.0.0.0:5099", null, "http://BOX:5099")]
    [InlineData("http://+:8080", "192.168.1.51", "http://192.168.1.51:8080")]
    [InlineData("http://localhost:5099", "192.168.1.51", "http://localhost:5099")]
    [InlineData("https://127.0.0.1:5443", null, "https://127.0.0.1:5443")]
    public void The_listen_address_is_written_the_way_a_person_types_it(string bound, string? lan, string expected)
    {
        Assert.Equal(expected, ListenAddressRules.Describe([bound], 5099, lan, "BOX"));
    }

    [Fact]
    public void With_nothing_bound_yet_the_configured_port_on_this_machine_is_shown()
    {
        Assert.Equal("http://192.168.1.51:5099", ListenAddressRules.Describe([], 5099, "192.168.1.51", "BOX"));
    }

    [Theory]
    [InlineData("library", "Library")]
    [InlineData("downloads", "Downloads")]
    [InlineData("data", "Deluno data")]
    public void A_drive_is_named_for_what_it_holds(string role, string expected)
    {
        Assert.Equal(expected, DriveNaming.NameOf([role]));
    }

    [Fact]
    public void A_drive_holding_several_things_says_so()
    {
        Assert.Equal("Deluno data, library and downloads", DriveNaming.NameOf(["downloads", "library", "data"]));
        Assert.Equal("Library and downloads", DriveNaming.NameOf(["downloads", "library"]));
    }

    [Fact]
    public void Libraries_come_first_then_downloads_and_the_data_folder_last()
    {
        Assert.True(DriveNaming.Rank(["library"]) < DriveNaming.Rank(["downloads"]));
        Assert.True(DriveNaming.Rank(["downloads"]) < DriveNaming.Rank(["data"]));
        Assert.Equal(DriveNaming.Rank(["library"]), DriveNaming.Rank(["data", "library"]));
    }

    [Fact]
    public void The_typical_answer_is_the_median_and_the_slowest_five_percent_is_the_95th()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var tracker = new InMemoryApiLatencyTracker(clock);
        for (var i = 1; i <= 100; i++)
        {
            tracker.Record("/api/x", i, 200);
        }

        var times = tracker.GetAnswerTimes(TimeSpan.FromMinutes(15));

        Assert.Equal(100, times.RequestCount);
        Assert.Equal(50.5, times.TypicalMs);
        Assert.Equal(95, times.SlowestFivePercentMs);
    }

    [Fact]
    public void Answer_times_ignore_requests_older_than_the_window_and_are_zero_when_there_are_none()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var tracker = new InMemoryApiLatencyTracker(clock);
        Assert.Equal(new ApiAnswerTimes(0, 0, 0), tracker.GetAnswerTimes(TimeSpan.FromMinutes(15)));

        tracker.Record("/api/old", 9_000, 200);
        clock.Advance(TimeSpan.FromMinutes(20));
        tracker.Record("/api/new", 12, 200);

        var times = tracker.GetAnswerTimes(TimeSpan.FromMinutes(15));

        Assert.Equal(1, times.RequestCount);
        Assert.Equal(12, times.TypicalMs);
    }

    [Fact]
    public void Requests_and_server_errors_are_counted_for_the_day_and_start_again_at_midnight()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var tracker = new InMemoryApiLatencyTracker(clock);
        tracker.Record("/api/a", 5, 200);
        tracker.Record("/api/b", 5, 404);
        tracker.Record("/api/c", 5, 500);
        tracker.Record("/api/d", 5, 503);

        var today = tracker.GetDayTotals();
        Assert.Equal(4, today.Requests);
        Assert.Equal(2, today.Errors);

        clock.Advance(TimeSpan.FromHours(13));
        tracker.Record("/api/e", 5, 200);

        var tomorrow = tracker.GetDayTotals();
        Assert.Equal(1, tomorrow.Requests);
        Assert.Equal(0, tomorrow.Errors);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), tomorrow.SinceUtc);
    }
}
