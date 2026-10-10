using System.Net.Http.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Filesystem;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1242, from the rc.30.20 golden run (Retthree, Twicefilm, Twicetwo). At 22:18:43Z the planner's output scan found the
/// first send's cleaned file with no live hand-off (the first was dismissed by "I do not want it") and raised "Processor
/// output did not match anything". One second later the resend's hand-off imported that same file, and nothing closed the
/// problem: it stayed on System, Transfers, Activity and the bell until dismissed by hand.
/// </summary>
public sealed class AnUnmatchedOutputProblemClosesWhenItsFileIsImportedTests
{
    private const string Release = "Retthree.2020.1080p.WEB-DL.x264-GOLDEN";
    private const string Client = "transmission-1";
    private const string Hash = "hash-retthree";
    private const string Unmatched = "processor-unmatched-output";

    private sealed record Scene(string LibraryId, string Output, string DownloadFolder, string Film);

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static Task<ApplicationTestHost> StartAsync()
        => ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));

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
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Retthree", releaseYear = 2020, monitored = true });
        added.EnsureSuccessStatusCode();
        var film = (await added.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;
        return new Scene(library!.Id, output, folder, film);
    }

    private static Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, DateTimeOffset? addedUtc = null)
        => Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", Client, Hash, Release, scene.DownloadFolder, "Weir", addedUtc ?? DateTimeOffset.UnixEpoch),
            CancellationToken.None);

    private static Task RecordSendAsync(ApplicationTestHost app, Scene scene)
        => app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            scene.LibraryId, "movies", "movie", scene.Film, Release, "Fixture Indexer",
            Client, "Transmission", "sent", null, clientExternalId: Hash, cancellationToken: CancellationToken.None);

    /// <summary>The cleaned file, straight in the output folder as Weir wrote it on the rig.</summary>
    private static async Task<string> WriteOutputAsync(Scene scene)
    {
        var file = Path.Combine(scene.Output, Release + ".mkv");
        await File.WriteAllBytesAsync(file, new byte[2048]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));
        return file;
    }

    /// <summary>Send, the processor starts it, "I do not want it": the hand-off is dismissed.</summary>
    private static async Task<ProcessorHandoffItem> SentAndRemovedAsync(ApplicationTestHost app, Scene scene)
    {
        await RecordSendAsync(app, scene);
        var first = await HandoffAsync(app, scene);
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Started, null, null, null, CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Dismissed, null, null, "You said you do not want it.", CancellationToken.None);
        await Task.Delay(30);
        return first;
    }

    private static async Task<IReadOnlyList<string>> OpenUnmatchedAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IMovieCatalogRepository>().GetImportRecoverySummaryAsync(CancellationToken.None))
            .RecentCases.Where(item => item.FailureKind == Unmatched).Select(item => item.Id).ToArray();

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

    [Fact]
    public async Task The_scan_does_not_raise_the_problem_for_a_file_a_live_resend_of_the_same_release_will_import()
    {
        await using var app = await StartAsync();
        var scene = await SceneAsync(app);
        await SentAndRemovedAsync(app, scene);
        await RecordSendAsync(app, scene); // the resend, asked for; its hand-off is not made yet
        await WriteOutputAsync(scene);

        await PlanAsync(app);

        Assert.Empty(await OpenUnmatchedAsync(app));
        Assert.DoesNotContain(
            await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, null, null, CancellationToken.None),
            line => line.Category == "processing.output.unmatched");
    }

    [Fact]
    public async Task A_file_nobody_asked_for_again_is_still_listed_as_unmatched()
    {
        await using var app = await StartAsync();
        var scene = await SceneAsync(app);
        await SentAndRemovedAsync(app, scene);
        await WriteOutputAsync(scene);

        await PlanAsync(app);

        Assert.Single(await OpenUnmatchedAsync(app));
    }

    [Fact]
    public async Task The_problem_closes_at_once_everywhere_when_the_resends_hand_off_imports_the_file()
    {
        await using var app = await StartAsync();
        var scene = await SceneAsync(app);
        await SentAndRemovedAsync(app, scene);
        var output = await WriteOutputAsync(scene);

        // The scan finds the file with nothing to claim it, as it did at 22:18:43Z.
        await PlanAsync(app);
        var raised = Assert.Single(await OpenUnmatchedAsync(app));

        // One second later the resend is sent and its hand-off imports the very same file.
        await RecordSendAsync(app, scene);
        var second = await HandoffAsync(app, scene, DateTimeOffset.UtcNow.AddMinutes(5));
        await Repository(app).UpdateProcessorHandoffAsync(second.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        var library = (await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single();
        var queued = await app.Services.GetRequiredService<IProcessorOutputImportQueue>().QueueAsync(library, second, output, "processor-output-watch", CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(second.Id, ProcessorHandoffStatuses.Completed, output, queued.JobId, null, CancellationToken.None);
        await CompleteAsync(app, queued.JobId!);
        File.Delete(output);

        await PlanAsync(app);

        Assert.Empty(await OpenUnmatchedAsync(app));
        // ...and its row on System goes with it, not at the next health pass.
        var health = app.Services.GetRequiredService<IHealthCheckRepository>();
        Assert.Null(await health.GetAsync(ProblemTwins.OutputIssueRowId(raised), CancellationToken.None));
    }

    [Fact]
    public async Task A_problem_for_a_file_that_is_still_not_imported_stays_open()
    {
        await using var app = await StartAsync();
        var scene = await SceneAsync(app);
        await SentAndRemovedAsync(app, scene);
        await WriteOutputAsync(scene);
        await PlanAsync(app);
        Assert.Single(await OpenUnmatchedAsync(app));

        await PlanAsync(app);

        Assert.Single(await OpenUnmatchedAsync(app));
    }

    private static async Task PlanAsync(ApplicationTestHost app)
    {
        var services = app.Services;
        var summary = new Deluno.Integrations.DownloadClients.DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
        var overview = new Deluno.Integrations.DownloadClients.DownloadTelemetryOverview(summary, [], DateTimeOffset.UtcNow);
        var telemetry = AHandOffFollowsTheDownloadItIsForTests.Stub.Create<Deluno.Integrations.DownloadClients.IDownloadClientTelemetryService>((name, _) => name switch
        {
            "GetFreshOverviewAsync" => overview,
            "RunConfiguredHealthRemediationAsync" => new Deluno.Integrations.DownloadClients.DownloadHealthRemediationReport(0, 0, 0, 0, 0, []),
            _ => throw new NotSupportedException(name)
        });
        var processors = AHandOffFollowsTheDownloadItIsForTests.Stub.Create<IProcessorConnectionService>((name, _) => name switch
        {
            "GetStatusSupportAsync" => ProcessorStatusSupport.None,
            _ => throw new NotSupportedException(name)
        });

        await new WorkPlanner(
                NullLogger<WorkPlanner>.Instance,
                services.GetRequiredService<IJobQueueRepository>(),
                services.GetRequiredService<IConfiguration>(),
                TimeProvider.System)
            .PlanImportAutomationAsync(
                services.GetRequiredService<IJobScheduler>(),
                services.GetRequiredService<IProcessorRepository>(),
                services.GetRequiredService<ILibrariesRepository>(),
                services.GetRequiredService<ILibraryAvailabilityService>(),
                telemetry,
                processors,
                services.GetRequiredService<IActivityFeedRepository>(),
                services.GetRequiredService<IMovieCatalogRepository>(),
                services.GetRequiredService<ISeriesCatalogRepository>(),
                TimeProvider.System,
                CancellationToken.None);
    }

    private sealed class ReadableVideoProbe : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo(
                Status: "succeeded", Tool: "test", Message: null, DurationSeconds: 7200, Container: "matroska", Bitrate: 12_000_000,
                VideoStreams: [new MediaVideoStreamInfo(0, "h264", "High", 1920, 1080, "yuv420p", 23.976, 10_000_000, "eng")],
                AudioStreams: [], SubtitleStreams: []));
    }
}
