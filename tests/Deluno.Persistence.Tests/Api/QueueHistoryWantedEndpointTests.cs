using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The three single addresses Radarr and Sonarr have where Deluno had them
/// split (#1022): queue removal with its flags, history with an event type, and
/// wanted split into missing and cutoff-unmet.
/// </summary>
public sealed class QueueHistoryWantedEndpointTests
{
    // ------------------------------------------------------------------ queue

    [Fact]
    public async Task Deleting_a_queue_item_with_no_flags_removes_it_from_the_client_and_nothing_more()
    {
        var telemetry = new RecordingTelemetry();
        await using var app = await StartAsync(telemetry);

        var response = await app.Client.DeleteAsync("/api/queue/client-1/hash-abc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("client-1", "hash-abc", true, false, false), Assert.Single(telemetry.Removals));
    }

    [Fact]
    public async Task The_flags_in_the_query_reach_the_removal()
    {
        var telemetry = new RecordingTelemetry();
        await using var app = await StartAsync(telemetry);

        await app.Client.DeleteAsync("/api/queue/client-1/hash-abc?removeFromClient=false&blocklist=true&skipRedownload=true");

        Assert.Equal(("client-1", "hash-abc", false, true, true), Assert.Single(telemetry.Removals));
    }

    [Fact]
    public async Task A_removal_that_could_not_be_done_is_a_400_carrying_why()
    {
        var telemetry = new RecordingTelemetry { Succeed = false };
        await using var app = await StartAsync(telemetry);

        var response = await app.Client.DeleteAsync("/api/queue/client-1/hash-abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("did not work", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bulk_removal_applies_the_same_flags_to_each_download()
    {
        var telemetry = new RecordingTelemetry();
        await using var app = await StartAsync(telemetry);

        var response = await SendDeleteAsync(app.Client, "/api/queue/bulk?blocklist=true", new
        {
            items = new[] { new { clientId = "a", queueItemId = "1" }, new { clientId = "b", queueItemId = "2" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, telemetry.Removals.Count);
        Assert.All(telemetry.Removals, removal => Assert.True(removal.Blocklist));
    }

    [Fact]
    public async Task A_single_removal_of_a_download_Deluno_did_not_send_is_a_409()
    {
        var telemetry = new RecordingTelemetry { ForeignItemId = "hand-added" };
        await using var app = await StartAsync(telemetry);

        var response = await app.Client.DeleteAsync("/api/queue/client-1/hand-added");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Deluno did not send", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task One_download_Deluno_did_not_send_refuses_the_whole_bulk_call_and_removes_nothing()
    {
        var telemetry = new RecordingTelemetry { ForeignItemId = "hand-added" };
        await using var app = await StartAsync(telemetry);

        var response = await SendDeleteAsync(app.Client, "/api/queue/bulk", new
        {
            items = new[]
            {
                new { clientId = "client-1", queueItemId = "hash-abc" },
                new { clientId = "client-1", queueItemId = "hand-added" }
            }
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Deluno did not send", await response.Content.ReadAsStringAsync());
        Assert.Empty(telemetry.Removals);
    }

    [Fact]
    public async Task Bulk_removal_needs_at_least_one_complete_item()
    {
        var telemetry = new RecordingTelemetry();
        await using var app = await StartAsync(telemetry);

        var empty = await SendDeleteAsync(app.Client, "/api/queue/bulk", new { items = Array.Empty<object>() });
        var incomplete = await SendDeleteAsync(app.Client, "/api/queue/bulk", new { items = new[] { new { clientId = "a", queueItemId = "" } } });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Empty(telemetry.Removals);
    }

    [Fact]
    public async Task Removing_from_the_queue_needs_the_queue_scope()
    {
        var telemetry = new RecordingTelemetry();
        await using var app = await StartAsync(telemetry);
        using var readOnly = await KeyClientAsync(app, "read");
        using var queueKey = await KeyClientAsync(app, "queue");

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync("/api/queue/c/h")).StatusCode);
        Assert.Empty(telemetry.Removals);
        Assert.Equal(HttpStatusCode.OK, (await queueKey.DeleteAsync("/api/queue/c/h")).StatusCode);
        Assert.Single(telemetry.Removals);
    }

    // ---------------------------------------------------------------- history

    [Fact]
    public async Task History_names_each_download_by_the_furthest_it_got_and_filters_by_event_type()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var grabbed = await SeedDispatchAsync(app, "Grabbed.Only.2024", grab: "succeeded");
        var imported = await SeedDispatchAsync(app, "Imported.One.2024", grab: "succeeded", import: ("imported", "D:\\Films\\One.mkv", null));
        var grabFailed = await SeedDispatchAsync(app, "Grab.Refused.2024", grab: "failed", grabMessage: "Client said no");
        var importFailed = await SeedDispatchAsync(app, "Import.Broke.2024", grab: "succeeded", import: ("failed", null, "No video file"));

        var all = await HistoryAsync(app, string.Empty);
        Assert.Equal(4, all.GetProperty("items").GetArrayLength());

        var byId = all.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!);
        Assert.Equal("grabbed", byId[grabbed].GetProperty("eventType").GetString());
        Assert.Equal("downloadFolderImported", byId[imported].GetProperty("eventType").GetString());
        Assert.Equal("D:\\Films\\One.mkv", byId[imported].GetProperty("importedFilePath").GetString());
        Assert.Equal("downloadFailed", byId[grabFailed].GetProperty("eventType").GetString());
        Assert.Equal("Client said no", byId[grabFailed].GetProperty("message").GetString());
        Assert.Equal("No video file", byId[importFailed].GetProperty("message").GetString());

        Assert.Equal([grabbed], Ids(await HistoryAsync(app, "?eventType=grabbed")));
        Assert.Equal([imported], Ids(await HistoryAsync(app, "?eventType=downloadFolderImported")));
        Assert.Equal(
            new[] { grabFailed, importFailed }.Order(),
            Ids(await HistoryAsync(app, "?eventType=downloadFailed")).Order());
    }

    [Fact]
    public async Task History_pages_with_a_token_and_filters_by_media_type_and_title()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedDispatchAsync(app, "One.2024", grab: "succeeded", entityId: "t1");
        await SeedDispatchAsync(app, "Two.2024", grab: "succeeded", entityId: "t2");
        await SeedDispatchAsync(app, "Three.2024", grab: "succeeded", entityId: "t3", mediaType: "tv");

        var first = await HistoryAsync(app, "?pageSize=2");
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var second = await HistoryAsync(app, $"?pageSize=2&pageToken={Uri.EscapeDataString(first.GetProperty("nextPageToken").GetString()!)}");
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.False(second.GetProperty("hasMore").GetBoolean());

        Assert.Equal(2, (await HistoryAsync(app, "?mediaType=movies")).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await HistoryAsync(app, "?titleId=t3")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task History_refuses_an_event_or_media_type_it_does_not_have()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/history?eventType=movieFileDeleted")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/history?mediaType=music")).StatusCode);
    }

    // ----------------------------------------------------------------- wanted

    [Fact]
    public async Task Missing_and_cutoff_are_separate_lists_of_films_and_episodes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var missingFilm = await AddMovieAsync(app, "Missing Film", "missing");
        var upgradeFilm = await AddMovieAsync(app, "Upgrade Film", "upgrade");
        var series = await AddSeriesWithEpisodesAsync(app, "Some Show", 2);

        var missing = await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/missing");
        var cutoff = await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/cutoff");

        Assert.Equal(3, missing.GetProperty("totalRecords").GetInt32());
        var missingRecords = missing.GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal(3, missingRecords.Length);
        Assert.Equal("movies", missingRecords[0].GetProperty("mediaType").GetString());
        Assert.Equal(missingFilm, missingRecords[0].GetProperty("id").GetString());
        Assert.All(missingRecords.Skip(1), record =>
        {
            Assert.Equal("tv", record.GetProperty("mediaType").GetString());
            Assert.Equal(series, record.GetProperty("titleId").GetString());
            Assert.Equal(1, record.GetProperty("seasonNumber").GetInt32());
        });

        Assert.Equal(1, cutoff.GetProperty("totalRecords").GetInt32());
        Assert.Equal(upgradeFilm, cutoff.GetProperty("records")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_page_can_straddle_the_films_and_the_episodes_without_repeating_or_dropping_any()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddMovieAsync(app, "Film A", "missing");
        await AddMovieAsync(app, "Film B", "missing");
        await AddSeriesWithEpisodesAsync(app, "Show", 3);

        var seen = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var body = await app.Client.GetFromJsonAsync<JsonElement>($"/api/wanted/missing?pageSize=2&page={page}");
            Assert.Equal(5, body.GetProperty("totalRecords").GetInt32());
            seen.AddRange(body.GetProperty("records").EnumerateArray().Select(record => record.GetProperty("id").GetString()!));
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task The_media_type_narrows_the_list_and_monitored_defaults_to_true()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddMovieAsync(app, "Film", "missing");
        await AddSeriesWithEpisodesAsync(app, "Show", 2);

        Assert.Equal(1, (await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/missing?mediaType=movies")).GetProperty("totalRecords").GetInt32());
        var episodes = await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/missing?mediaType=tv");
        Assert.Equal(2, episodes.GetProperty("totalRecords").GetInt32());

        var unmonitor = await app.Client.PutAsJsonAsync("/api/series/episodes/monitoring", new
        {
            episodeIds = episodes.GetProperty("records").EnumerateArray().Select(record => record.GetProperty("id").GetString()).ToArray(),
            monitored = false
        });
        Assert.True(unmonitor.IsSuccessStatusCode, await unmonitor.Content.ReadAsStringAsync());

        Assert.Equal(1, (await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/missing")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(2, (await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/missing?monitored=false&mediaType=tv")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/wanted/missing?mediaType=music")).StatusCode);
    }

    [Fact]
    public async Task History_and_wanted_need_a_sign_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/wanted/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/wanted/cutoff")).StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<ApplicationTestHost> StartAsync(RecordingTelemetry telemetry)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientTelemetryService>();
            services.AddSingleton<IDownloadClientTelemetryService>(telemetry);
        });

    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = $"{scopes} key", scopes });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static Task<HttpResponseMessage> SendDeleteAsync(HttpClient client, string url, object body)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url) { Content = JsonContent.Create(body) });

    private static async Task<JsonElement> HistoryAsync(ApplicationTestHost app, string query)
    {
        var response = await app.Client.GetAsync($"/api/history{query}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Ids(JsonElement page)
        => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();

    private static async Task<string> SeedDispatchAsync(
        ApplicationTestHost app,
        string release,
        string grab,
        (string Status, string? Path, string? Failure)? import = null,
        string? grabMessage = null,
        string entityId = "title-1",
        string mediaType = "movies")
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var id = await jobs.RecordDownloadDispatchAsync(
            "library-1", mediaType, mediaType == "movies" ? "movie" : "series", entityId, release, "Indexer",
            "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await dispatches.RecordGrabAsync(id, grab, grab == "succeeded" ? 200 : 500, grabMessage, grab == "succeeded" ? null : "refused", null, CancellationToken.None);
        if (import is { } outcome)
        {
            await dispatches.RecordImportOutcomeAsync(id, outcome.Status, outcome.Path, outcome.Failure is null ? null : "no-video", outcome.Failure, CancellationToken.None);
        }

        return id;
    }

    private static async Task<string> AddMovieAsync(ApplicationTestHost app, string title, string wantedStatus)
    {
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = 2020, monitored = true });
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await app.Services.GetRequiredService<IMediaStateRepository>().EnsureWantedStateAsync(
            MediaKind.Movie, id, "library-1", wantedStatus, "test", hasFile: wantedStatus == "upgrade",
            currentQuality: null, targetQuality: null, qualityCutoffMet: false, CancellationToken.None);
        return id;
    }

    private static async Task<string> AddSeriesWithEpisodesAsync(ApplicationTestHost app, string title, int episodes)
    {
        var added = await app.Client.PostAsJsonAsync("/api/series/", new { title, startYear = 2020, monitored = true });
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await app.Services.GetRequiredService<ISeriesCatalogRepository>().SyncEpisodeCatalogueAsync(
            id,
            Enumerable.Range(1, episodes)
                .Select(number => new CatalogueEpisodeItem(1, number, $"Episode {number}", null, DateTimeOffset.Parse("2020-01-01T00:00:00Z").AddDays(number)))
                .ToArray(),
            "provider",
            CancellationToken.None);
        return id;
    }

    private sealed class RecordingTelemetry : IDownloadClientTelemetryService
    {
        public bool Succeed { get; init; } = true;

        /// <summary>A queue item id Deluno "did not send"; the shared ownership check refuses it.</summary>
        public string? ForeignItemId { get; init; }

        public Task<DownloadOwnership> CheckOwnershipAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult(queueItemId == ForeignItemId
                ? new DownloadOwnership(false, $"Deluno did not send the download \"{queueItemId}\".")
                : new DownloadOwnership(true, null));

        public List<(string ClientId, string QueueItemId, bool RemoveFromClient, bool Blocklist, bool SkipRedownload)> Removals { get; } = [];

        public Task<QueueRemovalResult> RemoveWithOptionsAsync(
            string clientId,
            string queueItemId,
            bool removeFromClient,
            bool blocklist,
            bool skipRedownload,
            CancellationToken cancellationToken)
        {
            if (queueItemId == ForeignItemId)
            {
                return Task.FromResult(new QueueRemovalResult(
                    clientId, queueItemId, "removed", false, $"Deluno did not send the download \"{queueItemId}\".", false, false, null, null)
                {
                    Refused = true
                });
            }

            Removals.Add((clientId, queueItemId, removeFromClient, blocklist, skipRedownload));
            return Task.FromResult(new QueueRemovalResult(
                clientId, queueItemId, "removed", Succeed, Succeed ? "ok" : "That did not work.", blocklist, false, null, null));
        }

        public Task<DownloadClientActionResult> ExecuteActionAsync(string clientId, DownloadClientActionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientTelemetrySnapshot>> GetUnfilteredSnapshotsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadCleanupPreview?> PreviewCleanupAsync(string clientId, string queueItemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadHealthRemediationReport> RunConfiguredHealthRemediationAsync(DownloadTelemetryOverview overview, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ReclaimCompletedAsync(string clientId, string queueItemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QueueRemovalResult> RemoveWithReasonAsync(string clientId, string queueItemId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ClearClientWarningsAsync(string clientId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
