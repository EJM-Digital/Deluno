using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>#922: what a timing job writes on the subtitle's row for each outcome it can end with.</summary>
public sealed class SubtitleSyncJobHandlerRecordedStatusTests
{
    [Theory]
    [InlineData(SubtitleTimingOutcome.Moved, "moved")]
    [InlineData(SubtitleTimingOutcome.AlreadyInTime, "already_in_time")]
    [InlineData(SubtitleTimingOutcome.Inconclusive, "not_checked")]
    public async Task The_outcome_is_written_on_the_row_the_job_was_told_about(SubtitleTimingOutcome outcome, string stored)
    {
        var sync = new Mock<ISubtitleTimingSync>();
        sync.Setup(s => s.SyncAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<SubtitleTimingPolicy?>()))
            .ReturnsAsync(new SubtitleTimingResult(outcome == SubtitleTimingOutcome.Moved, TimeSpan.Zero, "the reason", outcome));
        var subtitles = new Mock<IMediaSubtitleRepository>();
        var identity = new SubtitleSyncJobHandler.SubtitleSyncIdentity(MediaKind.Movie, "movie-1", "Alien", "en", true, "provider", "source");
        var payload = JsonSerializer.Serialize(new SubtitleSyncJobHandler.SubtitleSyncPayload("/media/a.mkv", "/media/a.en.srt", "en", null, identity));

        var message = await new SubtitleSyncJobHandler(sync.Object, subtitles.Object, new Mock<IActivityFeedRepository>().Object)
            .HandleAsync(TestJobs.Create("subtitle.sync", payload), CancellationToken.None);

        Assert.Equal("a.en.srt: the reason", message);
        subtitles.Verify(r => r.RecordTimingStatusAsync(MediaKind.Movie, "movie-1", "en", false, true, stored, "the reason", It.IsAny<CancellationToken>()), Times.Once);
    }
}
