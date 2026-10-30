using System.Reflection;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Guides;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// Coverage register (#1329), quality and decision engine: the guide update check's stored state. Turning the check on reads
/// as never-checked, up to date or update available from the last report; turning it off reads as disabled and clears the
/// error, and the last report stays for review. The hosted service asks again every twelve hours. Real SQLite.
/// </summary>
public sealed class TheGuideUpdateCheckStateFollowsTheSwitchAndTheLastReportTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T06:00:00Z");

    private static async Task<(SqliteGuideUpdateCheckStore Store, FixedTimeProvider Time)> StartAsync(TestStorage storage)
    {
        var time = new FixedTimeProvider(Start);
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (new SqliteGuideUpdateCheckStore(storage.Factory, time), time);
    }

    private static GuideUpdateCheckReport Report(DateTimeOffset checkedUtc, bool somethingChanged)
        => new(
            "baseline",
            "remote",
            checkedUtc,
            true,
            [],
            somethingChanged ? [new GuideUpdateCheckAddedSource("custom-format", "movies", "docs/json/radarr/cf/new.json")] : [],
            somethingChanged ? "One new source." : "Nothing changed.");

    [Fact]
    public async Task A_fresh_install_reads_as_disabled_and_turning_the_check_on_before_any_check_reads_as_never_checked()
    {
        using var storage = TestStorage.Create();
        var (store, _) = await StartAsync(storage);

        var initial = await store.GetAsync(CancellationToken.None);
        Assert.False(initial.IsEnabled);
        Assert.Equal(GuideUpdateCheckStatuses.Disabled, initial.Status);

        var enabled = await store.SetEnabledAsync(true, CancellationToken.None);

        Assert.True(enabled.IsEnabled);
        Assert.Equal(GuideUpdateCheckStatuses.NeverChecked, enabled.Status);
        Assert.Equal(GuideUpdateCheckStatuses.NeverChecked, (await store.GetAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Turning_the_check_on_again_reads_the_last_report_as_up_to_date_or_update_available()
    {
        using var storage = TestStorage.Create();
        var (store, time) = await StartAsync(storage);
        await store.SetEnabledAsync(true, CancellationToken.None);

        await store.SaveSuccessAsync(Report(time.GetUtcNow(), somethingChanged: false), CancellationToken.None);
        await store.SetEnabledAsync(false, CancellationToken.None);
        var upToDate = await store.SetEnabledAsync(true, CancellationToken.None);
        Assert.Equal(GuideUpdateCheckStatuses.UpToDate, upToDate.Status);

        await store.SaveSuccessAsync(Report(time.GetUtcNow(), somethingChanged: true), CancellationToken.None);
        await store.SetEnabledAsync(false, CancellationToken.None);
        var available = await store.SetEnabledAsync(true, CancellationToken.None);
        Assert.Equal(GuideUpdateCheckStatuses.UpdateAvailable, available.Status);
    }

    [Fact]
    public async Task Turning_the_check_off_reads_as_disabled_clears_the_error_and_keeps_the_last_report_for_review()
    {
        using var storage = TestStorage.Create();
        var (store, time) = await StartAsync(storage);
        await store.SetEnabledAsync(true, CancellationToken.None);
        var report = Report(time.GetUtcNow(), somethingChanged: true);
        await store.SaveSuccessAsync(report, CancellationToken.None);
        await store.SaveFailureAsync("GitHub could not be reached.", time.GetUtcNow(), CancellationToken.None);
        Assert.Equal("GitHub could not be reached.", (await store.GetAsync(CancellationToken.None)).Error);

        var disabled = await store.SetEnabledAsync(false, CancellationToken.None);

        Assert.False(disabled.IsEnabled);
        Assert.Equal(GuideUpdateCheckStatuses.Disabled, disabled.Status);
        Assert.Null(disabled.Error);
        var stored = await store.GetAsync(CancellationToken.None);
        Assert.Equal(GuideUpdateCheckStatuses.Disabled, stored.Status);
        Assert.Null(stored.Error);
        Assert.NotNull(stored.Report);
        Assert.Equal(report.Summary, stored.Report!.Summary);
    }

    [Fact]
    public void The_hosted_service_asks_again_every_twelve_hours()
    {
        var interval = typeof(GuideUpdateCheckHostedService)
            .GetField("PollInterval", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null);

        Assert.Equal(TimeSpan.FromHours(12), interval);
    }
}
