using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// The pass that stops Deluno claiming to hold files that are gone.
///
/// <para>Deluno never looked at a file again after importing it, so a film
/// deleted outside Deluno still showed as held, was never searched for, and
/// answered "you already have this at the quality you asked for" when asked why
/// it would not download. Three wrong answers, all sounding certain. The scan
/// and the repair both already existed; nothing ran them. DESIGN-007 decision
/// 11.</para>
///
/// <para><b>What is guarded here is what the pass refuses to touch.</b> It runs
/// unattended, so the question is not whether it works but whether it can do
/// harm. Marking a tracked file missing only ever corrects Deluno's own note
/// and never goes near a disk. Cleaning up a staging artifact deletes a file,
/// and queueing an orphan for review makes a judgement about somebody else's
/// media. The first is safe to automate and the other two are not, and nothing
/// but this test says so.</para>
/// </summary>
public sealed class LibraryFileCheckTests
{
    [Fact]
    public async Task It_marks_a_vanished_file_missing()
    {
        var reconciliation = Reconciliation(Missing("movie-1"), Missing("movie-2"));

        await Planner().RunLibraryFileCheckAsync(reconciliation.Object, Mock.Of<IActivityFeedRepository>(), CancellationToken.None);

        reconciliation.Verify(
            service => service.RepairAsync(
                It.Is<FilesystemReconciliationRepairRequest>(request => request.Action == "mark-missing"),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    /// <summary>
    /// The whole reason this pass is allowed to run on its own.
    /// </summary>
    [Fact]
    public async Task It_repairs_nothing_that_touches_a_disk_or_makes_a_judgement()
    {
        var reconciliation = Reconciliation(
            Missing("movie-1"),
            Issue("orphanFile", "queue-import-review"),
            Issue("partialImportArtifact", "cleanup-artifact"),
            Issue("libraryRootUnreachable"));

        await Planner().RunLibraryFileCheckAsync(reconciliation.Object, Mock.Of<IActivityFeedRepository>(), CancellationToken.None);

        // Exactly one repair, and it is the one that only edits a database row.
        reconciliation.Verify(
            service => service.RepairAsync(It.IsAny<FilesystemReconciliationRepairRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        reconciliation.Verify(
            service => service.RepairAsync(
                It.Is<FilesystemReconciliationRepairRequest>(request => request.Action != "mark-missing"),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A title going from held to missing is something a person will see on a
    /// shelf. They should learn it from Deluno rather than from the gap.
    /// </summary>
    [Fact]
    public async Task It_says_so_when_something_changed()
    {
        var activity = new Mock<IActivityFeedRepository>();

        await Planner().RunLibraryFileCheckAsync(Reconciliation(Missing("movie-1")).Object, activity.Object, CancellationToken.None);

        activity.Verify(
            feed => feed.RecordActivityAsync(
                "library.file.missing",
                It.Is<string>(message => message.Contains("no longer on disk", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// And stays quiet when nothing did. A pass that runs every six hours and
    /// announces itself each time is one people learn to scroll past.
    /// </summary>
    [Fact]
    public async Task It_says_nothing_when_every_file_is_where_it_should_be()
    {
        var activity = new Mock<IActivityFeedRepository>();

        await Planner().RunLibraryFileCheckAsync(Reconciliation().Object, activity.Object, CancellationToken.None);

        activity.VerifyNoOtherCalls();
    }

    // ------------------------------------------------------------------ helpers

    private static WorkPlanner Planner()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new WorkPlanner(
            NullLogger<WorkPlanner>.Instance,
            jobs.Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System);
    }

    private static Mock<IFilesystemReconciliationService> Reconciliation(params FilesystemReconciliationIssue[] issues)
    {
        var service = new Mock<IFilesystemReconciliationService>();
        service.Setup(instance => instance.ScanAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilesystemReconciliationReport(DateTimeOffset.UnixEpoch, 1, issues.Length, issues));
        service.Setup(instance => instance.RepairAsync(
                It.IsAny<FilesystemReconciliationRepairRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilesystemReconciliationRepairRequest request, CancellationToken _) =>
                new FilesystemReconciliationRepairResult(true, request.Action, "Repaired."));
        return service;
    }

    private static FilesystemReconciliationIssue Missing(string entityId)
        => Issue("missingTrackedFile", "mark-missing", entityId);

    private static FilesystemReconciliationIssue Issue(string kind, string? repairAction = null, string entityId = "movie-1")
        => new(
            Id: $"{kind}:{entityId}",
            Kind: kind,
            Severity: "critical",
            MediaType: "movies",
            LibraryId: "library-1",
            LibraryName: "Films",
            Path: $"/films/{entityId}.mkv",
            Title: entityId,
            Summary: kind,
            RecommendedAction: kind,
            RepairActions: repairAction is null ? [] : [repairAction],
            EntityId: entityId);
}
