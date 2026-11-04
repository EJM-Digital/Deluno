using System.Runtime.CompilerServices;
using Deluno.Contracts;
using Deluno.Integrations.Processors;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#909, #1055, #1144: what System, Health says about each processor: reachable or not, which libraries it serves, and whether its watched and output folders agree with theirs.</summary>
public sealed class ProcessorHealthCheckWordsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");

    private static ProcessorConnectionItem Connection(string status = "healthy", string? message = null, bool enabled = true, DateTimeOffset? tested = null, DateTimeOffset? created = null)
        => new("connection-1", "Weir", "generic-webhook", "https://weir.example.test/webhook", "Authorization", null, enabled, status, message, tested ?? Now.AddMinutes(-1), created ?? Now.AddDays(-1), Now);

    private static LibraryItem Library(string id = "library-1", string name = "Films", string workflow = "refine-before-import", string? processorName = "Weir", string? connectionId = null, string? downloads = "/data/films", string? ready = "/data/films-ready")
        => ((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with
        {
            Id = id, Name = name, MediaType = "movies", ImportWorkflow = workflow, ProcessorName = processorName, ProcessorConnectionId = connectionId,
            DownloadsPath = downloads!, ProcessorOutputPath = ready
        };

    private static ProcessorStatusSupport Answered(string? watched = "/data/films") => new(ProcessorAnswer.Answered, true, true, null) { WatchedPath = watched };

    private static async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(
        ProcessorConnectionItem[] connections,
        LibraryItem[] libraries,
        ProcessorStatusSupport? support = null,
        IReadOnlyList<ProcessorLibraryFolder>? folders = null,
        TimeProvider? clock = null)
    {
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(connections);
        processors.Setup(repo => repo.ListProcessorPathMappingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var service = new Mock<IProcessorConnectionService>();
        service.Setup(s => s.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>())).ReturnsAsync(support ?? Answered());
        service.Setup(s => s.GetLibraryFoldersAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>())).ReturnsAsync(folders ?? []);
        var libraryRepository = new Mock<ILibrariesRepository>();
        libraryRepository.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(libraries);
        return await new ProcessorHealthCheck(processors.Object, service.Object, libraryRepository.Object, clock).RunAsync(CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task No_enabled_processor_means_nothing_to_say()
    {
        Assert.Empty(await RunAsync([Connection(enabled: false)], [Library()]));
        Assert.Empty(await RunAsync([], [Library()]));
    }

    [Fact]
    public async Task A_reachable_processor_serving_no_library_is_just_reachable()
    {
        var outcome = Assert.Single(await RunAsync([Connection()], [Library(workflow: "import-directly")]));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("processor:connection-1", outcome.Id);
        Assert.Equal("Weir (processor)", outcome.Name);
        Assert.Equal("Weir is reachable.", outcome.Message);
        Assert.Equal(HealthCheckCategories.Processor, outcome.Category);
    }

    [Fact]
    public async Task A_processor_that_did_not_pass_its_test_says_what_it_said_and_what_to_do()
    {
        var outcome = Assert.Single(await RunAsync([Connection("unreachable", "Connection refused.")], [Library()]));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Weir: Connection refused.", outcome.Message);
        Assert.Equal("Deluno could not reach this processor on its last check.", outcome.Cause);
        Assert.Equal("Check that Weir is running and reachable, then test the connection.", outcome.Fix);
        Assert.Equal("""{"processorId":"connection-1"}""", outcome.DetailsJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_processor_that_failed_with_no_words_is_not_answering(string? message)
    {
        var outcome = Assert.Single(await RunAsync([Connection("unreachable", message)], [Library()]));

        Assert.Equal("Weir is not answering.", outcome.Message);
    }

    [Fact]
    public async Task A_secret_stored_on_another_computer_says_to_enter_it_again()
    {
        var outcome = Assert.Single(await RunAsync([Connection("unreachable", "Denied.") with { SecretUnreadable = true }], [Library()]));

        Assert.Equal("The saved secret was stored on another computer, so this computer cannot read it.", outcome.Cause);
        Assert.Equal("Open Weir in Connections, enter its secret again and save.", outcome.Fix);
    }

    [Fact]
    public async Task A_processor_added_a_moment_ago_and_never_tested_is_left_alone_until_the_grace_ends()
    {
        var added = Connection("unknown", created: Now.AddMinutes(-4)) with { LastHealthTestUtc = null };

        Assert.Empty(await RunAsync([added], [Library()], clock: new FixedClock(Now)));
        Assert.Single(await RunAsync([added with { CreatedUtc = Now - DownloadClientHealthCheck.NewClientGrace }], [Library()], clock: new FixedClock(Now)), item => item.Id == "processor:connection-1");
    }

    [Fact]
    public async Task A_processor_never_tested_but_healthy_is_reachable_and_one_tested_is_not_given_a_grace()
    {
        var healthy = Connection("healthy", created: Now.AddSeconds(-5)) with { LastHealthTestUtc = null };
        var tested = Connection("unreachable", "Down.", tested: Now.AddSeconds(-2), created: Now.AddSeconds(-5));

        Assert.Equal(HealthCheckStatuses.Ok, Assert.Single(await RunAsync([healthy], [Library(workflow: "x")], clock: new FixedClock(Now))).Status);
        Assert.Equal(HealthCheckStatuses.Problem, Assert.Single(await RunAsync([tested], [Library()], clock: new FixedClock(Now))).Status);
    }

    [Fact]
    public async Task A_library_is_found_by_the_connection_id_it_names_whatever_the_name_it_remembers()
    {
        var byId = Library(processorName: "Old name", connectionId: "CONNECTION-1");
        var byOtherId = Library(id: "library-2", name: "Shows", processorName: "Weir", connectionId: "connection-9");

        var outcomes = await RunAsync([Connection()], [byId, byOtherId]);

        Assert.Contains(outcomes, item => item.Id == "processor:connection-1:watching:library-1");
        Assert.DoesNotContain(outcomes, item => item.Id.EndsWith(":library-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_library_with_no_connection_id_is_found_by_the_processor_name_in_any_case()
    {
        var outcomes = await RunAsync([Connection()], [Library(processorName: "WEIR", connectionId: " ")]);

        Assert.Contains(outcomes, item => item.Id == "processor:connection-1:watching:library-1");
    }

    [Fact]
    public async Task A_watched_folder_that_agrees_is_ok_whatever_the_slashes_and_case()
    {
        var outcome = Assert.Single(await RunAsync([Connection()], [Library(downloads: "D:\\Data\\Films\\")], Answered(" d:/data/films ")), item => item.Id.Contains(":watching:"));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("Weir watches the Films library's downloads folder", outcome.Name);
        Assert.Equal("Weir is watching  d:/data/films , the Films library's downloads folder.", outcome.Message);
    }

    [Fact]
    public async Task A_watched_folder_that_disagrees_says_both_and_where_to_fix_it()
    {
        var outcome = Assert.Single(await RunAsync([Connection()], [Library()], Answered("/data/other")), item => item.Id.Contains(":watching:"));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Weir says it is watching /data/other, but the Films library's downloads folder is /data/films.", outcome.Message);
        Assert.Equal("The folder Weir watches and the Films library's downloads folder no longer agree.", outcome.Cause);
        Assert.Equal("Point Weir at /data/films, or update the Films library's downloads folder to match Weir.", outcome.Fix);
        Assert.Equal("""{"processorId":"connection-1","libraryId":"library-1","watchedPath":"/data/other","expectedPath":"/data/films"}""", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_processor_that_does_not_answer_the_capabilities_check_leaves_the_folder_unknown()
    {
        var outcome = Assert.Single(await RunAsync([Connection()], [Library()], new ProcessorStatusSupport(ProcessorAnswer.Unreachable, false, false, "no answer")), item => item.Id.Contains(":watching:"));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Equal("Weir did not answer Deluno's capabilities check, so Deluno cannot tell what folder it is watching.", outcome.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_processor_that_does_not_say_which_folder_it_watches_is_unknown(string? watched)
    {
        var outcome = Assert.Single(await RunAsync([Connection()], [Library()], Answered(watched)), item => item.Id.Contains(":watching:"));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Equal("Not reported by the processor: Weir does not say which folder it is watching.", outcome.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_library_with_no_downloads_folder_has_nothing_to_compare_the_watched_folder_with(string? downloads)
    {
        var outcome = Assert.Single(await RunAsync([Connection()], [Library(downloads: downloads)], Answered("/data/films")), item => item.Id.Contains(":watching:"));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Equal("Weir reports watching /data/films, but the Films library has no downloads folder configured yet to compare it against.", outcome.Message);
    }

    private static ProcessorStatusSupport PerLibrary() => new(ProcessorAnswer.Answered, true, true, null) { HasLibraryFolders = true };

    [Fact]
    public async Task A_ready_folder_that_agrees_is_ok_and_one_that_does_not_says_both()
    {
        var folders = new[] { new ProcessorLibraryFolder("library-1", "/data/films", true, "/data/films-ready/") };
        var wrong = new[] { new ProcessorLibraryFolder("library-1", "/data/films", true, "/data/elsewhere") };

        var ok = Assert.Single(await RunAsync([Connection()], [Library()], PerLibrary(), folders), item => item.Id.Contains(":writing:"));
        var problem = Assert.Single(await RunAsync([Connection()], [Library()], PerLibrary(), wrong), item => item.Id.Contains(":writing:"));

        Assert.Equal("processor:connection-1:writing:library-1", ok.Id);
        Assert.Equal("Weir writes the Films library's cleaned files to the ready folder", ok.Name);
        Assert.Equal("Weir writes to /data/films-ready/, the ready folder Films imports from.", ok.Message);
        Assert.Equal(HealthCheckStatuses.Problem, problem.Status);
        Assert.Equal("Weir says it writes to /data/elsewhere, but Films imports from /data/films-ready.", problem.Message);
        Assert.Equal("The folder Weir writes to and the Films library's ready folder no longer agree.", problem.Cause);
        Assert.Equal("Point Weir's output at /data/films-ready, or update the Films library's ready folder to match.", problem.Fix);
        Assert.Equal("""{"processorId":"connection-1","libraryId":"library-1","outputPath":"/data/elsewhere","expectedPath":"/data/films-ready"}""", problem.DetailsJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_library_with_no_ready_folder_has_nothing_to_compare_the_output_folder_with(string? ready)
    {
        var folders = new[] { new ProcessorLibraryFolder("library-1", "/data/films", true, "/data/out") };

        var outcome = Assert.Single(await RunAsync([Connection()], [Library(ready: ready)], PerLibrary(), folders), item => item.Id.Contains(":writing:"));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Equal("Weir reports writing to /data/out, but the Films library has no ready folder configured yet to compare it against.", outcome.Message);
    }
}
