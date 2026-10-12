using Deluno.Filesystem;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using static Deluno.Persistence.Tests.Api.AHandOffFollowsTheDownloadItIsForTests;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1253 (scenario suite, rc.30.23): Try again on a failed hand-off only marked it waiting, and the planner sent it on its
/// next pass, 27 to 47 seconds later. The person pressed a button, so the send starts at once, with the planner's own rules
/// and checks, and never twice. The processor's replies are Weir's recorded ones (tests/fixtures/recorded/weir).
/// </summary>
public sealed class TryAgainSendsTheHandOffAtOnceTests
{
    private const string Release = "Nosferatu.1922.1080p.BluRay.x264-HD4U";
    private const string Client = "deluge-1";

    private sealed class WeirClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A host whose processor answers with Weir's recorded reply to a hand-off, counting the hand-offs it was sent.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public ApplicationTestHost App { get; set; } = null!;
        public int Sent;
        public ManualResetEventSlim Reached { get; } = new();
        public ManualResetEventSlim Go { get; } = new(initialState: true);
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(string recordedState, bool holdTheSend = false)
    {
        var rig = new Rig();
        if (holdTheSend) rig.Go.Reset();
        var handler = RecordedReplies.Handler(
            "weir",
            [RecordedReplies.Weir(recordedState), RecordedReplies.Weir("library-folders")],
            (request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    Interlocked.Increment(ref rig.Sent);
                    rig.Reached.Set();
                    rig.Go.Wait(TimeSpan.FromSeconds(30));
                }

                return null;
            });
        rig.App = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddSingleton<IProcessorConnectionService>(new ProcessorConnectionService(new WeirClients(handler))));
        return rig;
    }

    private sealed record Scene(string LibraryId, string Downloads, string DownloadFolder);

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
            new CreateProcessorConnectionRequest("Weir", "weir", "http://weir.example/api/v1/intake/webhook/deluno", null, null, true),
            CancellationToken.None);
        await LegacyConnectionNames.SetAsync(app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(), "processor_connections", created.Id, "Weir");
        return new Scene(library!.Id, downloads, folder);
    }

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<ProcessorHandoffItem> FailedHandoffAsync(ApplicationTestHost app, Scene scene)
    {
        var made = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", Client, $"hash-{Release}", Release, scene.DownloadFolder, "Weir", DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        return (await Repository(app).UpdateProcessorHandoffAsync(made.Id, ProcessorHandoffStatuses.Failed, null, null, "Weir could not finish it.", CancellationToken.None))!;
    }

    private static Task<HttpResponseMessage> TryAgainAsync(ApplicationTestHost app, ProcessorHandoffItem handoff)
        => app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/retry", null);

    private static DownloadQueueItem Item(Scene scene)
        => new(
            $"hash-{Release}", Client, "Deluge", "deluge", "movies", "Nosferatu 1922", Release,
            "deluno-movies", DownloadQueueStatuses.Completed, 100, 0, 0, 100, 100, 0, "Fixture Indexer", null,
            DateTimeOffset.UtcNow.AddHours(-2), scene.DownloadFolder, scene.LibraryId);

    /// <summary>One real planner pass; what it sends goes to a stand-in that only counts.</summary>
    private static async Task PlanAsync(ApplicationTestHost app, Scene scene, List<ProcessorHandoffItem> plannerSent)
    {
        var services = app.Services;
        var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
        var overview = new DownloadTelemetryOverview(
            summary,
            [new DownloadClientTelemetrySnapshot(
                Client, "Deluge", "deluge", "http://127.0.0.1:8112", "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "password"),
                summary, [Item(scene)], [], DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow);
        var telemetry = Stub.Create<IDownloadClientTelemetryService>((name, _) => name switch
        {
            "GetFreshOverviewAsync" => overview,
            "RunConfiguredHealthRemediationAsync" => new DownloadHealthRemediationReport(0, 0, 0, 0, 0, []),
            _ => throw new NotSupportedException(name)
        });
        var processors = Stub.Create<IProcessorConnectionService>((name, args) =>
        {
            switch (name)
            {
                case "SubmitAsync":
                    plannerSent.Add((ProcessorHandoffItem)args[1]!);
                    return new ProcessorSubmissionResult(true, ProcessorHandoffStatuses.Submitted, "Accepted.", 202);
                case "GetStatusSupportAsync":
                    return ProcessorStatusSupport.None;
                default:
                    throw new NotSupportedException(name);
            }
        });

        await new WorkPlanner(
                new Quiet(),
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

    private sealed class Quiet : Microsoft.Extensions.Logging.ILogger<WorkPlanner>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) throw new InvalidOperationException(formatter(state, exception), exception);
        }
    }

    [Fact]
    public async Task Try_again_sends_the_hand_off_to_the_processor_before_the_press_is_answered()
    {
        await using var rig = await StartAsync("handoff-accepted");
        var app = rig.App;
        var scene = await SceneAsync(app);
        var handoff = await FailedHandoffAsync(app, scene);

        var response = await TryAgainAsync(app, handoff);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, rig.Sent);
        var stored = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Submitted, stored.Status);
        Assert.Null(stored.FailureMessage);
        var lines = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", handoff.Id, CancellationToken.None);
        Assert.Contains(lines, line => line.Category == "processing.retry-requested");
        Assert.Contains(lines, line => line.Category == "processing.submitted");
    }

    [Fact]
    public async Task The_planner_does_not_send_a_hand_off_Try_again_has_already_sent()
    {
        await using var rig = await StartAsync("handoff-accepted");
        var app = rig.App;
        var scene = await SceneAsync(app);
        var handoff = await FailedHandoffAsync(app, scene);
        Assert.Equal(HttpStatusCode.Accepted, (await TryAgainAsync(app, handoff)).StatusCode);
        var plannerSent = new List<ProcessorHandoffItem>();

        await PlanAsync(app, scene, plannerSent);
        await PlanAsync(app, scene, plannerSent);

        Assert.Empty(plannerSent);
        Assert.Equal(1, rig.Sent);
    }

    [Fact]
    public async Task A_planner_pass_that_runs_while_Try_again_is_sending_skips_the_hand_off()
    {
        await using var rig = await StartAsync("handoff-accepted", holdTheSend: true);
        var app = rig.App;
        var scene = await SceneAsync(app);
        var handoff = await FailedHandoffAsync(app, scene);

        var press = TryAgainAsync(app, handoff);
        Assert.True(await Task.Run(() => rig.Reached.Wait(TimeSpan.FromSeconds(20))), "the send reached the processor");
        var plannerSent = new List<ProcessorHandoffItem>();
        await PlanAsync(app, scene, plannerSent); // a pass while the press is still talking to the processor
        rig.Go.Set();
        var response = await press;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(plannerSent);
        Assert.Equal(1, rig.Sent);
        await PlanAsync(app, scene, plannerSent);
        Assert.Empty(plannerSent);
        Assert.Equal(1, rig.Sent);
    }

    [Theory]
    [InlineData("handoff-refused-not-watched", "No Weir workflow watches the folder")]
    [InlineData("handoff-refused-wrong-secret", "Invalid or missing X-Webhook-Secret")]
    public async Task A_retry_the_processor_refuses_reads_as_refused_and_is_not_sent_again(string recordedState, string weirsReason)
    {
        await using var rig = await StartAsync(recordedState);
        var app = rig.App;
        var scene = await SceneAsync(app);
        var handoff = await FailedHandoffAsync(app, scene);

        var response = await TryAgainAsync(app, handoff);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(weirsReason, await response.Content.ReadAsStringAsync());
        var stored = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Failed, stored.Status);
        Assert.Contains(weirsReason, stored.FailureMessage);

        var plannerSent = new List<ProcessorHandoffItem>();
        await PlanAsync(app, scene, plannerSent);
        Assert.Empty(plannerSent);
        Assert.Equal(1, rig.Sent);
    }

    [Fact]
    public async Task A_retry_for_a_download_whose_files_are_gone_is_refused_without_asking_the_processor()
    {
        await using var rig = await StartAsync("handoff-accepted");
        var app = rig.App;
        var scene = await SceneAsync(app);
        var handoff = await FailedHandoffAsync(app, scene);
        Directory.Delete(scene.Downloads, recursive: true);

        var response = await TryAgainAsync(app, handoff);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, rig.Sent);
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task A_retry_for_a_download_already_imported_is_refused_without_asking_the_processor()
    {
        await using var rig = await StartAsync("handoff-accepted");
        var app = rig.App;
        var scene = await SceneAsync(app);
        var handoff = await FailedHandoffAsync(app, scene);
        var id = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            scene.LibraryId, "movies", "movie", "movie-nosferatu", Release, "Fixture Indexer",
            Client, "Deluge", "sent", null, clientExternalId: $"hash-{Release}", cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordImportOutcomeAsync(
            id, "imported", Path.Combine(app.DataRoot, "Films", "Nosferatu (1922)", "Nosferatu (1922).mkv"), null, null, CancellationToken.None);

        var response = await TryAgainAsync(app, handoff);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, rig.Sent);
    }

    private static async Task<IReadOnlyList<ActivityEventItem>> LinesAsync(ApplicationTestHost app, string handoffId)
        => await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", handoffId, CancellationToken.None);

    [Fact]
    public async Task A_retry_Deluno_refuses_says_why_in_Activity_and_never_that_it_will_try_again()
    {
        await using var rig = await StartAsync("handoff-accepted");
        var app = rig.App;
        var scene = await SceneAsync(app);
        var handoff = await FailedHandoffAsync(app, scene);
        Directory.Delete(scene.Downloads, recursive: true);

        Assert.Equal(HttpStatusCode.Conflict, (await TryAgainAsync(app, handoff)).StatusCode);

        var lines = await LinesAsync(app, handoff.Id);
        Assert.DoesNotContain(lines, line => line.Category == "processing.retry-requested");
        Assert.DoesNotContain(lines, line => line.Message.Contains("will try", StringComparison.OrdinalIgnoreCase));
        var refused = Assert.Single(lines, line => line.Category == "processing.retry-refused");
        Assert.Contains("are gone", refused.Message);
    }

    [Fact]
    public async Task A_retry_that_goes_ahead_says_it_will_try_again_before_the_hand_off_is_submitted()
    {
        await using var rig = await StartAsync("handoff-accepted");
        var app = rig.App;
        var scene = await SceneAsync(app);
        var handoff = await FailedHandoffAsync(app, scene);

        Assert.Equal(HttpStatusCode.Accepted, (await TryAgainAsync(app, handoff)).StatusCode);

        var lines = await LinesAsync(app, handoff.Id);
        Assert.DoesNotContain(lines, line => line.Category == "processing.retry-refused");
        var will = Assert.Single(lines, line => line.Category == "processing.retry-requested");
        var submitted = Assert.Single(lines, line => line.Category == "processing.submitted");
        Assert.True(will.CreatedUtc <= submitted.CreatedUtc);
    }

    [Fact]
    public async Task A_retry_the_processor_refuses_still_says_it_tried_because_the_send_went_ahead()
    {
        await using var rig = await StartAsync("handoff-refused-not-watched");
        var app = rig.App;
        var scene = await SceneAsync(app);
        var handoff = await FailedHandoffAsync(app, scene);

        Assert.Equal(HttpStatusCode.Conflict, (await TryAgainAsync(app, handoff)).StatusCode);

        var lines = await LinesAsync(app, handoff.Id);
        Assert.Single(lines, line => line.Category == "processing.retry-requested");
        Assert.Single(lines, line => line.Category == "processing.submission-failed");
        Assert.DoesNotContain(lines, line => line.Category == "processing.retry-refused");
    }
}
