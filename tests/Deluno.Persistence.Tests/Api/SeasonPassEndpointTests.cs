using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The cross-series Season Pass screen's own endpoints (#791):
/// <c>GET /api/series/season-pass</c> and <c>PUT /api/series/season-pass/monitoring</c>.
/// </summary>
public sealed class SeasonPassEndpointTests
{
    [Fact]
    public async Task Lists_seasons_across_every_series_and_bulk_toggles_at_the_season_level()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await app.Client.PostAsJsonAsync(
            "/api/series/",
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true));
        created.EnsureSuccessStatusCode();
        var series = await created.Content.ReadFromJsonAsync<SeriesListItem>();
        Assert.NotNull(series);

        var repository = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await repository.SyncEpisodeCatalogueAsync(
            series!.Id,
            [
                new CatalogueEpisodeItem(1, 1, "Good News About Hell", null, DateTimeOffset.Parse("2022-02-18T00:00:00Z")),
                new CatalogueEpisodeItem(1, 2, "Half Loop", null, DateTimeOffset.Parse("2022-02-18T00:00:00Z"))
            ],
            source: "tmdb",
            CancellationToken.None);

        var listed = await app.Client.GetFromJsonAsync<List<SeriesSeasonPassItem>>("/api/series/season-pass");
        Assert.NotNull(listed);
        var season1 = Assert.Single(listed!, item => item.SeriesId == series.Id && item.SeasonNumber == 1);
        Assert.Equal(2, season1.EpisodeCount);
        Assert.Equal(2, season1.MonitoredCount);

        var toggled = await app.Client.PutAsJsonAsync(
            "/api/series/season-pass/monitoring",
            new { scopes = new[] { new { seriesId = series.Id, seasonNumber = 1 } }, monitored = false });
        Assert.True(toggled.IsSuccessStatusCode, await toggled.Content.ReadAsStringAsync());
        using var toggledBody = JsonDocument.Parse(await toggled.Content.ReadAsStringAsync());
        Assert.Equal(2, toggledBody.RootElement.GetProperty("updated").GetInt32());

        var afterToggle = await app.Client.GetFromJsonAsync<List<SeriesSeasonPassItem>>("/api/series/season-pass");
        Assert.Equal(0, Assert.Single(afterToggle!, item => item.SeriesId == series.Id && item.SeasonNumber == 1).MonitoredCount);
    }

    [Fact]
    public async Task Rejects_a_bulk_toggle_with_no_scopes()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PutAsJsonAsync(
            "/api/series/season-pass/monitoring",
            new { scopes = Array.Empty<object>(), monitored = true });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
