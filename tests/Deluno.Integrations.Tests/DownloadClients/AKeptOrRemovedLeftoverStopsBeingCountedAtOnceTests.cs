using System.Runtime.CompilerServices;
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
/// #1288 (found by the end-to-end group problemskinds): the bell and the Dashboard count leftover folders from the last scan's memory, and a
/// folder the person kept was still counted until the next scan, up to a minute later, though Transfers had already stopped listing it. A leftover
/// that is kept leaves the count with the same answer that closes it on the Transfers screen.
/// </summary>
public sealed class AKeptOrRemovedLeftoverStopsBeingCountedAtOnceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-19T12:00:00Z");

    [Fact]
    public async Task Keeping_a_leftover_takes_it_out_of_the_count_the_bell_reads()
    {
        var directory = Directory.CreateTempSubdirectory("kept-leftover-count-");
        try
        {
            var entry = Path.Combine(directory.FullName, "Left.Behind.2019.1080p.WEB-DL.x264-GRP");
            Directory.CreateDirectory(entry);
            await File.WriteAllTextAsync(Path.Combine(entry, "film.mkv"), "bytes");
            Directory.SetLastWriteTimeUtc(entry, DateTime.UtcNow.AddHours(-3));

            var kept = new List<string>();
            var memory = new LeftoverScanMemory();
            var service = CreateService(directory.FullName, "Left.Behind.2019.1080p.WEB-DL.x264-GRP", kept, memory);

            var found = await service.ListAsync(CancellationToken.None);
            Assert.Single(found);
            Assert.Equal(1, memory.Last!.Count);

            var answer = await service.KeepAsync(entry, CancellationToken.None);

            Assert.True(answer.Done, answer.Message);
            Assert.Equal(0, memory.Last!.Count);
            Assert.Equal(0, memory.Last.Bytes);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static OrphanedDownloadService CreateService(string downloadsPath, string grabbedRelease, List<string> kept, LeftoverScanMemory memory)
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
            new Mock<IActivityFeedRepository>().Object, TimeProvider.System, NullLogger<OrphanedDownloadService>.Instance, scanMemory: memory);
    }
}
