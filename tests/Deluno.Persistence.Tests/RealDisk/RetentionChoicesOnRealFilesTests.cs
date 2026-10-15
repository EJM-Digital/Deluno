using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The other things a retention rule can do or be told, on a real library with the real recycle bin: give the title a tag and
/// change nothing else, wait for a person before removing, let a kept title leave the list, and drop a rule with its marks.
/// </summary>
public sealed class RetentionChoicesOnRealFilesTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
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
        Assert.True((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.HasFile);
        await world.SqlAsync("movies", $"UPDATE movie_entries SET created_utc = '{DateTimeOffset.UtcNow.AddDays(-365):O}' WHERE id = '{id}';");
        return (id, file);
    }

    private static async Task<string> RuleAsync(RealDiskLibrary world, string expiryAction, string removalMode = "automatic", string? tag = null)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films", level = "movie", isEnabled = true,
            steps = new[] { new { type = "condition", op = "keep", condition = "added:beyond:30" } },
            mediaServerIds = Array.Empty<string>(), removeFromServers = false, waitDays = 1, removalMode, checkIntervalHours = 24,
            expiryAction, expiryTag = expiryAction == "tag" ? (tag ?? "leaving") : null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> CheckAsync(RealDiskLibrary world, string ruleId)
    {
        var response = await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task TheDayArrivesAsync(RealDiskLibrary world)
        => world.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");

    [Fact]
    public async Task On_its_day_a_tag_rule_gives_the_title_its_tag_and_leaves_its_files_and_monitoring_alone()
    {
        await using var world = await StartWorldAsync();
        var (id, file) = await AnOldFilmAsync(world);
        var rule = await RuleAsync(world, "tag", tag: "leaving");

        await CheckAsync(world, rule);
        await TheDayArrivesAsync(world);
        await CheckAsync(world, rule);

        Assert.True(File.Exists(file));
        Assert.All(File.ReadAllBytes(file), value => Assert.Equal(0x42, value));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        var movie = (await world.Movies.GetByIdAsync(id, CancellationToken.None))!;
        Assert.True(movie.Monitored);
        Assert.True(movie.HasFile);
        Assert.Contains("leaving", await world.App.Client.GetStringAsync("/api/tags"), StringComparison.OrdinalIgnoreCase);
        var carried = await world.App.Services.GetRequiredService<Deluno.Contracts.ITitleTagService>().ListNamesAsync(Deluno.Contracts.MediaKind.Movie, id, CancellationToken.None);
        Assert.Equal(["leaving"], carried);
    }

    [Fact]
    public async Task An_ask_first_rule_waits_on_its_day_and_removes_only_when_a_person_confirms_it()
    {
        await using var world = await StartWorldAsync();
        var (id, file) = await AnOldFilmAsync(world);
        var rule = await RuleAsync(world, "delete", removalMode: "ask");

        await CheckAsync(world, rule);
        await TheDayArrivesAsync(world);
        await CheckAsync(world, rule);

        Assert.True(File.Exists(file), "on its day an ask-first rule only moves the title to Ready to remove");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.OK, (await world.App.Client.GetAsync($"/api/movies/{id}")).StatusCode);

        var confirmed = await world.App.Client.PostAsJsonAsync("/api/retention/leaving-soon/remove", new { ruleId = rule, entityIds = new[] { id } });

        Assert.True(confirmed.IsSuccessStatusCode, await confirmed.Content.ReadAsStringAsync());
        Assert.False(File.Exists(file), "confirming sends the file to the recycle bin");
        Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, (await world.App.Client.GetAsync($"/api/movies/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_kept_title_leaves_the_leaving_soon_list_at_once_and_its_files_are_never_removed()
    {
        await using var world = await StartWorldAsync();
        var (id, file) = await AnOldFilmAsync(world);
        var rule = await RuleAsync(world, "delete");
        await CheckAsync(world, rule);
        Assert.NotEmpty((await world.App.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon")).EnumerateArray());

        var kept = await world.App.Client.PostAsJsonAsync("/api/retention/keeps", new { entityId = id, level = "movie", ruleId = rule, note = "Keep" });
        var blank = await world.App.Client.PostAsJsonAsync("/api/retention/keeps", new { entityId = "", level = "movie" });
        await TheDayArrivesAsync(world);
        await CheckAsync(world, rule);

        Assert.True(kept.IsSuccessStatusCode, await kept.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Choose a title to keep", await blank.Content.ReadAsStringAsync());
        Assert.Empty((await world.App.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon")).EnumerateArray());
        Assert.True(File.Exists(file));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_rule_removes_it_and_its_marks_and_a_second_delete_is_404_and_no_file_moves()
    {
        await using var world = await StartWorldAsync();
        var (_, file) = await AnOldFilmAsync(world);
        var rule = await RuleAsync(world, "delete");
        await CheckAsync(world, rule);
        Assert.True(Convert.ToInt64(await world.ScalarAsync("platform", "SELECT COUNT(*) FROM retention_marks;")) >= 1);

        var deleted = await world.App.Client.DeleteAsync($"/api/retention/rules/{rule}");
        var again = await world.App.Client.DeleteAsync($"/api/retention/rules/{rule}");

        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(0L, Convert.ToInt64(await world.ScalarAsync("platform", "SELECT COUNT(*) FROM retention_marks;")));
        Assert.Equal(0L, Convert.ToInt64(await world.ScalarAsync("platform", $"SELECT COUNT(*) FROM retention_rules WHERE id = '{rule}';")));
        Assert.True(File.Exists(file));
    }
}
