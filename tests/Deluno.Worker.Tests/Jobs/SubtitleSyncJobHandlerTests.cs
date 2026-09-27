using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

public sealed class SubtitleSyncJobHandlerTests
{
    [Fact]
    public async Task A_queued_timing_policy_is_forwarded_to_the_sync_service()
    {
        var timingSync = new Mock<ISubtitleTimingSync>(MockBehavior.Strict);
        timingSync
            .Setup(sync => sync.SyncAsync(
                @"D:\Media\film.mkv",
                @"D:\Media\film.en.srt",
                "ja",
                It.IsAny<CancellationToken>(),
                It.Is<SubtitleTimingPolicy?>(policy =>
                    policy != null
                    && policy.Enabled == false
                    && policy.SyncOnlyBelow == SubtitleSyncThreshold.SameSource
                    && policy.MaxOffsetSeconds == 14
                    && policy.RequiredPeakSigma == 4.2
                    && policy.ExcludedProviders != null
                    && policy.ExcludedProviders.Contains("opensubtitles"))))
            .ReturnsAsync(new SubtitleTimingResult(false, TimeSpan.Zero, "disabled"));

        var policy = new SubtitleTimingPolicy(
            Enabled: false,
            SyncOnlyBelow: SubtitleSyncThreshold.SameSource,
            MaxOffsetSeconds: 14,
            RequiredPeakSigma: 4.2,
            ExcludedProviders: ["opensubtitles"]);
        var payload = JsonSerializer.Serialize(new SubtitleSyncJobHandler.SubtitleSyncPayload(
            @"D:\Media\film.mkv",
            @"D:\Media\film.en.srt",
            "ja",
            policy));

        var handler = new SubtitleSyncJobHandler(
            timingSync.Object,
            Mock.Of<IMediaSubtitleRepository>(MockBehavior.Strict),
            Mock.Of<IActivityFeedRepository>(MockBehavior.Strict));
        var message = await handler.HandleAsync(TestJobs.Create("subtitle.sync", payload), CancellationToken.None);

        Assert.Equal("film.en.srt: disabled", message);
        timingSync.VerifyAll();
    }

    [Fact]
    public async Task An_old_timing_job_without_a_policy_keeps_the_default_path()
    {
        var timingSync = new Mock<ISubtitleTimingSync>(MockBehavior.Strict);
        timingSync
            .Setup(sync => sync.SyncAsync(
                @"D:\Media\film.mkv",
                @"D:\Media\film.en.srt",
                null,
                It.IsAny<CancellationToken>(),
                null))
            .ReturnsAsync(new SubtitleTimingResult(false, TimeSpan.Zero, "default"));

        var payload = JsonSerializer.Serialize(new SubtitleSyncJobHandler.SubtitleSyncPayload(
            @"D:\Media\film.mkv",
            @"D:\Media\film.en.srt",
            null));

        var handler = new SubtitleSyncJobHandler(
            timingSync.Object,
            Mock.Of<IMediaSubtitleRepository>(MockBehavior.Strict),
            Mock.Of<IActivityFeedRepository>(MockBehavior.Strict));
        var message = await handler.HandleAsync(TestJobs.Create("subtitle.sync", payload), CancellationToken.None);

        Assert.Equal("film.en.srt: default", message);
        timingSync.VerifyAll();
    }

    /// <summary>
    /// #897: a confident mismatch is not just left where it is - it is
    /// blocklisted, dropped from the held subtitles and named in Activity, so
    /// the next search tries something else instead of finding the same wrong
    /// file waiting for it again.
    /// </summary>
    [Fact]
    public async Task A_mismatched_result_rejects_the_subtitle()
    {
        var timingSync = new Mock<ISubtitleTimingSync>();
        timingSync
            .Setup(sync => sync.SyncAsync(
                @"D:\Media\film.mkv", @"D:\Media\film.en.srt", null, It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new SubtitleTimingResult(
                false, TimeSpan.Zero, "Never lines up.", SubtitleTimingOutcome.Mismatched));

        var subtitles = new Mock<IMediaSubtitleRepository>(MockBehavior.Strict);
        subtitles
            .Setup(repo => repo.BlockSubtitleAsync(
                MediaKind.Movie, "movie-1", "en", "gestdown|abc", "gestdown", null, "Never lines up.",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        subtitles
            .Setup(repo => repo.RemoveSubtitleAsync(
                MediaKind.Movie, "movie-1", "en", false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var activity = new Mock<IActivityFeedRepository>(MockBehavior.Strict);
        activity
            .Setup(repo => repo.RecordActivityAsync(
                "subtitle.rejected",
                It.Is<string>(message => message.Contains("Dune") && message.Contains("Never lines up.")),
                null, null, "movie", "movie-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityEventItem(
                "activity-1", "subtitle.rejected", "Rejected.", null, null, "movie", "movie-1", DateTimeOffset.UtcNow));

        var payload = JsonSerializer.Serialize(new SubtitleSyncJobHandler.SubtitleSyncPayload(
            @"D:\Media\film.mkv",
            @"D:\Media\film.en.srt",
            null,
            null,
            new SubtitleSyncJobHandler.SubtitleSyncIdentity(
                MediaKind.Movie, "movie-1", "Dune", "en", HearingImpaired: false, "gestdown", "gestdown|abc")));

        var handler = new SubtitleSyncJobHandler(timingSync.Object, subtitles.Object, activity.Object);
        var message = await handler.HandleAsync(TestJobs.Create("subtitle.sync", payload), CancellationToken.None);

        Assert.Equal("film.en.srt: Never lines up.", message);
        subtitles.VerifyAll();
        activity.VerifyAll();
    }

    /// <summary>
    /// #922: an inconclusive result keeps the subtitle exactly as it was, and
    /// is remembered as "not checked" rather than looking like it was never
    /// asked about at all.
    /// </summary>
    [Fact]
    public async Task An_inconclusive_result_is_marked_not_checked()
    {
        var timingSync = new Mock<ISubtitleTimingSync>();
        timingSync
            .Setup(sync => sync.SyncAsync(
                @"D:\Media\film.mkv", @"D:\Media\film.en.srt", null, It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new SubtitleTimingResult(
                false, TimeSpan.Zero, "No clear peak.", SubtitleTimingOutcome.Inconclusive));

        var subtitles = new Mock<IMediaSubtitleRepository>(MockBehavior.Strict);
        subtitles
            .Setup(repo => repo.RecordTimingStatusAsync(
                MediaKind.Movie, "movie-1", "en", false, false, "not_checked", "No clear peak.",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var payload = JsonSerializer.Serialize(new SubtitleSyncJobHandler.SubtitleSyncPayload(
            @"D:\Media\film.mkv",
            @"D:\Media\film.en.srt",
            null,
            null,
            new SubtitleSyncJobHandler.SubtitleSyncIdentity(
                MediaKind.Movie, "movie-1", "Dune", "en", HearingImpaired: false, "gestdown", "gestdown|abc")));

        var handler = new SubtitleSyncJobHandler(
            timingSync.Object, subtitles.Object, Mock.Of<IActivityFeedRepository>(MockBehavior.Strict));
        var message = await handler.HandleAsync(TestJobs.Create("subtitle.sync", payload), CancellationToken.None);

        Assert.Equal("film.en.srt: No clear peak.", message);
        subtitles.VerifyAll();
    }
}
