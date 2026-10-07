using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Live on the rig (#606, #615): Deluge's status message for a healthy
/// torrent is "OK". Deluno passed it straight through as the item's
/// ErrorMessage, so the generic, client-agnostic health evaluator - which
/// treats any non-blank ErrorMessage as a critical finding regardless of
/// status - flagged every Deluge download as "Deluge (live) queue failed:
/// OK", whatever the torrent's real state.
/// </summary>
public sealed class ADelugeHealthyStatusIsNotAnErrorTests
{
    [Fact]
    public async Task A_healthy_torrents_ok_message_is_not_surfaced_as_an_error()
    {
        var item = await Snapshot(message: "OK");

        Assert.Null(item.ErrorMessage);
    }

    [Fact]
    public async Task A_healthy_torrents_ok_message_produces_no_client_stalled_finding()
    {
        var item = await Snapshot(message: "OK");

        var findings = Evaluate(item);

        Assert.DoesNotContain(findings, finding => finding.Kind == "client-stalled");
    }

    [Fact]
    public async Task A_finished_torrent_with_a_real_deluge_error_is_a_machine_problem_never_a_stalled_download()
    {
        var item = await Snapshot(message: "Torrent Error");

        Assert.Equal("Torrent Error", item.ErrorMessage);
        Assert.True(item.IsSetupProblem);
        // Finished (100%), so nothing is stalled (#1154): the error is handled
        // as the missing-file or disk problem it is and can never be struck.
        var findings = new DownloadHealthEvaluator().Evaluate(
            new RecoveryQueueSnapshot(
                item.Status, item.ErrorMessage, item.SourcePath, item.SpeedMbps, item.AddedUtc, item.EtaSeconds,
                item.ReleaseName, item.Peers, item.IsSetupProblem, item.Progress),
            capturedUtc: item.AddedUtc.AddHours(3));
        Assert.DoesNotContain(findings, finding => finding.Kind == "client-stalled");
        var problem = Assert.Single(findings, finding => finding.Kind == DownloadHealthStrikes.ClientSetupProblem);
        Assert.Equal("Torrent Error", problem.Evidence);
        Assert.False(DownloadHealthStrikes.Counts(problem.Kind));
    }

    private static IReadOnlyList<RecoveryHealthFinding> Evaluate(DownloadQueueItem item)
        => new DownloadHealthEvaluator().Evaluate(
            new RecoveryQueueSnapshot(
                item.Status,
                item.ErrorMessage,
                item.SourcePath,
                item.SpeedMbps,
                item.AddedUtc,
                item.EtaSeconds,
                item.ReleaseName),
            capturedUtc: item.AddedUtc);

    private static async Task<DownloadQueueItem> Snapshot(string message)
    {
        var savePath = @"C:\Deluno\Downloads-Complete\Deluge";
        var torrent = "{\"result\":{\"torrents\":{\"abc123\":{\"name\":\"House.on.Haunted.Hill.1959.1080p.BluRay.x264-AMIABLE\",\"state\":\"Seeding\""
            + ",\"progress\":100.0,\"download_payload_rate\":0,\"upload_payload_rate\":0,\"eta\":0,\"total_size\":5000000000,\"total_done\":5000000000,\"num_peers\":0,"
            + "\"time_added\":1727000000,\"label\":\"movies\",\"message\":" + Json(message) + ",\"save_path\":" + Json(savePath)
            + ",\"move_completed\":true,\"move_completed_path\":" + Json(savePath) + "}}},\"error\":null,\"id\":2}";
        var handler = new DelugeHandler(torrent);
        var snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static DownloadClientItem Client()
        => new("deluge-1", "Deluge", "deluge", "localhost", 8112, null, null, "http://localhost:8112/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelugeHandler(string updateUi) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var answer = body.Contains("web.update_ui", StringComparison.Ordinal) ? updateUi : """{"result":true,"error":null,"id":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
