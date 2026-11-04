using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Worker.Security;
using Deluno.Worker.Tests.Support;
using Microsoft.Extensions.Logging;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>#1392: a password reset is written to Activity in plain words and sent to the notification connections; a connection that is down never undoes the reset.</summary>
public sealed class ActivityAccountResetReporterTests
{
    [Fact]
    public async Task A_reset_is_written_to_Activity_and_sent_to_the_notification_connections_in_the_same_words()
    {
        var activity = new Mock<IActivityFeedRepository>();
        var notifications = new Mock<IOutboundNotificationService>();
        var log = new RecordingLogger<ActivityAccountResetReporter>();

        await new ActivityAccountResetReporter(activity.Object, notifications.Object, log).ReportResetAsync(CancellationToken.None);

        activity.Verify(a => a.RecordActivityAsync(
            "security.password-reset", "Password reset with a recovery code. Every signed-in session was ended.", null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
        notifications.Verify(n => n.DispatchAsync(
            "security.password-reset", "Deluno password reset", "Password reset with a recovery code. Every signed-in session was ended.", null, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()), Times.Once);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task A_notification_connection_that_is_down_is_warned_about_and_the_reset_still_stands()
    {
        var activity = new Mock<IActivityFeedRepository>();
        var notifications = new Mock<IOutboundNotificationService>();
        var down = new HttpRequestException("connection refused");
        notifications
            .Setup(n => n.DispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ThrowsAsync(down);
        var log = new RecordingLogger<ActivityAccountResetReporter>();

        await new ActivityAccountResetReporter(activity.Object, notifications.Object, log).ReportResetAsync(CancellationToken.None);

        activity.Verify(a => a.RecordActivityAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Could not send the password reset notification.", entry.Message);
        Assert.Same(down, entry.Exception);
    }

    [Fact]
    public async Task A_cancelled_send_is_not_swallowed()
    {
        var activity = new Mock<IActivityFeedRepository>();
        var notifications = new Mock<IOutboundNotificationService>();
        notifications
            .Setup(n => n.DispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ActivityAccountResetReporter(activity.Object, notifications.Object, new RecordingLogger<ActivityAccountResetReporter>()).ReportResetAsync(CancellationToken.None));
    }
}
