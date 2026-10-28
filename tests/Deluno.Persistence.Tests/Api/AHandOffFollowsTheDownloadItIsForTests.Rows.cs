using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Hand-off endings against the real host, its real SQLite files and real folders (coverage issue 1295): a hand-off whose
/// library is gone or no longer processes is closed, a library that is merely unreachable keeps its hand-offs, a waiting
/// hand-off whose download was removed raises nothing, and removing a title ends every hand-off it left behind so a
/// late completion is refused as too late.
/// </summary>
public sealed partial class AHandOffFollowsTheDownloadItIsForTests
{
    private static async Task WatchWithLibrariesAsync(ApplicationTestHost app, DateTimeOffset now, IReadOnlyList<LibraryItem> workable, IReadOnlyList<LibraryItem> every, bool withDispatches = false)
    {
        var services = app.Services;
        var processors = Stub.Create<IProcessorConnectionService>((name, _) => name switch
        {
            "GetStatusSupportAsync" => CanReport,
            _ => throw new NotSupportedException(name)
        });
        await new ProcessorHandoffWatcher(
                services.GetRequiredService<IProcessorRepository>(),
                processors,
                services.GetRequiredService<IActivityFeedRepository>(),
                services.GetRequiredService<IMovieCatalogRepository>(),
                services.GetRequiredService<ISeriesCatalogRepository>(),
                services.GetRequiredService<IProcessorOutputImportQueue>(),
                withDispatches ? services.GetRequiredService<IDownloadDispatchesRepository>() : null)
            .WatchAsync(workable, now, CancellationToken.None, every);
    }

    private static async Task DispatchWithHashAsync(ApplicationTestHost app, Scene scene, string release, string hash, bool archived)
    {
        var id = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            scene.LibraryId, "movies", "movie", "movie-" + hash, release, "Fixture Indexer", Client, "Deluge", "sent", null,
            cancellationToken: CancellationToken.None);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.RecordGrabAsync(id, "succeeded", 200, null, null, null, CancellationToken.None, externalId: hash);
        if (archived)
        {
            await dispatches.ArchiveDispatchAsync(id, "The download was removed.", CancellationToken.None);
        }
    }

    private static async Task<string?> StoredStatusAsync(ApplicationTestHost app, string handoffId)
        => await ApiScenes.TextAsync(app, "platform", $"SELECT status FROM processor_handoffs WHERE id = '{handoffId}';");

    [Fact]
    public async Task A_hand_off_whose_library_is_gone_or_no_longer_processes_is_closed_and_one_whose_folder_is_only_unreachable_is_kept()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await SentHandoffAsync(app, scene);
        var libraries = await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow.AddMinutes(10);

        // The library's folder cannot be reached right now: it is not workable, but it still exists.
        await WatchWithLibrariesAsync(app, now, [], libraries);
        Assert.Equal(ProcessorHandoffStatuses.Started, await StoredStatusAsync(app, handoff.Id));

        // The library stopped processing before import.
        var switched = libraries.Select(item => item with { ImportWorkflow = "standard" }).ToArray();
        await WatchWithLibrariesAsync(app, now, switched, switched);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, await StoredStatusAsync(app, handoff.Id));
        Assert.Contains("no longer processes downloads", (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.FailureMessage);

        // A second hand-off whose library was removed altogether.
        var other = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", Client, "hash-other", "Other.Film.2021.1080p-DELUNO", scene.DownloadFolder, "Weir"),
            CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(other.Id, ProcessorHandoffStatuses.Started, null, null, null, CancellationToken.None);
        await WatchWithLibrariesAsync(app, now, [], []);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, await StoredStatusAsync(app, other.Id));
        Assert.Contains("has been removed", (await Repository(app).GetProcessorHandoffAsync(other.Id, CancellationToken.None))!.FailureMessage);
        // Closing a hand-off never touches the download's own files.
        Assert.True(File.Exists(Path.Combine(scene.DownloadFolder, Release + ".mkv")));
    }

    [Fact]
    public async Task A_waiting_hand_off_whose_download_was_removed_is_closed_quietly_and_one_whose_download_is_still_there_is_reported()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var libraries = await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None);
        var gone = await HandoffAsync(app, scene);
        await DispatchWithHashAsync(app, scene, Release, $"hash-{Release}", archived: true);
        var later = DateTimeOffset.UtcNow.AddMinutes(41);

        await WatchWithLibrariesAsync(app, later, libraries, libraries, withDispatches: true);

        Assert.Equal(ProcessorHandoffStatuses.Dismissed, await StoredStatusAsync(app, gone.Id));
        Assert.Contains("is gone", (await Repository(app).GetProcessorHandoffAsync(gone.Id, CancellationToken.None))!.FailureMessage);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM activity_events WHERE category = 'processing.waiting-too-long';"));

        // Another download that is still in the client: the wait running out is reported.
        var kept = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", Client, "hash-still-there", "Still.There.2021.1080p-DELUNO", scene.DownloadFolder, "Weir", DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        await DispatchWithHashAsync(app, scene, "Still.There.2021.1080p-DELUNO", "hash-still-there", archived: false);

        await WatchWithLibrariesAsync(app, later, libraries, libraries, withDispatches: true);

        Assert.Equal(ProcessorHandoffStatuses.Failed, await StoredStatusAsync(app, kept.Id));
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM activity_events WHERE category = 'processing.waiting-too-long';"));
        Assert.True(File.Exists(Path.Combine(scene.DownloadFolder, Release + ".mkv")));
    }

    [Fact]
    public async Task Removing_a_title_dismisses_every_hand_off_it_left_even_one_the_processor_is_busy_with_and_a_late_completion_queues_no_import()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var movieId = await AddMovieAsync(app);
        var busy = await SentHandoffAsync(app, scene);
        await RecordDispatchAsync(app, scene, imported: false, entityId: movieId);
        Assert.Equal(ProcessorHandoffStatuses.Started, await StoredStatusAsync(app, busy.Id));

        var removed = await app.Client.DeleteAsync($"/api/movies/{movieId}");
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());

        Assert.Equal(ProcessorHandoffStatuses.Dismissed, await StoredStatusAsync(app, busy.Id));
        var late = await ReportAsync(app, scene, busy.Id, "completed", outputPath: Path.Combine(scene.Output, Release + ".mkv"));
        // The removal closed the hand-off on purpose, so a finished report is answered as already handled, never imported.
        Assert.True(HttpStatusCode.Accepted == late.StatusCode, await late.Content.ReadAsStringAsync());
        Assert.Contains("superseded", await late.Content.ReadAsStringAsync());
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, await StoredStatusAsync(app, busy.Id));
        Assert.Equal(0, await ImportJobCountAsync(app));
        // Removing the title without deleting files leaves the download where the client put it.
        Assert.True(File.Exists(Path.Combine(scene.DownloadFolder, Release + ".mkv")));
    }
}
