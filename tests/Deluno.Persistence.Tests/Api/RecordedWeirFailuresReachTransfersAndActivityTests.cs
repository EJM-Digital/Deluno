using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What a person is shown, and what the buttons do, for hand-offs Weir really reported (tests/fixtures/recorded/deluno, the reports the
/// real Weir rc.13 sent Deluno rc.30.30). Each report is posted as Weir sent it to the real host with a real SQLite data folder; the
/// result is read back from the database files on disk, not only through the repositories.
/// </summary>
public sealed class RecordedWeirFailuresReachTransfersAndActivityTests
{
    private const string VmOutputRoot = @"C:\Weir\Ready\Movies";

    private static Exchange Recorded(string state) => RecordedReplies.Deluno(state).Exchanges[0];

    private static string OnDiskText(ApplicationTestHost app, string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, database)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    [Fact]
    public async Task A_hand_off_Weir_really_failed_is_dismissed_with_its_reason_kept_and_a_line_in_Activity_but_one_Weir_finished_well_or_still_holds_is_not()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var failedReport = Recorded("events-failed-under-minimum");
        var failed = await HandoffAsync(app, scene, failedReport);
        await ReportAsync(app, scene, failed, failedReport);
        var finishedReport = Recorded("events-completed");
        var finished = await HandoffAsync(app, scene, finishedReport);
        await ReportAsync(app, scene, finished, finishedReport, createOutput: true);
        var inFlight = await HandoffAsync(app, scene, "Still.With.Weir.2020.1080p.WEB-DL-DELUNO");
        Assert.Equal(ProcessorHandoffStatuses.Failed, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{failed.Id}'"));
        Assert.Equal(ProcessorHandoffStatuses.Completed, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{finished.Id}'"));

        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{finished.Id}/dismiss", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{inFlight.Id}/dismiss", new { })).StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Completed, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{finished.Id}'"));
        Assert.Equal(ProcessorHandoffStatuses.Waiting, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{inFlight.Id}'"));

        var dismissed = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{failed.Id}/dismiss", new { });

        Assert.True(dismissed.IsSuccessStatusCode, await dismissed.Content.ReadAsStringAsync());
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{failed.Id}'"));
        // The reason Weir gave stays on the record, word for word.
        Assert.Equal(
            JsonNode.Parse(failedReport.Request.Body!)!["message"]!.GetValue<string>(),
            OnDiskText(app, "platform.db", $"SELECT failure_message FROM processor_handoffs WHERE id = '{failed.Id}'"));
        using var feed = JsonDocument.Parse(await app.Client.GetStringAsync("/api/activity?pageSize=100"));
        Assert.Contains(feed.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("category").GetString() == "processing.dismissed");
        Assert.NotEqual("0", OnDiskText(app, "jobs.db", "SELECT COUNT(*) FROM activity_events WHERE category = 'processing.dismissed'"));
    }

    [Theory]
    [InlineData("events-failed-under-minimum")]
    [InlineData("events-failed-unreadable")]
    public async Task The_hand_off_list_carries_what_Weir_really_said_once_for_the_Transfers_drawer(string state)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var report = Recorded(state);
        var handoff = await HandoffAsync(app, scene, report);
        await ReportAsync(app, scene, handoff, report);

        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs");

        var item = list.EnumerateArray().Single(entry => entry.GetProperty("id").GetString() == handoff.Id);
        var said = JsonNode.Parse(report.Request.Body!)!["message"]!.GetValue<string>();
        Assert.Contains(said, item.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        // Said once: the reason is in the hand-off's own field and in no other field of the row.
        Assert.Equal(1, item.EnumerateObject().Count(field => field.Value.ValueKind == JsonValueKind.String && field.Value.GetString()!.Contains(said, StringComparison.Ordinal)));
        Assert.Equal(item.GetProperty("failureMessage").GetString(), OnDiskText(app, "platform.db", $"SELECT failure_message FROM processor_handoffs WHERE id = '{handoff.Id}'"));
    }

    [Fact]
    public async Task A_hand_off_Weir_reports_gone_before_it_could_clean_it_is_listed_as_a_gone_download_that_does_not_blame_the_processor()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var report = Recorded("events-failed-source-gone");
        var handoff = await HandoffAsync(app, scene, report);
        await ReportAsync(app, scene, handoff, report);

        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs");

        var item = list.EnumerateArray().Single(entry => entry.GetProperty("id").GetString() == handoff.Id);
        Assert.Equal(ProcessorHandoffStatuses.SourceGone, item.GetProperty("status").GetString());
        Assert.Equal(ProcessorHandoffStatuses.SourceGone, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));
        var said = item.GetProperty("failureMessage").GetString()!;
        Assert.Contains("The download was gone", said, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_hand_off_list_shows_a_hand_off_with_Weir_and_one_Weir_handed_back_apart_from_a_failed_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var failedReport = Recorded("events-failed-under-minimum");
        var failed = await HandoffAsync(app, scene, failedReport);
        await ReportAsync(app, scene, failed, failedReport);
        var doneReport = Recorded("events-completed");
        var done = await HandoffAsync(app, scene, doneReport);
        await ReportAsync(app, scene, done, doneReport, createOutput: true);
        var withWeir = await HandoffAsync(app, scene, "Still.With.Weir.2020.1080p.WEB-DL-DELUNO");

        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs");

        string StatusOf(ProcessorHandoffItem handoff) => list.EnumerateArray().Single(entry => entry.GetProperty("id").GetString() == handoff.Id).GetProperty("status").GetString()!;
        Assert.Equal(ProcessorHandoffStatuses.Waiting, StatusOf(withWeir));
        Assert.Equal(ProcessorHandoffStatuses.Completed, StatusOf(done));
        Assert.Equal(ProcessorHandoffStatuses.Failed, StatusOf(failed));
        // What is on disk agrees with what the list says.
        Assert.Equal("3", OnDiskText(app, "platform.db", "SELECT COUNT(DISTINCT status) FROM processor_handoffs"));
    }

    [Fact]
    public async Task Import_original_on_a_movie_case_for_a_hand_off_Weir_really_finished_releases_it_and_a_case_of_another_kind_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var report = Recorded("events-completed");
        var release = ReleaseOf(report);
        var handoff = await HandoffAsync(app, scene, report);
        await ReportAsync(app, scene, handoff, report, createOutput: true);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(scene.LibraryId, "movies", "movie", "movie-recorded", release, "Fixture", "qbittorrent", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: $"queue-{release}");
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var fault = await movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(release, "processedFileIsLess", "The processed file is shorter than the download.", "Import the original.", JsonSerializer.Serialize(new { DispatchId = dispatchId })),
            CancellationToken.None);
        var ordinary = await movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(release, "grab-timeout", "Never showed.", "Try again.", JsonSerializer.Serialize(new { DispatchId = dispatchId })),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{ordinary.Id}/import-original", new { })).StatusCode);
        Assert.Equal("open", OnDiskText(app, "movies.db", $"SELECT status FROM movie_import_recovery_cases WHERE id = '{ordinary.Id}'"));

        var response = await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{fault.Id}/import-original", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(ProcessorHandoffStatuses.Released, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));
        Assert.Equal("resolved", OnDiskText(app, "movies.db", $"SELECT status FROM movie_import_recovery_cases WHERE id = '{fault.Id}'"));
        Assert.Equal("case_import_original", OnDiskText(app, "movies.db", $"SELECT event_kind FROM movie_import_recovery_events WHERE case_id = '{fault.Id}'"));
    }

    private sealed record Scene(string LibraryId, string Downloads, string Output);

    private static string ReleaseOf(Exchange recorded) => JsonNode.Parse(recorded.Request.Body!)!["releaseName"]!.GetValue<string>();

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

    private static Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, Exchange recorded) => HandoffAsync(app, scene, ReleaseOf(recorded));

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, string release)
        => await app.Services.GetRequiredService<IProcessorRepository>().EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", "qbittorrent", $"queue-{release}", release, Path.Combine(scene.Downloads, release), "Weir"),
            CancellationToken.None);

    /// <summary>Posts the recorded body: Deluno's ids for the library and the hand-off, the output under the test's folder, everything else as Weir sent it.</summary>
    private static async Task ReportAsync(ApplicationTestHost app, Scene scene, ProcessorHandoffItem handoff, Exchange recorded, bool createOutput = false)
    {
        var body = JsonNode.Parse(recorded.Request.Body!)!.AsObject();
        body["libraryId"] = scene.LibraryId;
        body["handoffId"] = handoff.Id;
        string Local(string path)
        {
            var relative = path.StartsWith(VmOutputRoot, StringComparison.OrdinalIgnoreCase) ? path[VmOutputRoot.Length..].TrimStart('\\') : path;
            return Path.Combine(scene.Output, Path.Combine(relative.Split('\\')));
        }

        if (body["outputPath"] is { } outputPath)
        {
            body["outputPath"] = Local(outputPath.GetValue<string>());
        }

        if (body["outputFiles"] is JsonArray files)
        {
            var local = files.Select(f => Local(f!.GetValue<string>())).ToArray();
            body["outputFiles"] = new JsonArray(local.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
            if (createOutput)
            {
                foreach (var file in local)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllBytesAsync(file, new byte[2048]);
                }
            }
        }

        var response = await app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)recorded.Reply.Status, response.StatusCode);
    }
}
