using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Processing;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1239: the reports Weir really sends Deluno. tests/fixtures/recorded/deluno/&lt;version&gt;/events-* were written down by a recording
/// proxy in front of the real Deluno (rc.30.30) while the real Weir (1.0.0-rc.13) reported finished, failed and gone hand-offs, and
/// they hold Deluno's own reply to each. Here each recorded body is posted, as Weir sent it, to Deluno's processor events endpoint
/// for a hand-off Deluno holds, and the answer must be the recorded answer. Only what belongs to the test setup is changed in the
/// body: Deluno's library and hand-off ids and the folder the output sits in (the golden VM's own paths).
/// </summary>
public sealed class WeirRealReportsTests
{
    private const string VmOutputRoot = @"C:\Weir\Ready\Movies";
    private const string VmTvOutputRoot = @"C:\Weir\Ready\TV";

    [Theory]
    [InlineData("events-completed", 0)]
    [InlineData("events-completed", 1)]
    [InlineData("events-completed", 2)]
    [InlineData("events-completed-resent", 0)]
    public async Task A_finished_hand_off_Weir_reports_is_accepted_and_an_import_is_queued(string state, int exchange)
    {
        var recorded = RecordedReplies.Deluno(state).Exchanges[exchange];
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, recorded);

        var response = await PostAsync(app, scene, handoff, recorded, createOutput: true);

        Assert.Equal((HttpStatusCode)recorded.Reply.Status, response.StatusCode);
        Assert.Equal(202, recorded.Reply.Status);
        var said = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("completed", said["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(said["importJobId"]?.GetValue<string>()), "The recorded 202 carried the import it queued.");
        Assert.Equal(handoff.Id, said["handoffId"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_finished_report_queues_the_import_into_the_library_that_grabbed_it_from_the_file_Weir_made_and_names_the_download_it_was_given()
    {
        var recorded = RecordedReplies.Deluno("events-completed").Exchanges[0];
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, recorded);

        var response = await PostAsync(app, scene, handoff, recorded, createOutput: true);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["importJobId"]!.GetValue<string>();
        var job = await app.Services.GetRequiredService<Deluno.Jobs.Data.IJobQueueRepository>().GetAsync(jobId, CancellationToken.None);
        var queued = System.Text.Json.JsonSerializer.Deserialize<Deluno.Filesystem.ImportExecuteRequest>(
            job!.PayloadJson!, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("filesystem.import.execute", job.JobType);
        Assert.Equal(scene.LibraryId, queued.Preview.LibraryId);
        Assert.StartsWith(scene.Output, queued.Preview.SourcePath);
        Assert.Contains("processed", queued.Preview.Tags ?? []);
        Assert.Equal(handoff.SourcePath, queued.ProcessedFromPath);
        Assert.Equal(handoff.Id, queued.HandoffId);
        Assert.Equal("auto", queued.TransferMode, ignoreCase: true);
    }

    [Theory]
    [InlineData("events-too-late", 0)]
    [InlineData("events-too-late", 1)]
    public async Task A_finished_hand_off_Weir_reports_after_the_person_stopped_waiting_is_a_409_and_queues_no_import(string state, int exchange)
    {
        var recorded = RecordedReplies.Deluno(state).Exchanges[exchange];
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, recorded);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, "stopped", CancellationToken.None);

        var response = await PostAsync(app, scene, handoff, recorded, createOutput: true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(409, recorded.Reply.Status);
        var said = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(JsonNode.Parse(recorded.Reply.Body!)!["status"]!.GetValue<string>(), said["status"]!.GetValue<string>());
        Assert.Contains("too late", said["message"]!.GetValue<string>());
        Assert.Contains("No import was queued", said["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("events-failed-under-minimum")]
    [InlineData("events-failed-source-gone")]
    [InlineData("events-failed-unreadable")]
    public async Task A_failed_hand_off_Weir_reports_is_accepted_and_nothing_is_imported(string state)
    {
        var recorded = RecordedReplies.Deluno(state).Exchanges[0];
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, recorded);

        var response = await PostAsync(app, scene, handoff, recorded, createOutput: false);

        Assert.Equal((HttpStatusCode)recorded.Reply.Status, response.StatusCode);
        var said = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("failed", said["status"]!.GetValue<string>());
        Assert.True(said["importJobId"] is null, "A failure queues no import.");
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.NotEqual(ProcessorHandoffStatuses.Completed, stored?.Status);
        if (state == "events-failed-source-gone")
        {
            // failureClass source_gone is Weir's word for a download deleted before it began; Deluno ends the hand-off as that.
            Assert.Equal(ProcessorHandoffStatuses.SourceGone, stored?.Status);
        }
    }

    // ----- Weir 1.0.0-rc.18, recorded on the golden VM for #1239 and #1262. Weir made these hand-offs itself, straight to its own
    // intake, so the real Deluno in front of the recording proxy had no row for them and answered 409 (recorded with each); the proxy
    // answered some itself, and each recording's note says which. Here the same bodies reach a Deluno that holds the hand-off.

    /// <summary>A report for a hand-off Deluno has no row for is a 409 saying so, in the words Deluno really answered (the recorded reply).</summary>
    [Theory]
    [InlineData("events-superseded")]
    [InlineData("events-completed-after-takeover")]
    [InlineData("events-completed-collision-replace-first")]
    [InlineData("events-completed-collision-replace-second")]
    [InlineData("events-completed-collision-keep-both-first")]
    [InlineData("events-completed-collision-keep-both-second")]
    [InlineData("events-completed-season-pack")]
    [InlineData("events-failed-partial-season-pack")]
    [InlineData("events-failed-partial-season-pack-corrupt")]
    public async Task A_report_Weir_sends_for_a_hand_off_Deluno_has_no_row_for_is_answered_409_as_recorded(string state)
    {
        var recorded = RecordedReplies.Deluno(state).Single;
        await using var app = await ApplicationTestHost.StartAsync();
        var tv = state.Contains("season-pack", StringComparison.Ordinal);
        var scene = tv ? await TvSceneAsync(app) : await SceneAsync(app);

        var response = await PostBodyAsync(app, scene, JsonNode.Parse(recorded.Request.Body!)!["handoffId"]!.GetValue<string>(), recorded, createOutput: true, tv ? VmTvOutputRoot : VmOutputRoot);

        Assert.Equal(409, recorded.Reply.Status);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            JsonNode.Parse(recorded.Reply.Body!)!["message"]!.GetValue<string>(),
            JsonNode.Parse(await response.Content.ReadAsStringAsync())!["message"]!.GetValue<string>());
    }

    /// <summary>
    /// A single file Weir finished is imported from where Weir put it, whatever it had to do with the name: the file a resend took over
    /// from the first send, a different file under a name the output already held with the "replace" policy (the same path) and with
    /// "keep both" (the second beside the first as <c>stem (2).ext</c>).
    /// </summary>
    [Theory]
    [InlineData("events-completed-after-takeover", ".mkv")]
    [InlineData("events-completed-collision-replace-first", ".replace.mkv")]
    [InlineData("events-completed-collision-replace-second", ".replace.mkv")]
    [InlineData("events-completed-collision-keep-both-first", ".keep-both.mkv")]
    [InlineData("events-completed-collision-keep-both-second", ".keep-both (2).mkv")]
    public async Task A_single_file_Weir_finished_under_a_taken_name_is_accepted_and_imported_from_where_it_put_it(string state, string nameEnds)
    {
        var recorded = RecordedReplies.Deluno(state).Single;
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, recorded);

        var response = await PostAsync(app, scene, handoff, recorded, createOutput: true);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var said = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("completed", said["status"]!.GetValue<string>());
        var queued = await QueuedImportAsync(app, said);
        Assert.EndsWith(nameEnds, queued.Preview.SourcePath);
        Assert.StartsWith(scene.Output, queued.Preview.SourcePath);
    }

    /// <summary>Weir's report for a season pack of two episodes: a folder and both files, imported as the one pack.</summary>
    [Fact]
    public async Task A_season_pack_Weir_finished_is_accepted_and_imported_as_one_pack_from_its_folder()
    {
        var recorded = RecordedReplies.Deluno("events-completed-season-pack").Single;
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await TvSceneAsync(app);
        var handoff = await HandoffAsync(app, scene, recorded, "tv");

        var response = await PostAsync(app, scene, handoff, recorded, createOutput: true, VmTvOutputRoot);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await QueuedImportAsync(app, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
        var folder = JsonNode.Parse(recorded.Request.Body!)!["outputPath"]!.GetValue<string>();
        Assert.Equal(Path.Combine(scene.Output, folder[(folder.LastIndexOf('\\') + 1)..]), queued.Preview.SourcePath);
    }

    /// <summary>
    /// Weir 1.0.0-rc.18's report of a send it dropped for a newer one and of a season pack with one unreadable episode: each is a failure
    /// Deluno ends as failed.
    /// </summary>
    [Theory]
    [InlineData("events-superseded")]
    [InlineData("events-failed-partial-season-pack")]
    [InlineData("events-failed-partial-season-pack-corrupt")]
    public async Task A_failure_Weir_reports_for_a_dropped_or_partly_unreadable_hand_off_is_accepted_as_failed(string state)
    {
        var recorded = RecordedReplies.Deluno(state).Single;
        await using var app = await ApplicationTestHost.StartAsync();
        var tv = state.Contains("season-pack", StringComparison.Ordinal);
        var scene = tv ? await TvSceneAsync(app) : await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, recorded, tv ? "tv" : "movies");

        var response = await PostAsync(app, scene, handoff, recorded, createOutput: true, tv ? VmTvOutputRoot : VmOutputRoot);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("failed", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["status"]!.GetValue<string>());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Failed, stored?.Status);
    }

    /// <summary>
    /// Weir's report of a release it rejected (an audio-only file under its reject policy, left in place) for a hand-off whose download
    /// Deluno cannot find in any client: Deluno will not refuse a release it cannot find, says so, and keeps the hand-off for a person.
    /// With the download present (<c>A_rejection_says_who_removed_the_download</c>) the same body is accepted and the release blocked.
    /// </summary>
    [Fact]
    public async Task A_rejection_Weir_reports_for_a_download_Deluno_cannot_find_is_a_409_that_says_so()
    {
        var recorded = RecordedReplies.Deluno("events-failed-rejected").Single;
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, recorded);

        var response = await PostAsync(app, scene, handoff, recorded, createOutput: false);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("could not refuse that release", await response.Content.ReadAsStringAsync());
        Assert.Equal(202, recorded.Reply.Status);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.NotEqual(ProcessorHandoffStatuses.Failed, stored?.Status);
    }

    /// <summary>
    /// The proxy answered one of Weir's finished reports 503 and nothing more reached it in the next 300 seconds: Weir did not send it
    /// again within five minutes, so the report is owed until Weir's next restart or Deluno's own reading of Weir's status. Deluno
    /// takes the body that was sent once and, if it is sent again, answers the second as already handled.
    /// </summary>
    [Fact]
    public async Task A_completed_report_Deluno_did_not_answer_is_imported_once_whenever_it_is_sent_again()
    {
        var recording = RecordedReplies.Deluno("events-completed-answered-503-not-repeated");
        Assert.Single(recording.Exchanges);
        Assert.Equal(503, recording.Exchanges[0].Reply.Status);
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, recording.Exchanges[0]);

        var first = await PostAsync(app, scene, handoff, recording.Exchanges[0], createOutput: true);
        var second = await PostAsync(app, scene, handoff, recording.Exchanges[0], createOutput: false);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Contains("alreadyHandled", await second.Content.ReadAsStringAsync());
        var jobs = await app.Services.GetRequiredService<Deluno.Jobs.Data.IJobQueueRepository>().ListAsync(50, CancellationToken.None);
        Assert.Single(jobs, job => job.JobType == "filesystem.import.execute");
    }

    /// <summary>
    /// Weir does not send a report again that Deluno refused for its credential (the proxy answered once and nothing more arrived in the
    /// 130 seconds that follow). The refusal Weir was given is the one Deluno really gives.
    /// </summary>
    [Theory]
    [InlineData("events-answered-401-not-repeated", "reply-no-sign-in", 401)]
    [InlineData("events-answered-403-not-repeated", "reply-key-without-imports-scope", 403)]
    public void A_report_Deluno_refuses_for_its_credential_is_not_sent_again_and_the_refusal_is_Delunos_own(string state, string refusal, int status)
    {
        var recording = RecordedReplies.Deluno(state);
        var delunos = RecordedReplies.Deluno(refusal).Exchanges.First(e => e.Request.Method == "POST" && e.Request.Path.EndsWith("/outcome/send-again", StringComparison.Ordinal));

        Assert.Single(recording.Exchanges);
        Assert.Equal(status, recording.Exchanges[0].Reply.Status);
        Assert.Equal(status, delunos.Reply.Status);
    }

    private static async Task<Deluno.Filesystem.ImportExecuteRequest> QueuedImportAsync(ApplicationTestHost app, JsonNode said)
    {
        var job = await app.Services.GetRequiredService<Deluno.Jobs.Data.IJobQueueRepository>().GetAsync(said["importJobId"]!.GetValue<string>(), CancellationToken.None);
        return System.Text.Json.JsonSerializer.Deserialize<Deluno.Filesystem.ImportExecuteRequest>(
            job!.PayloadJson!, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static async Task<Scene> TvSceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "TvDownloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "TvRefined")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Shows",
            mediaType = "tv",
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

    private sealed record Scene(string LibraryId, string Downloads, string Output);

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

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

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, Exchange recorded, string mediaType = "movies")
    {
        var release = JsonNode.Parse(recorded.Request.Body!)!["releaseName"]!.GetValue<string>();
        return await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, mediaType, "qbittorrent", $"queue-{release}", release, Path.Combine(scene.Downloads, release), "Weir"),
            CancellationToken.None);
    }

    /// <summary>Posts the recorded body: Deluno's ids for the library and the hand-off, the output under the test's folder, everything else as Weir sent it.</summary>
    private static Task<HttpResponseMessage> PostAsync(ApplicationTestHost app, Scene scene, ProcessorHandoffItem handoff, Exchange recorded, bool createOutput, string vmOutputRoot = VmOutputRoot)
        => PostBodyAsync(app, scene, handoff.Id, recorded, createOutput, vmOutputRoot);

    private static Task<HttpResponseMessage> PostBodyAsync(ApplicationTestHost app, Scene scene, string handoffId, Exchange recorded, bool createOutput, string vmOutputRoot = VmOutputRoot)
    {
        var body = JsonNode.Parse(recorded.Request.Body!)!.AsObject();
        body["libraryId"] = scene.LibraryId;
        body["handoffId"] = handoffId;
        string Local(string path)
        {
            var relative = path.StartsWith(vmOutputRoot, StringComparison.OrdinalIgnoreCase) ? path[vmOutputRoot.Length..].TrimStart('\\') : path;
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
                    File.WriteAllBytes(file, new byte[2048]);
                }
            }
        }

        return app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
    }
}
