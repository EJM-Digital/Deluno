using System.Runtime.CompilerServices;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#909: what System, Health says about each library root, for every state a root can be in.</summary>
public sealed class LibraryRootHealthCheckWordsTests
{
    private static LibraryItem Library(string id, string name, string? rootPath)
        => ((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with { Id = id, Name = name, RootPath = rootPath! };

    private static LibraryRootHealthCheck Check(LibraryItem[] libraries, params LibraryRootStatus[] statuses)
    {
        var repository = new Mock<ILibrariesRepository>();
        repository.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(libraries);
        var availability = new Mock<ILibraryAvailabilityService>();
        availability.Setup(service => service.ReadAsync(It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibraryAvailability(new HashSet<string>(), statuses.ToDictionary(status => status.LibraryId)));
        return new LibraryRootHealthCheck(repository.Object, availability.Object);
    }

    private static LibraryRootStatus Status(LibraryRootProblem problem, bool warn = false, bool reachable = true, bool? writable = true, double? free = 40.5)
        => new("l1", "Films", "/media/films", reachable, writable, free, problem, warn, "Words from the check.");

    private static readonly LibraryItem Films = Library("l1", "Films", "/media/films");

    [Fact]
    public async Task A_root_that_is_fine_is_ok_in_the_checks_own_words_with_its_numbers()
    {
        var outcome = Assert.Single(await Check([Films], Status(LibraryRootProblem.None)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("library:l1:root", outcome.Id);
        Assert.Equal("Films (library root)", outcome.Name);
        Assert.Equal("Words from the check.", outcome.Message);
        Assert.Equal("""{"libraryId":"l1","reachable":true,"writable":true,"freePercent":40.5}""", outcome.DetailsJson);
        Assert.Equal(HealthCheckCategories.Library, Check([], []).Category);
    }

    [Theory]
    [InlineData(true, false, null, """{"libraryId":"l1","reachable":true,"writable":null,"freePercent":null}""")]
    [InlineData(false, true, 12.0, """{"libraryId":"l1","reachable":false,"writable":false,"freePercent":12}""")]
    public async Task The_numbers_are_written_as_they_stand_including_the_ones_the_check_could_not_read(bool reachable, bool writableKnown, double? free, string expected)
    {
        var status = Status(LibraryRootProblem.None, reachable: reachable, writable: writableKnown ? false : null, free: free);

        var outcome = Assert.Single(await Check([Films], status).RunAsync(CancellationToken.None));

        Assert.Equal(expected, outcome.DetailsJson);
    }

    [Fact]
    public async Task Space_that_is_low_but_not_yet_paused_is_a_problem_with_its_own_reason()
    {
        var outcome = Assert.Single(await Check([Films], Status(LibraryRootProblem.None, warn: true)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Free space on the Films library's root is running low.", outcome.Cause);
        Assert.Equal("Free up space, or move some of the Films library elsewhere, before it reaches the pause threshold.", outcome.Fix);
    }

    [Fact]
    public async Task A_root_that_cannot_be_reached_says_to_check_the_drive()
    {
        var outcome = Assert.Single(await Check([Films], Status(LibraryRootProblem.Unreachable)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("The Films library's root could not be reached -- check that the drive or share is mounted.", outcome.Cause);
        Assert.Equal("Reconnect the drive or share. Deluno resumes the Films library on its own once the path answers again.", outcome.Fix);
    }

    [Fact]
    public async Task A_root_that_refuses_writes_says_to_remount_it()
    {
        var outcome = Assert.Single(await Check([Films], Status(LibraryRootProblem.ReadOnly)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("The Films library's root answers but refuses writes -- often a read-only mount, full permissions, or a share reconnected read-only.", outcome.Cause);
        Assert.Equal("Remount or repair permissions on the Films library's root so Deluno can write to it again.", outcome.Fix);
    }

    [Fact]
    public async Task A_root_at_the_pause_threshold_says_to_free_up_space()
    {
        var outcome = Assert.Single(await Check([Films], Status(LibraryRootProblem.LowFreeSpace)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Free space on the Films library's root fell to or below the configured pause threshold.", outcome.Cause);
        Assert.Equal("Free up space on the Films library's root. Deluno resumes it on its own once it is back above the threshold.", outcome.Fix);
    }

    [Fact]
    public async Task A_state_the_check_does_not_know_is_unknown_not_a_problem()
    {
        var outcome = Assert.Single(await Check([Films], Status((LibraryRootProblem)99)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_library_with_no_root_path_is_skipped(string? rootPath)
    {
        var outcomes = await Check([Library("l1", "Films", rootPath)], Status(LibraryRootProblem.Unreachable)).RunAsync(CancellationToken.None);

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_library_the_availability_check_did_not_read_is_skipped()
    {
        var outcomes = await Check([Films]).RunAsync(CancellationToken.None);

        Assert.Empty(outcomes);
    }
}
