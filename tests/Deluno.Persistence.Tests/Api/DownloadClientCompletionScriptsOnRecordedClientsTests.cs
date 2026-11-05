using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What each client's own completion script posts to POST /api/download-clients/{id}/webhook (coverage register, engines):
/// the SABnzbd and NZBGet post-processing scripts, the Deluge Execute plugin and the Transmission script-torrent-done. Each one
/// names the download only by the id its client gave it when Deluno added it, which here is the id from that client's recorded
/// real "add" reply (tests/fixtures/recorded), with no dispatch id: the callback must find the download by that id alone.
/// </summary>
public sealed class DownloadClientCompletionScriptsOnRecordedClientsTests
{
    private sealed record Added(string ClientName, string ExternalId, string ReleaseName);

    private static Added SabnzbdAdded()
    {
        var reply = JsonNode.Parse(RecordedReplies.Sabnzbd("finished-folder").Exchanges[0].Reply.Body!)!;
        return new("SABnzbd", reply["nzo_ids"]![0]!.GetValue<string>(), "Recorded.Folder");
    }

    private static Added NzbgetAdded()
    {
        var reply = JsonNode.Parse(RecordedReplies.Nzbget("finished-folder").Exchanges[0].Reply.Body!)!;
        return new("NZBGet", reply["result"]!.ToJsonString(), "Recorded.Folder");
    }

    private static Added DelugeAdded()
    {
        var reply = JsonNode.Parse(RecordedReplies.Deluge("finished-folder").Exchanges[0].Reply.Body!)!;
        return new("Deluge", reply["result"]!.GetValue<string>(), "Recorded.deluge.Folder");
    }

    private static Added TransmissionAdded()
    {
        var reply = JsonNode.Parse(RecordedReplies.Transmission("finished-folder").Exchanges[0].Reply.Body!)!;
        var added = reply["arguments"]!["torrent-added"]!;
        return new("Transmission", added["hashString"]!.GetValue<string>(), added["name"]!.GetValue<string>());
    }

    private static async Task<string> SentAsync(ApplicationTestHost app, Added added, string? libraryId = null)
    {
        libraryId ??= await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var id = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", "movie-1", added.ReleaseName, "Fixture", "client-1", added.ClientName, "sent", null, cancellationToken: CancellationToken.None);
        await dispatches.RecordGrabAsync(id, "succeeded", 200, null, null, null, CancellationToken.None, externalId: added.ExternalId);
        return id;
    }

    private static async Task<JsonElement> PostAsync(ApplicationTestHost app, object body)
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients/client-1/webhook", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<DownloadDispatchItem> StoredAsync(ApplicationTestHost app, string dispatchId)
        => (await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchAsync(dispatchId, CancellationToken.None))!;

    [Fact]
    public async Task The_SABnzbd_post_processing_script_finishes_the_download_by_the_id_SABnzbd_gave_it_and_a_failure_is_recorded_in_its_words()
    {
        var added = SabnzbdAdded();
        await using var app = await ApplicationTestHost.StartAsync();
        var finished = await SentAsync(app, added);

        var reply = await PostAsync(app, new { @event = "nzb_completed", hash = added.ExternalId, name = added.ReleaseName, savePath = @"C:\Downloads\Completed\Recorded.Folder" });

        Assert.Equal("Download completion recorded.", reply.GetProperty("message").GetString());
        Assert.Equal(finished, reply.GetProperty("dispatchId").GetString());
        Assert.NotNull((await StoredAsync(app, finished)).ClientCompletedUtc);

        var other = await SentAsync(app, added with { ExternalId = "SABnzbd_nzo_other" }, (await StoredAsync(app, finished)).LibraryId);
        var failed = await PostAsync(app, new { @event = "nzb_failed", hash = "SABnzbd_nzo_other", name = added.ReleaseName, failureReason = "Aborted, cannot be completed" });
        Assert.Equal("Download failure recorded.", failed.GetProperty("message").GetString());
        Assert.Equal("Aborted, cannot be completed", (await StoredAsync(app, other)).ImportFailureMessage);
    }

    [Fact]
    public async Task The_NZBGet_post_processing_script_finishes_the_download_by_the_id_NZBGet_gave_it_and_a_failure_is_recorded_in_its_words()
    {
        var added = NzbgetAdded();
        await using var app = await ApplicationTestHost.StartAsync();
        var finished = await SentAsync(app, added);

        var reply = await PostAsync(app, new { @event = "nzb_completed", hash = added.ExternalId, name = added.ReleaseName, savePath = @"C:\Downloads\Completed\Recorded.Folder" });

        Assert.Equal("Download completion recorded.", reply.GetProperty("message").GetString());
        Assert.Equal(finished, reply.GetProperty("dispatchId").GetString());
        Assert.NotNull((await StoredAsync(app, finished)).ClientCompletedUtc);

        var other = await SentAsync(app, added with { ExternalId = "9999" }, (await StoredAsync(app, finished)).LibraryId);
        var failed = await PostAsync(app, new { @event = "nzb_failed", hash = "9999", name = added.ReleaseName, failureReason = "Par-check failed." });
        Assert.Equal("Download failure recorded.", failed.GetProperty("message").GetString());
        Assert.Equal("Par-check failed.", (await StoredAsync(app, other)).ImportFailureMessage);
    }

    [Theory]
    [InlineData("deluge")]
    [InlineData("transmission")]
    public async Task The_torrent_done_script_of_Deluge_and_Transmission_finishes_the_download_by_the_hash_the_client_gave_it(string client)
    {
        var added = client == "deluge" ? DelugeAdded() : TransmissionAdded();
        await using var app = await ApplicationTestHost.StartAsync();
        var finished = await SentAsync(app, added);

        var reply = await PostAsync(app, new { @event = "torrent_completed", hash = added.ExternalId, name = added.ReleaseName, savePath = @"C:\golden\recorded", sizeBytes = 576682030L });

        Assert.Equal("Download completion recorded.", reply.GetProperty("message").GetString());
        Assert.Equal(finished, reply.GetProperty("dispatchId").GetString());
        Assert.NotNull((await StoredAsync(app, finished)).ClientCompletedUtc);
    }

    [Fact]
    public async Task A_script_naming_a_hash_Deluno_never_recorded_for_the_client_is_refused()
    {
        var added = TransmissionAdded();
        await using var app = await ApplicationTestHost.StartAsync();
        var finished = await SentAsync(app, added);

        var response = await app.Client.PostAsJsonAsync("/api/download-clients/client-1/webhook", new { @event = "torrent_completed", hash = "0000000000000000000000000000000000000000", name = added.ReleaseName });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await StoredAsync(app, finished)).ClientCompletedUtc);
    }
}
