using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Removing downloads from the Queue when the request is right (coverage register, area api), against the real replies of
/// qBittorrent 5.2.1 (tests/fixtures/recorded) served over a real loopback port: what Deluno really sends the client for a
/// removal with and without the files, with and without a blocklist, one at a time and in bulk, and what it will not touch.
/// </summary>
public sealed class QueueRoutesAgainstRecordedAppsTests
{
    private const string HashA = "69ca35bca234b9cb822abc435ddbababf6147591";
    private const string HashB = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";
    private const string ReleaseA = "First.Film.2020.1080p.WEB-DL-DELUNO";
    private const string ReleaseB = "Second.Film.2021.1080p.WEB-DL-DELUNO";

    private static Task<RecordedAppServer> QbittorrentAsync()
    {
        var before = RecordedReplies.Qbittorrent("downloading").Exchanges[0];
        var after = RecordedReplies.Qbittorrent("removed-with-data").Exchanges[1];
        return RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "removed-with-data" }.Select(RecordedReplies.Qbittorrent),
            RecordedAppServer.InOrder("torrents/info", before, after, before, after, before, after));
    }

    private sealed record Scene(string ClientId, string LibraryId, string DispatchA, string DispatchB);

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, RecordedAppServer qbit)
    {
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent",
            host = qbit.Host,
            port = qbit.Port,
            username = "deluno",
            password = "a-test-password",
            moviesCategory = "movies",
            tvCategory = "tv",
            priority = 10,
            isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var client = await created.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = client.GetProperty("id").GetString()!;
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();

        async Task<string> SentAsync(string release, string hash, string entity)
        {
            var id = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", entity, release, "Fixture", clientId, client.GetProperty("name").GetString()!, "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.RecordGrabAsync(id, "succeeded", 200, null, null, null, CancellationToken.None, externalId: hash);
            return id;
        }

        return new Scene(clientId, libraryId, await SentAsync(ReleaseA, HashA, "movie-1"), await SentAsync(ReleaseB, HashB, "movie-2"));
    }

    private static async Task<HttpResponseMessage> DeleteAsync(ApplicationTestHost app, string url, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await app.Client.SendAsync(request);
    }

    [Fact]
    public async Task Removing_one_queue_item_deletes_it_and_its_files_at_the_client_and_closes_Delunos_record_of_it()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, qbit);

        var response = await DeleteAsync(app, $"/api/queue/{scene.ClientId}/{HashA}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var delete = Assert.Single(qbit.Requests, request => request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal));
        Assert.Contains(HashA, delete.Body, StringComparison.Ordinal);
        Assert.Contains("deleteFiles=true", delete.Body, StringComparison.Ordinal);
        Assert.Empty(qbit.Unanswered);
        Assert.Equal("archived", (await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchIncludingArchivedAsync(scene.DispatchA, CancellationToken.None))!.Status);
        Assert.NotEqual("archived", (await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchIncludingArchivedAsync(scene.DispatchB, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Removing_with_a_blocklist_refuses_the_release_and_stopping_tracking_sends_the_client_nothing()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, qbit);

        var blocked = await DeleteAsync(app, $"/api/queue/{scene.ClientId}/{HashA}?blocklist=true&skipRedownload=true");
        Assert.True(blocked.IsSuccessStatusCode, await blocked.Content.ReadAsStringAsync());
        Assert.Contains(ReleaseA, await app.Client.GetStringAsync("/api/blocked-releases"), StringComparison.Ordinal);
        Assert.True(await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type LIKE '%search%';") == 0, "skipRedownload means no replacement search");

        var deletesBefore = qbit.Requests.Count(request => request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal));
        var tracking = await DeleteAsync(app, $"/api/queue/{scene.ClientId}/{HashB}?removeFromClient=false");
        Assert.True(tracking.IsSuccessStatusCode, await tracking.Content.ReadAsStringAsync());
        Assert.Equal(deletesBefore, qbit.Requests.Count(request => request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal)));
        Assert.Equal("archived", (await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchIncludingArchivedAsync(scene.DispatchB, CancellationToken.None))!.Status);
        Assert.DoesNotContain(ReleaseB, await app.Client.GetStringAsync("/api/blocked-releases"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_several_queue_items_in_bulk_deletes_each_at_the_client_and_a_download_Deluno_did_not_send_stops_the_whole_call()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, qbit);

        var refused = await DeleteAsync(app, "/api/queue/bulk", new { items = new[] { new { clientId = scene.ClientId, queueItemId = HashA }, new { clientId = scene.ClientId, queueItemId = "added-by-hand" } } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.DoesNotContain(qbit.Requests, request => request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal));

        var response = await DeleteAsync(app, "/api/queue/bulk", new { items = new[] { new { clientId = scene.ClientId, queueItemId = HashA }, new { clientId = scene.ClientId, queueItemId = HashB } } });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var deletes = qbit.Requests.Where(request => request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, deletes.Length);
        Assert.Contains(deletes, request => request.Body.Contains(HashA, StringComparison.Ordinal));
        Assert.Contains(deletes, request => request.Body.Contains(HashB, StringComparison.Ordinal));
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        Assert.Equal("archived", (await dispatches.GetDispatchIncludingArchivedAsync(scene.DispatchA, CancellationToken.None))!.Status);
        Assert.Equal("archived", (await dispatches.GetDispatchIncludingArchivedAsync(scene.DispatchB, CancellationToken.None))!.Status);
        Assert.Empty(qbit.Unanswered);
    }
}
