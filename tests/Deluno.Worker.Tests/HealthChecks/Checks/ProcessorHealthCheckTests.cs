using Deluno.Contracts;
using Deluno.Integrations.Processors;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #962: a processor connection's own reported watched folder is in its own
/// filesystem namespace. The health check must translate it through the
/// connection's path mappings before comparing it against the library's
/// processed-output folder -- otherwise a correctly configured mapping would
/// still read on screen as a mismatch.
/// </summary>
public sealed class ProcessorHealthCheckTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");

    [Fact]
    public async Task A_watched_path_that_maps_to_the_librarys_folder_is_ok()
    {
        var connection = Connection();
        var library = Library(downloadsPath: "D:\\Refined");

        var outcomes = await RunAsync(
            connection,
            [library],
            support: new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { WatchedPath = "/data/refined" },
            mappings: [Mapping("D:\\Refined", "/data/refined")]);

        var watching = Assert.Single(outcomes, item => item.Id.Contains(":watching:"));
        Assert.Equal(HealthCheckStatuses.Ok, watching.Status);
        Assert.Contains("D:\\Refined", watching.Message);
    }

    [Fact]
    public async Task An_unmapped_watched_path_that_does_not_match_is_still_a_problem()
    {
        var connection = Connection();
        var library = Library(downloadsPath: "D:\\Refined");

        var outcomes = await RunAsync(
            connection,
            [library],
            support: new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { WatchedPath = "/data/wrong" },
            mappings: [Mapping("D:\\Refined", "/data/refined")]);

        var watching = Assert.Single(outcomes, item => item.Id.Contains(":watching:"));
        Assert.Equal(HealthCheckStatuses.Problem, watching.Status);
    }

    [Fact]
    public async Task With_no_mappings_configured_behaviour_is_unchanged()
    {
        var connection = Connection();
        var library = Library(downloadsPath: "D:\\Refined");

        var outcomes = await RunAsync(
            connection,
            [library],
            support: new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { WatchedPath = "D:\\Refined" },
            mappings: []);

        var watching = Assert.Single(outcomes, item => item.Id.Contains(":watching:"));
        Assert.Equal(HealthCheckStatuses.Ok, watching.Status);
    }

    [Fact]
    public async Task A_processor_with_a_folder_per_library_is_read_by_the_library_id()
    {
        var connection = Connection();
        var library = Library(downloadsPath: "D:\\Refined");

        var outcomes = await RunAsync(
            connection,
            [library],
            support: new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { HasLibraryFolders = true },
            mappings: [Mapping("D:\\Refined", "/data/refined")],
            folders:
            [
                new ProcessorLibraryFolder("someone-else", "/data/other", false),
                new ProcessorLibraryFolder("library-1", "/data/refined", true)
            ]);

        var watching = Assert.Single(outcomes, item => item.Id.Contains(":watching:"));
        Assert.Equal(HealthCheckStatuses.Ok, watching.Status);
        Assert.Contains("D:\\Refined", watching.Message);
    }

    [Fact]
    public async Task A_folder_per_library_list_with_no_entry_for_the_library_is_not_reported()
    {
        var outcomes = await RunAsync(
            Connection(),
            [Library(downloadsPath: "D:\\Refined")],
            support: new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { HasLibraryFolders = true },
            mappings: [],
            folders: [new ProcessorLibraryFolder("someone-else", "/data/other", false)]);

        var watching = Assert.Single(outcomes, item => item.Id.Contains(":watching:"));
        Assert.Equal(HealthCheckStatuses.Unknown, watching.Status);
        Assert.Contains("Not reported", watching.Message);
    }

    [Fact]
    public async Task A_folder_per_library_entry_that_disagrees_with_the_downloads_folder_is_a_problem()
    {
        var outcomes = await RunAsync(
            Connection(),
            [Library(downloadsPath: "D:\\Refined")],
            support: new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { HasLibraryFolders = true },
            mappings: [],
            folders: [new ProcessorLibraryFolder("library-1", "E:\\Elsewhere", false)]);

        Assert.Equal(HealthCheckStatuses.Problem, Assert.Single(outcomes, item => item.Id.Contains(":watching:")).Status);
    }

    [Fact]
    public async Task The_folder_the_processor_writes_to_is_compared_with_the_ready_folder_not_the_downloads_folder()
    {
        var support = new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { HasLibraryFolders = true };
        var agreeing = await RunAsync(
            Connection(),
            [Library(downloadsPath: "C:\\Downloads\\Completed\\TV", processorOutputPath: "E:\\Weir\\Ready\\TV")],
            support,
            mappings: [],
            folders: [new ProcessorLibraryFolder("library-1", "C:\\Downloads\\Completed\\TV", true, "E:\\Weir\\Ready\\TV")]);

        Assert.Equal(HealthCheckStatuses.Ok, Assert.Single(agreeing, item => item.Id.Contains(":watching:")).Status);
        Assert.Equal(HealthCheckStatuses.Ok, Assert.Single(agreeing, item => item.Id.Contains(":writing:")).Status);

        var wrongOutput = await RunAsync(
            Connection(),
            [Library(downloadsPath: "C:\\Downloads\\Completed\\TV", processorOutputPath: "E:\\Weir\\Ready\\TV")],
            support,
            mappings: [],
            folders: [new ProcessorLibraryFolder("library-1", "C:\\Downloads\\Completed\\TV", true, "E:\\Elsewhere")]);

        Assert.Equal(HealthCheckStatuses.Ok, Assert.Single(wrongOutput, item => item.Id.Contains(":watching:")).Status);
        Assert.Equal(HealthCheckStatuses.Problem, Assert.Single(wrongOutput, item => item.Id.Contains(":writing:")).Status);
    }

    [Fact]
    public async Task An_output_folder_the_processor_does_not_report_adds_no_line()
    {
        var outcomes = await RunAsync(
            Connection(),
            [Library(downloadsPath: "C:\\Downloads\\Completed\\TV", processorOutputPath: "E:\\Weir\\Ready\\TV")],
            new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { HasLibraryFolders = true },
            mappings: [],
            folders: [new ProcessorLibraryFolder("library-1", "C:\\Downloads\\Completed\\TV", true, "")]);

        Assert.DoesNotContain(outcomes, item => item.Id.Contains(":writing:"));
    }

    private static async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(
        ProcessorConnectionItem connection,
        IReadOnlyList<LibraryItem> libraries,
        ProcessorStatusSupport support,
        IReadOnlyList<ProcessorPathMappingItem> mappings,
        IReadOnlyList<ProcessorLibraryFolder>? folders = null)
    {
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([connection]);
        processors.Setup(repo => repo.ListProcessorPathMappingsAsync(connection.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mappings);

        var connections = new Mock<IProcessorConnectionService>();
        connections.Setup(service => service.GetStatusSupportAsync(connection, It.IsAny<CancellationToken>()))
            .ReturnsAsync(support);
        connections.Setup(service => service.GetLibraryFoldersAsync(connection, It.IsAny<CancellationToken>()))
            .ReturnsAsync(folders ?? []);

        var libraryRepository = new Mock<ILibrariesRepository>();
        libraryRepository.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(libraries);

        var check = new ProcessorHealthCheck(processors.Object, connections.Object, libraryRepository.Object);
        return await check.RunAsync(CancellationToken.None);
    }

    private static ProcessorConnectionItem Connection() => new(
        "connection-1", "Weir", "generic-webhook", "https://weir.example.test/webhook",
        "Authorization", null, true, "healthy", null, Now, Now, Now);

    private static LibraryItem Library(string downloadsPath, string? processorOutputPath = null)
    {
        var library = (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        return library with
        {
            Id = "library-1",
            Name = "Films",
            MediaType = "movies",
            ImportWorkflow = "refine-before-import",
            ProcessorName = "Weir",
            DownloadsPath = downloadsPath,
            ProcessorOutputPath = processorOutputPath
        };
    }

    private static ProcessorPathMappingItem Mapping(string delunoPath, string processorPath) => new(
        Id: Guid.NewGuid().ToString("N"),
        ProcessorConnectionId: "connection-1",
        DelunoPath: delunoPath,
        ProcessorPath: processorPath,
        IsEnabled: true,
        Priority: 10,
        CreatedUtc: Now,
        UpdatedUtc: Now);

    [Fact]
    public async Task Two_libraries_whose_folder_disagrees_read_differently()
    {
        var films = Library(downloadsPath: "/data/films");
        var shows = films with { Id = "library-2", Name = "Shows", DownloadsPath = "/data/shows" };

        var outcomes = await RunAsync(
            Connection(),
            [films, shows],
            support: new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { WatchedPath = "/data/elsewhere" },
            mappings: []);

        SameProblemReadsDifferentlyPerItemTests.AssertEachReadsDifferently(
            outcomes.Where(item => item.Id.Contains(":watching:")).ToArray());
    }
}
