using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The download-client history, completion-callback and health-finding routes when the request is right (coverage register,
/// area api), with the facts of real replies: SABnzbd 5.1.3's recorded failed download is what a person dismisses, and
/// qBittorrent 5.2.1's recorded finished torrent is what its completion callback names (tests/fixtures/recorded).
/// </summary>
public sealed class ClientHistoryWebhookAndHealthRoutesAgainstRecordedAppsTests
{
    private static JsonNode FinishedTorrent() => JsonNode.Parse(RecordedReplies.Qbittorrent("finished-folder").Body("torrents/info"))![0]!;

    [Fact]
    public async Task Dismissing_a_failed_row_of_a_clients_history_is_remembered_by_Deluno_and_changes_nothing_at_the_client()
    {
        var failed = JsonNode.Parse(RecordedReplies.Sabnzbd("failed").Body("mode=history&output"))!["history"]!["slots"]![0]!;
        var itemId = failed["nzo_id"]!.GetValue<string>();
        await using var sab = await RecordedAppServer.StartAsync("sabnzbd", new[] { "version", "failed", "settings" }.Select(RecordedReplies.Sabnzbd));
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await (await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "sabnzbd",
            host = sab.Host,
            port = sab.Port,
            password = "0123456789abcdef0123456789abcdef",
            moviesCategory = "movies",
            tvCategory = "tv",
            priority = 10,
            isEnabled = true
        })).Content.ReadFromJsonAsync<JsonElement>();
        var clientId = client.GetProperty("id").GetString()!;
        Assert.Empty((await app.Client.GetFromJsonAsync<string[]>("/api/download-clients/history/dismissed"))!);
        var requestsBefore = sab.Requests.Count;

        var dismissed = await app.Client.PostAsJsonAsync($"/api/download-clients/{clientId}/history/{itemId}/dismiss", new { });

        Assert.True(dismissed.IsSuccessStatusCode, await dismissed.Content.ReadAsStringAsync());
        var body = await dismissed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(itemId, body.GetProperty("itemId").GetString());
        Assert.True(body.GetProperty("dismissed").GetBoolean());
        Assert.Equal([$"client-history:{clientId}:{itemId}"], (await app.Client.GetFromJsonAsync<string[]>("/api/download-clients/history/dismissed"))!);
        Assert.Equal(requestsBefore, sab.Requests.Count);
        Assert.Empty(sab.Unanswered);
    }

    [Fact]
    public async Task A_completion_callback_for_a_finished_torrent_marks_the_matching_download_complete_once_and_an_unknown_one_is_not_found()
    {
        var torrent = FinishedTorrent();
        var hash = torrent["hash"]!.GetValue<string>();
        var name = torrent["name"]!.GetValue<string>();
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", "movie-1", name, "Fixture", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await dispatches.RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: hash);
        Assert.Null((await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!.ClientCompletedUtc);

        var first = await app.Client.PostAsJsonAsync("/api/download-clients/client-1/webhook", new
        {
            @event = "completed",
            hash,
            name,
            savePath = torrent["save_path"]!.GetValue<string>(),
            sizeBytes = torrent["size"]!.GetValue<long>()
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var result = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.Equal(dispatchId, result.GetProperty("dispatchId").GetString());
        Assert.NotNull((await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!.ClientCompletedUtc);

        var repeat = await (await app.Client.PostAsJsonAsync("/api/download-clients/client-1/webhook", new { @event = "completed", hash, name })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("skipped duplicate webhook", repeat.GetProperty("message").GetString(), StringComparison.Ordinal);

        var unknown = await app.Client.PostAsJsonAsync("/api/download-clients/client-1/webhook", new { @event = "completed", hash = "0000000000000000000000000000000000000000", name = "Something.Else" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("No matching dispatch found.", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ignoring_a_health_finding_for_a_stalled_download_records_the_ignore_for_the_chosen_days_and_an_unknown_one_is_not_found()
    {
        var torrent = JsonNode.Parse(RecordedReplies.Qbittorrent("stalled").Body("torrents/info"))![0]!;
        var hash = torrent["hash"]!.GetValue<string>();
        var name = torrent["name"]!.GetValue<string>();
        await using var app = await ApplicationTestHost.StartAsync();
        var health = app.Services.GetRequiredService<IDownloadHealthRepository>();
        await health.RecordDownloadHealthObservationsAsync(
            "client-1", [hash],
            [new DownloadHealthObservation("client-1", hash, name, DownloadHealthStrikes.ClientStalled, "warning", "No throughput for 30 minutes.")],
            DownloadHealthStrikes.DefaultWindow, TimeSpan.FromHours(72), CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync($"/api/download-clients/client-1/queue/{hash}/health/{DownloadHealthStrikes.ClientStalled}/ignore", new { durationDays = 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(hash, record.GetProperty("queueItemId").GetString());
        var stored = Assert.Single(await health.ListDownloadHealthRecordsAsync(100, CancellationToken.None));
        Assert.NotNull(stored.IgnoredUntilUtc);
        Assert.InRange((stored.IgnoredUntilUtc!.Value - DateTimeOffset.UtcNow).TotalDays, 2.9, 3.1);

        var unknown = await app.Client.PostAsJsonAsync($"/api/download-clients/client-1/queue/not-recorded/health/{DownloadHealthStrikes.ClientStalled}/ignore", new { durationDays = 3 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_callback_naming_a_download_of_a_different_client_matches_nothing_by_dispatch_id_or_by_hash()
    {
        var torrent = FinishedTorrent();
        var hash = torrent["hash"]!.GetValue<string>();
        var name = torrent["name"]!.GetValue<string>();
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", "movie-1", name, "Fixture", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await dispatches.RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: hash);

        var byId = await app.Client.PostAsJsonAsync("/api/download-clients/client-2/webhook", new { @event = "completed", dispatchId, name });
        var byHash = await app.Client.PostAsJsonAsync("/api/download-clients/client-2/webhook", new { @event = "completed", hash, name });

        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byHash.StatusCode);
        Assert.Contains("No matching dispatch found.", await byId.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null((await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!.ClientCompletedUtc);
        var rightClient = await app.Client.PostAsJsonAsync("/api/download-clients/client-1/webhook", new { @event = "completed", dispatchId, name });
        Assert.Equal(HttpStatusCode.OK, rightClient.StatusCode);
    }

    [Fact]
    public async Task A_failed_event_records_the_import_outcome_failed_with_the_clients_own_words_a_typed_failure_and_a_webhook_failed_timeline_event()
    {
        var failed = JsonNode.Parse(RecordedReplies.Sabnzbd("failed").Body("mode=history&output"))!["history"]!["slots"]![0]!;
        var reason = failed["fail_message"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(reason), "the recorded failed download carries SABnzbd's own words");
        var name = failed["name"]!.GetValue<string>();
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", "movie-1", name, "Fixture", "client-1", "SABnzbd", "sent", null, cancellationToken: CancellationToken.None);
        await dispatches.RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: failed["nzo_id"]!.GetValue<string>());

        var withReason = await app.Client.PostAsJsonAsync("/api/download-clients/client-1/webhook", new { @event = "failed", dispatchId, failureReason = reason });

        Assert.Equal(HttpStatusCode.OK, withReason.StatusCode);
        Assert.Equal("Download failure recorded.", (await withReason.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        var stored = (await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!;
        Assert.Equal("failed", stored.ImportStatus);
        Assert.Equal(Deluno.Integrations.DownloadClients.DownloadClientFailureCodes.ClientReported, stored.ImportFailureCode);
        Assert.Equal(reason, stored.ImportFailureMessage);
        Assert.NotNull(stored.Failure);
        Assert.Equal("download-client", stored.Failure!.ServiceType);
        var timeline = await dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None);
        var webhookFailed = Assert.Single(timeline, item => item.EventType == "webhook-failed");
        Assert.Contains(reason, webhookFailed.DetailsJson!, StringComparison.Ordinal);

        // Without a reason the client's silence is said in the default words.
        var second = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", "movie-2", "Another.Release.2020.1080p", "Fixture", "client-1", "SABnzbd", "sent", null, cancellationToken: CancellationToken.None);
        await app.Client.PostAsJsonAsync("/api/download-clients/client-1/webhook", new { @event = "failed", dispatchId = second });
        Assert.Equal("Download client reported failure.", (await dispatches.GetDispatchAsync(second, CancellationToken.None))!.ImportFailureMessage);
    }
}
