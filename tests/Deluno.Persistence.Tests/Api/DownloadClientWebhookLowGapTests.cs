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
/// Low-severity gap rows (#1329) of the download-client callback (POST /api/download-clients/{id}/webhook). The download it
/// names is qBittorrent 5.2.1's own recorded torrent (tests/fixtures/recorded/qbittorrent).
/// </summary>
public sealed class DownloadClientWebhookLowGapTests
{
    private static async Task<string> SentAsync(ApplicationTestHost app)
    {
        var torrent = JsonNode.Parse(RecordedReplies.Qbittorrent("finished-folder").Body("torrents/info"))![0]!;
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var id = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", "movie-1", torrent["name"]!.GetValue<string>(), "Fixture", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await dispatches.RecordGrabAsync(id, "succeeded", 200, null, null, null, CancellationToken.None, externalId: torrent["hash"]!.GetValue<string>());
        return id;
    }

    [Theory]
    [InlineData("{\"dispatchId\":\"DISPATCH\"}")]
    [InlineData("{\"event\":\"\",\"dispatchId\":\"DISPATCH\"}")]
    [InlineData("{\"event\":\"   \",\"dispatchId\":\"DISPATCH\"}")]
    [InlineData("{\"event\":null,\"dispatchId\":\"DISPATCH\"}")]
    public async Task A_callback_with_no_event_or_a_blank_one_is_acknowledged_and_changes_nothing(string body)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var dispatchId = await SentAsync(app);

        var response = await app.Client.PostAsync(
            "/api/download-clients/client-1/webhook",
            new StringContent(body.Replace("DISPATCH", dispatchId), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(reply.GetProperty("accepted").GetBoolean());
        Assert.Contains("acknowledged, no action taken", reply.GetProperty("message").GetString(), StringComparison.Ordinal);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var stored = (await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None))!;
        Assert.Null(stored.ClientCompletedUtc);
        Assert.NotEqual("failed", stored.ImportStatus);
        Assert.DoesNotContain(await dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None), item => item.EventType.StartsWith("webhook-", StringComparison.Ordinal));
    }
}
