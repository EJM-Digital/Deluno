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

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, Exchange recorded)
    {
        var release = JsonNode.Parse(recorded.Request.Body!)!["releaseName"]!.GetValue<string>();
        return await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", "qbittorrent", $"queue-{release}", release, Path.Combine(scene.Downloads, release), "Weir"),
            CancellationToken.None);
    }

    /// <summary>Posts the recorded body: Deluno's ids for the library and the hand-off, the output under the test's folder, everything else as Weir sent it.</summary>
    private static Task<HttpResponseMessage> PostAsync(ApplicationTestHost app, Scene scene, ProcessorHandoffItem handoff, Exchange recorded, bool createOutput)
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
                    File.WriteAllBytes(file, new byte[2048]);
                }
            }
        }

        return app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
    }
}
