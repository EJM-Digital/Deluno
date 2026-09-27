using System.Net.Http.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// <c>PUT /api/series/{id}/monitor-new-episodes</c> (#801) - the series
/// screen/drawer's own control for the per-series "monitor new episodes"
/// preset, changeable at any time and not only at series add time.
/// </summary>
public sealed class SeriesMonitorNewEpisodesEndpointTests
{
    [Fact]
    public async Task Defaults_to_on_and_can_be_turned_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await app.Client.PostAsJsonAsync(
            "/api/series/",
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true));
        created.EnsureSuccessStatusCode();
        var series = await created.Content.ReadFromJsonAsync<SeriesListItem>();
        Assert.NotNull(series);
        Assert.True(series!.MonitorNewEpisodes);

        var turnedOff = await app.Client.PutAsJsonAsync(
            $"/api/series/{series.Id}/monitor-new-episodes",
            new { monitorNewEpisodes = false });
        Assert.True(turnedOff.IsSuccessStatusCode, await turnedOff.Content.ReadAsStringAsync());

        var refetched = await app.Client.GetFromJsonAsync<SeriesListItem>($"/api/series/{series.Id}");
        Assert.NotNull(refetched);
        Assert.False(refetched!.MonitorNewEpisodes);
    }

    [Fact]
    public async Task Returns_not_found_for_an_unknown_series()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PutAsJsonAsync(
            "/api/series/does-not-exist/monitor-new-episodes",
            new { monitorNewEpisodes = false });

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
