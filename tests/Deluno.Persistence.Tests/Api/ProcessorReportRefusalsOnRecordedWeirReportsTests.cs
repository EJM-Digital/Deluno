using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The processor events route refuses a report it cannot act on, and answers a repeated one as already handled. The reports are the ones
/// Weir really sent (tests/fixtures/recorded/deluno, #1239) with only Deluno's library and hand-off ids and the folder changed to the test's
/// own, then altered in the one member each case is about (a message taken out, a source path put in).
/// </summary>
public sealed class ProcessorReportRefusalsOnRecordedWeirReportsTests
{
    private const string Release = "Recorded.AudioOnly.2020.1080p.WEB-DL.x264-E2E.mkv";
    private const string Client = "client-1";

    private sealed record Scene(string LibraryId, string Downloads, string Output);

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, string importWorkflow = "refine-before-import")
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = root,
            downloadsPath = downloads,
            importWorkflow,
            processorName = "Weir",
            processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = await response.Content.ReadFromJsonAsync<LibraryItem>();
        return new Scene(library!.Id, downloads, output);
    }

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene)
        => await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", Client, $"hash-{Release}", Release, Path.Combine(scene.Downloads, Release), "Weir"),
            CancellationToken.None);

    private static Task RecordDispatchAsync(ApplicationTestHost app, Scene scene)
        => app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            scene.LibraryId, "movies", "movie", "movie-1", Release, "Fixture Indexer",
            Client, "Deluge", "sent", null, clientExternalId: $"hash-{Release}", cancellationToken: CancellationToken.None);

    private static JsonObject Rejected(Scene scene, string handoffId)
        => RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-failed-rejected-known-handoff"), scene.LibraryId, handoffId, scene.Output, Release);

    [Fact]
    public async Task A_failed_report_with_no_message_is_a_400_that_asks_for_the_reason_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene);
        var statusBefore = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))?.Status;
        var body = Rejected(scene, handoff.Id);
        Assert.NotNull(body["message"]);
        body.Remove("message");

        var response = await RecordedWeirReports.PostAsync(app.Client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Failed processor events should explain what went wrong.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(await app.Services.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None));
        Assert.Equal(statusBefore, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task A_library_on_standard_import_refuses_a_processor_completion_with_a_409_that_names_it()
    {
        var recorded = RecordedWeirReports.Exchange("events-completed");
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, importWorkflow: "standard");
        var body = RecordedWeirReports.Body(recorded, scene.LibraryId, "any-handoff", scene.Output);

        var response = await RecordedWeirReports.PostAsync(app.Client, body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var said = (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["message"]!;
        Assert.Equal("Films is configured for standard import, so Deluno will not accept a processor completion event for it.", said);
        Assert.Empty(await Repository(app).ListProcessorHandoffsAsync(scene.LibraryId, 50, CancellationToken.None));
    }

    [Fact]
    public async Task A_source_path_that_is_not_the_hand_offs_is_a_400_and_nothing_is_imported()
    {
        var recorded = RecordedWeirReports.Exchange("events-completed");
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var release = RecordedWeirReports.ReleaseOf(recorded);
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", Client, $"hash-{release}", release, Path.Combine(scene.Downloads, release), "Weir"),
            CancellationToken.None);
        var body = RecordedWeirReports.Body(recorded, scene.LibraryId, handoff.Id, scene.Output);
        foreach (var file in RecordedWeirReports.OutputFilesOf(body))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, new byte[2048]);
        }

        body["sourcePath"] = Path.Combine(scene.Downloads, "Some.Other.Download.2019.1080p");

        var response = await RecordedWeirReports.PostAsync(app.Client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("The supplied source path does not match the processor hand-off. Deluno did not queue an import.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.NotEqual(ProcessorHandoffStatuses.Completed, stored?.Status);
        Assert.Null(stored?.ImportJobId);
    }

    [Fact]
    public async Task The_same_rejection_reported_again_is_answered_as_already_handled_and_blocks_and_searches_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene);

        var first = await RecordedWeirReports.PostAsync(app.Client, Rejected(scene, handoff.Id));
        var again = await RecordedWeirReports.PostAsync(app.Client, Rejected(scene, handoff.Id));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        var said = JsonNode.Parse(await again.Content.ReadAsStringAsync())!;
        // Answered as handled, never as a second refusal: the first report blocked the release and started the search, and the repeat does neither again.
        Assert.True((bool)said["alreadyHandled"]!);
        Assert.Equal(handoff.Id, (string)said["handoffId"]!);
        Assert.Single(await app.Services.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None));
        var rejections = (await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, null, null, CancellationToken.None))
            .Where(item => item.Category == "processing.rejected");
        Assert.Single(rejections);
    }

    [Fact]
    public async Task A_rejection_Deluno_cannot_act_on_is_a_409_and_is_written_to_Activity_as_not_acted_on()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);

        var response = await RecordedWeirReports.PostAsync(app.Client, Rejected(scene, handoff.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var said = (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["message"]!;
        Assert.Contains("could not refuse that release", said, StringComparison.Ordinal);
        var line = Assert.Single(
            await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, null, null, CancellationToken.None),
            item => item.Category == "processing.rejected.not-acted");
        Assert.Equal(said, line.Message);
        Assert.Empty(await app.Services.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None));
    }
}
