using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The processor routes when the request is right (coverage register, area api), against the real replies of Weir
/// 1.0.0-rc.13 (tests/fixtures/recorded/weir) served over a real loopback port, and the real reports Weir sent Deluno
/// (tests/fixtures/recorded/deluno): a connection reads the processor's real capabilities, stopping a wait sends Weir the
/// real cancel and takes its real answers, and a hand-off Weir really failed is dismissed, kept, imported or searched again.
/// </summary>
public sealed class ProcessorRoutesAgainstRecordedWeirTests
{
    private const string VmOutputRoot = @"C:\Weir\Ready\Movies";
    private const string Secret = "a-webhook-secret";

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static Task<RecordedAppServer> WeirAsync(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept, params string[] states)
        => RecordedAppServer.StartAsync("weir", states.Select(RecordedReplies.Weir), intercept);

    private static object ConnectionForm(RecordedAppServer weir)
        => new { provider = "weir", submissionUrl = weir.BaseUrl + "/api/v1/intake/webhook/deluno", authHeaderName = "X-Webhook-Secret", secret = Secret, isEnabled = true };

    private static async Task<JsonElement> ConnectAsync(ApplicationTestHost app, RecordedAppServer weir)
    {
        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", ConnectionForm(weir));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed record Scene(string LibraryId, string Downloads, string Output);

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, string processorName = "Weir")
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
            processorName,
            processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = await response.Content.ReadFromJsonAsync<LibraryItem>();
        return new Scene(library!.Id, downloads, output);
    }

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, string release, string? connectionName = "Weir")
        => await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", "qbittorrent", $"queue-{release}", release, Path.Combine(scene.Downloads, release), connectionName),
            CancellationToken.None);

    /// <summary>Posts a real Weir report of the recorded state: Deluno's ids for the library and the hand-off, everything else as Weir sent it.</summary>
    private static async Task ReportAsync(ApplicationTestHost app, Scene scene, ProcessorHandoffItem handoff, string state)
    {
        var body = JsonNode.Parse(RecordedReplies.Deluno(state).Exchanges[0].Request.Body!)!.AsObject();
        body["libraryId"] = scene.LibraryId;
        body["handoffId"] = handoff.Id;
        var response = await app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string ReleaseOf(string state)
        => JsonNode.Parse(RecordedReplies.Deluno(state).Exchanges[0].Request.Body!)!["releaseName"]!.GetValue<string>();

    [Fact]
    public async Task A_processor_connection_is_added_edited_and_removed_and_each_save_asks_the_real_processor_what_it_is()
    {
        await using var weir = await WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await ConnectAsync(app, weir);

        var id = created.GetProperty("id").GetString()!;
        Assert.StartsWith("Weir on", created.GetProperty("name").GetString(), StringComparison.Ordinal);
        var askedOnCreate = weir.Requests.Count(request => request.PathAndQuery == "/api/v1/intake/capabilities");
        Assert.True(askedOnCreate >= 1, "adding the connection asks the processor what it is");

        var edit = await app.Client.PutAsJsonAsync($"/api/integrations/processors/connections/{id}", new { provider = "weir", submissionUrl = weir.BaseUrl + "/api/v1/intake/webhook/deluno", authHeaderName = "X-Webhook-Secret", secret = "a-new-secret", isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.False((await edit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isEnabled").GetBoolean());
        Assert.True(weir.Requests.Count(request => request.PathAndQuery == "/api/v1/intake/capabilities") > askedOnCreate, "saving again asks the processor again");
        Assert.DoesNotContain(weir.Unanswered, line => line.Contains("capabilities", StringComparison.Ordinal));

        var scene = await SceneAsync(app);
        var waiting = await HandoffAsync(app, scene, "Waiting.Film.2020.1080p.WEB-DL-DELUNO", created.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/integrations/processors/connections/{id}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/integrations/processors/connections")).Replace(" ", ""));
        var after = await Repository(app).GetProcessorHandoffAsync(waiting.Id, CancellationToken.None);
        Assert.NotEqual(ProcessorHandoffStatuses.Waiting, after?.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/integrations/processors/connections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/integrations/processors/connections/no-such-connection", new { submissionUrl = weir.BaseUrl, secret = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_processor_path_mapping_is_added_used_to_read_where_Weir_says_it_put_the_output_and_removed()
    {
        await using var weir = await WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var connection = await ConnectAsync(app, weir);
        var id = connection.GetProperty("id").GetString()!;
        var scene = await SceneAsync(app, connection.GetProperty("name").GetString()!);

        // Weir names its output folder by the path on Weir's machine; Deluno reads the same folder at its own path.
        var added = await app.Client.PostAsJsonAsync($"/api/integrations/processors/connections/{id}/path-mappings", new { delunoPath = scene.Output, processorPath = VmOutputRoot, isEnabled = true });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var mappingId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal(VmOutputRoot, Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>($"/api/integrations/processors/connections/{id}/path-mappings")).EnumerateArray()).GetProperty("processorPath").GetString());

        var recorded = RecordedReplies.Deluno("events-completed").Exchanges[0];
        var release = JsonNode.Parse(recorded.Request.Body!)!["releaseName"]!.GetValue<string>();
        var handoff = await HandoffAsync(app, scene, release, connection.GetProperty("name").GetString());
        var body = JsonNode.Parse(recorded.Request.Body!)!.AsObject();
        body["libraryId"] = scene.LibraryId;
        body["handoffId"] = handoff.Id;
        foreach (var file in body["outputFiles"]!.AsArray())
        {
            var relative = file!.GetValue<string>()[VmOutputRoot.Length..].TrimStart('\\');
            var local = Path.Combine(scene.Output, Path.Combine(relative.Split('\\')));
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            await File.WriteAllBytesAsync(local, new byte[2048]);
        }

        var report = await app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.True(report.IsSuccessStatusCode, await report.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored!.Status);
        Assert.StartsWith(scene.Output, stored.OutputPath!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(stored.ImportJobId);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/integrations/processors/connections/{id}/path-mappings/{mappingId}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync($"/api/integrations/processors/connections/{id}/path-mappings")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/integrations/processors/connections/{id}/path-mappings/{mappingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync($"/api/integrations/processors/connections/{id}/path-mappings", new { delunoPath = "", processorPath = "x" })).StatusCode);
    }

    [Fact]
    public async Task Stopping_the_wait_for_a_queued_hand_off_sends_the_real_cancel_and_ends_it_but_a_processor_already_working_on_it_is_not_interrupted()
    {
        var cancelled = RecordedReplies.Weir("handoff-cancelled").Exchanges[0];
        var working = RecordedReplies.Weir("handoff-cancel-working").Exchanges[0];
        var mode = "cancelled";
        await using var weir = await WeirAsync(
            (request, _) => request.Method == HttpMethod.Delete ? (mode == "cancelled" ? cancelled : working).Reply.ToResponse() : null,
            "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var connection = await ConnectAsync(app, weir);
        var scene = await SceneAsync(app);
        var queued = await HandoffAsync(app, scene, "Queued.Film.2020.1080p.WEB-DL-DELUNO", connection.GetProperty("name").GetString());
        var busy = await HandoffAsync(app, scene, "Busy.Film.2020.1080p.WEB-DL-DELUNO", connection.GetProperty("name").GetString());

        var stopped = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{queued.Id}/stop-waiting", content: null);

        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.Contains("it was asked to drop", (await stopped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, (await Repository(app).GetProcessorHandoffAsync(queued.Id, CancellationToken.None))!.Status);
        Assert.Contains(weir.Requests, request => request.Method == "DELETE" && request.PathAndQuery.EndsWith($"/handoffs/deluno/{queued.Id}", StringComparison.Ordinal));

        mode = "working";
        var refused = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{busy.Id}/stop-waiting", content: null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("has started work", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(ProcessorHandoffStatuses.Waiting, (await Repository(app).GetProcessorHandoffAsync(busy.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Sending_the_outcome_again_tells_the_real_processor_what_became_of_the_file_and_marks_it_heard()
    {
        var delivered = RecordedReplies.Weir("outcome-delivered").Exchanges[0];
        await using var weir = await WeirAsync(RecordedAppServer.Answering("POST", "/outcome", delivered), "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var connection = await ConnectAsync(app, weir);
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, "Imported.Film.2020.1080p.WEB-DL-DELUNO", connection.GetProperty("name").GetString());
        var now = DateTimeOffset.UtcNow;
        await Repository(app).SaveProcessorOutcomeAsync(
            new ProcessorOutcomeRecord(handoff.Id, connection.GetProperty("name").GetString()!, scene.LibraryId, handoff.ReleaseName, ProcessorOutcomeRecord.Imported,
                Path.Combine(scene.Output, "Imported Film (2020).mkv"), null, now.AddMinutes(-5), ProcessorOutcomeDeliveries.Pending, 1, now.AddMinutes(-5), now.AddMinutes(-4), false, null, now.AddMinutes(-5), connection.GetProperty("id").GetString()),
            CancellationToken.None);

        var response = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/outcome/send-again", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var said = Assert.Single(weir.Requests, request => request.Method == "POST" && request.PathAndQuery.EndsWith("/outcome", StringComparison.Ordinal));
        Assert.Equal("imported", JsonNode.Parse(said.Body)!["outcome"]!.GetValue<string>());
        var stored = await Repository(app).GetProcessorOutcomeAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorOutcomeDeliveries.Delivered, stored!.Delivery);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/integrations/processors/handoffs/no-such-handoff/outcome/send-again", content: null)).StatusCode);
    }

    [Theory]
    [InlineData("events-failed-under-minimum")]
    [InlineData("events-failed-unreadable")]
    public async Task A_hand_off_Weir_really_failed_can_be_kept_waiting_on_then_imported_as_the_original(string state)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, ReleaseOf(state));
        await ReportAsync(app, scene, handoff, state);
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);

        var keep = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/keep-waiting", content: null);
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Started, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/keep-waiting", content: null)).StatusCode);

        // It ends badly again (the same real report), and now the person takes the original.
        await ReportAsync(app, scene, handoff, state);
        var original = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original", content: null);
        Assert.Equal(HttpStatusCode.Accepted, original.StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Released, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Contains("without processing", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/integrations/processors/handoffs/no-such-handoff/import-original", content: null)).StatusCode);
    }

    [Fact]
    public async Task Dismissing_a_hand_off_Weir_really_failed_keeps_the_reason_and_a_hand_off_still_with_Weir_cannot_be_dismissed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var failed = await HandoffAsync(app, scene, ReleaseOf("events-failed-under-minimum"));
        await ReportAsync(app, scene, failed, "events-failed-under-minimum");
        var waiting = await HandoffAsync(app, scene, "Still.Waiting.2020.1080p.WEB-DL-DELUNO");

        var dismissed = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{failed.Id}/dismiss", content: null);

        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(failed.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored!.Status);
        Assert.False(string.IsNullOrWhiteSpace(stored.FailureMessage), "the reason it ended stays on the record");
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsync($"/api/integrations/processors/handoffs/{waiting.Id}/dismiss", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/integrations/processors/handoffs/no-such-handoff/dismiss", content: null)).StatusCode);
    }

    [Fact]
    public async Task Giving_up_on_a_hand_off_Weir_really_failed_blocklists_the_release_ends_the_hand_off_and_searches_for_another()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var release = ReleaseOf("events-failed-unreadable");
        var handoff = await HandoffAsync(app, scene, release);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(scene.LibraryId, "movies", "movie", "movie-1", release, "Fixture", "qbittorrent", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: $"queue-{release}");
        await ReportAsync(app, scene, handoff, "events-failed-unreadable");

        var response = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", content: null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Contains("blocklisted the release and started a new search", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Contains(release, await app.Client.GetStringAsync("/api/blocked-releases"), StringComparison.Ordinal);
        Assert.True(await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type LIKE '%search%';") >= 1, "a replacement search is queued");
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", content: null)).StatusCode);
    }

    [Fact]
    public async Task The_hand_off_list_names_each_hand_off_with_its_status_and_can_be_narrowed_to_one_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var failed = await HandoffAsync(app, scene, ReleaseOf("events-failed-under-minimum"));
        await ReportAsync(app, scene, failed, "events-failed-under-minimum");
        await HandoffAsync(app, scene, "Waiting.Film.2020.1080p.WEB-DL-DELUNO");

        var all = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs");
        var narrowed = await app.Client.GetFromJsonAsync<JsonElement>($"/api/integrations/processors/handoffs?libraryId={scene.LibraryId}&take=1");
        var other = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs?libraryId=no-such-library");

        Assert.Equal(2, all.GetArrayLength());
        Assert.Equal("failed", all.EnumerateArray().Single(item => item.GetProperty("id").GetString() == failed.Id).GetProperty("status").GetString());
        Assert.Equal(1, narrowed.GetArrayLength());
        Assert.Equal(0, other.GetArrayLength());
    }

    [Fact]
    public async Task Deleting_a_stray_item_from_a_processor_output_folder_removes_exactly_that_item_and_nothing_outside_the_folder()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        // The stray item is a file Weir really produced (its name is the recorded report's) that nothing claimed.
        var strayName = Path.GetFileName(JsonNode.Parse(RecordedReplies.Deluno("events-completed").Exchanges[0].Request.Body!)!["outputFiles"]![0]!.GetValue<string>().Replace('\\', '/'));
        var stray = Path.Combine(scene.Output, strayName);
        var strayFolder = Directory.CreateDirectory(Path.Combine(scene.Output, "Stray Folder")).FullName;
        await File.WriteAllBytesAsync(stray, [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(strayFolder, "inside.mkv"), [4, 5]);
        var outside = Path.Combine(scene.Downloads, "Not.The.Output.mkv");
        await File.WriteAllBytesAsync(outside, [9]);

        var file = await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = scene.LibraryId, path = stray });
        var folder = await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = scene.LibraryId, path = strayFolder });

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(HttpStatusCode.OK, folder.StatusCode);
        Assert.False(File.Exists(stray));
        Assert.False(Directory.Exists(strayFolder));
        var refused = await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = scene.LibraryId, path = outside });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True(File.Exists(outside), "a file outside the output folder is never touched");
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = scene.LibraryId, path = stray })).StatusCode);
        Assert.Contains(strayName, await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_a_librarys_workflow_makes_it_hand_downloads_to_the_processor_so_a_real_Weir_report_is_accepted_only_after_it()
    {
        await using var weir = await WeirAsync(null, "capabilities");
        await using var app = await ApplicationTestHost.StartAsync();
        var connection = await ConnectAsync(app, weir);
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var scene = new Scene(libraryId, Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName, output);
        var release = ReleaseOf("events-failed-under-minimum");
        var handoff = await HandoffAsync(app, scene, release, connection.GetProperty("name").GetString());

        // A library that imports in the standard way does not take a processor's report.
        var body = JsonNode.Parse(RecordedReplies.Deluno("events-failed-under-minimum").Exchanges[0].Request.Body!)!.AsObject();
        body["libraryId"] = libraryId;
        body["handoffId"] = handoff.Id;
        var before = await app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, before.StatusCode);
        Assert.Contains("configured for standard import", await before.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var saved = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/workflow", new
        {
            importWorkflow = "refine-before-import",
            processorConnectionId = connection.GetProperty("id").GetString(),
            processorName = connection.GetProperty("name").GetString(),
            processorOutputPath = output,
            processorTimeoutMinutes = 2880,
            processorFailureMode = "block",
            cleanupMode = "keep-source"
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var library = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refine-before-import", library.GetProperty("importWorkflow").GetString());
        Assert.Equal(output, library.GetProperty("processorOutputPath").GetString());
        Assert.Equal(connection.GetProperty("id").GetString(), library.GetProperty("processorConnectionId").GetString());
        var after = await app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.True(after.IsSuccessStatusCode, await after.Content.ReadAsStringAsync());
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);

        var missingFolder = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/workflow", new { importWorkflow = "refine-before-import", processorOutputPath = Path.Combine(app.DataRoot, "not-there") });
        Assert.Equal(HttpStatusCode.BadRequest, missingFolder.StatusCode);
        Assert.Equal(output, (await (await app.Client.GetAsync("/api/libraries")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single().GetProperty("processorOutputPath").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/libraries/no-such-library/workflow", new { importWorkflow = "standard" })).StatusCode);
    }
}
