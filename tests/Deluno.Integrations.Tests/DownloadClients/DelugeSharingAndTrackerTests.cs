using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Recovery.Policies;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #661 — Deluge never requested <c>ratio</c>/<c>seeding_time</c>, so
/// <see cref="DownloadQueueItem.Ratio"/>/<see cref="DownloadQueueItem.SeedingMinutes"/>
/// stayed null forever and a sharing rule could never be judged met. It also
/// never requested <c>tracker_status</c>, only the generic <c>message</c>
/// field which is blanked whenever it reads "OK" - so a torrent a tracker had
/// banned or unregistered still looked healthy. A person-paused torrent
/// ("Paused" state) also read identically to one merely queued.
/// </summary>
public sealed class DelugeSharingAndTrackerTests
{
    [Fact]
    public async Task Ratio_and_seeding_minutes_are_read_from_the_update_ui_response()
    {
        var item = await Snapshot(state: "Seeding", ratio: 1.75, seedingTime: 5400, trackerStatus: "Announce OK");

        Assert.Equal(1.75, item.Ratio);
        Assert.Equal(90, item.SeedingMinutes);
    }

    [Fact]
    public async Task Ratio_and_seeding_minutes_are_null_when_deluge_never_reports_them()
    {
        var item = await Snapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Announce sent");

        Assert.Null(item.Ratio);
        Assert.Null(item.SeedingMinutes);
    }

    [Fact]
    public async Task An_unregistered_tracker_surfaces_as_a_plain_words_error()
    {
        var item = await Snapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Error: unregistered torrent");

        Assert.Equal("Error: unregistered torrent", item.ErrorMessage);
    }

    [Fact]
    public async Task A_tracker_error_reaches_the_health_evaluator_as_a_client_stalled_finding()
    {
        var item = await Snapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Error: torrent banned");

        var findings = Evaluate(item);
        var finding = Assert.Single(findings, finding => finding.Kind == "client-stalled");
        Assert.Equal("Error: torrent banned", finding.Evidence);
    }

    [Fact]
    public async Task A_healthy_tracker_status_is_not_surfaced_as_an_error()
    {
        var item = await Snapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Announce sent");

        Assert.Null(item.ErrorMessage);
    }

    /// <summary>#880/#881: a tracker banning or unregistering the release is the release's problem, not this machine's.</summary>
    [Fact]
    public async Task A_tracker_error_is_not_a_setup_problem()
    {
        var item = await Snapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Error: torrent banned");

        Assert.False(item.IsSetupProblem);
    }

    /// <summary>#880/#881: Deluge's own message field, once it says anything besides "OK", is the daemon speaking for itself — a local problem.</summary>
    [Fact]
    public async Task Deluges_own_message_is_a_setup_problem()
    {
        var item = await Snapshot(state: "Error", ratio: null, seedingTime: null, trackerStatus: "Announce sent", message: "queue failed: Errno 28 No space left on device");

        Assert.True(item.IsSetupProblem);
        Assert.Equal("queue failed: Errno 28 No space left on device", item.ErrorMessage);
    }

    [Fact]
    public async Task Paused_by_a_person_is_distinct_from_queued()
    {
        var item = await Snapshot(state: "Paused", ratio: null, seedingTime: null, trackerStatus: "Announce sent");

        Assert.Equal(DownloadQueueStatuses.Paused, item.Status);
    }

    [Fact]
    public async Task Queued_stays_queued()
    {
        var item = await Snapshot(state: "Queued", ratio: null, seedingTime: null, trackerStatus: string.Empty);

        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
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

    private static async Task<DownloadQueueItem> Snapshot(string state, double? ratio, double? seedingTime, string trackerStatus, string message = "OK")
    {
        var savePath = @"C:\Deluno\Downloads\Deluge";
        var torrent = "{\"result\":{\"torrents\":{\"abc123\":{\"name\":\"Sherlock.Jr.1924.1080p.BluRay\",\"state\":" + Json(state)
            + ",\"progress\":40.0,\"download_payload_rate\":0,\"upload_payload_rate\":0,\"eta\":0,\"total_size\":5000000000,\"total_done\":2000000000,\"num_peers\":2,"
            + "\"time_added\":1727000000,\"label\":\"movies\",\"message\":" + Json(message) + ",\"save_path\":" + Json(savePath)
            + (ratio is null ? string.Empty : $",\"ratio\":{ratio.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            + (seedingTime is null ? string.Empty : $",\"seeding_time\":{seedingTime.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            + ",\"tracker_status\":" + Json(trackerStatus)
            + "}}},\"error\":null,\"id\":2}";
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
