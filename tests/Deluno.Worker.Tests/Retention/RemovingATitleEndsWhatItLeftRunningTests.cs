using System.Runtime.CompilerServices;
using Deluno.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Moq;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// Removing a title ends the work still going on about it (#563).
///
/// <para><b>Why this file exists.</b> On the rig the proof titles were removed
/// and nine recovery cases about their downloads stayed open and counted, until
/// each was dismissed by hand. Settling those cases is only half of it: a
/// hand-off left waiting goes on being waited for and raises a fresh case hours
/// later, for a title nobody manages any more — the counted problem would come
/// back on its own.</para>
/// </summary>
public sealed class RemovingATitleEndsWhatItLeftRunningTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public async Task The_cases_about_its_downloads_are_settled_by_what_it_fetched()
    {
        var scene = new Scene();
        scene.Dispatched("grab-1", Release);

        var metadata = await scene.RemoveAsync();

        Assert.Equal(Release, Assert.Single(scene.Settled.Scope!.ReleaseNames));
        Assert.Equal("grab-1", Assert.Single(scene.Settled.Scope.DispatchIds));
        Assert.Contains("Tears of Steel was removed from Deluno", scene.Settled.Reason);
        Assert.Equal("1", metadata["settledRecoveryCaseCount"]);
    }

    /// <summary>
    /// A successful import archives its dispatch at once, so most of what a
    /// title fetched over a week is archived — and a case raised about one of
    /// those is still open and still counted.
    /// </summary>
    [Fact]
    public async Task A_finished_download_is_still_one_of_the_titles_downloads()
    {
        var scene = new Scene();
        scene.Dispatched("grab-done", "Tears.of.Steel.2012.720p.WEB-DL-DELUNO", status: "archived");
        scene.Dispatched("grab-1", Release);

        await scene.RemoveAsync();

        Assert.Equal(["grab-done", "grab-1"], scene.Settled.Scope!.DispatchIds);
        Assert.Contains("Tears.of.Steel.2012.720p.WEB-DL-DELUNO", scene.Settled.Scope.ReleaseNames);
    }

    [Fact]
    public async Task A_hand_off_still_with_the_processor_is_ended_and_the_processor_is_asked_to_drop_it()
    {
        var scene = new Scene();
        scene.Dispatched("grab-1", Release);
        scene.HandedOff("h1", Release, ProcessorHandoffStatuses.Submitted);

        var metadata = await scene.RemoveAsync();

        scene.Processors.Verify(service => service.CancelHandoffAsync(It.IsAny<ProcessorConnectionItem>(), "h1", It.IsAny<CancellationToken>()), Times.Once);
        // Dismissed, not cancelled: a cancelled hand-off is a row on Transfers
        // waiting for a person, and the title it was about is gone.
        scene.Handoffs.Verify(repository => repository.UpdateProcessorHandoffAsync(
                "h1",
                ProcessorHandoffStatuses.Dismissed,
                null,
                null,
                It.Is<string>(said => said.Contains("Tears of Steel was removed from Deluno")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal("1", metadata["stoppedProcessorHandoffCount"]);
    }

    /// <summary>
    /// Another title's hand-off, and one that has already ended, are not this
    /// removal's to touch.
    /// </summary>
    [Fact]
    public async Task Only_the_removed_titles_open_hand_offs_are_ended()
    {
        var scene = new Scene();
        scene.Dispatched("grab-1", Release);
        scene.HandedOff("h1", Release, ProcessorHandoffStatuses.Completed);
        scene.HandedOff("h2", "Big.Buck.Bunny.2008.1080p.WEB-DL-DELUNO", ProcessorHandoffStatuses.Submitted);

        var metadata = await scene.RemoveAsync();

        scene.Processors.Verify(service => service.CancelHandoffAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("0", metadata["stoppedProcessorHandoffCount"]);
    }

    /// <summary>
    /// A processor mid-run is not interrupted — the same file imported twice is
    /// worse than one hand-off ending the ordinary way, when it answers.
    /// </summary>
    [Fact]
    public async Task A_processor_that_has_started_work_is_left_alone()
    {
        var scene = new Scene();
        scene.Dispatched("grab-1", Release);
        scene.HandedOff("h1", Release, ProcessorHandoffStatuses.Started);
        scene.Processors
            .Setup(service => service.CancelHandoffAsync(It.IsAny<ProcessorConnectionItem>(), "h1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorHandoffCancelResult(ProcessorAnswer.Busy, "Remuxing."));

        var metadata = await scene.RemoveAsync();

        scene.Handoffs.Verify(repository => repository.UpdateProcessorHandoffAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal("0", metadata["stoppedProcessorHandoffCount"]);
    }

    private sealed class Scene
    {
        private readonly List<DownloadDispatchItem> dispatches = [];
        private readonly List<ProcessorHandoffItem> handoffs = [];
        private readonly Mock<IDownloadDispatchesRepository> dispatchesRepository = new();
        private readonly Mock<ILibrariesRepository> libraries = new();

        public Mock<IProcessorRepository> Handoffs { get; } = new();

        public Mock<IProcessorConnectionService> Processors { get; } = new();

        public (ImportRecoveryCaseScope? Scope, string Reason) Settled { get; private set; } = (null, string.Empty);

        public void Dispatched(string id, string releaseName, string? status = null)
            => dispatches.Add(Blank<DownloadDispatchItem>() with { Id = id, ReleaseName = releaseName, Status = status! });

        /// <summary>
        /// The ordinary answers, set here so a test that wants a different one
        /// can say so and have it win.
        /// </summary>
        public Scene()
        {
            dispatchesRepository
                .Setup(repository => repository.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (dispatches, (string?)null));
            libraries
                .Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([Blank<LibraryItem>() with { Id = "lib-1" }]);
            Handoffs
                .Setup(repository => repository.ListOpenProcessorHandoffsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => handoffs.Where(h => ProcessorHandoffStatuses.IsWithProcessor(h.Status)).ToArray());
            Handoffs
                .Setup(repository => repository.FindProcessorConnectionByNameAsync("Weir", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Blank<ProcessorConnectionItem>() with { Name = "Weir", IsEnabled = true });
            Handoffs
                .Setup(repository => repository.UpdateProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, string status, string? _, string? _, string? said, CancellationToken _) =>
                    Blank<ProcessorHandoffItem>() with { Id = id, Status = status, FailureMessage = said });
            Processors
                .Setup(service => service.CancelHandoffAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessorHandoffCancelResult(ProcessorAnswer.Answered, "Dropped."));
        }

        public void HandedOff(string id, string releaseName, string status)
            => handoffs.Add(Blank<ProcessorHandoffItem>() with
            {
                Id = id,
                ReleaseName = releaseName,
                Status = status,
                ProcessorName = "Weir",
                LibraryId = "lib-1"
            });

        public async Task<Dictionary<string, string?>> RemoveAsync()
        {
            var removal = new TitleRemovalService(
                libraries.Object,
                Mock.Of<IIntakeRepository>(),
                Mock.Of<IJobQueueRepository>(),
                Mock.Of<IActivityFeedRepository>(),
                Mock.Of<IRecycleBinService>(),
                dispatchesRepository.Object,
                Handoffs.Object,
                Processors.Object);

            return await removal.RemoveAsync(
                new TitleToRemove(
                    "movies",
                    "movie",
                    "m1",
                    "Tears of Steel",
                    2012,
                    null,
                    (_, _) => AsyncEnumerable.Empty<string>(),
                    _ => Task.FromResult(true),
                    (scope, reason, _) =>
                    {
                        Settled = (scope, reason);
                        return Task.FromResult(scope.IsEmpty ? 0 : 1);
                    }),
                new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false),
                CancellationToken.None);
        }

        private static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }
}
