using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The processor callback's replies and limits are a contract (#1023): the table
/// in docs/external-integration-api.md says which status a processor gets for
/// what, and how many files one report may list, and this holds the code to it.
/// A processor built against the document (Weir) must not meet a different
/// answer in production.
///
/// <para>#1239: a report Weir really sent (tests/fixtures/recorded/deluno/*/events-*) is posted from its recording wherever one matches the case; a case with no real report stays
/// inline on purpose, and tests/fixtures/recorded/MISSING.md lists it with the Weir state that would produce it.</para>
/// </summary>
public sealed class ProcessorCallbackContractTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public async Task The_limits_are_the_ones_the_document_states()
    {
        Assert.Equal(1000, ProcessorEventLimits.MaxOutputFiles);
        Assert.Equal(4096, ProcessorEventLimits.MaxPathLength);
        var document = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../docs/external-integration-api.md")));
        Assert.Contains("at most 1000 files", document);
        Assert.Contains("4096 characters", document);
    }

    [Fact]
    public async Task A_report_may_list_exactly_the_maximum_number_of_files()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);

        var response = await ReportAsync(app, scene, handoff.Id, "started", Files(scene, ProcessorEventLimits.MaxOutputFiles));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task A_report_listing_more_than_the_maximum_is_refused_with_a_400_that_says_so_and_nothing_is_queued()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);

        var response = await ReportAsync(app, scene, handoff.Id, "completed", Files(scene, ProcessorEventLimits.MaxOutputFiles + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var said = await response.Content.ReadAsStringAsync();
        Assert.Contains("outputFiles", said);
        Assert.Contains("at most 1000", said);
        Assert.Contains("1001", said);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Waiting, stored?.Status);
        Assert.Empty(await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_path_longer_than_the_maximum_is_refused_with_a_400()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);

        var response = await ReportAsync(
            app, scene, handoff.Id, "started", [Path.Combine(scene.Output, new string('x', ProcessorEventLimits.MaxPathLength))]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("4096", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task No_sign_in_is_401_and_a_key_without_the_imports_scope_is_403()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        var body = new { libraryId = scene.LibraryId, handoffId = handoff.Id, status = "started" };

        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/integrations/processors/events", body)).StatusCode);

        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Read only", scopes = "read" });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var readOnly = app.CreateAnonymousClient();
        readOnly.DefaultRequestHeaders.Add("X-Api-Key", key);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync("/api/integrations/processors/events", body)).StatusCode);
    }

    [Fact]
    public async Task A_bad_status_or_a_completion_with_no_output_path_is_a_400()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);

        var badStatus = await ReportAsync(app, scene, handoff.Id, "halfway", null);
        var noOutput = await ReportAsync(app, scene, handoff.Id, "completed", null);

        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noOutput.StatusCode);
    }

    [Fact]
    public async Task A_report_for_a_library_that_does_not_exist_is_a_400_and_one_that_matches_no_hand_off_is_a_409()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        var noLibrary = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new { libraryId = "nope", status = "started" });
        Assert.Equal(HttpStatusCode.BadRequest, noLibrary.StatusCode);

        var noHandoff = await app.Client.PostAsJsonAsync(
            "/api/integrations/processors/events", new { libraryId = scene.LibraryId, handoffId = "not-a-handoff", status = "started" });
        Assert.Equal(HttpStatusCode.Conflict, noHandoff.StatusCode);
    }

    [Fact]
    public async Task A_report_repeated_for_a_hand_off_already_started_is_accepted_again_and_a_late_one_is_a_409()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(app, scene, handoff.Id, "started", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(app, scene, handoff.Id, "started", null)).StatusCode);

        // A person told Deluno to stop waiting; a completion after that is late.
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Cancelled, null, null, "stopped", CancellationToken.None);
        var late = await LateRecordedReportAsync(app, scene, handoff);

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    /// <summary>The report Weir really sent for a hand-off Deluno had stopped waiting for (events-too-late), and the answer it got.</summary>
    [Fact]
    public async Task A_finished_report_Weir_really_sent_for_a_dismissed_hand_off_is_a_409_with_the_recorded_words()
    {
        var recorded = RecordedWeirReports.Exchange("events-too-late");
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, "stopped", CancellationToken.None);

        var late = await LateRecordedReportAsync(app, scene, handoff);

        Assert.Equal((HttpStatusCode)recorded.Reply.Status, late.StatusCode);
        var said = await late.Content.ReadAsStringAsync();
        Assert.Contains("too late", said);
        Assert.Contains("No import was queued", said);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))?.Status);
    }

    private static Task<HttpResponseMessage> LateRecordedReportAsync(ApplicationTestHost app, Scene scene, ProcessorHandoffItem handoff)
        => RecordedWeirReports.PostAsync(
            app.Client,
            RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-too-late"), scene.LibraryId, handoff.Id, scene.Output, Release));

    private sealed record Scene(string LibraryId, string Downloads, string Output);

    private static IProcessorRepository Repository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static string[] Files(Scene scene, int count)
        => Enumerable.Range(0, count).Select(index => Path.Combine(scene.Output, Release, $"{index}.mkv")).ToArray();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
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
            importWorkflow = "refine-before-import",
            processorName = "Weir",
            processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = await response.Content.ReadFromJsonAsync<LibraryItem>();
        return new Scene(library!.Id, downloads, output);
    }

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene)
        => await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                scene.LibraryId,
                "movies",
                "sabnzbd",
                $"nzo-{Release}",
                Release,
                Path.Combine(scene.Downloads, Release),
                "Weir"),
            CancellationToken.None);

    private static Task<HttpResponseMessage> ReportAsync(
        ApplicationTestHost app,
        Scene scene,
        string handoffId,
        string status,
        string[]? outputFiles,
        string? outputPath = null)
        => app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId,
            status,
            outputPath = outputPath ?? (outputFiles is null ? null : Path.Combine(scene.Output, Release)),
            outputFiles
        });
}
