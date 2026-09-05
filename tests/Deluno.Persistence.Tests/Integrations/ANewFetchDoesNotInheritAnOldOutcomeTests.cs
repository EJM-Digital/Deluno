using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Fetching a release again is a new attempt, not a repeat of the last one.
///
/// <para>A queue item is joined to its processor hand-off by infohash and by
/// download path. Both are identical every time the same release is fetched, so
/// a re-download matched the hand-off from last time and inherited its
/// outcome.</para>
///
/// <para>Measured on the lab rig on 2026-09-05. A brand new download of Big
/// Buck Bunny matched a <c>completed</c> hand-off from the day before and was
/// reported <c>imported</c> — while the library folder was empty and the film
/// read Missing. That status is not one the import planner accepts, so no
/// hand-off was created, no import was attempted, and nothing failed. The
/// download stopped existing as far as Deluno was concerned, which is the
/// hardest kind of defect to see: an absence.</para>
///
/// <para>The dispatch is what says a new attempt has started, so a hand-off
/// created before the current dispatch cannot be about it.</para>
/// </summary>
public sealed class ANewFetchDoesNotInheritAnOldOutcomeTests
{
    private static readonly DateTimeOffset Yesterday = DateTimeOffset.Parse("2026-09-04T05:22:42Z");
    private static readonly DateTimeOffset Today = DateTimeOffset.Parse("2026-09-05T22:19:00Z");

    [Fact]
    public void A_handoff_from_the_previous_fetch_is_not_about_this_one()
    {
        var stale = Handoff(Yesterday);

        Assert.True(DownloadClientTelemetryService.IsFromAnEarlierAttempt(
            stale, QueueItem(), [Dispatch(Today)]));
    }

    [Fact]
    public void The_handoff_for_this_fetch_is_kept()
    {
        var current = Handoff(Today.AddSeconds(30));

        Assert.False(DownloadClientTelemetryService.IsFromAnEarlierAttempt(
            current, QueueItem(), [Dispatch(Today)]));
    }

    /// <summary>
    /// With nothing to compare against, the hand-off stays. Losing the
    /// Processing stage for an item Deluno cannot place is worse than showing a
    /// stale one.
    /// </summary>
    [Fact]
    public void A_handoff_with_no_dispatch_to_compare_against_is_kept()
    {
        Assert.False(DownloadClientTelemetryService.IsFromAnEarlierAttempt(
            Handoff(Yesterday), QueueItem(), []));
    }

    /// <summary>A dispatch for a different release says nothing about this one.</summary>
    [Fact]
    public void A_dispatch_for_another_release_is_not_compared_against()
    {
        var other = Dispatch(Today) with { ReleaseName = "Something.Else.2020.1080p" };

        Assert.False(DownloadClientTelemetryService.IsFromAnEarlierAttempt(
            Handoff(Yesterday), QueueItem(), [other]));
    }

    private static ProcessorHandoffItem Handoff(DateTimeOffset createdUtc)
        => new(
            "handoff-1",
            "library-1",
            "movies",
            "client-1",
            "1800621d8a6a",
            "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO",
            @"C:\Deluno\Downloads-Complete\Movies\Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO",
            null,
            "completed",
            @"C:\Deluno\Refined\Movies\bbb.mkv",
            "import-1",
            null,
            createdUtc,
            createdUtc);

    private static DownloadQueueItem QueueItem()
        => new(
            "1800621d8a6a",
            "client-1",
            "qBittorrent",
            "qbittorrent",
            "movies",
            "Big Buck Bunny",
            "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO",
            "deluno-movies",
            DownloadQueueStatuses.Completed,
            1.0,
            0,
            0,
            100,
            100,
            0,
            "Lab Torznab",
            null,
            Today);

    private static DownloadDispatchItem Dispatch(DateTimeOffset createdUtc)
        => new(
            Id: "dispatch-1",
            LibraryId: "library-1",
            MediaType: "movies",
            EntityType: "movie",
            EntityId: "movie-1",
            ReleaseName: "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO",
            IndexerName: "Lab Torznab",
            DownloadClientId: "client-1",
            DownloadClientName: "qBittorrent",
            Status: "sent",
            NotesJson: null,
            CreatedUtc: createdUtc,
            GrabStatus: "succeeded",
            GrabAttemptedUtc: createdUtc,
            GrabResponseCode: 200,
            GrabMessage: null,
            GrabFailureCode: null,
            GrabResponseJson: null,
            DetectedUtc: null,
            TorrentHashOrItemId: "1800621d8a6a",
            DownloadedBytes: null,
            ImportStatus: null,
            ImportDetectedUtc: null,
            ImportCompletedUtc: null,
            ImportedFilePath: null,
            ImportFailureCode: null,
            ImportFailureMessage: null,
            CircuitOpenUntilUtc: null,
            NextRetryEligibleUtc: null,
            AttemptCount: 1);
}
