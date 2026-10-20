using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Notifications;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// The library-paused and library-resumed notifications (#909), on a real folder that goes missing and comes back: each is sent once,
/// to the outbound channels, with the same plain words Activity gets, and a library that stays gone is not announced again.
/// </summary>
public sealed class APausedOrResumedLibraryIsToldToTheOutboundChannelsTests
{
    [Fact]
    public async Task A_library_that_goes_missing_is_announced_once_as_paused_and_when_it_is_back_once_as_resumed()
    {
        var notifications = new Mock<IOutboundNotificationService>();
        var clock = new AdvanceableClock(DateTimeOffset.Parse("2026-10-20T12:00:00Z"));
        var service = new LibraryAvailabilityService(
            new Mock<IActivityFeedRepository>().Object, clock, NullLogger<LibraryAvailabilityService>.Instance, null, notifications.Object);
        var root = Path.Combine(Path.GetTempPath(), $"deluno-notify-{Guid.NewGuid():N}");

        try
        {
            await service.ReadAsync([Library("library-1", root)], CancellationToken.None);
            clock.Advance(TimeSpan.FromHours(1));
            await service.ReadAsync([Library("library-1", root)], CancellationToken.None);

            notifications.Verify(
                service => service.DispatchAsync(
                    "library.paused",
                    "Library library-1 paused",
                    It.Is<string>(message => message.Contains("Deluno has paused it", StringComparison.Ordinal) && message.Contains("nothing has been changed", StringComparison.Ordinal)),
                    null,
                    It.IsAny<CancellationToken>(),
                    null),
                Times.Once);
            notifications.Verify(
                service => service.DispatchAsync("library.resumed", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()),
                Times.Never);

            Directory.CreateDirectory(root);
            clock.Advance(TimeSpan.FromHours(1));
            var back = await service.ReadAsync([Library("library-1", root)], CancellationToken.None);

            Assert.True(back.IsUsable("library-1"));
            notifications.Verify(
                service => service.DispatchAsync(
                    "library.resumed",
                    "Library library-1 resumed",
                    It.Is<string>(message => message.Contains("reachable and writable again", StringComparison.Ordinal)),
                    null,
                    It.IsAny<CancellationToken>(),
                    null),
                Times.Once);
            notifications.Verify(
                service => service.DispatchAsync("library.paused", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static LibraryItem Library(string id, string rootPath)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            id, $"Library {id}", "movies", "main", rootPath, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private sealed class AdvanceableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
