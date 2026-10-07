using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Deluno.Worker.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Net;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1150, #1153, #1155: a hand-off belongs to one acquisition of a download.
/// Deluno is the main stop against duplicate processing, so a download it has
/// already imported is never handed over again, a download that comes back
/// after its title was removed and re-added is handed over afresh, a download
/// its client moved is sent from where it is now, and a processor that sends a
/// second copy is answered like any report already acted on.
///
/// Everything here goes through the real endpoints, repositories and planner.
/// </summary>
public sealed class AHandOffFollowsTheDownloadItIsForTests
{
    private const string Release = "Nosferatu.1922.1080p.BluRay.x264-HD4U";
    private const string Client = "deluge-1";

    // ---- #1155: a second copy of an imported download -------------------------------------------

    [Fact]
    public async Task A_second_copy_of_an_imported_download_is_answered_already_handled_and_deleted_without_a_blocklist_entry()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene, imported: true);
        var output = await WriteOutputAsync(scene);

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = output
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("alreadyHandled", body);
        Assert.False(File.Exists(output), "the processor's second copy is deleted");
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)!), "and so is the release folder it sat in");

        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored?.Status);
        Assert.Null(stored?.ImportJobId);
        Assert.Null(stored?.FailureMessage);

        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>()
            .ListActivityAsync(20, "processor-handoff", handoff.Id, CancellationToken.None);
        var said = Assert.Single(activity, item => item.Category == "processing.output.already-imported");
        Assert.Contains("second copy", said.Message);

        // Settled as already imported: nothing blocklisted, no search, no import.
        Assert.Empty(await app.Services.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None));
        var jobs = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None);
        Assert.DoesNotContain(jobs, job => job.JobType == "filesystem.import.execute");

        // The same report again, now that the output is gone, is still answered 202.
        var again = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = output
        });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Contains("alreadyHandled", await again.Content.ReadAsStringAsync());
        activity = await app.Services.GetRequiredService<IActivityFeedRepository>()
            .ListActivityAsync(20, "processor-handoff", handoff.Id, CancellationToken.None);
        Assert.Single(activity, item => item.Category == "processing.output.already-imported");
    }

    [Fact]
    public async Task A_genuine_upgrade_is_a_new_download_with_its_own_hand_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene, imported: true);
        await Task.Delay(30);
        await RecordDispatchAsync(app, scene, imported: false); // the same release fetched again, newer
        var output = await WriteOutputAsync(scene);

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = output
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("alreadyImported", await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(output), "an upgrade's output is not thrown away");
    }

    [Fact]
    public async Task Try_again_is_refused_once_the_hand_off_has_a_result()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await Repository(app).UpdateProcessorHandoffAsync(
            handoff.Id, ProcessorHandoffStatuses.Failed, Path.Combine(scene.Output, Release), null, "1 of 2 imported.", CancellationToken.None);

        var response = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already has its processed file", await response.Content.ReadAsStringAsync());
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))?.Status);
    }

    // ---- #1155: keyed to the current acquisition ------------------------------------------------

    [Fact]
    public async Task A_download_that_comes_back_after_an_open_hand_off_gets_a_new_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var repository = Repository(app);
        var first = await HandoffAsync(app, scene, addedUtc: DateTimeOffset.UtcNow.AddHours(-1));
        await repository.UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Started, null, null, null, CancellationToken.None);

        // The same download again while its hand-off is still open: that is the same acquisition.
        var same = await HandoffAsync(app, scene, addedUtc: DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(first.Id, same.Id);
        Assert.Equal(ProcessorHandoffStatuses.Started, same.Status);

        // Removed, restored and added again: a download added after the hand-off was made.
        var again = await HandoffAsync(app, scene, addedUtc: DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(ProcessorHandoffStatuses.Waiting, again.Status);
    }

    [Fact]
    public async Task The_planner_hands_a_returning_download_to_the_processor_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var repository = Repository(app);
        var first = await HandoffAsync(app, scene, addedUtc: DateTimeOffset.UtcNow.AddHours(-1));
        await repository.UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Started, null, null, null, CancellationToken.None);
        var submitted = new List<ProcessorHandoffItem>();

        await PlanAsync(app, scene, Item(scene, scene.DownloadFolder, addedUtc: DateTimeOffset.UtcNow.AddMinutes(5)), submitted);

        var sent = Assert.Single(submitted);
        Assert.NotEqual(first.Id, sent.Id);
        Assert.Equal(scene.DownloadFolder, sent.SourcePath);
    }

    [Fact]
    public async Task A_hand_off_still_working_for_an_imported_download_is_closed_and_never_sent_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Started, null, null, null, CancellationToken.None);
        await RecordDispatchAsync(app, scene, imported: true);
        var submitted = new List<ProcessorHandoffItem>();

        await PlanAsync(app, scene, Item(scene, scene.DownloadFolder), submitted);

        Assert.Empty(submitted);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored?.Status);
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>()
            .ListActivityAsync(20, "processor-handoff", handoff.Id, CancellationToken.None);
        Assert.Single(activity, item => item.Category == "processing.handoff.closed-imported");
    }

    [Fact]
    public async Task A_failed_hand_off_for_an_imported_download_is_not_sent_again_by_the_planner()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Waiting, null, null, null, CancellationToken.None);
        await RecordDispatchAsync(app, scene, imported: true);
        var submitted = new List<ProcessorHandoffItem>();

        await PlanAsync(app, scene, Item(scene, scene.DownloadFolder), submitted);

        Assert.Empty(submitted);
    }

    // ---- #1150: the path is where the download is now --------------------------------------------

    [Fact]
    public async Task A_hand_off_made_while_the_client_was_still_moving_the_download_is_sent_from_where_it_ended_up()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var incomplete = Path.Combine(app.DataRoot, "Incomplete", Release);
        var stale = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", Client, $"hash-{Release}", Release, incomplete, "Weir"),
            CancellationToken.None);
        var submitted = new List<ProcessorHandoffItem>();

        await PlanAsync(app, scene, Item(scene, scene.DownloadFolder), submitted);

        var sent = Assert.Single(submitted);
        Assert.Equal(stale.Id, sent.Id);
        Assert.Equal(scene.DownloadFolder, sent.SourcePath);
        var all = await Repository(app).ListProcessorHandoffsAsync(scene.LibraryId, 50, CancellationToken.None);
        Assert.Single(all);
    }

    // ---- #1155: a limit on waiting for the processor ----------------------------------------------

    [Fact]
    public async Task A_hand_off_never_sent_raises_one_clear_item_the_person_can_retry_from()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app, withConnection: false);
        var handoff = await HandoffAsync(app, scene);

        await PlanAsync(app, scene, item: null, submitted: [], clock: new LaterClock(TimeSpan.FromMinutes(45)));

        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Failed, stored?.Status);
        Assert.Contains("never picked up", stored?.FailureMessage);
        Assert.Contains("send it again", stored?.FailureMessage);
        Assert.True(ProcessorHandoffStatuses.NeedsPerson(stored?.Status, stored?.FailureMessage));
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>()
            .ListActivityAsync(20, "processor-handoff", handoff.Id, CancellationToken.None);
        Assert.Single(activity, item => item.Category == "processing.waiting-too-long");
    }

    // ---- #1153: who removed a rejected download -------------------------------------------------

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_rejection_says_who_removed_the_download(bool? sourceRemoved, bool processorRemoved)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene, imported: false);

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "failed",
            disposition = "rejected",
            sourceRemoved,
            message = "No usable audio track.",
            failureClass = "guardrail"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var blocked = Assert.Single(await app.Services.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None));
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        if (processorRemoved)
        {
            Assert.Contains("and removed it", blocked.Reason);
            Assert.Contains("and removed it", stored?.FailureMessage);
        }
        else
        {
            Assert.DoesNotContain("removed it", blocked.Reason);
            Assert.Contains("Weir refused it", blocked.Reason);
            Assert.Contains("will remove it from", blocked.Reason);
            Assert.DoesNotContain("and removed it", stored?.FailureMessage);
        }
    }

    // ---- #1155: output Deluno will never import is cleared; the rest is listed ------------------------

    [Fact]
    public async Task Output_of_a_hand_off_whose_original_was_imported_instead_is_cleared_with_one_activity_line()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var movieId = await AddMovieAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene, imported: false, entityId: movieId);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Released, null, null, null, CancellationToken.None);
        var output = await WriteOutputAsync(scene);

        var removed = await SweepAsync(app);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)!), "the processor's folder for that hand-off is gone");
        var lines = await SweepActivityAsync(app);
        var said = Assert.Single(lines);
        Assert.Contains("imported the original download instead", said.Message);
        Assert.Contains(Release, said.Message);
        Assert.True(Directory.Exists(scene.Output), "only the hand-off's own folder goes, never the processor's output folder");
    }

    [Fact]
    public async Task Output_of_a_download_whose_title_was_removed_is_cleared()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var movieId = await AddMovieAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene, imported: false, entityId: movieId);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Completed, null, null, null, CancellationToken.None);
        var output = await WriteOutputAsync(scene);

        // Still in the catalogue: its output may yet be imported, so it stays.
        Assert.Equal(0, await SweepAsync(app));
        Assert.True(File.Exists(output));

        var removedResponse = await app.Client.DeleteAsync($"/api/movies/{movieId}?deleteFiles=true");
        Assert.True(removedResponse.IsSuccessStatusCode, await removedResponse.Content.ReadAsStringAsync());

        Assert.Equal(1, await SweepAsync(app));
        Assert.False(File.Exists(output));
        Assert.Contains("film was removed", Assert.Single(await SweepActivityAsync(app)).Message);
    }

    [Fact]
    public async Task Output_of_an_older_release_a_newer_download_of_the_same_title_replaced_is_cleared()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var movieId = await AddMovieAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene, imported: false, entityId: movieId);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Failed, null, null, "Weir could not finish it.", CancellationToken.None);
        var output = await WriteOutputAsync(scene);
        await Task.Delay(30);
        // A better release of the same film, fetched later, is in the library.
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var newer = await jobs.RecordDownloadDispatchAsync(
            scene.LibraryId, "movies", "movie", movieId, "Nosferatu.1922.2160p.UHD.BluRay.x265-HD4U", "Fixture Indexer",
            Client, "Deluge", "sent", null, clientExternalId: "hash-newer", cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordImportOutcomeAsync(
            newer, "imported", Path.Combine(app.DataRoot, "Films", "Nosferatu (1922)", "Nosferatu (1922).mkv"), null, null, CancellationToken.None);

        Assert.Equal(1, await SweepAsync(app));
        Assert.False(File.Exists(output));
        Assert.Contains("newer download of the same title", Assert.Single(await SweepActivityAsync(app)).Message);
    }

    [Fact]
    public async Task Output_a_hand_off_is_still_waiting_for_is_never_swept()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var movieId = await AddMovieAsync(app);
        await HandoffAsync(app, scene); // waiting
        await RecordDispatchAsync(app, scene, imported: true, entityId: movieId);
        var output = await WriteOutputAsync(scene);

        Assert.Equal(0, await SweepAsync(app));

        Assert.True(File.Exists(output));
        Assert.Empty(await SweepActivityAsync(app));
    }

    [Fact]
    public async Task Leftovers_from_an_older_build_with_no_hand_off_are_left_alone_and_listed_for_the_person_to_clear()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        // The Ready\Movies-style folder an older build left, with no hand-off row at all.
        var leftover = Directory.CreateDirectory(Path.Combine(scene.Output, "Pulp.Fiction.1994.1080p.BluRay.x264-OLD")).FullName;
        var file = Path.Combine(leftover, "Pulp.Fiction.1994.1080p.BluRay.x264-OLD.mkv");
        await File.WriteAllBytesAsync(file, new byte[2048]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));

        Assert.Equal(0, await SweepAsync(app));
        Assert.True(File.Exists(file), "Deluno cannot say it will never import this, so it does not delete it");

        using var scope = app.Services.CreateScope();
        var check = scope.ServiceProvider.GetServices<IHealthCheck>()
            .OfType<Deluno.Worker.HealthChecks.Checks.ProcessorOutputSafetyNetHealthCheck>().Single();
        var outcomes = await check.RunAsync(CancellationToken.None);
        var listed = Assert.Single(outcomes);
        Assert.Contains("Pulp.Fiction.1994", listed.Message);
        Assert.Contains("clear it", listed.Fix);
    }

    [Fact]
    public async Task The_sweep_never_reaches_outside_the_processors_output_folder()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var movieId = await AddMovieAsync(app);
        var handoff = await HandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene, imported: false, entityId: movieId);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Released, null, null, null, CancellationToken.None);
        // The download itself, in the downloads folder, shares the hand-off's name and is not the processor's to clear.
        var download = Path.Combine(scene.DownloadFolder, Release + ".mkv");
        await WriteOutputAsync(scene);

        await SweepAsync(app);

        Assert.True(File.Exists(download));
    }

    private static async Task<int> SweepAsync(ApplicationTestHost app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ProcessorOutputSweep>().SweepAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<Deluno.Jobs.Contracts.ActivityEventItem>> SweepActivityAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, null, null, CancellationToken.None))
            .Where(item => item.Category == ProcessorOutputSweep.ActivityCategory)
            .ToArray();

    private static async Task<string> AddMovieAsync(ApplicationTestHost app)
    {
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Nosferatu", releaseYear = 1922, monitored = true });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private sealed record Scene(string LibraryId, string Downloads, string Output, string DownloadFolder);

    private static IProcessorRepository Repository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, bool withConnection = true)
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
        if (withConnection)
        {
            var created = await Repository(app).CreateProcessorConnectionAsync(
                new CreateProcessorConnectionRequest("Weir", "generic-webhook", "http://127.0.0.1:9/hook", null, null, true),
                CancellationToken.None);
            // #1076: the name is worked out from the address now; the library names the processor the way an older install did.
            await LegacyConnectionNames.SetAsync(app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(), "processor_connections", created.Id, "Weir");
        }

        return new Scene(library!.Id, downloads, output, folder);
    }

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, DateTimeOffset? addedUtc = null)
        => await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                scene.LibraryId, "movies", Client, $"hash-{Release}", Release, scene.DownloadFolder, "Weir", addedUtc ?? DateTimeOffset.UnixEpoch),
            CancellationToken.None);

    private static async Task<string> WriteOutputAsync(Scene scene)
    {
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, Release)).FullName;
        var file = Path.Combine(folder, Release + ".mkv");
        await File.WriteAllBytesAsync(file, new byte[2048]);
        await File.WriteAllTextAsync(Path.Combine(folder, Release + ".nfo"), "info");
        // Long since written, so it counts as finished rather than still being written.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));
        File.SetLastWriteTimeUtc(Path.Combine(folder, Release + ".nfo"), DateTime.UtcNow.AddMinutes(-30));
        return file;
    }

    private static async Task RecordDispatchAsync(ApplicationTestHost app, Scene scene, bool imported, string entityId = "movie-nosferatu")
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var id = await jobs.RecordDownloadDispatchAsync(
            scene.LibraryId, "movies", "movie", entityId, Release, "Fixture Indexer",
            Client, "Deluge", "sent", null, clientExternalId: $"hash-{Release}", cancellationToken: CancellationToken.None);
        if (imported)
        {
            await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordImportOutcomeAsync(
                id, "imported", Path.Combine(app.DataRoot, "Films", "Nosferatu (1922)", "Nosferatu (1922).mkv"), null, null, CancellationToken.None);
        }
    }

    private static DownloadQueueItem Item(Scene scene, string sourcePath, DateTimeOffset? addedUtc = null)
        => new(
            $"hash-{Release}", Client, "Deluge", "deluge", "movies", "Nosferatu 1922", Release,
            "deluno-movies", DownloadQueueStatuses.Completed, 100, 0, 0, 100, 100, 0, "Fixture Indexer", null,
            addedUtc ?? DateTimeOffset.UtcNow.AddHours(-2), sourcePath, scene.LibraryId);

    /// <summary>
    /// A stand-in for an interface that answers only the calls a test cares
    /// about. Anything else is a mistake in the test and throws.
    /// </summary>
    private class Stub : DispatchProxy
    {
        public Func<string, object?[], object?> Answer { get; set; } = (name, _) => throw new NotSupportedException(name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = Answer(targetMethod!.Name, args ?? []);
            var returns = targetMethod.ReturnType;
            if (returns == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = returns.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [result]);
            }

            return result;
        }

        public static T Create<T>(Func<string, object?[], object?> answer) where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy).Answer = answer;
            return proxy;
        }
    }

    /// <summary>A scheduled pass only logs what it throws; a test should see it.</summary>
    private sealed class FailingLogger : Microsoft.Extensions.Logging.ILogger<WorkPlanner>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                throw new InvalidOperationException(formatter(state, exception), exception);
            }
        }
    }

    private sealed class LaterClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }

    /// <summary>One planner pass over the real repositories, the processor connection recording what it was sent.</summary>
    private static async Task PlanAsync(
        ApplicationTestHost app,
        Scene scene,
        DownloadQueueItem? item,
        List<ProcessorHandoffItem> submitted,
        TimeProvider? clock = null)
    {
        var services = app.Services;
        var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
        var overview = new DownloadTelemetryOverview(
            summary,
            item is null
                ? []
                : [new DownloadClientTelemetrySnapshot(
                    Client, "Deluge", "deluge", "http://127.0.0.1:8112", "healthy", null,
                    new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "password"),
                    summary, [item], [], DateTimeOffset.UtcNow)],
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
                    submitted.Add((ProcessorHandoffItem)args[1]!);
                    return new ProcessorSubmissionResult(true, ProcessorHandoffStatuses.Submitted, "Accepted.", 202);
                case "GetStatusSupportAsync":
                    return ProcessorStatusSupport.None;
                default:
                    throw new NotSupportedException(name);
            }
        });

        await new WorkPlanner(
                new FailingLogger(),
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
                clock ?? TimeProvider.System,
                CancellationToken.None);
    }
}
