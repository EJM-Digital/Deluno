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
/// #1529 (golden VM build 77be2e48f): Weir answered a re-download with its recorded "already cleaned this exact file"
/// report (events-completed-resent), naming the first run's copy, which Deluno had imported and deleted. The import queued
/// for it failed three times with missingSource and the title never got its file; before that fix the same report was
/// answered "already handled". Whatever the processor, a reported output that is not there, for a title still waiting for
/// its file, ends the hand-off once through the library's failure rule, with one Activity line. A resend is no cure (Weir
/// decides "done" by the source file, not the hand-off id).
/// </summary>
public sealed class AReportedOutputThatIsNotThereEndsTheHandOffClearlyTests
{
    private const string Release = "Gpagain3xzse11.2020.1080p.WEB-DL.x264-GOLDEN";
    private const string Client = "qbittorrent";

    private sealed record Scene(LibraryItem Library, ProcessorHandoffItem Handoff, string Output, string OutputFile);

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, string failureMode = "block", bool filmWaits = true)
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
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Gpagain3xzse11", releaseYear = 2020, monitored = true });
        added.EnsureSuccessStatusCode();
        var film = (await added.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", film, Release, "Fixture Indexer",
            Client, "qBittorrent", "sent", null, clientExternalId: $"hash-{Release}", cancellationToken: CancellationToken.None);
        if (!filmWaits)
        {
            var db = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
            await using var connection = await db.OpenConnectionAsync("movies", CancellationToken.None);
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE movie_wanted_state SET has_file = 1 WHERE movie_id = '{film}';";
            await command.ExecuteNonQueryAsync();
        }

        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "movies", Client, $"hash-{Release}", Release, folder, "Weir", DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        // The first run's cleaned file: imported and deleted, so it is not there.
        return new Scene(library, handoff, output, Path.Combine(output, "Movies", Release + ".mkv"));
    }

    /// <summary>Weir's recorded "already cleaned this exact file" report, for this release and hand-off.</summary>
    private static Task<HttpResponseMessage> AlreadyCleanedAsync(ApplicationTestHost app, Scene scene)
        => RecordedWeirReports.PostAsync(
            app.Client,
            RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-completed-resent"), scene.Library.Id, scene.Handoff.Id, scene.Output, Release + ".mkv"));

    private static async Task<int> ImportJobsAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None))
            .Count(job => job.JobType == "filesystem.import.execute");

    private static async Task<ProcessorHandoffItem> CurrentAsync(ApplicationTestHost app, Scene scene)
        => (await Repository(app).GetProcessorHandoffAsync(scene.Handoff.Id, CancellationToken.None))!;

    private static async Task<int> GaveUpLinesAsync(ApplicationTestHost app, Scene scene)
        => (await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", scene.Handoff.Id, CancellationToken.None))
            .Count(item => item.Category == "processing.output-missing.gave-up");

    [Fact]
    public async Task The_already_cleaned_report_naming_a_file_that_is_gone_fails_the_hand_off_once_and_imports_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        var response = await AlreadyCleanedAsync(app, scene);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("alreadyHandled", body);
        Assert.Equal(0, await ImportJobsAsync(app));
        var ended = await CurrentAsync(app, scene);
        Assert.Equal(ProcessorHandoffStatuses.Failed, ended.Status);
        Assert.Contains("not there", ended.FailureMessage);
        Assert.Equal(1, await GaveUpLinesAsync(app, scene));

        // The same report again changes nothing and says nothing more.
        await AlreadyCleanedAsync(app, scene);
        Assert.Equal(1, await GaveUpLinesAsync(app, scene));
        Assert.Equal(0, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_library_that_imports_the_original_when_processing_fails_releases_the_hand_off_for_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, "import-original");

        await AlreadyCleanedAsync(app, scene);

        Assert.Equal(ProcessorHandoffStatuses.Released, (await CurrentAsync(app, scene)).Status);
        Assert.Equal(1, await GaveUpLinesAsync(app, scene));
        Assert.Equal(0, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task Two_reports_at_once_for_a_file_that_is_gone_have_one_outcome()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        await Task.WhenAll(AlreadyCleanedAsync(app, scene), AlreadyCleanedAsync(app, scene), AlreadyCleanedAsync(app, scene));

        Assert.Equal(1, await GaveUpLinesAsync(app, scene));
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await CurrentAsync(app, scene)).Status);
        Assert.Equal(0, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_good_report_that_got_there_first_is_never_overwritten_by_a_missing_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        // Weir's recorded report of a finished file, with the files it names written where it says.
        var body = RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-completed"), scene.Library.Id, scene.Handoff.Id, scene.Output, Release + ".mkv");
        var written = RecordedWeirReports.OutputFilesOf(body);
        foreach (var file in written)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, new byte[2048]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));
        }

        var good = await RecordedWeirReports.PostAsync(app.Client, body);
        Assert.True(good.StatusCode == HttpStatusCode.Accepted, await good.Content.ReadAsStringAsync());
        Assert.Equal(1, await ImportJobsAsync(app));

        foreach (var file in written)
        {
            File.Delete(file);
        }

        await AlreadyCleanedAsync(app, scene);

        Assert.Equal(ProcessorHandoffStatuses.Completed, (await CurrentAsync(app, scene)).Status);
        Assert.Equal(0, await GaveUpLinesAsync(app, scene));
        Assert.Equal(1, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_missing_output_for_a_title_that_already_has_its_file_is_still_a_repeat_of_the_import_that_took_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, filmWaits: false);

        await AlreadyCleanedAsync(app, scene);

        Assert.Equal(0, await GaveUpLinesAsync(app, scene));
        Assert.NotEqual(ProcessorHandoffStatuses.Failed, (await CurrentAsync(app, scene)).Status);
    }
}
