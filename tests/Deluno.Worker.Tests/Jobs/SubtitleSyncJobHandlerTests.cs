using System.Text.Json;
using Deluno.Contracts;
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

        var handler = new SubtitleSyncJobHandler(timingSync.Object);
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

        var handler = new SubtitleSyncJobHandler(timingSync.Object);
        var message = await handler.HandleAsync(TestJobs.Create("subtitle.sync", payload), CancellationToken.None);

        Assert.Equal("film.en.srt: default", message);
        timingSync.VerifyAll();
    }
}
