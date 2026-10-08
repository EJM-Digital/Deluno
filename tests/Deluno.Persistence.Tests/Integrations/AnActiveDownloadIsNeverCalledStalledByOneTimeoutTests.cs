using Deluno.Platform.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Recovery.Policies;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Rig, 8 Oct: an active Deluge download running at 8 MB/s showed
/// "Error: timed out" and a client-stalled finding, and the bell said
/// "Talking to Deluge ... timed out". One timed-out poll, or a tracker that
/// timed out beside a moving download, is not a stalled download or a client
/// error.
/// </summary>
public sealed class AnActiveDownloadIsNeverCalledStalledByOneTimeoutTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");

    [Fact]
    public void A_tracker_timeout_beside_a_download_moving_at_8_MBps_is_not_a_stalled_finding()
    {
        var item = Snapshot("downloading", "Error: timed out", speedMbps: 8);

        var findings = new DownloadHealthEvaluator().Evaluate(item, Now);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.ClientStalled);
    }

    [Fact]
    public void The_same_error_with_nothing_arriving_is_still_a_finding()
    {
        var item = Snapshot("downloading", "Error: timed out", speedMbps: 0);

        var findings = new DownloadHealthEvaluator().Evaluate(item, Now);

        Assert.Contains(findings, finding => finding.Kind == DownloadHealthStrikes.ClientStalled);
    }

    [Fact]
    public void One_timed_out_poll_keeps_the_last_good_reading_and_only_a_repeat_shows_the_error()
    {
        var clientId = Guid.NewGuid().ToString("n");
        var good = Reading(clientId, "healthy", failed: false);

        Assert.Same(good, DownloadClientTelemetryService.HoldLastGoodReading(clientId, good));

        var timedOut = Reading(clientId, "degraded", failed: true);
        // First failure: the last good reading is still what the screen shows.
        Assert.Same(good, DownloadClientTelemetryService.HoldLastGoodReading(clientId, timedOut));
        // Second failure in a row: now it is a client error.
        Assert.Equal("degraded", DownloadClientTelemetryService.HoldLastGoodReading(clientId, Reading(clientId, "degraded", failed: true))!.HealthStatus);

        // A good poll in between starts the count again.
        var recovered = Reading(clientId, "healthy", failed: false);
        Assert.Same(recovered, DownloadClientTelemetryService.HoldLastGoodReading(clientId, recovered));
        Assert.Same(recovered, DownloadClientTelemetryService.HoldLastGoodReading(clientId, Reading(clientId, "degraded", failed: true)));
    }

    [Fact]
    public void A_client_that_never_answered_fails_at_once()
    {
        var clientId = Guid.NewGuid().ToString("n");
        var timedOut = Reading(clientId, "degraded", failed: true);

        Assert.Same(timedOut, DownloadClientTelemetryService.HoldLastGoodReading(clientId, timedOut));
    }

    // Golden path, 9 Oct: Deluge hung on a magnet. Download Clients (the last login test) said Healthy while the
    // Dashboard (the live poll) said "not answering". The live verdict is the one that is written down.
    [Fact]
    public void A_client_whose_polls_fail_is_stored_as_not_answering_even_though_its_login_test_passed()
    {
        var verdict = DownloadClientTelemetryService.LiveHealthToStore("healthy", Reading("c1", "degraded", failed: true));

        Assert.NotNull(verdict);
        Assert.Equal("unreachable", verdict!.Value.Status);
        Assert.Contains("timed out", verdict.Value.Message);
        Assert.NotNull(verdict.Value.Failure);
    }

    [Fact]
    public void A_client_already_stored_as_not_answering_is_not_written_again_on_every_poll()
    {
        Assert.Null(DownloadClientTelemetryService.LiveHealthToStore("unreachable", Reading("c1", "degraded", failed: true)));
    }

    [Fact]
    public void A_clean_poll_clears_a_not_answering_mark_but_leaves_a_degraded_that_a_test_stored()
    {
        var cleared = DownloadClientTelemetryService.LiveHealthToStore("unreachable", Reading("c1", "healthy", failed: false));
        Assert.Equal("healthy", cleared!.Value.Status);

        // A Usenet client with no provider yet is "degraded" from its test; a clean poll does not hide that.
        Assert.Null(DownloadClientTelemetryService.LiveHealthToStore("degraded", Reading("c1", "healthy", failed: false)));
        Assert.Null(DownloadClientTelemetryService.LiveHealthToStore("healthy", Reading("c1", "healthy", failed: false)));
    }

    private static RecoveryQueueSnapshot Snapshot(string status, string error, double speedMbps)
        => new(status, error, "/downloads/Example.Show.S01E01", speedMbps, Now.AddHours(-1), 600, "Example.Show.S01E01.1080p-GRP", Peers: 12);

    private static DownloadClientTelemetrySnapshot Reading(string clientId, string health, bool failed)
        => new(clientId, "Deluge", "deluge", null, health, failed ? "timed out" : "Connected.",
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0), [], [], Now)
        {
            LastFailure = failed
                ? new IntegrationFailure("download-client", clientId, "Deluge", "telemetry", IntegrationFailureKind.Timeout, IntegrationRetryState.RetryScheduled, "timed out")
                : null
        };
}
