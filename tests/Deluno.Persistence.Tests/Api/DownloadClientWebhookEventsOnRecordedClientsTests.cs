using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The download-client completion callback (POST /api/download-clients/{id}/webhook) for each name a client may give its event,
/// and for a completion or a failure that arrives a second time. The facts of the finished download (its hash, name, size and
/// folder) are qBittorrent 5.2.1's own recorded reply (tests/fixtures/recorded/qbittorrent), as the client would put them in the callback.
/// </summary>
public sealed class DownloadClientWebhookEventsOnRecordedClientsTests
{
    private static JsonNode FinishedTorrent() => JsonNode.Parse(RecordedReplies.Qbittorrent("finished-folder").Body("torrents/info"))![0]!;

    private static async Task<string> SentAsync(ApplicationTestHost app, string movieId)
    {
        var torrent = FinishedTorrent();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var id = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", movieId, torrent["name"]!.GetValue<string>(), "Fixture", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await dispatches.RecordGrabAsync(id, "succeeded", 200, null, null, null, CancellationToken.None, externalId: torrent["hash"]!.GetValue<string>());
        return id;
    }

    private static async Task<JsonElement> PostAsync(ApplicationTestHost app, object body)
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients/client-1/webhook", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("download.completed")]
    [InlineData("torrent_completed")]
    [InlineData("nzb_completed")]
    [InlineData("finished")]
    [InlineData("Download.Completed")]
    [InlineData("TORRENT_COMPLETED")]
    [InlineData("Finished")]
    public async Task Every_name_a_client_gives_a_finished_download_records_the_completion(string eventName)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var dispatchId = await SentAsync(app, "movie-1");

        var reply = await PostAsync(app, new { @event = eventName, dispatchId });

        Assert.Equal("Download completion recorded.", reply.GetProperty("message").GetString());
        var stored = (await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchAsync(dispatchId, CancellationToken.None))!;
        Assert.NotNull(stored.ClientCompletedUtc);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("download.failed")]
    [InlineData("torrent_failed")]
    [InlineData("nzb_failed")]
    [InlineData("error")]
    [InlineData("Download.Failed")]
    [InlineData("NZB_FAILED")]
    [InlineData("Error")]
    public async Task Every_name_a_client_gives_a_failed_download_records_the_failure_in_the_clients_words(string eventName)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var dispatchId = await SentAsync(app, "movie-1");

        var reply = await PostAsync(app, new { @event = eventName, dispatchId, failureReason = "Unpacking failed." });

        Assert.Equal("Download failure recorded.", reply.GetProperty("message").GetString());
        var stored = (await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchAsync(dispatchId, CancellationToken.None))!;
        Assert.Equal("failed", stored.ImportStatus);
        Assert.Equal("Unpacking failed.", stored.ImportFailureMessage);
    }

    [Fact]
    public async Task An_event_Deluno_has_no_name_for_is_accepted_and_nothing_is_changed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var dispatchId = await SentAsync(app, "movie-1");

        var reply = await PostAsync(app, new { @event = "paused", dispatchId });

        Assert.Contains("acknowledged, no action taken", reply.GetProperty("message").GetString(), StringComparison.Ordinal);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        Assert.Null((await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!.ClientCompletedUtc);
        Assert.DoesNotContain(await dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None), item => item.EventType.StartsWith("webhook-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_completion_records_the_size_the_client_reported_and_a_timeline_event_with_where_it_saved_it()
    {
        var torrent = FinishedTorrent();
        var size = torrent["size"]!.GetValue<long>();
        var savePath = torrent["save_path"]!.GetValue<string>();
        await using var app = await ApplicationTestHost.StartAsync();
        var dispatchId = await SentAsync(app, "movie-1");

        await PostAsync(app, new { @event = "completed", dispatchId, savePath, sizeBytes = size });

        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var stored = (await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!;
        Assert.Equal(size, stored.DownloadedBytes);
        var completed = Assert.Single(await dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None), item => item.EventType == "webhook-completed");
        var details = JsonNode.Parse(completed.DetailsJson!)!;
        Assert.Equal(savePath, details["savePath"]!.GetValue<string>());
        Assert.Equal(size, details["sizeBytes"]!.GetValue<long>());
        Assert.Equal("webhook", details["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_second_completion_is_accepted_and_skipped_and_writes_no_second_timeline_event()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var dispatchId = await SentAsync(app, "movie-1");
        await PostAsync(app, new { @event = "completed", dispatchId, sizeBytes = 1000 });
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var firstDetectedAt = (await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!.ClientCompletedUtc;

        var second = await PostAsync(app, new { @event = "completed", dispatchId, sizeBytes = 2000 });

        Assert.Equal("Dispatch already detected; skipped duplicate webhook.", second.GetProperty("message").GetString());
        Assert.True(second.GetProperty("accepted").GetBoolean());
        var stored = (await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!;
        Assert.Equal(firstDetectedAt, stored.ClientCompletedUtc);
        Assert.Equal(1000, stored.DownloadedBytes);
        Assert.Single(await dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None), item => item.EventType == "webhook-completed");
    }

    [Fact]
    public async Task A_failure_for_a_download_that_already_has_an_import_outcome_is_accepted_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var dispatchId = await SentAsync(app, "movie-1");
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.RecordImportOutcomeAsync(dispatchId, "imported", "/films/One.mkv", null, null, CancellationToken.None);

        var reply = await PostAsync(app, new { @event = "failed", dispatchId, failureReason = "Too late to matter." });

        Assert.Equal("Dispatch already has import outcome; skipped duplicate webhook.", reply.GetProperty("message").GetString());
        var stored = (await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!;
        Assert.Equal("imported", stored.ImportStatus);
        Assert.Null(stored.ImportFailureMessage);
        Assert.DoesNotContain(await dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None), item => item.EventType == "webhook-failed");
    }
}
