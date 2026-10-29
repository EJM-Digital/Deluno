using Deluno.Notifications;
using Deluno.Notifications.Data;
using Microsoft.Extensions.Logging;
using Moq;

namespace Deluno.Worker.Tests.OutboundDispatch;

/// <summary>When Deluno cannot even read the list of webhooks, an event is dropped quietly, not allowed to break the work that raised it (#1329).</summary>
public sealed class OutboundDispatchLoadingLowGapTests
{
    private static (OutboundNotificationService Service, Mock<IHttpClientFactory> Http, Mock<ILogger<OutboundNotificationService>> Log) Service(Exception loadFailure)
    {
        var repository = new Mock<INotificationRepository>();
        repository.Setup(instance => instance.AreOutboundNotificationsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(instance => instance.ListNotificationWebhooksWithSecretsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(loadFailure);
        var http = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var log = new Mock<ILogger<OutboundNotificationService>>();
        log.Setup(instance => instance.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return (new OutboundNotificationService(repository.Object, http.Object, log.Object), http, log);
    }

    [Fact]
    public async Task A_webhook_list_that_cannot_be_loaded_is_logged_as_a_warning_and_the_dispatch_returns_quietly()
    {
        var (service, _, log) = Service(new InvalidOperationException("database is locked"));

        await service.DispatchAsync("grab", "Sintel", "Grabbed Sintel", null, CancellationToken.None);

        log.Verify(instance => instance.Log(
            LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<InvalidOperationException>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task A_cancelled_load_is_not_swallowed()
    {
        var (service, _, _) = Service(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.DispatchAsync("grab", "Sintel", "Grabbed Sintel", null, CancellationToken.None));
    }
}
