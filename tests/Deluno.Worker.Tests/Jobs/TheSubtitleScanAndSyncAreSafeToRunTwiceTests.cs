using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// Coverage register (#1329), background workers inventory: the subtitle scan and the subtitle sync are safe to run twice for the
/// same work. A second scan of a slice hands over the same whole truth and finds nothing more to remove; a second sync of the
/// same subtitle forwards the same paths and records what it now finds (it moved the first time, so the second time it is already
/// in time) without rejecting anything.
/// </summary>
public sealed class TheSubtitleScanAndSyncAreSafeToRunTwiceTests
{
    private static MediaSubtitleRow Embedded(string language)
        => new(language, SubtitleSources.Embedded, false, false, null, 2, "subrip", null);

    private static MediaSubtitleRow Sidecar(string language)
        => new(language, SubtitleSources.External, false, false, $"/media/Film.{language}.srt", null, "srt", null);

    private static MediaSubtitleRow Fetched(string language, string sourceKey)
        => new(language, SubtitleSources.Fetched, false, false, $"/media/Film.{language}.srt", null, "srt",
            "gestdown", MatchRung: 0, SourceKey: sourceKey);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_scan_run_again_over_what_it_just_recorded_hands_over_the_same_whole_truth(bool videoWasProbed)
    {
        MediaSubtitleRow[] found = [Sidecar("en")];
        MediaSubtitleRow[] recordedBefore = [Embedded("de"), Embedded("fr"), Sidecar("es")];

        var first = LibrarySubtitleScanJobHandler.WholeTruth(videoWasProbed, recordedBefore, found);
        var second = LibrarySubtitleScanJobHandler.WholeTruth(videoWasProbed, first, found);
        var third = LibrarySubtitleScanJobHandler.WholeTruth(videoWasProbed, second, found);

        Assert.Equal(second, third);
        Assert.Equal(
            first.OrderBy(row => row.Language, StringComparer.Ordinal).ThenBy(row => row.Source, StringComparer.Ordinal),
            second.OrderBy(row => row.Language, StringComparer.Ordinal).ThenBy(row => row.Source, StringComparer.Ordinal));
    }

    [Fact]
    public void A_fetched_subtitle_that_went_missing_is_removed_once_and_a_second_scan_finds_nothing_left_to_remove()
    {
        var recorded = new[] { Fetched("en", "gestdown|abc"), Sidecar("fr") };
        MediaSubtitleRow[] onDisk = [Sidecar("fr")];

        var removedFirst = LibrarySubtitleScanJobHandler.Removed(recorded, onDisk).ToArray();
        var recordedAfter = LibrarySubtitleScanJobHandler.WholeTruth(videoWasProbed: false, recorded: onDisk, found: onDisk);
        var removedSecond = LibrarySubtitleScanJobHandler.Removed(recordedAfter, onDisk).ToArray();

        Assert.Equal("gestdown|abc", Assert.Single(removedFirst).SourceKey);
        Assert.Empty(removedSecond);
    }

    [Fact]
    public async Task The_same_subtitle_synced_twice_forwards_the_same_paths_both_times_and_records_each_outcome_without_rejecting()
    {
        var timingSync = new Mock<ISubtitleTimingSync>();
        timingSync
            .SetupSequence(sync => sync.SyncAsync("/media/film.mkv", "/media/film.en.srt", "en", It.IsAny<CancellationToken>(), It.IsAny<SubtitleTimingPolicy?>()))
            .ReturnsAsync(new SubtitleTimingResult(true, TimeSpan.FromSeconds(2), "moved 2.0 s", SubtitleTimingOutcome.Moved))
            .ReturnsAsync(new SubtitleTimingResult(false, TimeSpan.Zero, "already in time", SubtitleTimingOutcome.AlreadyInTime));
        var statuses = new List<string>();
        var repository = new Mock<IMediaSubtitleRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.RecordTimingStatusAsync(MediaKind.Movie, "movie-1", "en", false, false, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<MediaKind, string, string, bool, bool, string, string, CancellationToken>((_, _, _, _, _, status, _, _) => statuses.Add(status))
            .Returns(Task.CompletedTask);
        var handler = new SubtitleSyncJobHandler(timingSync.Object, repository.Object, Mock.Of<IActivityFeedRepository>(MockBehavior.Strict));
        var payload = JsonSerializer.Serialize(new SubtitleSyncJobHandler.SubtitleSyncPayload(
            "/media/film.mkv",
            "/media/film.en.srt",
            "en",
            Identity: new SubtitleSyncJobHandler.SubtitleSyncIdentity(MediaKind.Movie, "movie-1", "Film", "en", false, null, null)));

        var first = await handler.HandleAsync(TestJobs.Create("subtitle.sync", payload), CancellationToken.None);
        var second = await handler.HandleAsync(TestJobs.Create("subtitle.sync", payload), CancellationToken.None);

        Assert.Equal("film.en.srt: moved 2.0 s", first);
        Assert.Equal("film.en.srt: already in time", second);
        Assert.Equal(["moved", "already_in_time"], statuses);
        timingSync.Verify(
            sync => sync.SyncAsync("/media/film.mkv", "/media/film.en.srt", "en", It.IsAny<CancellationToken>(), It.IsAny<SubtitleTimingPolicy?>()),
            Times.Exactly(2));
    }
}
