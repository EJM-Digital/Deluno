using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>#920: the retry job a failed media server refresh schedules with backoff.</summary>
public sealed class MediaServerRefreshRetryJobHandlerTests
{
    [Fact]
    public async Task An_empty_payload_is_skipped_without_calling_the_notifier()
    {
        var notifier = new Mock<IMediaServerRefreshNotifier>(MockBehavior.Strict);
        var handler = new MediaServerRefreshRetryJobHandler(notifier.Object);

        var message = await handler.HandleAsync(TestJobs.Create(MediaServerJobTypes.RefreshRetry, payloadJson: null), CancellationToken.None);

        Assert.Contains("skipped", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_server_that_answers_reports_it_answered()
    {
        var notifier = new Mock<IMediaServerRefreshNotifier>();
        notifier.Setup(service => service.RetryAsync("server-1", "movies", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var handler = new MediaServerRefreshRetryJobHandler(notifier.Object);

        var payload = new MediaServerRefreshRetryJobPayload("server-1", "movies");
        var message = await handler.HandleAsync(
            TestJobs.Create(MediaServerJobTypes.RefreshRetry, payloadJson: JsonSerializer.Serialize(payload)),
            CancellationToken.None);

        Assert.Contains("answered", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_server_still_down_reports_another_retry_is_scheduled()
    {
        var notifier = new Mock<IMediaServerRefreshNotifier>();
        notifier.Setup(service => service.RetryAsync("server-1", "movies", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var handler = new MediaServerRefreshRetryJobHandler(notifier.Object);

        var payload = new MediaServerRefreshRetryJobPayload("server-1", "movies");
        var message = await handler.HandleAsync(
            TestJobs.Create(MediaServerJobTypes.RefreshRetry, payloadJson: JsonSerializer.Serialize(payload)),
            CancellationToken.None);

        Assert.Contains("still is not answering", message, StringComparison.OrdinalIgnoreCase);
    }
}
