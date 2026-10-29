using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: a snoozed health problem is out of the bell and its count for as long as it was snoozed, and comes back by itself when
/// the time is up, with nobody doing anything.
/// </summary>
public sealed class AnExpiredSnoozeBringsTheProblemBackTests
{
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static async Task<(string[] Rows, int Count)> BellAsync(ApplicationTestHost app)
    {
        var summary = await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/summary");
        return (summary.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()!).ToArray(), summary.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_snoozed_problem_leaves_the_bell_and_its_count_and_returns_when_the_snooze_runs_out()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        await app.Services.GetRequiredService<IHealthCheckRepository>().UpsertResultsAsync(
            [HealthCheckOutcome.Problem("test:disk", HealthCheckCategories.System, "Disk is nearly full", "The disk is nearly full.", "Too many files.", "Free some space.")],
            CancellationToken.None);
        var shown = await BellAsync(app);
        Assert.Contains("health:test:disk", shown.Rows);

        using var snoozed = await app.Client.PostAsJsonAsync("/api/system/health/test:disk/snooze", new { hours = 2 });
        snoozed.EnsureSuccessStatusCode();
        var hidden = await BellAsync(app);
        Assert.DoesNotContain("health:test:disk", hidden.Rows);
        Assert.Equal(shown.Count - 1, hidden.Count);

        clock.Advance(TimeSpan.FromMinutes(119));
        Assert.DoesNotContain("health:test:disk", (await BellAsync(app)).Rows);

        clock.Advance(TimeSpan.FromMinutes(2));
        var back = await BellAsync(app);
        Assert.Contains("health:test:disk", back.Rows);
        Assert.Equal(shown.Count, back.Count);
    }

    [Fact]
    public async Task Unsnoozing_brings_the_problem_back_at_once()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        await app.Services.GetRequiredService<IHealthCheckRepository>().UpsertResultsAsync(
            [HealthCheckOutcome.Problem("test:disk", HealthCheckCategories.System, "Disk is nearly full", "The disk is nearly full.", "Too many files.", "Free some space.")],
            CancellationToken.None);
        (await app.Client.PostAsJsonAsync("/api/system/health/test:disk/snooze", new { hours = 24 })).EnsureSuccessStatusCode();
        Assert.DoesNotContain("health:test:disk", (await BellAsync(app)).Rows);

        (await app.Client.PostAsync("/api/system/health/test:disk/unsnooze", content: null)).EnsureSuccessStatusCode();

        Assert.Contains("health:test:disk", (await BellAsync(app)).Rows);
    }
}
