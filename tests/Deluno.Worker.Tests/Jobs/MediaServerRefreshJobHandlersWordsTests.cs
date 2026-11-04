using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>#662, #920: the exact words the two media server jobs report, for each payload they can be given.</summary>
public sealed class MediaServerRefreshJobHandlersWordsTests
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    [Theory]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("""{"MediaType":"movies","Changes":[]}""")]
    public async Task A_blank_null_or_changeless_refresh_payload_says_no_changed_files_were_given(string payloadJson)
    {
        var handler = new MediaServerRefreshJobHandler(new Mock<IMediaServerRefreshNotifier>(MockBehavior.Strict).Object);

        var message = await handler.HandleAsync(TestJobs.Create(MediaServerJobTypes.Refresh, payloadJson: payloadJson), CancellationToken.None);

        Assert.Equal("Media server refresh skipped because no changed files were given.", message);
    }

    [Fact]
    public async Task One_change_is_told_in_the_singular_and_the_servers_notes_follow_the_sentence()
    {
        var notifier = new Mock<IMediaServerRefreshNotifier>();
        notifier
            .Setup(service => service.NotifyAsync("tv", It.IsAny<IReadOnlyList<MediaServerPathChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["Plex was slow.", "Jellyfin is asleep."]);
        var handler = new MediaServerRefreshJobHandler(notifier.Object);
        var payload = new MediaServerRefreshJobPayload("tv", [new MediaServerPathChange("/media/TV/Show/S01E01.mkv", MediaServerChangeKind.Created)]);

        var message = await handler.HandleAsync(TestJobs.Create(MediaServerJobTypes.Refresh, payloadJson: Json(payload)), CancellationToken.None);

        Assert.Equal("Told every configured media server about 1 changed file. Plex was slow. Jellyfin is asleep.", message);
    }

    [Fact]
    public async Task Without_notes_the_sentence_ends_at_the_count()
    {
        var notifier = new Mock<IMediaServerRefreshNotifier>();
        notifier
            .Setup(service => service.NotifyAsync("movies", It.IsAny<IReadOnlyList<MediaServerPathChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var handler = new MediaServerRefreshJobHandler(notifier.Object);
        var payload = new MediaServerRefreshJobPayload("movies", [
            new MediaServerPathChange("/a.mkv", MediaServerChangeKind.Created),
            new MediaServerPathChange("/b.mkv", MediaServerChangeKind.Created)]);

        var message = await handler.HandleAsync(TestJobs.Create(MediaServerJobTypes.Refresh, payloadJson: Json(payload)), CancellationToken.None);

        Assert.Equal("Told every configured media server about 2 changed files.", message);
        Assert.Equal(MediaServerJobTypes.Refresh, handler.JobType);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("null")]
    public async Task A_blank_or_null_retry_payload_says_no_server_was_named(string payloadJson)
    {
        var handler = new MediaServerRefreshRetryJobHandler(new Mock<IMediaServerRefreshNotifier>(MockBehavior.Strict).Object);

        var message = await handler.HandleAsync(TestJobs.Create(MediaServerJobTypes.RefreshRetry, payloadJson: payloadJson), CancellationToken.None);

        Assert.Equal("Media server refresh retry skipped: no server was named.", message);
    }

    [Fact]
    public async Task The_retry_answers_are_told_in_full_words()
    {
        var notifier = new Mock<IMediaServerRefreshNotifier>();
        notifier.Setup(service => service.RetryAsync("server-1", "movies", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        notifier.Setup(service => service.RetryAsync("server-2", "movies", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var handler = new MediaServerRefreshRetryJobHandler(notifier.Object);

        var answered = await handler.HandleAsync(
            TestJobs.Create(MediaServerJobTypes.RefreshRetry, payloadJson: Json(new MediaServerRefreshRetryJobPayload("server-1", "movies"))), CancellationToken.None);
        var silent = await handler.HandleAsync(
            TestJobs.Create(MediaServerJobTypes.RefreshRetry, payloadJson: Json(new MediaServerRefreshRetryJobPayload("server-2", "movies"))), CancellationToken.None);

        Assert.Equal("The server answered; anything it missed was sent.", answered);
        Assert.Equal("The server still is not answering; another retry is scheduled.", silent);
        Assert.Equal(MediaServerJobTypes.RefreshRetry, handler.JobType);
    }
}
