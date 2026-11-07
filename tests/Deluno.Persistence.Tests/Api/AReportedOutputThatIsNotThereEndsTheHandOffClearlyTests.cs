using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
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
/// decides "done" by the source file, not the hand-off id). The races are forced in an exact order by a hook on the
/// processor repository, not left to timing.
/// </summary>
public sealed class AReportedOutputThatIsNotThereEndsTheHandOffClearlyTests
{
    private const string Release = "Gpagain3xzse11.2020.1080p.WEB-DL.x264-GOLDEN";
    private const string Client = "qbittorrent";

    private sealed record Scene(LibraryItem Library, ProcessorHandoffItem Handoff, string Output);

    /// <summary>The processor repository with a hook before every call, so a test makes the other side act at an exact moment.</summary>
    public class HookedRepository : DispatchProxy
    {
        public IProcessorRepository Target { get; set; } = null!;

        public Action<string, object?[]>? Before { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Before?.Invoke(targetMethod!.Name, args ?? []);
            try
            {
                return targetMethod!.Invoke(Target, args);
            }
            catch (TargetInvocationException exception)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
                throw;
            }
        }
    }

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<(ApplicationTestHost App, HookedRepository Hook)> HookedHostAsync()
    {
        HookedRepository? hook = null;
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(IProcessorRepository));
            services.Remove(original);
            services.AddSingleton<IProcessorRepository>(provider =>
            {
                var real = (IProcessorRepository)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
                var proxy = DispatchProxy.Create<IProcessorRepository, HookedRepository>();
                hook = (HookedRepository)(object)proxy;
                hook.Target = real;
                return proxy;
            });
        });
        _ = Repository(app);
        return (app, hook!);
    }

    private static bool IsEndingWrite(string name, object?[] args)
        => name == nameof(IProcessorRepository.UpdateProcessorHandoffIfStatusAsync)
           && args[2] is ProcessorHandoffStatuses.Failed or ProcessorHandoffStatuses.Released;

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, string failureMode = "block", bool filmWaits = true, bool withLink = true)
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
        if (withLink)
        {
            await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", film, Release, "Fixture Indexer",
                Client, "qBittorrent", "sent", null, clientExternalId: $"hash-{Release}", cancellationToken: CancellationToken.None);
        }

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
        return new Scene(library, handoff, output);
    }

    /// <summary>Weir's recorded "already cleaned this exact file" report: it names the first run's copy, which is gone.</summary>
    private static JsonObject AlreadyCleanedBody(Scene scene, string handoffId)
        => RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-completed-resent"), scene.Library.Id, handoffId, scene.Output, Release + ".mkv");

    /// <summary>Weir's recorded report of a finished file.</summary>
    private static JsonObject CleanedBody(Scene scene, string handoffId)
        => RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-completed", 1), scene.Library.Id, handoffId, scene.Output, Release + ".mkv");

    private static Task<HttpResponseMessage> AlreadyCleanedAsync(ApplicationTestHost app, Scene scene)
        => RecordedWeirReports.PostAsync(app.Client, AlreadyCleanedBody(scene, scene.Handoff.Id));

    private static void WriteFiles(JsonObject body)
    {
        foreach (var file in RecordedWeirReports.OutputFilesOf(body))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[2048]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));
        }
    }

    /// <summary>One pass of the hand-off watcher, a minute after the reports: past the pause before a missing output is judged.</summary>
    private static async Task WatchLaterAsync(ApplicationTestHost app)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var watcher = new Deluno.Worker.Services.ProcessorHandoffWatcher(
            services.GetRequiredService<IProcessorRepository>(),
            services.GetRequiredService<IProcessorConnectionService>(),
            services.GetRequiredService<IActivityFeedRepository>(),
            services.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>(),
            services.GetRequiredService<Deluno.Series.Data.ISeriesCatalogRepository>(),
            services.GetService<IProcessorOutputImportQueue>(),
            services.GetService<IDownloadDispatchesRepository>());
        var libraries = await services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None);
        await watcher.WatchAsync(libraries, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None, libraries);
    }

    private static async Task<int> ImportJobsAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None))
            .Count(job => job.JobType == "filesystem.import.execute");

    private static async Task<ProcessorHandoffItem> CurrentAsync(ApplicationTestHost app, string handoffId)
        => (await Repository(app).GetProcessorHandoffAsync(handoffId, CancellationToken.None))!;

    private static async Task<int> GaveUpLinesAsync(ApplicationTestHost app, string handoffId)
        => (await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", handoffId, CancellationToken.None))
            .Count(item => item.Category == "processing.output-missing.gave-up");

    private static async Task<string> BodyOfAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    [Fact]
    public async Task The_already_cleaned_report_is_answered_at_once_and_the_hand_off_fails_once_after_the_second_look()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        var response = await AlreadyCleanedAsync(app, scene);

        // Answered at once, nothing decided yet: a share can show a file late.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await BodyOfAsync(response);
        Assert.Contains("checking", body);
        Assert.DoesNotContain("alreadyHandled", body);
        Assert.Equal(ProcessorHandoffStatuses.Submitted, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Equal(0, await GaveUpLinesAsync(app, scene.Handoff.Id));
        await WatchLaterAsync(app);
        var ended = await CurrentAsync(app, scene.Handoff.Id);
        Assert.Equal(ProcessorHandoffStatuses.Failed, ended.Status);
        Assert.Contains("not there", ended.FailureMessage);
        Assert.Equal(1, await GaveUpLinesAsync(app, scene.Handoff.Id));
        Assert.Equal(0, await ImportJobsAsync(app));

        // Looking again, or the same report again, changes nothing and says nothing more.
        await WatchLaterAsync(app);
        await AlreadyCleanedAsync(app, scene);
        await WatchLaterAsync(app);
        Assert.Equal(1, await GaveUpLinesAsync(app, scene.Handoff.Id));
    }

    [Fact]
    public async Task A_library_that_imports_the_original_when_processing_fails_releases_the_hand_off_for_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, "import-original");

        await AlreadyCleanedAsync(app, scene);
        await WatchLaterAsync(app);

        Assert.Equal(ProcessorHandoffStatuses.Released, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Equal(1, await GaveUpLinesAsync(app, scene.Handoff.Id));
        Assert.Equal(0, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_send_that_records_its_answer_between_the_read_and_the_write_does_not_stop_the_ending()
    {
        var (app, hook) = await HookedHostAsync();
        await using var _ = app;
        var scene = await SceneAsync(app);
        await AlreadyCleanedAsync(app, scene);
        var fired = 0;
        hook.Before = (name, args) =>
        {
            // The send's answer lands first: submitted becomes accepted, still with the processor.
            if (IsEndingWrite(name, args) && Interlocked.Increment(ref fired) == 1)
            {
                hook.Target.UpdateProcessorHandoffAsync(scene.Handoff.Id, ProcessorHandoffStatuses.Accepted, null, null, null, CancellationToken.None).GetAwaiter().GetResult();
            }
        };

        await WatchLaterAsync(app);

        Assert.Equal(ProcessorHandoffStatuses.Failed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Equal(1, await GaveUpLinesAsync(app, scene.Handoff.Id));
    }

    [Fact]
    public async Task Two_looks_that_both_read_the_hand_off_before_either_writes_have_one_outcome()
    {
        var (app, hook) = await HookedHostAsync();
        await using var _ = app;
        var scene = await SceneAsync(app);
        await AlreadyCleanedAsync(app, scene);
        using var both = new Barrier(2);
        var arrived = 0;
        hook.Before = (name, args) =>
        {
            // Each look holds at its write until the other has read too: they overlap.
            if (IsEndingWrite(name, args) && Interlocked.Increment(ref arrived) <= 2)
            {
                Assert.True(both.SignalAndWait(TimeSpan.FromSeconds(20)), "the two looks never met at the write");
            }
        };

        await Task.WhenAll(Task.Run(() => WatchLaterAsync(app)), Task.Run(() => WatchLaterAsync(app)));

        Assert.Equal(1, await GaveUpLinesAsync(app, scene.Handoff.Id));
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Equal(0, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_good_report_that_lands_between_the_second_look_and_its_write_wins()
    {
        var (app, hook) = await HookedHostAsync();
        await using var _ = app;
        var scene = await SceneAsync(app);
        var good = CleanedBody(scene, scene.Handoff.Id);
        WriteFiles(good);
        await AlreadyCleanedAsync(app, scene);
        var fired = 0;
        hook.Before = (name, args) =>
        {
            if (IsEndingWrite(name, args) && Interlocked.Increment(ref fired) == 1)
            {
                var answer = RecordedWeirReports.PostAsync(app.Client, good).GetAwaiter().GetResult();
                Assert.Equal(HttpStatusCode.Accepted, answer.StatusCode);
            }
        };

        await WatchLaterAsync(app);

        Assert.Equal(ProcessorHandoffStatuses.Completed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Equal(0, await GaveUpLinesAsync(app, scene.Handoff.Id));
        Assert.Equal(1, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_missing_output_reported_for_an_earlier_send_never_ends_the_live_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        // The release is sent again: the first send ends and the same source path gets a new hand-off.
        await Repository(app).UpdateProcessorHandoffAsync(scene.Handoff.Id, ProcessorHandoffStatuses.Failed, null, null, "ended", CancellationToken.None);
        var live = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.Library.Id, "movies", Client, $"hash-{Release}", Release, scene.Handoff.SourcePath, "Weir", DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None);
        Assert.NotEqual(scene.Handoff.Id, live.Id);
        await Repository(app).UpdateProcessorHandoffAsync(live.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);

        // The earlier send's report arrives, naming the first run's file, which is gone.
        var stale = await AlreadyCleanedAsync(app, scene);
        await WatchLaterAsync(app);

        Assert.Equal(HttpStatusCode.Accepted, stale.StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Submitted, (await CurrentAsync(app, live.Id)).Status);
        Assert.Equal(0, await GaveUpLinesAsync(app, live.Id));

        // And the live send's own good report is applied, not refused.
        var body = CleanedBody(scene, live.Id);
        WriteFiles(body);
        var good = await RecordedWeirReports.PostAsync(app.Client, body);
        Assert.Equal(HttpStatusCode.Accepted, good.StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Completed, (await CurrentAsync(app, live.Id)).Status);
    }

    [Fact]
    public async Task An_output_that_is_there_when_Deluno_looks_again_is_imported_not_failed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var report = AlreadyCleanedBody(scene, scene.Handoff.Id);
        var response = await RecordedWeirReports.PostAsync(app.Client, report);
        Assert.Contains("checking", await BodyOfAsync(response));

        // A share showed the file late: it is there by the second look.
        WriteFiles(report);
        await WatchLaterAsync(app);

        Assert.Equal(0, await GaveUpLinesAsync(app, scene.Handoff.Id));
        Assert.Equal(1, await ImportJobsAsync(app));
        Assert.Equal(ProcessorHandoffStatuses.Completed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
    }

    [Fact]
    public async Task A_download_with_no_catalogue_link_and_nothing_imported_gets_the_same_ending()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, withLink: false);

        var response = await AlreadyCleanedAsync(app, scene);
        await WatchLaterAsync(app);

        Assert.DoesNotContain("alreadyHandled", await BodyOfAsync(response));
        Assert.Equal(1, await GaveUpLinesAsync(app, scene.Handoff.Id));
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Equal(0, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_download_with_no_catalogue_link_whose_output_an_earlier_import_already_took_is_already_imported_not_failed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, withLink: false);
        // An import of this very output ran to its end and moved the file: the import records say so, the file's absence does not.
        var named = RecordedWeirReports.OutputFilesOf(AlreadyCleanedBody(scene, scene.Handoff.Id)).First();
        var job = await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest("filesystem.import.execute", "processor", "{}", "movie", null,
                IdempotencyKey: $"processor-output:{scene.Library.Id}:{Path.GetFullPath(named).ToLowerInvariant()}"),
            CancellationToken.None);
        await CompleteJobAsync(app, job.Id);

        var response = await AlreadyCleanedAsync(app, scene);
        await WatchLaterAsync(app);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("alreadyImported", await BodyOfAsync(response));
        Assert.Equal(0, await GaveUpLinesAsync(app, scene.Handoff.Id));
        Assert.NotEqual(ProcessorHandoffStatuses.Failed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
    }

    [Fact]
    public async Task A_missing_output_for_a_title_that_already_has_its_file_is_not_failed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, filmWaits: false);

        await AlreadyCleanedAsync(app, scene);
        await WatchLaterAsync(app);

        Assert.Equal(0, await GaveUpLinesAsync(app, scene.Handoff.Id));
        Assert.NotEqual(ProcessorHandoffStatuses.Failed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Equal(1, await ImportJobsAsync(app));
    }

    [Fact]
    public async Task A_report_that_arrives_before_the_send_is_recorded_is_still_judged_after_the_send_writes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        // The order in the golden VM log: the hand-off is still waiting when the report comes, and the send writes right after it.
        await Repository(app).UpdateProcessorHandoffAsync(scene.Handoff.Id, ProcessorHandoffStatuses.Waiting, null, null, null, CancellationToken.None);

        var response = await AlreadyCleanedAsync(app, scene);
        Assert.Contains("checking", await BodyOfAsync(response));
        var sent = await Repository(app).UpdateProcessorHandoffIfStatusAsync(
            scene.Handoff.Id, ProcessorHandoffStatuses.Waiting, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        Assert.True(sent);
        await WatchLaterAsync(app);

        Assert.Equal(ProcessorHandoffStatuses.Failed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Equal(1, await GaveUpLinesAsync(app, scene.Handoff.Id));
    }

    [Fact]
    public async Task The_card_does_not_promise_a_look_that_the_stall_timeout_would_have_to_keep()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        await AlreadyCleanedAsync(app, scene);

        // The pending look is kept apart from the hand-off's own message, which stays as it was.
        var pending = await CurrentAsync(app, scene.Handoff.Id);
        Assert.Null(pending.FailureMessage);
        Assert.NotNull(pending.MissingOutput);
        // Other writes to the hand-off leave it in place.
        await Repository(app).UpdateProcessorHandoffAsync(scene.Handoff.Id, ProcessorHandoffStatuses.Accepted, null, null, "Weir has it.", CancellationToken.None);
        Assert.NotNull((await CurrentAsync(app, scene.Handoff.Id)).MissingOutput);
        await WatchLaterAsync(app);
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Null((await CurrentAsync(app, scene.Handoff.Id)).MissingOutput);
    }

    [Fact]
    public async Task An_output_found_on_the_second_look_that_an_import_already_took_settles_the_hand_off_and_imports_nothing_over_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, withLink: false);
        var report = AlreadyCleanedBody(scene, scene.Handoff.Id);
        await RecordedWeirReports.PostAsync(app.Client, report);
        // Meanwhile an import of this very output ran to its end, and the file is back where it was named.
        var named = RecordedWeirReports.OutputFilesOf(report).First();
        WriteFiles(report);
        var job = await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest("filesystem.import.execute", "processor", "{}", "movie", null,
                IdempotencyKey: $"processor-output:{scene.Library.Id}:{Path.GetFullPath(named).ToLowerInvariant()}"),
            CancellationToken.None);
        await CompleteJobAsync(app, job.Id);
        File.SetLastWriteTimeUtc(named, DateTime.UtcNow.AddMinutes(-30));

        await WatchLaterAsync(app);

        Assert.Equal(ProcessorHandoffStatuses.Completed, (await CurrentAsync(app, scene.Handoff.Id)).Status);
        Assert.Equal(0, await GaveUpLinesAsync(app, scene.Handoff.Id));
        Assert.Equal(1, await ImportJobsAsync(app));
    }

    private static async Task CompleteJobAsync(ApplicationTestHost app, string jobId)
    {
        var db = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        await using var connection = await db.OpenConnectionAsync("jobs", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE job_queue SET status = 'completed', completed_utc = '{DateTimeOffset.UtcNow:O}' WHERE id = '{jobId}';";
        await command.ExecuteNonQueryAsync();
    }
}
