using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Retention;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The retention schedule (RetentionScheduleHostedService) on a real library folder and the real database files: a rule is
/// checked only once its interval has passed, the first check only marks a title as Leaving soon and leaves its file in place,
/// and a later check, after the title's wait, moves it to the recycle bin. A rule whose interval has not passed is left alone.
/// </summary>
public sealed class RetentionScheduleOnDiskTests
{
    /// <summary>The real clock moved on by a fixed amount, with real timers.</summary>
    private sealed class SkewedClock(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + skew;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => System.CreateTimer(callback, state, dueTime, period);
    }

    private static async Task<(string Id, string File)> AnOldFilmAsync(RealDiskLibrary world)
    {
        var folder = Path.Combine(world.Root, "Nosferatu (1922)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv");
        File.WriteAllBytes(file, Enumerable.Repeat((byte)0x42, 4096).ToArray());
        var response = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title = "Nosferatu", releaseYear = 1922, monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await world.SqlAsync("movies", $"UPDATE movie_entries SET created_utc = '{DateTimeOffset.UtcNow.AddDays(-365):O}' WHERE id = '{id}';");
        return (id, file);
    }

    private static async Task RuleAsync(RealDiskLibrary world, int intervalHours)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films",
            level = "movie",
            isEnabled = true,
            steps = new[] { new { type = "condition", op = "keep", condition = "added:beyond:30" } },
            mediaServerIds = Array.Empty<string>(),
            removeFromServers = false,
            waitDays = 1,
            removalMode = "automatic",
            checkIntervalHours = intervalHours,
            expiryAction = "delete"
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task RunOnceAsync(RealDiskLibrary world, TimeSpan skew, Func<Task<bool>> done, string what)
    {
        var service = new RetentionScheduleHostedService(
            world.App.Services.GetRequiredService<IServiceScopeFactory>(),
            new SkewedClock(skew),
            NullLogger<RetentionScheduleHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(done, what, TimeSpan.FromSeconds(30));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_rule_is_checked_when_its_interval_has_passed_the_first_check_only_marks_and_the_check_after_the_wait_removes_the_title()
    {
        await using var world = await RealDiskLibrary.OpenAsync(await ApplicationTestHost.StartAsync());
        var (id, file) = await AnOldFilmAsync(world);
        await RuleAsync(world, intervalHours: 24);

        // Two hours after the rule was made its 24-hour interval has not passed: nothing is checked.
        var early = new RetentionScheduleHostedService(
            world.App.Services.GetRequiredService<IServiceScopeFactory>(), new SkewedClock(TimeSpan.FromHours(2)), NullLogger<RetentionScheduleHostedService>.Instance);
        await early.StartAsync(CancellationToken.None);
        await early.StopAsync(CancellationToken.None);
        Assert.Equal(0L, Convert.ToInt64(await world.ScalarAsync("platform", "SELECT COUNT(*) FROM retention_marks;")));
        Assert.True(File.Exists(file));

        // A day and an hour on, it is due: the title is marked Leaving soon and its file stays.
        await RunOnceAsync(world, TimeSpan.FromHours(25),
            async () => Convert.ToInt64(await world.ScalarAsync("platform", "SELECT COUNT(*) FROM retention_marks;")) == 1,
            "the due rule to mark the old film");
        Assert.True(File.Exists(file), "marking never removes a file");
        Assert.Equal(HttpStatusCode.OK, (await world.App.Client.GetAsync($"/api/movies/{id}")).StatusCode);

        // Its wait is over and the next interval has passed: the title goes to the recycle bin, where Restore finds it.
        await world.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");
        await RunOnceAsync(world, TimeSpan.FromHours(60),
            async () => (await world.App.Client.GetAsync($"/api/movies/{id}")).StatusCode == HttpStatusCode.NotFound,
            "the second due check to remove the old film");
        Assert.False(File.Exists(file));
        Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
    }
}
