using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    /// <summary>
    /// #1028: the Season Pass bulk endpoint used to change monitored state
    /// without ever going through <c>TitleRemovalService.CancelDownloadForUnmonitoredTitleAsync</c>
    /// (#900/#947's own path), so unmonitoring a season here left its
    /// downloads running while unmonitoring the same episode from the show's
    /// own page cancelled them. This proves the bulk endpoint now cancels an
    /// in-flight per-episode download and says so in Activity.
    /// </summary>
    [Fact]
    public async Task Unmonitoring_a_season_from_Season_Pass_cancels_its_episode_download()
    {
        var telemetry = new FakeDownloadClientTelemetryService();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientTelemetryService>();
            services.AddSingleton<IDownloadClientTelemetryService>(telemetry);
        });

        var created = await app.Client.PostAsJsonAsync(
            "/api/series/",
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true));
        created.EnsureSuccessStatusCode();
        var series = await created.Content.ReadFromJsonAsync<SeriesListItem>();
        Assert.NotNull(series);

        var repository = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await repository.SyncEpisodeCatalogueAsync(
            series!.Id,
            [new CatalogueEpisodeItem(1, 1, "Good News About Hell", null, DateTimeOffset.Parse("2022-02-18T00:00:00Z"))],
            source: "tmdb",
            CancellationToken.None);
        var inventory = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var episodeId = inventory!.Episodes.Single().EpisodeId;

        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            "lib-1", "tv", "episode", episodeId, "Severance.S01E01.1080p.WEB-DL-GROUP", "TestIndexer",
            "client-1", "Test Client", "sent", null, cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, "hash-s01e01", downloadedBytes: 1024, completed: false, CancellationToken.None);

        var toggled = await app.Client.PutAsJsonAsync(
            "/api/series/season-pass/monitoring",
            new { scopes = new[] { new { seriesId = series.Id, seasonNumber = 1 } }, monitored = false });
        Assert.True(toggled.IsSuccessStatusCode, await toggled.Content.ReadAsStringAsync());

        var call = Assert.Single(telemetry.Calls);
        Assert.Equal("client-1", call.ClientId);
        Assert.Equal(DownloadClientActions.DeleteWithData, call.Action);
        Assert.Equal("hash-s01e01", call.QueueItemId);

        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        var entry = (await activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(item => item.Category == "episode.download-cancelled");
        Assert.Contains("no longer monitored", entry.Message);
        Assert.Contains("cancelled its download", entry.Message);
    }

    /// <summary>
    /// The #947 season-pack rule, exercised through Season Pass: a season
    /// pack dispatched under the series and covering episodes from two
    /// seasons is left running while unmonitoring only one of those seasons
    /// - the other episode it covers is still monitored - and only cancelled
    /// once the second season is unmonitored too.
    /// </summary>
    [Fact]
    public async Task Unmonitoring_one_season_leaves_a_season_pack_running_until_every_season_it_covers_is_unmonitored()
    {
        var telemetry = new FakeDownloadClientTelemetryService();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientTelemetryService>();
            services.AddSingleton<IDownloadClientTelemetryService>(telemetry);
        });

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
                new CatalogueEpisodeItem(2, 1, "Hello, Ms. Cobel", null, DateTimeOffset.Parse("2025-01-17T00:00:00Z"))
            ],
            source: "tmdb",
            CancellationToken.None);
        var inventory = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var season1EpisodeId = inventory!.Episodes.Single(episode => episode.SeasonNumber == 1).EpisodeId;
        var season2EpisodeId = inventory.Episodes.Single(episode => episode.SeasonNumber == 2).EpisodeId;

        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var notesJson = JsonSerializer.Serialize(new { episodeIds = new[] { season1EpisodeId, season2EpisodeId } });
        var packDispatchId = await jobs.RecordDownloadDispatchAsync(
            "lib-1", "tv", "series", series.Id, "Severance.Complete.1080p.WEB-DL-GROUP", "TestIndexer",
            "client-1", "Test Client", "sent", notesJson, cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(packDispatchId, "hash-pack", downloadedBytes: 1024, completed: false, CancellationToken.None);

        var firstToggle = await app.Client.PutAsJsonAsync(
            "/api/series/season-pass/monitoring",
            new { scopes = new[] { new { seriesId = series.Id, seasonNumber = 1 } }, monitored = false });
        Assert.True(firstToggle.IsSuccessStatusCode, await firstToggle.Content.ReadAsStringAsync());

        // Season 2 is still monitored, so the pack keeps downloading.
        Assert.Empty(telemetry.Calls);
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        var keptRunning = (await activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(item => item.Category == "series.season-pack-kept-running");
        Assert.Contains("still monitored", keptRunning.Message);

        var secondToggle = await app.Client.PutAsJsonAsync(
            "/api/series/season-pass/monitoring",
            new { scopes = new[] { new { seriesId = series.Id, seasonNumber = 2 } }, monitored = false });
        Assert.True(secondToggle.IsSuccessStatusCode, await secondToggle.Content.ReadAsStringAsync());

        // Now every episode the pack covers is unmonitored, so it is cancelled.
        var call = Assert.Single(telemetry.Calls);
        Assert.Equal("hash-pack", call.QueueItemId);
    }

    private sealed class FakeDownloadClientTelemetryService : IDownloadClientTelemetryService
    {
        public List<(string ClientId, string Action, string QueueItemId)> Calls { get; } = [];

        public Task<DownloadClientActionResult> ExecuteActionAsync(string clientId, DownloadClientActionRequest request, CancellationToken cancellationToken)
        {
            Calls.Add((clientId, request.Action, request.QueueItemId));
            return Task.FromResult(new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, true, "ok"));
        }

        public Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientTelemetrySnapshot>> GetSnapshotsForLeftoverScanAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadCleanupPreview?> PreviewCleanupAsync(string clientId, string queueItemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadHealthRemediationReport> RunConfiguredHealthRemediationAsync(DownloadTelemetryOverview overview, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ReclaimCompletedAsync(string clientId, string queueItemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QueueRemovalResult> RemoveWithReasonAsync(string clientId, string queueItemId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ClearClientWarningsAsync(string clientId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
