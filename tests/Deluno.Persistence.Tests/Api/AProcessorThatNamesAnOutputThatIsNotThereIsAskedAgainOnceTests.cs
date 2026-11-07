using System.Net;
using System.Net.Http.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1529 (golden VM build 77be2e48f): a re-download's new hand-off was sent, and the processor reported "completed" 5 ms
/// later naming the cleaned output of the FIRST hand-off, which Deluno had imported and deleted. The import queued for it
/// failed three times with missingSource and the title never got its file. Whatever the processor, a completed report
/// whose output is not there while the original download still is, is answered by asking the processor once more under a
/// new hand-off; a second report that is also missing ends the hand-off by the library's failure rule. Never a loop.
/// The report shape is Weir's recorded one (tests/fixtures/recorded events-completed).
/// </summary>
public sealed class AProcessorThatNamesAnOutputThatIsNotThereIsAskedAgainOnceTests
{
    private const string Release = "Gpagain3xzse11.2020.1080p.WEB-DL.x264-GOLDEN";

    private sealed record Scene(LibraryItem Library, ProcessorHandoffItem Handoff, string Output, string OutputFile);

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, string failureMode = "block")
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
            processorOutputPath = output,
            processorFailureMode = failureMode
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "movies", "qbittorrent", $"hash-{Release}", Release, folder, "Weir", DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        // Weir's cleaned file of the first hand-off: imported and deleted, so it is not there.
        return new Scene(library, handoff, output, Path.Combine(output, Release + ".mkv"));
    }

    private static Task<HttpResponseMessage> WeirReportsAsync(ApplicationTestHost app, Scene scene, string handoffId)
        => RecordedWeirReports.PostAsync(
            app.Client,
            RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-completed"), scene.Library.Id, handoffId, scene.Output, Release + ".mkv"));

    private static async Task<int> ImportJobsAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None))
            .Count(job => job.JobType == "filesystem.import.execute");

    private static async Task<ProcessorHandoffItem> CurrentAsync(ApplicationTestHost app, Scene scene)
        => (await Repository(app).FindProcessorHandoffAsync(scene.Library.Id, null, scene.Handoff.SourcePath, CancellationToken.None))!;

    [Fact]
    public async Task A_completed_report_for_a_file_that_is_not_there_asks_the_processor_again_once_and_the_second_answer_is_imported()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        var first = await WeirReportsAsync(app, scene, scene.Handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(0, await ImportJobsAsync(app));
        var again = await CurrentAsync(app, scene);
        Assert.NotEqual(scene.Handoff.Id, again.Id);
        Assert.Equal(ProcessorHandoffStatuses.Waiting, again.Status);
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", again.Id, CancellationToken.None);
        var line = Assert.Single(activity, item => item.Category == "processing.output-missing.resubmitted");
        Assert.Contains("not there", line.Message);
        Assert.Contains("asked", line.Message);

        // The second answer names a file that exists.
        await File.WriteAllBytesAsync(scene.OutputFile, new byte[2048]);
        File.SetLastWriteTimeUtc(scene.OutputFile, DateTime.UtcNow.AddMinutes(-30));
        await Repository(app).UpdateProcessorHandoffAsync(again.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        var second = await WeirReportsAsync(app, scene, again.Id);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(1, await ImportJobsAsync(app));
        Assert.Equal(ProcessorHandoffStatuses.Completed, (await CurrentAsync(app, scene)).Status);
    }

    [Fact]
    public async Task A_second_report_that_is_also_missing_ends_the_hand_off_once_and_never_asks_a_third_time()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        await WeirReportsAsync(app, scene, scene.Handoff.Id);
        var again = await CurrentAsync(app, scene);
        await Repository(app).UpdateProcessorHandoffAsync(again.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);

        var second = await WeirReportsAsync(app, scene, again.Id);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(0, await ImportJobsAsync(app));
        var ended = await CurrentAsync(app, scene);
        Assert.Equal(again.Id, ended.Id);
        Assert.Equal(ProcessorHandoffStatuses.Failed, ended.Status);
        Assert.Contains("not there", ended.FailureMessage);
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", ended.Id, CancellationToken.None);
        Assert.Single(activity, item => item.Category == "processing.output-missing.gave-up");
        Assert.Single(activity, item => item.Category == "processing.output-missing.resubmitted");
    }

    [Fact]
    public async Task A_library_that_imports_the_original_when_processing_fails_releases_it_after_the_second_missing_report()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, "import-original");
        await WeirReportsAsync(app, scene, scene.Handoff.Id);
        var again = await CurrentAsync(app, scene);
        await Repository(app).UpdateProcessorHandoffAsync(again.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);

        await WeirReportsAsync(app, scene, again.Id);

        Assert.Equal(ProcessorHandoffStatuses.Released, (await CurrentAsync(app, scene)).Status);
        Assert.Equal(0, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_missing_output_is_not_asked_again_when_the_original_download_is_gone_too()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        Directory.Delete(scene.Handoff.SourcePath, recursive: true);

        var response = await WeirReportsAsync(app, scene, scene.Handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(scene.Handoff.Id, (await CurrentAsync(app, scene)).Id);
    }
}
