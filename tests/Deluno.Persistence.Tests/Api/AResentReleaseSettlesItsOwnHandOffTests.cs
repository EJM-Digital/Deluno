using System.Net;
using System.Net.Http.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1226: a release removed with "I don't want it" and sent again to the SAME client is a new hand-off for the same
/// download. Its own report, or the import that used its output, settles it; the output is never imported twice; and
/// what Activity says about the two sends is one story.
///
/// <para>#1239: a report Weir really sent (tests/fixtures/recorded/deluno/*/events-*) is posted from its recording wherever one matches the case; a case with no real report stays
/// inline on purpose, and tests/fixtures/recorded/MISSING.md lists it with the Weir state that would produce it.</para>
/// </summary>
public sealed class AResentReleaseSettlesItsOwnHandOffTests
{
    private const string Release = "Retwice.2020.1080p.BluRay.x264-HD4U";
    private const string Client = "transmission-1";

    private sealed record Scene(string LibraryId, string Output, string DownloadFolder, string Film);

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
        var library = await response.Content.ReadFromJsonAsync<LibraryItem>();
        var created = await Repository(app).CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "generic-webhook", "http://127.0.0.1:9/hook", null, null, true),
            CancellationToken.None);
        await LegacyConnectionNames.SetAsync(app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(), "processor_connections", created.Id, "Weir");
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Retwice", releaseYear = 2020, monitored = true });
        added.EnsureSuccessStatusCode();
        var film = (await added.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;
        return new Scene(library!.Id, output, folder, film);
    }

    private static Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, DateTimeOffset? addedUtc = null)
        => Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", Client, "hash-retwice", Release, scene.DownloadFolder, "Weir", addedUtc ?? DateTimeOffset.UnixEpoch),
            CancellationToken.None);

    private static async Task RecordSendAsync(ApplicationTestHost app, Scene scene)
        => await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            scene.LibraryId, "movies", "movie", scene.Film, Release, "Fixture Indexer",
            Client, "Transmission", "sent", null, clientExternalId: "hash-retwice", cancellationToken: CancellationToken.None);

    private static async Task<string> WriteOutputAsync(Scene scene)
    {
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, Release)).FullName;
        var file = Path.Combine(folder, Release + ".mkv");
        await File.WriteAllBytesAsync(file, new byte[2048]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));
        return file;
    }

    /// <summary>
    /// Weir's recorded report of a finished release folder (events-completed, #1239) for this test's release and hand-off: the folder
    /// named for the release in its output folder, with the film inside. <paramref name="outputPath"/> is the file the test wrote there.
    /// </summary>
    private static Task<HttpResponseMessage> ReportAsync(ApplicationTestHost app, Scene scene, string handoffId, string status, string outputPath)
    {
        Assert.Equal("completed", status);
        var body = RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-completed", 1), scene.LibraryId, handoffId, scene.Output, Release);
        Assert.Equal([outputPath], RecordedWeirReports.OutputFilesOf(body));
        return RecordedWeirReports.PostAsync(app.Client, body);
    }

    private static async Task<IReadOnlyList<JobQueueItem>> ImportJobsAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None))
            .Where(job => job.JobType == "filesystem.import.execute").ToArray();

    private static async Task CompleteAsync(ApplicationTestHost app, string jobId)
    {
        var db = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        await using var connection = await db.OpenConnectionAsync("jobs", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE job_queue SET status = 'completed', completed_utc = @now WHERE id = @id;";
        foreach (var (name, value) in new[] { ("@id", jobId), ("@now", DateTimeOffset.UtcNow.ToString("O")) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<ActivityEventItem>> ActivityAsync(ApplicationTestHost app, string handoffId)
        => await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, "processor-handoff", handoffId, CancellationToken.None);

    /// <summary>Send, "I don't want it", resend to the same client: the first id is replaced by a new one.</summary>
    private static async Task<(ProcessorHandoffItem First, ProcessorHandoffItem Second)> SentRemovedAndSentAgainAsync(ApplicationTestHost app, Scene scene)
    {
        await RecordSendAsync(app, scene);
        var first = await HandoffAsync(app, scene);
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Dismissed, null, null, "You said you do not want it.", CancellationToken.None);
        await Task.Delay(30);
        await RecordSendAsync(app, scene);
        var second = await HandoffAsync(app, scene, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.NotEqual(first.Id, second.Id);
        await Repository(app).UpdateProcessorHandoffAsync(second.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        return (first, second);
    }

    [Fact]
    public async Task The_new_send_settles_whichever_report_comes_first_and_the_output_is_imported_once()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));
        var scene = await SceneAsync(app);
        var (first, second) = await SentRemovedAndSentAgainAsync(app, scene);
        var output = await WriteOutputAsync(scene);

        // The first send's late report carries the output the live send is waiting for, so the live send takes it (#1232):
        // one import, and the new send's own report after it is answered as already handled.
        var late = await ReportAsync(app, scene, first.Id, "completed", output);
        Assert.Equal(HttpStatusCode.Accepted, late.StatusCode);
        var settled = (await Repository(app).GetProcessorHandoffAsync(second.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Completed, settled.Status);
        var job = Assert.Single(await ImportJobsAsync(app));
        Assert.Equal(job.Id, settled.ImportJobId);
        var own = await ReportAsync(app, scene, second.Id, "completed", output);
        Assert.Equal(HttpStatusCode.Accepted, own.StatusCode);
        Assert.Single(await ImportJobsAsync(app));

        // The import runs and finishes; the processor's report again, or its output found again, imports nothing more.
        await CompleteAsync(app, job.Id);
        File.Delete(output);
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(app, scene, second.Id, "completed", output)).StatusCode);
        Assert.Single(await ImportJobsAsync(app));
    }

    [Fact]
    public async Task An_output_already_imported_is_never_queued_a_second_time_whoever_finds_it()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));
        var scene = await SceneAsync(app);
        var (_, second) = await SentRemovedAndSentAgainAsync(app, scene);
        var output = await WriteOutputAsync(scene);
        var library = (await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single();
        var queue = app.Services.GetRequiredService<IProcessorOutputImportQueue>();

        var first = await queue.QueueAsync(library, second, output, "processor", CancellationToken.None);
        Assert.NotNull(first.JobId);
        await CompleteAsync(app, first.JobId!);

        // The file is still there, not written since: the same import.
        var again = await queue.QueueAsync(library, second, output, "processor-watch", CancellationToken.None);
        Assert.True(again.AlreadyImported);
        Assert.Null(again.JobId);

        // And once the import took it away, there is nothing left to import either.
        File.Delete(output);
        Assert.True((await queue.QueueAsync(library, second, output, "processor-output-watch", CancellationToken.None)).AlreadyImported);
        Assert.Single(await ImportJobsAsync(app));

        // A new file the processor writes at the same path later is a new import.
        await File.WriteAllBytesAsync(output, new byte[4096]);
        File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddMinutes(5));
        Assert.False((await queue.QueueAsync(library, second, output, "processor", CancellationToken.None)).AlreadyImported);
    }

    [Fact]
    public async Task An_import_queued_under_the_replaced_id_settles_the_hand_off_that_replaced_it()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));
        var scene = await SceneAsync(app);
        var (first, second) = await SentRemovedAndSentAgainAsync(app, scene);
        var output = await WriteOutputAsync(scene);
        var library = (await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single();

        // Weir answered, Deluno imported the file - but the import carries the first send's id.
        var queued = await app.Services.GetRequiredService<IProcessorOutputImportQueue>().QueueAsync(library, first, output, "processor-watch", CancellationToken.None);
        var job = (await app.Services.GetRequiredService<IJobQueueRepository>().GetAsync(queued.JobId!, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Submitted, (await Repository(app).GetProcessorHandoffAsync(second.Id, CancellationToken.None))?.Status);

        using var scope = app.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().OfType<FilesystemImportExecuteJobHandler>().Single();
        await handler.HandleAsync(job, CancellationToken.None);

        var settled = (await Repository(app).GetProcessorHandoffAsync(second.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Completed, settled.Status);
        Assert.Equal(job.Id, settled.ImportJobId);
        Assert.Contains(await ActivityAsync(app, second.Id), line => line.Category == "processing.handoff.settled-by-import");
    }

    [Fact]
    public async Task What_Activity_says_about_the_two_sends_is_one_story_and_never_will_not_import_beside_imported()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var (first, second) = await SentRemovedAndSentAgainAsync(app, scene);

        // Deluno owes Weir "will not import" for the send it removed, and says it after the resend exists.
        var record = await ProcessorOutcomeDelivery.RecordAndSendAsync(
            Repository(app),
            app.Services.GetRequiredService<IProcessorConnectionService>(),
            app.Services.GetRequiredService<IActivityFeedRepository>(),
            first,
            ProcessorHandoffOutcome.NotImported,
            null,
            "Deluno removed it, so it will not import it.",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(ProcessorOutcomeDeliveries.Replaced, record?.Delivery);
        var lines = await ActivityAsync(app, first.Id);
        var said = Assert.Single(lines, line => line.Category == "processing.outcome-superseded");
        Assert.DoesNotContain("will not import", said.Message);
        Assert.DoesNotContain("could not tell", said.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lines, line => line.Category is "processing.outcome-reported" or "processing.outcome-not-reported" or "processing.outcome-delayed");
        Assert.Equal(ProcessorHandoffStatuses.Submitted, (await Repository(app).GetProcessorHandoffAsync(second.Id, CancellationToken.None))?.Status);
    }

    // ---- #1232: the processor had not started the first send when it was removed --------------------

    [Fact]
    public async Task The_first_sends_late_completed_report_with_its_output_is_applied_to_the_waiting_resend_and_imports_once()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));
        var scene = await SceneAsync(app);
        var (first, second) = await SentRemovedAndSentAgainAsync(app, scene);
        var output = await WriteOutputAsync(scene);

        // The sequence the golden run logged: the processor finishes the first send and reports it with its output.
        var late = await ReportAsync(app, scene, first.Id, "completed", output);
        Assert.Equal(HttpStatusCode.Accepted, late.StatusCode);

        var settled = (await Repository(app).GetProcessorHandoffAsync(second.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Completed, settled.Status);
        Assert.Equal(output, settled.OutputPath);
        var job = Assert.Single(await ImportJobsAsync(app));
        Assert.Equal(job.Id, settled.ImportJobId);
        Assert.NotNull((await Repository(app).FindSupersededProcessorHandoffAsync(scene.LibraryId, first.Id, CancellationToken.None))?.SettledUtc);

        // The story of the resend says what happened to it, and does not say nothing changed.
        var lines = await ActivityAsync(app, second.Id);
        Assert.Contains(lines, line => line.Category == "processing.superseded-adopted");
        Assert.DoesNotContain(lines, line => line.Category == "processing.superseded-settled");

        // The resend's own report, and the first send's again, import nothing more.
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(app, scene, second.Id, "completed", output)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(app, scene, first.Id, "completed", output)).StatusCode);
        Assert.Single(await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_completed_answer_with_no_output_for_a_source_already_cleaned_is_matched_to_that_output_and_never_strands_the_resend()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));
        var scene = await SceneAsync(app);
        var (first, second) = await SentRemovedAndSentAgainAsync(app, scene);
        var output = await WriteOutputAsync(scene);

        // The processor answers the resend "completed" with no output path, because it cleaned the source for the first send.
        await WatchAsync(app, new ProcessorHandoffStatusResult(ProcessorAnswer.Answered, ProcessorHandoffStates.Completed, LastChangedUtc: DateTimeOffset.UtcNow));

        var settled = (await Repository(app).GetProcessorHandoffAsync(second.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Completed, settled.Status);
        Assert.Equal(output, settled.OutputPath);
        var job = Assert.Single(await ImportJobsAsync(app));
        Assert.Equal(job.Id, settled.ImportJobId);

        // The first send's report, arriving after, finds the resend done: nothing is imported again.
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(app, scene, first.Id, "completed", output)).StatusCode);
        Assert.Single(await ImportJobsAsync(app));
        Assert.Equal(job.Id, (await Repository(app).GetProcessorHandoffAsync(second.Id, CancellationToken.None))?.ImportJobId);
    }

    [Fact]
    public async Task A_completed_answer_with_no_output_is_left_alone_when_the_send_replaced_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));
        var scene = await SceneAsync(app);
        await RecordSendAsync(app, scene);
        var only = await HandoffAsync(app, scene);
        await Repository(app).UpdateProcessorHandoffAsync(only.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        await WriteOutputAsync(scene);

        await WatchAsync(app, new ProcessorHandoffStatusResult(ProcessorAnswer.Answered, ProcessorHandoffStates.Completed, LastChangedUtc: DateTimeOffset.UtcNow));

        Assert.Equal(ProcessorHandoffStatuses.Submitted, (await Repository(app).GetProcessorHandoffAsync(only.Id, CancellationToken.None))?.Status);
        Assert.Empty(await ImportJobsAsync(app));
    }

    private static async Task WatchAsync(ApplicationTestHost app, ProcessorHandoffStatusResult status)
    {
        var processors = AHandOffFollowsTheDownloadItIsForTests.Stub.Create<IProcessorConnectionService>((name, _) => name switch
        {
            "GetStatusSupportAsync" => new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null),
            "GetHandoffStatusAsync" => status,
            _ => throw new NotSupportedException(name)
        });
        var libraries = await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None);
        await new Deluno.Worker.Services.ProcessorHandoffWatcher(
                Repository(app),
                processors,
                app.Services.GetRequiredService<IActivityFeedRepository>(),
                app.Services.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>(),
                app.Services.GetRequiredService<Deluno.Series.Data.ISeriesCatalogRepository>(),
                app.Services.GetRequiredService<IProcessorOutputImportQueue>())
            .WatchAsync(libraries, DateTimeOffset.UtcNow, CancellationToken.None, libraries);
    }

    [Fact]
    public void An_imported_line_never_repeats_the_processors_note_from_another_send_and_never_ends_in_two_full_stops()
    {
        // The line the golden run read: the processor's reply to "it has" carried its stored note from the first send.
        var record = new ProcessorOutcomeRecord(
            "h-2", "Weir", "lib", "Retthree.mkv", ProcessorOutcomeRecord.Imported, "/ready/Retthree.mkv", null,
            DateTimeOffset.UtcNow, ProcessorOutcomeDeliveries.Delivered, 1, null, null, false,
            "Weir recorded that Deluno imported the file. Deluno will not import this file: Deluno removed Retthree.mkv, so it will not import it. Weir kept its copy in the hand-back folder.",
            DateTimeOffset.UtcNow, null);
        var imported = ProcessorOutcomeDelivery.Describe(record).Message;
        Assert.Contains("Weir recorded that Deluno imported the file.", imported);
        Assert.DoesNotContain("will not import", imported);

        var removed = record with
        {
            Outcome = ProcessorOutcomeRecord.NotImported,
            ImportedPath = null,
            Reason = "Deluno removed Retthree.mkv, so it will not import it.",
            ProcessorSaid = "Weir recorded that the file will not be imported, and kept its copy."
        };
        var notImported = ProcessorOutcomeDelivery.Describe(removed).Message;
        Assert.DoesNotContain("..", notImported);
        Assert.Contains("will not import", notImported);
        Assert.DoesNotContain("has Retthree", notImported);
    }

    /// <summary>Every file reads as a two-hour 1080p H.264 film, so the test is about the hand-off, not the installed ffprobe.</summary>
    private sealed class ReadableVideoProbe : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo(
                Status: "succeeded", Tool: "test", Message: null, DurationSeconds: 7200, Container: "matroska", Bitrate: 12_000_000,
                VideoStreams: [new MediaVideoStreamInfo(0, "h264", "High", 1920, 1080, "yuv420p", 23.976, 10_000_000, "eng")],
                AudioStreams: [], SubtitleStreams: []));
    }
}
