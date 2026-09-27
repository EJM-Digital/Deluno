using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Platform;
using Moq;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// #856: a title removed with its files is the same "file delete" concept a
/// replaced file's own delete fires for the import pipeline. One service
/// (<see cref="TitleRemovalService"/>) is the only place a person or a
/// retention rule removes a title's files, so this is where both callers'
/// dispatch is proven.
/// </summary>
public sealed class TitleRemovalFileDeletedNotificationTests
{
    private static (TitleRemovalService Removal, Mock<IOutboundNotificationService> Outbound, Mock<INotificationService> Preferences) Build(bool enabled)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([(LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "lib-movies", MediaType = "movies" }]);

        var recycleBin = new Mock<IRecycleBinService>();
        recycleBin.Setup(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecycleBinMoveResult(1, 0, [], []));

        var outbound = new Mock<IOutboundNotificationService>();
        var preferences = new Mock<INotificationService>();
        preferences.Setup(service => service.GetPreferencesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPreferences { FileDeletedEnabled = enabled });

        var removal = new TitleRemovalService(
            libraries.Object,
            Mock.Of<IIntakeRepository>(),
            Mock.Of<IJobQueueRepository>(),
            Mock.Of<IActivityFeedRepository>(),
            recycleBin.Object,
            outboundNotificationService: outbound.Object,
            notificationService: preferences.Object);

        return (removal, outbound, preferences);
    }

    private static TitleToRemove Target(string title = "Dementia 13") => new(
        "movies",
        "movie",
        "m1",
        title,
        1963,
        null,
        (_, cancellationToken) => OneFile(cancellationToken),
        _ => Task.FromResult(true),
        (_, _, _) => Task.FromResult(0));

    private static async IAsyncEnumerable<string> OneFile([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return @"C:\Deluno\Movies\Dementia 13 (1963)\Dementia 13.mkv";
    }

    [Fact]
    public async Task Removing_a_title_with_its_files_dispatches_file_deleted_when_the_toggle_is_on()
    {
        var (removal, outbound, _) = Build(enabled: true);

        await removal.RemoveAsync(Target(), new TitleRemovalOptions(DeleteFiles: true, AddImportListExclusion: false), CancellationToken.None);

        outbound.Verify(service => service.DispatchAsync("file.deleted", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Removing_a_title_with_its_files_stays_silent_when_the_toggle_is_off()
    {
        var (removal, outbound, _) = Build(enabled: false);

        await removal.RemoveAsync(Target(), new TitleRemovalOptions(DeleteFiles: true, AddImportListExclusion: false), CancellationToken.None);

        outbound.Verify(service => service.DispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Removing_a_title_without_deleting_files_never_dispatches_file_deleted()
    {
        var (removal, outbound, _) = Build(enabled: true);

        await removal.RemoveAsync(Target(), new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false), CancellationToken.None);

        outbound.Verify(service => service.DispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
