using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Retention;
using Deluno.Integrations.MediaServers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Retention checks on the real host, real library folders and the real databases, with a local server that answers as Jellyfin 12.2.0
/// recorded. The schedule checks every due rule as "schedule", goes on to the next rule when one cannot be checked, and brings the
/// servers' Leaving soon collections up to date after; a check that is still reading a slow server neither holds up saving the rule nor
/// runs beside another check of the same rule, and a check that began before the rule changed then changes nothing.
/// </summary>
public sealed class RetentionChecksAndTheScheduleOnRealFilesTests
{
    private sealed class SkewedClock(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + skew;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => System.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>The one thing swapped for this test: asking for a Plex client fails outright, which stands for a check that cannot finish.</summary>
    private sealed class RegistryThatFailsForPlex(IMediaServerClientRegistry real) : IMediaServerClientRegistry
    {
        public IMediaServerClient? Find(string kind) => string.Equals(kind, "plex", StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException("The Plex client could not be built.")
            : real.Find(kind);
    }

    private static async Task<string> AFilmAsync(RealDiskLibrary world, string title, int year, string imdb, string fileName)
    {
        var folder = Path.Combine(world.Root, $"{title} ({year})");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, fileName);
        File.WriteAllBytes(file, Enumerable.Repeat((byte)0x42, 4096).ToArray());
        var response = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, imdbId = imdb, monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await world.SqlAsync("movies", $"UPDATE movie_entries SET created_utc = '{DateTimeOffset.UtcNow.AddDays(-365):O}' WHERE id = '{id}';");
        return file;
    }

    private static async Task<string> ServerAsync(RealDiskLibrary world, RecordedJellyfinServer jellyfin)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/media-servers", new { kind = "jellyfin", name = "Living room", baseUrl = jellyfin.BaseUrl, token = "jf-secret-token-1", isEnabled = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static object RuleBody(string name, string[] serverIds, string action = "delete", bool enabled = true, int hours = 24, int waitDays = 1)
        => new
        {
            name, level = "movie", isEnabled = enabled, steps = new[] { new { type = "condition", op = "keep", condition = "added:beyond:30" } },
            mediaServerIds = serverIds, removeFromServers = false, waitDays, removalMode = "automatic", checkIntervalHours = hours, expiryAction = action
        };

    private static async Task<string> RuleAsync(RealDiskLibrary world, object body)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/retention/rules", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static Task TheDayArrivesAsync(RealDiskLibrary world)
        => world.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");

    private static async Task<long> CountAsync(RealDiskLibrary world, string sql)
        => Convert.ToInt64(await world.ScalarAsync("platform", sql));

    // ---- the schedule -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_due_rule_is_checked_as_schedule_one_that_cannot_be_checked_does_not_stop_the_others_and_the_servers_collections_follow()
    {
        await using var jellyfin = await RecordedJellyfinServer.StartAsync();
        await using var world = await RealDiskLibrary.OpenAsync(await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.Replace(ServiceDescriptor.Singleton<IMediaServerClientRegistry>(provider => new RegistryThatFailsForPlex(new MediaServerClientRegistry(provider.GetServices<IMediaServerClient>()))))));
        var sintel = await AFilmAsync(world, "Sintel", 2010, "tt1727587", "Sintel (2010) [WEB 1080p].mkv");
        var server = await ServerAsync(world, jellyfin);
        var plex = JsonDocument.Parse(await (await world.App.Client.PostAsJsonAsync("/api/media-servers", new { kind = "plex", name = "Study", baseUrl = "http://127.0.0.1:1", token = "plex-token", isEnabled = true })).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var broken = await RuleAsync(world, RuleBody("Cannot be read", [plex], "log"));
        var first = await RuleAsync(world, RuleBody("Old films on the server", [server], "delete"));
        var second = await RuleAsync(world, RuleBody("Old films again", [], "log"));
        var idle = await RuleAsync(world, RuleBody("Off", [], "log", enabled: false));

        var service = new RetentionScheduleHostedService(world.App.Services.GetRequiredService<IServiceScopeFactory>(), new SkewedClock(TimeSpan.FromHours(25)), NullLogger<RetentionScheduleHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(async () => await CountAsync(world, "SELECT COUNT(*) FROM retention_runs WHERE trigger = 'schedule';") >= 2, "both readable due rules to be checked", TimeSpan.FromSeconds(30));
        await Wait.UntilAsync(async () => await CountAsync(world, "SELECT COUNT(*) FROM retention_collections;") >= 1, "the server's collection to be brought up to date", TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        async Task<string[]> TriggersOf(string rule)
        {
            using var runs = JsonDocument.Parse(await world.App.Client.GetStringAsync($"/api/retention/rules/{rule}/runs"));
            var items = runs.RootElement.ValueKind == JsonValueKind.Array ? runs.RootElement : runs.RootElement.GetProperty("items");
            return items.EnumerateArray().Select(run => run.GetProperty("trigger").GetString()!).ToArray();
        }

        Assert.Equal(["schedule"], await TriggersOf(first));
        Assert.Equal(["schedule"], await TriggersOf(second));
        Assert.Empty(await TriggersOf(broken));
        Assert.Empty(await TriggersOf(idle));
        Assert.Equal(0L, await CountAsync(world, $"SELECT COUNT(*) FROM retention_runs WHERE rule_id = '{idle}';"));
        Assert.True(File.Exists(sintel), "a first check only marks a title, so nothing is removed yet");
        Assert.True(jellyfin.Count("POST /Collections") > 0 || jellyfin.Count("GET /Users/") > 0, "the server was asked about its Leaving soon collections after the checks");
    }

    // ---- one check at a time ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Saving_a_rule_does_not_wait_for_a_check_still_reading_the_server_and_a_second_check_waits_its_turn_and_the_first_then_removes_nothing()
    {
        await using var jellyfin = await RecordedJellyfinServer.StartAsync();
        await using var world = await RealDiskLibrary.OpenAsync(await ApplicationTestHost.StartAsync());
        var film = await AFilmAsync(world, "Nosferatu", 1922, "tt0013442", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv");
        var server = await ServerAsync(world, jellyfin);
        var rule = await RuleAsync(world, RuleBody("Old films", [server]));
        Assert.True((await world.App.Client.PostAsync($"/api/retention/rules/{rule}/check", null)).IsSuccessStatusCode, "the first check marks the title");
        await TheDayArrivesAsync(world);
        jellyfin.CatalogueHeldBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsBefore = jellyfin.Count("GET /Items");

        var firstCheck = world.App.Client.PostAsync($"/api/retention/rules/{rule}/check", null);
        await Wait.UntilAsync(() => Task.FromResult(jellyfin.Count("GET /Items") > readsBefore), "the first check to be reading the server's films", TimeSpan.FromSeconds(30));
        // The person changes what the rule does on the day, from removing to only writing it down, while the check is still reading.
        var save = await world.App.Client.PutAsJsonAsync($"/api/retention/rules/{rule}", RuleBody("Old films", [server], "log"));
        Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());
        Assert.False(firstCheck.IsCompleted, "saving the rule did not wait for the check, and the check is still reading");

        var secondCheck = world.App.Client.PostAsync($"/api/retention/rules/{rule}/check", null);
        await Task.Delay(800);
        Assert.False(secondCheck.IsCompleted, "the second check of the same rule waits for the first");
        Assert.Equal(readsBefore + 1, jellyfin.Count("GET /Items"));

        jellyfin.CatalogueHeldBack.SetResult();
        Assert.True((await firstCheck).IsSuccessStatusCode);
        Assert.True((await secondCheck).IsSuccessStatusCode);
        Assert.True(File.Exists(film), "the check that began before the rule changed removed nothing, and neither did the one after it");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Switching_a_rule_to_delete_starts_the_wait_of_titles_already_leaving_again_from_today_so_nothing_goes_the_same_day()
    {
        await using var world = await RealDiskLibrary.OpenAsync(await ApplicationTestHost.StartAsync());
        var film = await AFilmAsync(world, "Nosferatu", 1922, "tt0013442", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv");
        var rule = await RuleAsync(world, RuleBody("Old films", [], "log", waitDays: 3));
        Assert.True((await world.App.Client.PostAsync($"/api/retention/rules/{rule}/check", null)).IsSuccessStatusCode);
        await TheDayArrivesAsync(world);
        Assert.Equal(1L, await CountAsync(world, "SELECT COUNT(*) FROM retention_marks WHERE remove_after_utc < strftime('%Y-%m-%dT%H:%M:%fZ','now');"));

        var save = await world.App.Client.PutAsJsonAsync($"/api/retention/rules/{rule}", RuleBody("Old films", [], "delete", waitDays: 3));
        Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());
        Assert.True((await world.App.Client.PostAsync($"/api/retention/rules/{rule}/check", null)).IsSuccessStatusCode);

        Assert.True(File.Exists(film), "a title whose day had come under a rule that only wrote it down is not removed the moment the rule starts deleting");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(0L, await CountAsync(world, "SELECT COUNT(*) FROM retention_marks WHERE remove_after_utc < strftime('%Y-%m-%dT%H:%M:%fZ','now');"));
        Assert.Equal(HttpStatusCode.OK, (await world.App.Client.GetAsync("/api/retention/rules")).StatusCode);
    }
}
