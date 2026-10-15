using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Removing downloads from the Queue, with what Deluno keeps read back out of its own SQLite file: against the real replies of
/// qBittorrent 5.2.1 (tests/fixtures/recorded) served over a loopback port, what the client is told for a removal with the
/// default flags, with a blocklist, with the files kept and in bulk, and what each leaves in the jobs database (the download's
/// record, the refused release, the replacement search).
/// </summary>
public sealed class QueueRemovalOnTheRealDatabaseTests
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

    private sealed record Scene(string ClientId, string DispatchA, string DispatchB);

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, RecordedAppServer qbit)
    {
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
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

        return new Scene(clientId, await SentAsync(ReleaseA, HashA, "movie-1"), await SentAsync(ReleaseB, HashB, "movie-2"));
    }

    /// <summary>One value from the jobs database file itself, read beside the running application.</summary>
    private static object? JobsScalar(ApplicationTestHost app, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "jobs.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static string? DispatchStatus(ApplicationTestHost app, string dispatchId)
        => (string?)JobsScalar(app, "SELECT status FROM download_dispatches WHERE id = @id;", ("@id", dispatchId));

    private static long SearchJobs(ApplicationTestHost app) => (long)JobsScalar(app, "SELECT COUNT(*) FROM job_queue WHERE job_type LIKE '%search%';")!;

    private static async Task<HttpResponseMessage> DeleteAsync(ApplicationTestHost app, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await app.Client.SendAsync(request);
    }

    private static bool IsDelete(RecordedAppServer.Seen request) => request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal);

    [Fact]
    public async Task A_removal_with_no_flags_deletes_at_the_client_with_its_files_archives_the_record_and_blocklists_and_searches_for_nothing()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, qbit);

        var response = await DeleteAsync(app, $"/api/queue/{scene.ClientId}/{HashA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delete = Assert.Single(qbit.Requests, IsDelete);
        Assert.Contains(HashA, delete.Body, StringComparison.Ordinal);
        Assert.Contains("deleteFiles=true", delete.Body, StringComparison.Ordinal);
        Assert.Equal("archived", DispatchStatus(app, scene.DispatchA));
        Assert.NotEqual("archived", DispatchStatus(app, scene.DispatchB));
        Assert.DoesNotContain(ReleaseA, await app.Client.GetStringAsync("/api/blocked-releases"), StringComparison.Ordinal);
        Assert.Equal(0, SearchJobs(app));
    }

    [Fact]
    public async Task The_flags_in_the_query_decide_what_is_kept_refused_and_searched_for()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, qbit);

        // Refused and replaced: the release is blocklisted and a replacement search is queued.
        var replaced = await DeleteAsync(app, $"/api/queue/{scene.ClientId}/{HashA}?blocklist=true");
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Contains(ReleaseA, await app.Client.GetStringAsync("/api/blocked-releases"), StringComparison.Ordinal);
        Assert.Equal(1, SearchJobs(app));
        Assert.Equal("archived", DispatchStatus(app, scene.DispatchA));

        // The client is left alone and Deluno only stops tracking: nothing is deleted, refused or searched for.
        var deletesBefore = qbit.Requests.Count(IsDelete);
        var tracking = await DeleteAsync(app, $"/api/queue/{scene.ClientId}/{HashB}?removeFromClient=false");
        Assert.Equal(HttpStatusCode.OK, tracking.StatusCode);
        Assert.Equal(deletesBefore, qbit.Requests.Count(IsDelete));
        Assert.Equal("archived", DispatchStatus(app, scene.DispatchB));
        Assert.DoesNotContain(ReleaseB, await app.Client.GetStringAsync("/api/blocked-releases"), StringComparison.Ordinal);
        Assert.Equal(1, SearchJobs(app));
    }

    [Fact]
    public async Task A_bulk_removal_applies_the_same_flags_to_each_download_answers_a_result_for_each_and_records_each_in_the_database()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, qbit);

        var response = await DeleteAsync(app, "/api/queue/bulk?blocklist=true&skipRedownload=true",
            new { items = new[] { new { clientId = scene.ClientId, queueItemId = HashA }, new { clientId = scene.ClientId, queueItemId = HashB } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);
        Assert.All(results, result => Assert.True(result.GetProperty("succeeded").GetBoolean(), result.ToString()));
        var deletes = qbit.Requests.Where(IsDelete).ToArray();
        Assert.Equal(2, deletes.Length);
        Assert.All(deletes, request => Assert.Contains("deleteFiles=true", request.Body, StringComparison.Ordinal));
        Assert.Equal("archived", DispatchStatus(app, scene.DispatchA));
        Assert.Equal("archived", DispatchStatus(app, scene.DispatchB));
        var blocked = await app.Client.GetStringAsync("/api/blocked-releases");
        Assert.Contains(ReleaseA, blocked, StringComparison.Ordinal);
        Assert.Contains(ReleaseB, blocked, StringComparison.Ordinal);
        Assert.Equal(0, SearchJobs(app));
    }

    [Fact]
    public async Task A_bulk_removal_of_nothing_or_of_more_than_two_hundred_is_refused_and_a_foreign_download_refuses_the_whole_call_leaving_every_record_as_it_was()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, qbit);

        var empty = await DeleteAsync(app, "/api/queue/bulk", new { items = Array.Empty<object>() });
        var blank = await DeleteAsync(app, "/api/queue/bulk", new { items = new[] { new { clientId = scene.ClientId, queueItemId = "" } } });
        var tooMany = await DeleteAsync(app, "/api/queue/bulk", new { items = Enumerable.Range(0, 201).Select(i => new { clientId = scene.ClientId, queueItemId = $"hash-{i}" }).ToArray() });
        var foreign = await DeleteAsync(app, "/api/queue/bulk", new { items = new[] { new { clientId = scene.ClientId, queueItemId = HashA }, new { clientId = scene.ClientId, queueItemId = "added-by-hand" } } });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("at most 200", await tooMany.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        Assert.Contains("Deluno did not send", await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(qbit.Requests, IsDelete);
        Assert.NotEqual("archived", DispatchStatus(app, scene.DispatchA));
        Assert.NotEqual("archived", DispatchStatus(app, scene.DispatchB));
    }
}
