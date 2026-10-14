using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The import-recovery routes of both engines when the request is right (coverage register, area api): a case is
/// opened by hand, listed, dismissed, deleted, tried again, stopped wanting, searched for again or imported unprocessed,
/// and each leaves the download, the title and the real files as it says. The refusals are proved by the route walk.
/// </summary>
public sealed class ImportRecoveryRoutesDoWhatTheySayTests
{
    private const string Release = "Lost.Title.2010.1080p.WEB-DL-DELUNO";

    public static TheoryData<string> Kinds => new() { "movies", "series" };

    /// <summary>How many recovery cases the engine's real SQLite file holds in the given status (any status when null).</summary>
    private static async Task<long> CaseRowsAsync(ApplicationTestHost app, string kind, string? status = null)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(kind == "movies" ? "movies" : "series", CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {(kind == "movies" ? "movie" : "series")}_import_recovery_cases" + (status is null ? ";" : $" WHERE status = '{status}';");
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static string MediaType(string kind) => kind == "movies" ? "movies" : "tv";

    private static string EntityType(string kind) => kind == "movies" ? "movie" : "series";

    private static async Task<JsonElement> OpenCaseAsync(ApplicationTestHost app, string kind, string failureKind, string? detailsJson)
    {
        var response = await app.Client.PostAsJsonAsync($"/api/{kind}/import-recovery", new
        {
            title = Release,
            failureKind,
            summary = "Something went wrong.",
            recommendedAction = "Try again.",
            detailsJson
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<IReadOnlyList<string>> OpenCaseIdsAsync(ApplicationTestHost app, string kind)
        => await ApiPayload.ListIdsAsync(app.Client, $"/api/{kind}/import-recovery");

    private static async Task<string> DispatchAsync(ApplicationTestHost app, string kind, string libraryId, string entityId, string status = "sent", string clientId = "sabnzbd")
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        return await jobs.RecordDownloadDispatchAsync(
            libraryId, MediaType(kind), EntityType(kind), entityId, Release, "Fixture", clientId, "SABnzbd", status, null,
            cancellationToken: CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_case_opened_by_hand_is_listed_open_and_one_without_a_title_or_summary_is_refused_in_plain_words(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var opened = await OpenCaseAsync(app, kind, "importFailed", null);

        Assert.Equal(Release, opened.GetProperty("title").GetString());
        Assert.Equal("open", opened.GetProperty("status").GetString());
        Assert.Equal("importFailed", opened.GetProperty("failureKind").GetString());
        Assert.Contains(opened.GetProperty("id").GetString()!, await OpenCaseIdsAsync(app, kind));
        Assert.Equal(1, await CaseRowsAsync(app, kind, "open"));

        var refused = await app.Client.PostAsJsonAsync($"/api/{kind}/import-recovery", new { title = "", summary = "" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Contains(kind == "movies" ? "Give this import issue a movie title." : "Give this import issue", text, StringComparison.Ordinal);
        Assert.Contains("Add a short summary", text, StringComparison.Ordinal);
        Assert.Single(await OpenCaseIdsAsync(app, kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Dismissing_a_case_closes_it_keeps_its_row_as_dismissed_and_settles_the_alert_it_stood_for(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var opened = await OpenCaseAsync(app, kind, "importFailed", null);
        var id = opened.GetProperty("id").GetString()!;

        var dismiss = await app.Client.PostAsync($"/api/{kind}/import-recovery/{id}/dismiss", content: null);

        Assert.Equal(HttpStatusCode.OK, dismiss.StatusCode);
        Assert.Equal("dismissed", (await dismiss.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.DoesNotContain(id, await OpenCaseIdsAsync(app, kind));
        Assert.Equal(1, await CaseRowsAsync(app, kind, "dismissed"));
        Assert.Equal(0, await CaseRowsAsync(app, kind, "open"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/{kind}/import-recovery/nope/dismiss", content: null)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Deleting_a_case_removes_it_for_good_and_a_second_delete_is_not_found(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await OpenCaseAsync(app, kind, "importFailed", null)).GetProperty("id").GetString()!;
        var table = kind == "movies" ? "movie_import_recovery_cases" : "series_import_recovery_cases";
        var database = kind == "movies" ? "movies" : "series";
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, database, $"SELECT COUNT(*) FROM {table};"));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/{kind}/import-recovery/{id}")).StatusCode);

        Assert.Equal(0, await ApiScenes.ScalarAsync(app, database, $"SELECT COUNT(*) FROM {table};"));
        Assert.Equal(0, await CaseRowsAsync(app, kind));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/{kind}/import-recovery/{id}")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_failed_import_is_run_again_by_its_case_and_the_files_on_disk_are_left_alone(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, filePath, _) = await TitleOnDiskAsync(app, kind);
        var dispatchId = await DispatchAsync(app, kind, libraryId, "title-1");
        var failed = await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest("filesystem.import.execute", "download-client",
                JsonSerializer.Serialize(new { DispatchId = dispatchId, ReleaseName = Release }), null, null),
            CancellationToken.None);
        await ApiScenes.ExecuteAsync(app, "jobs", $"UPDATE job_queue SET status = 'dead-letter', last_error = 'The import failed.' WHERE id = '{failed.Id}';");
        var id = (await OpenCaseAsync(app, kind, "import-failed", JsonSerializer.Serialize(new { DispatchId = dispatchId }))).GetProperty("id").GetString()!;

        var response = await app.Client.PostAsJsonAsync($"/api/{kind}/import-recovery/{id}/retry", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("importing", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("queued", await ApiScenes.TextAsync(app, "jobs", $"SELECT status FROM job_queue WHERE id = '{failed.Id}';"));
        Assert.DoesNotContain(id, await OpenCaseIdsAsync(app, kind));
        Assert.True(File.Exists(filePath), "trying an import again never touches a file already in the library");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Stopping_wanting_ends_the_download_and_stops_monitoring_the_title_but_keeps_its_files(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, filePath, titleId) = await TitleOnDiskAsync(app, kind);
        var dispatchId = await DispatchAsync(app, kind, libraryId, titleId);
        var id = (await OpenCaseAsync(app, kind, "vanished-downloading", JsonSerializer.Serialize(new { DispatchId = dispatchId }))).GetProperty("id").GetString()!;
        Assert.True(await IsMonitoredAsync(app, kind, titleId));

        var response = await app.Client.PostAsync($"/api/{kind}/import-recovery/{id}/stop-wanting", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("stopped wanting", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await IsMonitoredAsync(app, kind, titleId), "the title is no longer wanted");
        var dispatch = await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchIncludingArchivedAsync(dispatchId, CancellationToken.None);
        Assert.Equal("archived", dispatch!.Status);
        Assert.DoesNotContain(id, await OpenCaseIdsAsync(app, kind));
        Assert.True(File.Exists(filePath), "stopping wanting a title never deletes its files");
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/{kind}/import-recovery/{id}/stop-wanting", content: null)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Searching_again_for_a_held_back_processed_file_queues_a_search_for_the_title_and_blocklists_nothing(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, _, titleId) = await TitleOnDiskAsync(app, kind);
        var dispatchId = await DispatchAsync(app, kind, libraryId, titleId);
        var id = (await OpenCaseAsync(app, kind, "processedFileIsLess", JsonSerializer.Serialize(new { DispatchId = dispatchId }))).GetProperty("id").GetString()!;

        var response = await app.Client.PostAsJsonAsync($"/api/{kind}/import-recovery/{id}/search-again", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("searching for", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var queued = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(200, CancellationToken.None);
        var search = Assert.Single(queued, job => job.Source == "recovery-case.retry");
        Assert.Equal(LibrarySearchJobTypes.For(MediaType(kind)), search.JobType);
        Assert.Contains($"\"targetEntityId\":\"{titleId}\"", search.PayloadJson, StringComparison.Ordinal);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/blocked-releases")).Replace(" ", ""));
        Assert.DoesNotContain(id, await OpenCaseIdsAsync(app, kind));
        Assert.Equal(1, await CaseRowsAsync(app, kind, "resolved"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Importing_the_original_releases_the_processor_hand_off_so_the_unprocessed_file_is_imported(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, _, titleId) = await TitleOnDiskAsync(app, kind);
        var dispatchId = await DispatchAsync(app, kind, libraryId, titleId);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: "nzo-1");
        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var connection = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "weir", "http://127.0.0.1:8585", "X-Api-Key", "secret", IsEnabled: true), CancellationToken.None);
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads", Release)).FullName;
        var original = Path.Combine(downloads, Release + ".mkv");
        await File.WriteAllBytesAsync(original, [9, 9, 9, 9]);
        var created = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(libraryId, MediaType(kind), "sabnzbd", "nzo-1", Release, original, connection.Name), CancellationToken.None);
        await processors.UpdateProcessorHandoffAsync(created.Id, ProcessorHandoffStatuses.Completed, Path.Combine(app.DataRoot, "refined.mkv"), null, null, CancellationToken.None);
        var id = (await OpenCaseAsync(app, kind, "processedFileIsLess", JsonSerializer.Serialize(new { DispatchId = dispatchId }))).GetProperty("id").GetString()!;

        var response = await app.Client.PostAsJsonAsync($"/api/{kind}/import-recovery/{id}/import-original", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("without processing", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(ProcessorHandoffStatuses.Released, (await processors.GetProcessorHandoffAsync(created.Id, CancellationToken.None))!.Status);
        Assert.True(File.Exists(original), "the unprocessed original is still in the download folder, ready to import");
        Assert.DoesNotContain(id, await OpenCaseIdsAsync(app, kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Importing_the_original_for_any_other_kind_of_case_is_refused_and_the_case_stays_open(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await OpenCaseAsync(app, kind, "importFailed", null)).GetProperty("id").GetString()!;

        var response = await app.Client.PostAsJsonAsync($"/api/{kind}/import-recovery/{id}/import-original", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("only applies to a processed file", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(id, await OpenCaseIdsAsync(app, kind));
    }

    private static async Task<(string LibraryId, string FilePath, string TitleId)> TitleOnDiskAsync(ApplicationTestHost app, string kind)
    {
        if (kind == "movies")
        {
            var film = await ApiScenes.FilmOnDiskAsync(app);
            return (film.LibraryId, film.FilePath, film.MovieId);
        }

        var show = await ApiScenes.ShowOnDiskAsync(app);
        return (show.LibraryId, show.FilePath, show.SeriesId);
    }

    private static async Task<bool> IsMonitoredAsync(ApplicationTestHost app, string kind, string id)
    {
        var detail = await app.Client.GetFromJsonAsync<JsonElement>($"/api/{kind}/{id}");
        return detail.GetProperty("monitored").GetBoolean();
    }
}
