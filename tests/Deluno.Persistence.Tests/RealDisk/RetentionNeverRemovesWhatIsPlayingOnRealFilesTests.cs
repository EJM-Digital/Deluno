using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// "Never remove something while it plays": a retention rule that reads a connected media server asks it what is playing before it
/// removes anything. The real host runs over a real temp folder with a real film file and the real recycle bin; the media server is a
/// local server that answers as Jellyfin 12.2.0 recorded (its session list while Sintel plays, while nothing plays, and a refused sign-in).
/// While the film plays, its file stays and nothing else leaves either if the server cannot say; once it stops, the next check removes it.
/// </summary>
public sealed class RetentionNeverRemovesWhatIsPlayingOnRealFilesTests
{
    private static async Task<string> AFilmAsync(RealDiskLibrary world, string title, int year, string? imdb, string fileName)
    {
        var folder = Path.Combine(world.Root, $"{title} ({year})");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, fileName), Enumerable.Repeat((byte)0x42, 4096).ToArray());
        var response = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, imdbId = imdb, monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.True((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.HasFile);
        await world.SqlAsync("movies", $"UPDATE movie_entries SET created_utc = '{DateTimeOffset.UtcNow.AddDays(-365):O}' WHERE id = '{id}';");
        return Path.Combine(folder, fileName);
    }

    private static async Task<string> ServerAsync(RealDiskLibrary world, RecordedJellyfinServer jellyfin)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/media-servers", new { kind = "jellyfin", name = "Living room", baseUrl = jellyfin.BaseUrl, token = "jf-secret-token-1", isEnabled = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> RuleAsync(RealDiskLibrary world, string serverId)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films", level = "movie", isEnabled = true,
            steps = new[] { new { type = "condition", op = "keep", condition = "added:beyond:30" } },
            mediaServerIds = new[] { serverId }, removeFromServers = false, waitDays = 1, removalMode = "automatic", checkIntervalHours = 24, expiryAction = "delete", deferWhilePlaying = true
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> CheckAsync(RealDiskLibrary world, string ruleId)
    {
        var response = await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return text;
    }

    private static Task TheDayArrivesAsync(RealDiskLibrary world)
        => world.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");

    [Fact]
    public async Task A_film_playing_on_a_connected_server_keeps_its_file_on_its_day_and_goes_at_the_next_check_once_it_stops()
    {
        await using var jellyfin = await RecordedJellyfinServer.StartAsync();
        await using var world = await RealDiskLibrary.OpenAsync(await ApplicationTestHost.StartAsync());
        var sintel = await AFilmAsync(world, "Sintel", 2010, "tt1727587", "Sintel (2010) [WEB 1080p].mkv");
        var other = await AFilmAsync(world, "Nosferatu", 1922, "tt0013442", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv");
        var rule = await RuleAsync(world, await ServerAsync(world, jellyfin));
        await CheckAsync(world, rule);
        await TheDayArrivesAsync(world);

        jellyfin.Sessions = JellyfinSessions.Playing;
        var whilePlaying = await CheckAsync(world, rule);

        Assert.True(File.Exists(sintel), "the film that is playing stays on disk: " + whilePlaying);
        Assert.False(File.Exists(other), "a film nobody is watching still goes on its day: " + whilePlaying);
        Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.True(jellyfin.Count("GET /Sessions") > 0, "the server was asked what is playing");

        jellyfin.Sessions = JellyfinSessions.Idle;
        await CheckAsync(world, rule);

        Assert.False(File.Exists(sintel), "once it stops, the next check removes it");
        Assert.Equal(2, (await world.Bin.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task When_a_connected_server_cannot_say_what_is_playing_nothing_leaves_the_disk_until_it_can()
    {
        await using var jellyfin = await RecordedJellyfinServer.StartAsync();
        await using var world = await RealDiskLibrary.OpenAsync(await ApplicationTestHost.StartAsync());
        var first = await AFilmAsync(world, "Sintel", 2010, "tt1727587", "Sintel (2010) [WEB 1080p].mkv");
        var second = await AFilmAsync(world, "Nosferatu", 1922, "tt0013442", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv");
        var rule = await RuleAsync(world, await ServerAsync(world, jellyfin));
        await CheckAsync(world, rule);
        await TheDayArrivesAsync(world);

        jellyfin.Sessions = JellyfinSessions.Refused;
        var whileBlind = await CheckAsync(world, rule);

        Assert.True(File.Exists(first) && File.Exists(second), "nothing is removed while the server cannot be asked: " + whileBlind);
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Matches("(?i)Living room|Jellyfin", await world.App.Client.GetStringAsync("/api/activity?take=100"));

        jellyfin.Sessions = JellyfinSessions.Idle;
        await CheckAsync(world, rule);

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Equal(2, (await world.Bin.ListAsync(CancellationToken.None)).Count);
    }
}
