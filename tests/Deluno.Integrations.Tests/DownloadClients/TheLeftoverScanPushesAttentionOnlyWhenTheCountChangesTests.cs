using System.Runtime.CompilerServices;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Coverage register (#1329), realtime hub: the leftover-folder scan asks for an AttentionChanged push (reason "orphans") when what
/// the bell counts changes between two scans. The first reading only sets the baseline, and an unchanged scan pushes nothing.
/// </summary>
public sealed class TheLeftoverScanPushesAttentionOnlyWhenTheCountChangesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-19T12:00:00Z");

    [Fact]
    public async Task A_leftover_folder_appearing_asks_for_one_orphans_push_and_the_first_scan_and_an_unchanged_scan_ask_for_none()
    {
        var directory = Directory.CreateTempSubdirectory("leftover-push-");
        try
        {
            var pushed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reasons = new List<string>();
            var publisher = new Mock<IRealtimeEventPublisher>();
            publisher
                .Setup(p => p.PublishDashboardEventAsync(LiveEventNames.AttentionChanged, It.IsAny<object>(), It.IsAny<CancellationToken>()))
                .Callback<string, object, CancellationToken>((_, payload, _) =>
                {
                    var reason = ((AttentionChangedEvent)payload).Reason;
                    lock (reasons) { reasons.Add(reason); }
                    pushed.TrySetResult(reason);
                })
                .Returns(Task.CompletedTask);
            var service = CreateService(directory.FullName, "Left.Behind.2019.1080p.WEB-DL.x264-GRP", [], new LeftoverScanMemory(), publisher.Object);

            await service.ListAsync(CancellationToken.None);
            await service.ListAsync(CancellationToken.None);
            await Task.Delay(LiveEventPublisherExtensions.AttentionCoalesceWindow * 3); // reason: the push is gathered for 300 ms; two scans with nothing new must still have sent none after that
            lock (reasons) { Assert.Empty(reasons); }

            var entry = Path.Combine(directory.FullName, "Left.Behind.2019.1080p.WEB-DL.x264-GRP");
            Directory.CreateDirectory(entry);
            await File.WriteAllTextAsync(Path.Combine(entry, "film.mkv"), "bytes");
            Directory.SetLastWriteTimeUtc(entry, DateTime.UtcNow.AddHours(-3));
            var found = await service.ListAsync(CancellationToken.None);

            Assert.Single(found);
            Assert.Equal("orphans", await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static OrphanedDownloadService CreateService(string downloadsPath, string grabbedRelease, List<string> kept, LeftoverScanMemory memory, IRealtimeEventPublisher publisher)
    {
        var library = new LibraryItem(
            "library-1", "Movies", "movies", "main", @"C:\Deluno\Library\Movies", downloadsPath, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, Now, Now);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);

        var platformSettings = new Mock<IPlatformSettingsRepository>();
        platformSettings.Setup(instance => instance.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformSettingsSnapshot)RuntimeHelpers.GetUninitializedObject(typeof(PlatformSettingsSnapshot)));

        var telemetry = new Mock<IDownloadClientTelemetryService> { CallBase = true };
        telemetry.Setup(instance => instance.GetSnapshotsForLeftoverScanAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var jobQueue = new Mock<IJobQueueRepository>();
        jobQueue.Setup(instance => instance.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        jobQueue.Setup(instance => instance.FindGrabbedReleaseNamesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> names, CancellationToken _) =>
                (IReadOnlyList<string>)names.Where(name => string.Equals(name, grabbedRelease, StringComparison.OrdinalIgnoreCase)).ToArray());

        var processors = new Mock<IProcessorRepository>();
        processors.Setup(instance => instance.ListProcessorHandoffsAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var keptDownloads = new Mock<IKeptDownloadsRepository>();
        keptDownloads.Setup(instance => instance.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => kept.ToArray());
        keptDownloads.Setup(instance => instance.KeepAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string path, CancellationToken _) => kept.Add(path))
            .Returns(Task.CompletedTask);

        return new OrphanedDownloadService(
            libraries.Object, platformSettings.Object, telemetry.Object, jobQueue.Object, processors.Object, keptDownloads.Object,
            new Mock<IActivityFeedRepository>().Object, TimeProvider.System, NullLogger<OrphanedDownloadService>.Instance, scanMemory: memory, realtimeEventPublisher: publisher);
    }
}
