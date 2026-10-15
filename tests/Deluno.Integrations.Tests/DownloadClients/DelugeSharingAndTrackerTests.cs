using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;
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
        var item = await RecordedSnapshot(state: "Seeding", ratio: 1.75, seedingTime: 5400, trackerStatus: "Announce OK");

        Assert.Equal(1.75, item.Ratio);
        Assert.Equal(90, item.SeedingMinutes);
    }

    [Fact]
    public async Task Ratio_and_seeding_minutes_are_null_when_deluge_never_reports_them()
    {
        var item = await RecordedSnapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Announce sent");

        Assert.Null(item.Ratio);
        Assert.Null(item.SeedingMinutes);
    }

    [Fact]
    public async Task An_unregistered_tracker_surfaces_as_a_plain_words_error()
    {
        var item = await RecordedSnapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Error: unregistered torrent");

        Assert.Equal("Error: unregistered torrent", item.ErrorMessage);
    }

    [Fact]
    public async Task A_tracker_error_reaches_the_health_evaluator_as_a_client_stalled_finding()
    {
        var item = await RecordedSnapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Error: torrent banned");

        var findings = Evaluate(item);
        // The default No progress rule owns a stall with nothing arriving (#1194); either way the client's words are kept.
        var finding = Assert.Single(findings, finding => finding.Kind is "client-stalled" or "no-progress");
        Assert.Equal("Error: torrent banned", finding.Evidence);
    }

    [Fact]
    public async Task A_healthy_tracker_status_is_not_surfaced_as_an_error()
    {
        var item = await RecordedSnapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Announce sent");

        Assert.Null(item.ErrorMessage);
    }

    /// <summary>#880/#881: a tracker banning or unregistering the release is the release's problem, not this machine's.</summary>
    [Fact]
    public async Task A_tracker_error_is_not_a_setup_problem()
    {
        var item = await RecordedSnapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Error: torrent banned");

        Assert.False(item.IsSetupProblem);
    }

    /// <summary>#880/#881: Deluge's own message field, once it says anything besides "OK", is the daemon speaking for itself — a local problem.</summary>
    [Fact]
    public async Task Deluges_own_message_is_a_setup_problem()
    {
        var item = await RecordedSnapshot(state: "Error", ratio: null, seedingTime: null, trackerStatus: "Announce sent", message: "queue failed: Errno 28 No space left on device");

        Assert.True(item.IsSetupProblem);
        Assert.Equal("queue failed: Errno 28 No space left on device", item.ErrorMessage);
    }

    [Fact]
    public async Task Paused_by_a_person_is_distinct_from_queued()
    {
        var item = await RecordedSnapshot(state: "Paused", ratio: null, seedingTime: null, trackerStatus: "Announce sent");

        Assert.Equal(DownloadQueueStatuses.Paused, item.Status);
    }

    [Fact]
    public async Task Queued_stays_queued()
    {
        var item = await RecordedSnapshot(state: "Queued", ratio: null, seedingTime: null, trackerStatus: string.Empty);

        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
    }

    /// <summary>#878, cleanup sheet C5: the tracker explicitly saying the torrent is unregistered normalizes to its own signal, distinct from any other tracker error.</summary>
    [Fact]
    public async Task An_unregistered_tracker_normalizes_to_the_tracker_unregistered_status()
    {
        var item = await RecordedSnapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Error: unregistered torrent");

        Assert.Equal(DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
    }

    /// <summary>A tracker complaint that is not the "gone for good" wording (banned, offline) is not read as unregistered.</summary>
    [Fact]
    public async Task A_different_tracker_error_is_not_read_as_unregistered()
    {
        var item = await RecordedSnapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Error: torrent banned");

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    [Fact]
    public async Task A_healthy_tracker_status_normalizes_to_ok()
    {
        var item = await RecordedSnapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Announce sent");

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    [Fact]
    public async Task No_tracker_status_at_all_normalizes_to_unknown()
    {
        var item = await RecordedSnapshot(state: "Queued", ratio: null, seedingTime: null, trackerStatus: string.Empty);

        Assert.Equal(DownloadTrackerStatuses.Unknown, item.TrackerStatus);
    }

    /// <summary>#878: an unregistered tracker acts immediately - James decided this does not wait for strikes to build up - so the finding carries its own strike limit of one.</summary>
    [Fact]
    public async Task An_unregistered_tracker_reaches_the_evaluator_as_an_immediate_critical_finding()
    {
        var item = await RecordedSnapshot(state: "Downloading", ratio: null, seedingTime: null, trackerStatus: "Error: unregistered torrent");

        var findings = Evaluate(item);
        var finding = Assert.Single(findings, finding => finding.Kind == DownloadHealthStrikes.TrackerUnregistered);
        Assert.Equal("critical", finding.Severity);
        Assert.Equal(1, finding.RuleStrikeLimit);
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
                item.ReleaseName,
                TrackerStatus: item.TrackerStatus),
            capturedUtc: item.AddedUtc);

    private static async Task<DownloadQueueItem> RecordedSnapshot(string state, double? ratio, double? seedingTime, string trackerStatus, string message = "OK")
    {
        // Deluge 2.2.0's recorded list of a torrent that is downloading, with the state, sharing figures, tracker status and message under test.
        // A figure Deluge does not report is taken out of the reply, as a null edit does.
        var handler = RecordedDelugeRig.Handler(RecordedDelugeRig.Downloading(
            ("state", state), ("label", "movies"), ("progress", 40.0), ("download_payload_rate", 0), ("upload_payload_rate", 0), ("eta", 0), ("num_peers", 2), ("message", message), ("ratio", ratio), ("seeding_time", seedingTime), ("tracker_status", trackerStatus)));
        var snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("deluge-1", "Deluge", "deluge", "localhost", 8112, null, null, "http://localhost:8112/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
