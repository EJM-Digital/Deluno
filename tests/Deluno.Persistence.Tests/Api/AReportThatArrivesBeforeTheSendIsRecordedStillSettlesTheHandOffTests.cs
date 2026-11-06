using System.Net;
using System.Net.Http.Json;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1529 (golden VM, redownload.arrives-through-the-workflow, 6 Nov 2026): Weir had a cleaned file for the release at once,
/// so its "completed" report reached Deluno while Deluno's own send was still waiting for Weir's answer. Deluno logged the
/// answer "202 (already handled)" at 12:13:17.014Z and "Hand-off sent" at 12:13:17.019Z; when the send returned, it wrote
/// "submitted" over the hand-off the report had just settled, so the hand-off stayed submitted for good. The report's own
/// shape is Weir's recorded one (tests/fixtures/recorded/deluno events-completed); the reply to the send is Weir's recorded
/// handoff-accepted.
/// </summary>
public sealed class AReportThatArrivesBeforeTheSendIsRecordedStillSettlesTheHandOffTests
{
    private const string Release = "Gpagain2xzse11.2020.1080p.WEB-DL.x264-GOLDEN";

    private sealed class WeirClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record Scene(LibraryItem Library, ProcessorConnectionItem Connection, ProcessorHandoffItem Handoff, string Output, string OutputFile);

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Completed")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Ready")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(downloads, Release)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, Release + ".mkv"), new byte[64]);
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
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;
        var connection = await Repository(app).CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "weir", "http://weir.example/api/v1/intake/webhook/deluno", null, null, true),
            CancellationToken.None);
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "movies", "qbittorrent", $"hash-{Release}", Release, folder, "Weir", DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        var file = Path.Combine(output, Release + ".mkv");
        await File.WriteAllBytesAsync(file, new byte[2048]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));
        return new Scene(library, connection, handoff, output, file);
    }

    private static Task<HttpResponseMessage> WeirReportsAsync(ApplicationTestHost app, Scene scene)
        => RecordedWeirReports.PostAsync(
            app.Client,
            RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-completed"), scene.Library.Id, scene.Handoff.Id, scene.Output, Release + ".mkv"));

    private static async Task<int> ImportJobsAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None))
            .Count(job => job.JobType == "filesystem.import.execute");

    [Fact]
    public async Task A_report_that_arrives_while_the_send_is_still_waiting_for_its_answer_settles_the_hand_off_and_imports_the_file_once()
    {
        ApplicationTestHost? app = null;
        Scene? scene = null;
        HttpStatusCode? early = null;
        string? earlyBody = null;
        var handler = RecordedReplies.Handler(
            "weir",
            [RecordedReplies.Weir("handoff-accepted"), RecordedReplies.Weir("library-folders")],
            (request, _) =>
            {
                if (request.Method != HttpMethod.Post) return null;
                // Weir already has the cleaned file, so it reports before it has answered the send.
                var response = WeirReportsAsync(app!, scene!).GetAwaiter().GetResult();
                early = response.StatusCode;
                earlyBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return null;
            });
        await using var host = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddSingleton<IProcessorConnectionService>(new ProcessorConnectionService(new WeirClients(handler))));
        app = host;
        scene = await SceneAsync(app);

        var services = app.Services;
        var (sent, submission) = await ProcessorHandoffSubmission.SubmitAsync(
            scene.Handoff, scene.Library, scene.Connection, "Gpagain2xzse11", null,
            Repository(app),
            services.GetRequiredService<IProcessorConnectionService>(),
            services.GetRequiredService<IActivityFeedRepository>(),
            CancellationToken.None);

        Assert.True(submission.IsAccepted);
        Assert.Equal(HttpStatusCode.Accepted, early);
        Assert.DoesNotContain("alreadyHandled", earlyBody);
        // The report settled it; the send's late write does not undo that.
        Assert.Equal(ProcessorHandoffStatuses.Completed, sent.Status);
        Assert.Equal(ProcessorHandoffStatuses.Completed, (await Repository(app).GetProcessorHandoffAsync(scene.Handoff.Id, CancellationToken.None))!.Status);
        Assert.Equal(1, await ImportJobsAsync(app));

        // A genuinely repeated report for the hand-off that was imported is still already handled, and imports nothing more.
        var again = await WeirReportsAsync(app, scene);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Contains("alreadyHandled", await again.Content.ReadAsStringAsync());
        Assert.Equal(1, await ImportJobsAsync(app));
    }
}
