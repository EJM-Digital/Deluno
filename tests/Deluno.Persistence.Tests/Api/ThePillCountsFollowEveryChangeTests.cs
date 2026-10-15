using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The shelf's filter pills (coverage register, area library): their counts and the total are read from the real catalogue each time,
/// so unmonitoring a film moves it from one pill to the other and the total stays what it was, over real films with real files.
/// </summary>
public sealed class ThePillCountsFollowEveryChangeTests
{
    [Fact]
    public async Task Unmonitoring_a_film_moves_it_between_pills_in_the_next_read_and_the_total_does_not_change()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var first = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var second = await ApiScenes.FilmOnDiskAsync(app, "Heat", 1995, first.LibraryId);
        Assert.True(File.Exists(first.FilePath) && File.Exists(second.FilePath));

        var before = await ReadAsync(app, "");

        Assert.Equal(2, before.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, before.GetProperty("facets").GetProperty("unmonitored").GetInt32());

        var changed = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { first.MovieId }, monitored = false });
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        var after = await ReadAsync(app, "");

        Assert.Equal(2, after.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, after.GetProperty("facets").GetProperty("unmonitored").GetInt32());
        var unmonitoredOnly = await ReadAsync(app, "&monitored=false");
        Assert.Equal(first.MovieId, Assert.Single(unmonitoredOnly.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());

        var back = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { first.MovieId }, monitored = true });
        Assert.True(back.IsSuccessStatusCode);
        Assert.Equal(0, (await ReadAsync(app, "")).GetProperty("facets").GetProperty("unmonitored").GetInt32());
    }

    private static async Task<JsonElement> ReadAsync(ApplicationTestHost app, string extra)
    {
        var response = await app.Client.GetAsync($"/api/movies/page?pageSize=50{extra}");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        return JsonSerializer.Deserialize<JsonElement>(raw);
    }
}
