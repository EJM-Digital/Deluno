using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>#662: the job that runs the notify off the import path.</summary>
public sealed class MediaServerRefreshJobHandlerTests
{
    [Fact]
    public async Task An_empty_payload_is_skipped_without_calling_the_notifier()
    {
        var notifier = new Mock<IMediaServerRefreshNotifier>(MockBehavior.Strict);
        var handler = new MediaServerRefreshJobHandler(notifier.Object);

        var message = await handler.HandleAsync(TestJobs.Create(MediaServerJobTypes.Refresh, payloadJson: null), CancellationToken.None);

        Assert.Contains("skipped", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_well_formed_payload_calls_the_notifier_with_every_change()
    {
        IReadOnlyList<MediaServerPathChange>? passed = null;
        var notifier = new Mock<IMediaServerRefreshNotifier>();
        notifier
            .Setup(service => service.NotifyAsync("movies", It.IsAny<IReadOnlyList<MediaServerPathChange>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<MediaServerPathChange>, CancellationToken>((_, changes, _) => passed = changes)
            .Returns(Task.CompletedTask);
        var handler = new MediaServerRefreshJobHandler(notifier.Object);

        var payload = new MediaServerRefreshJobPayload(
            "movies",
            [
                new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created),
                new MediaServerPathChange("/media/Movies/Sintel.old.mkv", MediaServerChangeKind.Deleted)
            ]);

        var message = await handler.HandleAsync(
            TestJobs.Create(MediaServerJobTypes.Refresh, payloadJson: System.Text.Json.JsonSerializer.Serialize(payload)),
            CancellationToken.None);

        Assert.Equal(2, passed!.Count);
        Assert.Contains("2 changed files", message);
    }
}
