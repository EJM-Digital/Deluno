using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Coverage register, area movie-detail: removing a film from its page while its download is still in qBittorrent. qBittorrent is
/// the real 5.2.1 app's recorded replies over a real loopback port (tests/fixtures/recorded/qbittorrent): Deluno signs in, and its
/// delete-with-data goes out as the request that was recorded, and the recorded "removed" answer comes back. The release is never
/// blocklisted: the film was not bad, it just is not wanted.
/// </summary>
public sealed class ARemovedFilmTakesItsRealDownloadWithItTests
{
    // The hash the recorded removal was made with: the request below must carry exactly it.
    private const string RecordedHash = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";

    [Fact]
    public async Task Removing_a_film_mid_download_sends_qBittorrent_the_recorded_delete_with_the_data_and_blocklists_nothing()
    {
        await using var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "resumed", "removed-with-data" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await ApplicationTestHost.StartAsync();

        var client = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(client.IsSuccessStatusCode, await client.Content.ReadAsStringAsync());
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var dispatchId = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId, "movies", "movie", movieId, "Sintel.2010.1080p.BluRay.x264-GRP", "Fixture Indexer", clientId, "qBittorrent", "sent", null,
            clientExternalId: RecordedHash, cancellationToken: CancellationToken.None);

        var removed = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { movieId }, operation = "remove" });

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        Assert.Equal(1, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("successCount").GetInt32());
        var delete = Assert.Single(qbit.Requests, request => request.PathAndQuery.EndsWith("/api/v2/torrents/delete", StringComparison.Ordinal));
        Assert.Equal("POST", delete.Method);
        Assert.Contains($"hashes={RecordedHash}", delete.Body, StringComparison.Ordinal);
        Assert.Contains("deleteFiles=true", delete.Body, StringComparison.Ordinal);
        Assert.Empty(qbit.Unanswered);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/movies/{movieId}")).StatusCode);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM blocked_releases;"));
        Assert.Equal("archived", await ApiScenes.TextAsync(app, "jobs", $"SELECT status FROM download_dispatches WHERE id = '{dispatchId}';"));
    }

    [Fact]
    public async Task Removing_a_film_leaves_another_films_download_running()
    {
        await using var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "resumed", "removed-with-data" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        async Task<string> AddAsync(string title, int year)
            => (await (await app.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId, searchForMissingNow = false })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var keep = await AddAsync("Tenet", 2020);
        var remove = await AddAsync("Sintel", 2010);
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId, "movies", "movie", keep, "Tenet.2020.1080p.BluRay.x264-GRP", "Fixture Indexer", clientId, "qBittorrent", "sent", null,
            clientExternalId: RecordedHash, cancellationToken: CancellationToken.None);

        var removed = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { remove }, operation = "remove" });

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        Assert.DoesNotContain(qbit.Requests, request => request.PathAndQuery.EndsWith("/api/v2/torrents/delete", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync($"/api/movies/{keep}")).StatusCode);
    }

    [Fact]
    public async Task Removing_a_show_sends_qBittorrent_the_recorded_delete_for_a_download_recorded_under_one_of_its_episodes_and_blocklists_nothing()
    {
        await using var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "resumed", "removed-with-data" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var show = await ApiScenes.ShowOnDiskAsync(app, "Severance", 2022);
        var other = await ApiScenes.ShowOnDiskAsync(app, "Andor", 2022, show.LibraryId);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            show.LibraryId, "tv", "episode", show.EpisodeId, "Severance.S01E01.1080p.WEB-DL-GRP", "Fixture Indexer", clientId, "qBittorrent", "sent", null,
            clientExternalId: RecordedHash, cancellationToken: CancellationToken.None);

        var removed = await app.Client.PostAsJsonAsync("/api/series/bulk", new { seriesIds = new[] { show.SeriesId }, operation = "remove" });

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        var delete = Assert.Single(qbit.Requests, request => request.PathAndQuery.EndsWith("/api/v2/torrents/delete", StringComparison.Ordinal));
        Assert.Contains($"hashes={RecordedHash}", delete.Body, StringComparison.Ordinal);
        Assert.Contains("deleteFiles=true", delete.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/series/{show.SeriesId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync($"/api/series/{other.SeriesId}")).StatusCode);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM blocked_releases;"));
        Assert.Equal("archived", await ApiScenes.TextAsync(app, "jobs", $"SELECT status FROM download_dispatches WHERE id = '{dispatchId}';"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removing_a_film_whose_download_is_with_Weir_cancels_the_hand_off_with_Weir_and_discards_what_it_would_have_made_with_or_without_the_files(bool deleteFiles)
    {
        await using var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "resumed", "removed-with-data" }.Select(RecordedReplies.Qbittorrent));
        // Weir's recorded cancel is for the id of its own recording; this film's hand-off has its own id, so the recorded answer is given to any id.
        await using var weir = await RecordedAppServer.StartAsync(
            "weir",
            new[] { "capabilities" }.Select(RecordedReplies.Weir),
            RecordedAppServer.Answering("DELETE", "/api/v1/intake/handoffs/deluno/", RecordedReplies.Weir("handoff-cancelled").Exchanges[0]));
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var connected = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", new
        {
            provider = "weir", submissionUrl = weir.BaseUrl + "/api/v1/intake/webhook/deluno", authHeaderName = "X-Webhook-Secret", secret = "a-webhook-secret", isEnabled = true
        });
        Assert.True(connected.IsSuccessStatusCode, await connected.Content.ReadAsStringAsync());
        var processorName = (await connected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films", mediaType = "movies", rootPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName,
            downloadsPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName,
            importWorkflow = "refine-before-import", processorName, processorOutputPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName
        });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        // The film already holds a file in the library, as a film that is being upgraded does.
        var film = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010, libraryId);
        var movieId = film.MovieId;
        const string release = "Sintel.2010.1080p.BluRay.x264-GRP";
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId, "movies", "movie", movieId, release, "Fixture Indexer", clientId, "qBittorrent", "sent", null,
            clientExternalId: RecordedHash, cancellationToken: CancellationToken.None);
        var source = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads", release)).FullName;
        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var handoff = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(libraryId, "movies", clientId, RecordedHash, release, source, processorName), CancellationToken.None);
        await processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);

        var removed = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { movieId }, operation = "remove", deleteFiles });

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        var cancel = Assert.Single(weir.Requests, request => request.Method == "DELETE");
        Assert.EndsWith($"/api/v1/intake/handoffs/deluno/{handoff.Id}", cancel.PathAndQuery, StringComparison.Ordinal);
        // Say what qBittorrent saw and what it could not answer, so a failure on a starved machine (the client gives up after 8 seconds) is told apart from a wrong request.
        Assert.True(
            qbit.Requests.Any(request => request.PathAndQuery.EndsWith("/api/v2/torrents/delete", StringComparison.Ordinal) && request.Body.Contains("deleteFiles=true", StringComparison.Ordinal)),
            "qBittorrent saw: " + string.Join("; ", qbit.Requests.Select(request => $"{request.Method} {request.PathAndQuery}")) + " | unanswered: " + string.Join("; ", qbit.Unanswered));
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM blocked_releases;"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/movies/{movieId}")).StatusCode);
        // Without the files, the library's copy stays where it is; with them, it goes (to the recycle bin) and nothing of the film is left behind.
        Assert.Equal(!deleteFiles, File.Exists(film.FilePath));
    }
}
