using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>A timing job that does not say which subtitle it is for does nothing and says so.</summary>
public sealed class SubtitleSyncJobHandlerUnreadableJobTests
{
    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"videoPath":"/media/film.mkv"}""")]
    [InlineData("""{"subtitlePath":"/media/film.en.srt"}""")]
    [InlineData("""{"videoPath":"  ","subtitlePath":"/media/film.en.srt"}""")]
    public async Task A_job_that_does_not_say_which_subtitle_it_is_for_does_nothing(string payload)
    {
        var handler = new SubtitleSyncJobHandler(
            Mock.Of<ISubtitleTimingSync>(MockBehavior.Strict),
            Mock.Of<IMediaSubtitleRepository>(MockBehavior.Strict),
            Mock.Of<IActivityFeedRepository>(MockBehavior.Strict));

        var message = await handler.HandleAsync(TestJobs.Create("subtitle.sync", payload), CancellationToken.None);

        Assert.Equal("This timing job does not say which subtitle it is for, so nothing was done.", message);
        Assert.Equal("subtitle.sync", handler.JobType);
    }

    [Fact]
    public async Task A_job_with_no_payload_at_all_does_nothing()
    {
        var handler = new SubtitleSyncJobHandler(
            Mock.Of<ISubtitleTimingSync>(MockBehavior.Strict),
            Mock.Of<IMediaSubtitleRepository>(MockBehavior.Strict),
            Mock.Of<IActivityFeedRepository>(MockBehavior.Strict));

        var message = await handler.HandleAsync(TestJobs.Create("subtitle.sync", payloadJson: null), CancellationToken.None);

        Assert.StartsWith("This timing job does not say", message);
    }
}
