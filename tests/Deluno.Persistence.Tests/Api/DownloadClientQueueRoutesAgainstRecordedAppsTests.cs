using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The queue-level download-client routes when the request is right (coverage register, area api), against the real
/// replies of qBittorrent 5.2.1 and SABnzbd 5.1.3 (tests/fixtures/recorded) served over a real loopback port: what
/// Deluno really sends to the client for a pause, a removal, a resume and a category check.
/// </summary>
public sealed class DownloadClientQueueRoutesAgainstRecordedAppsTests
{
    private const string Hash = "69ca35bca234b9cb822abc435ddbababf6147591";

    private static Task<RecordedAppServer> QbittorrentAsync(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept, params string[] states)
        => RecordedAppServer.StartAsync("qbittorrent", states.Select(RecordedReplies.Qbittorrent), intercept);

    private static Task<RecordedAppServer> SabnzbdAsync(params string[] states)
        => RecordedAppServer.StartAsync("sabnzbd", states.Select(RecordedReplies.Sabnzbd));

    private static async Task<JsonElement> CreateClientAsync(ApplicationTestHost app, object form)
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients", form);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object QbittorrentForm(RecordedAppServer server)
        => new { protocol = "qbittorrent", host = server.Host, port = server.Port, username = "deluno", password = "a-test-password", moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true };

    private static object SabnzbdForm(RecordedAppServer server)
        => new { protocol = "sabnzbd", host = server.Host, port = server.Port, password = "0123456789abcdef0123456789abcdef", moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true };

    /// <summary>A download Deluno itself sent to the client (the Queue only acts on those), as the dispatch record it leaves.</summary>
    private static async Task<string> SentAsync(ApplicationTestHost app, string clientId, string clientName, string itemId)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            libraryId, "movies", "movie", "movie-1", "Recorded.Film.2020.1080p.WEB-DL-DELUNO", "Fixture", clientId, clientName, "sent", null,
            cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: itemId);
        return dispatchId;
    }

    [Fact]
    public async Task Pausing_and_resuming_a_torrent_sends_the_real_stop_and_start_calls_for_that_torrent_and_answers_succeeded()
    {
        await using var qbit = await QbittorrentAsync(RecordedAppServer.InOrder("torrents/info", RecordedReplies.Qbittorrent("downloading").Exchanges[0], RecordedReplies.Qbittorrent("paused").Exchanges[1], RecordedReplies.Qbittorrent("resumed").Exchanges[2], RecordedReplies.Qbittorrent("resumed").Exchanges[4]), "sign-in", "version", "paused", "resumed");
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await CreateClientAsync(app, QbittorrentForm(qbit));
        var id = client.GetProperty("id").GetString()!;
        await SentAsync(app, id, client.GetProperty("name").GetString()!, Hash);

        var pause = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/queue/actions", new { action = "pause", queueItemId = Hash });
        Assert.True(pause.IsSuccessStatusCode, await pause.Content.ReadAsStringAsync());
        Assert.True((await pause.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("succeeded").GetBoolean());
        var resume = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/queue/actions", new { action = "resume", queueItemId = Hash });
        Assert.True(resume.IsSuccessStatusCode, await resume.Content.ReadAsStringAsync());

        var sent = qbit.Requests.Where(request => request.Method == "POST" && (request.PathAndQuery.EndsWith("torrents/stop", StringComparison.Ordinal) || request.PathAndQuery.EndsWith("torrents/start", StringComparison.Ordinal))).ToArray();
        Assert.Equal(2, sent.Length);
        Assert.All(sent, request => Assert.Contains(Hash, request.Body, StringComparison.Ordinal));
        Assert.Empty(qbit.Unanswered);

        var other = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/queue/actions", new { action = "pause", queueItemId = "ffffffffffffffffffffffffffffffffffffffff" });
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
    }

    [Fact]
    public async Task Removing_a_download_with_a_reason_deletes_it_and_its_files_at_the_client_and_closes_Delunos_record_of_it()
    {
        await using var qbit = await QbittorrentAsync(RecordedAppServer.InOrder("torrents/info", RecordedReplies.Qbittorrent("downloading").Exchanges[0], RecordedReplies.Qbittorrent("removed-with-data").Exchanges[1]), "sign-in", "version", "removed-with-data");
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await CreateClientAsync(app, QbittorrentForm(qbit));
        var id = client.GetProperty("id").GetString()!;
        var dispatchId = await SentAsync(app, id, client.GetProperty("name").GetString()!, Hash);

        var response = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/queue/{Hash}/remove", new { reason = "unwanted" });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("succeeded").GetBoolean());
        var delete = Assert.Single(qbit.Requests, request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal));
        Assert.Contains(Hash, delete.Body, StringComparison.Ordinal);
        Assert.Contains("deleteFiles=true", delete.Body, StringComparison.Ordinal);
        Assert.Empty(qbit.Unanswered);
        var stored = await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchIncludingArchivedAsync(dispatchId, CancellationToken.None);
        Assert.Equal("archived", stored!.Status);

        var unknownReason = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/queue/{Hash}/remove", new { reason = "because" });
        Assert.False(unknownReason.IsSuccessStatusCode);
    }

    /// <summary>
    /// Each reason the removal dialog offers, against qBittorrent's recorded replies: "It's bad" and "Remove this copy" delete the
    /// torrent with its files, "Just stop tracking it" sends the client nothing at all, and each answers with the result the dialog
    /// toasts. A client Deluno does not have is refused with a result that did not succeed.
    /// </summary>
    [Theory]
    [InlineData("failed", true, true)]
    [InlineData("reclaim", true, false)]
    [InlineData("forget", false, false)]
    public async Task Each_removal_reason_does_what_it_says_to_the_client_and_returns_the_result_the_dialog_shows(string reason, bool deletesAtClient, bool blocklists)
    {
        await using var qbit = await QbittorrentAsync(RecordedAppServer.InOrder("torrents/info", RecordedReplies.Qbittorrent("downloading").Exchanges[0], RecordedReplies.Qbittorrent("removed-with-data").Exchanges[1]), "sign-in", "version", "removed-with-data");
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await CreateClientAsync(app, QbittorrentForm(qbit));
        var id = client.GetProperty("id").GetString()!;
        var dispatchId = await SentAsync(app, id, client.GetProperty("name").GetString()!, Hash);

        var response = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/queue/{Hash}/remove", new { reason });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("succeeded").GetBoolean());
        Assert.Equal(blocklists, result.GetProperty("blocklisted").GetBoolean());
        var deletes = qbit.Requests.Where(request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal)).ToArray();
        if (deletesAtClient)
        {
            var delete = Assert.Single(deletes);
            Assert.Contains(Hash, delete.Body, StringComparison.Ordinal);
            Assert.Contains("deleteFiles=true", delete.Body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(deletes);
        }

        Assert.Empty(qbit.Unanswered);
        var stored = await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchIncludingArchivedAsync(dispatchId, CancellationToken.None);
        Assert.Equal("archived", stored!.Status);
        var unknownClient = await app.Client.PostAsJsonAsync($"/api/download-clients/no-such-client/queue/{Hash}/remove", new { reason });
        Assert.False(unknownClient.IsSuccessStatusCode);
        Assert.False((await unknownClient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Clearing_a_clients_warnings_asks_SABnzbd_to_clear_them_too()
    {
        await using var sab = await SabnzbdAsync("version", "idle", "settings", "warnings-cleared");
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await CreateClientAsync(app, SabnzbdForm(sab))).GetProperty("id").GetString()!;

        var response = await app.Client.PostAsync($"/api/download-clients/{id}/warnings/clear", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(sab.Requests, request => request.PathAndQuery.Contains("mode=warnings", StringComparison.Ordinal) && request.PathAndQuery.Contains("name=clear", StringComparison.Ordinal));
        Assert.Empty(sab.Unanswered);
    }

    [Fact]
    public async Task Checking_a_category_reads_the_clients_real_category_list_and_says_whether_it_exists()
    {
        await using var qbit = await QbittorrentAsync(null, "sign-in", "version", "settings");
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await CreateClientAsync(app, QbittorrentForm(qbit))).GetProperty("id").GetString()!;
        var recordedCategory = JsonDocument.Parse(RecordedReplies.Qbittorrent("settings").Body("torrents/categories")).RootElement.EnumerateObject().First().Name;

        var found = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/categories/check", new { category = recordedCategory });
        var missing = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/categories/check", new { category = "no-such-category" });

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        var foundBody = await found.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(foundBody.GetProperty("supported").GetBoolean());
        Assert.True(foundBody.GetProperty("found").GetBoolean(), foundBody.ToString());
        var missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(missingBody.GetProperty("found").GetBoolean());
        Assert.Empty(qbit.Unanswered);
        var blank = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/categories/check", new { category = " " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/download-clients/no-such-client/categories/check", new { category = "movies" })).StatusCode);
    }
}
