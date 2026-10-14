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
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A show's recovery cases on Transfers: Try again, Import the original, Search for another release and Delete, over HTTP, against
/// the real databases and the download folder on disk. None of them ever deletes the downloaded files or blocklists a release.
/// The movie side is covered by <c>TryAgainOnARecoveryCaseTests</c> and <c>ProcessorFaultRecoveryActionsTests</c>.
/// </summary>
public sealed class TvRecoveryCasesOverHttpOnRealFilesTests
{
    private const string Release = "Show.Name.S01E01.1080p.WEB-DL-DELUNO";

    [Fact]
    public async Task Try_again_on_a_download_the_client_never_showed_ends_it_and_searches_for_that_show_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (library, dispatchId, download) = await SeedAsync(app, "grab");
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.MarkNeverDetectedAsync(dispatchId, "SABnzbd accepted this download and then never showed it.", CancellationToken.None);
        var recoveryCase = await AddCaseAsync(app, "grab-timeout", dispatchId);

        var response = await app.Client.PostAsJsonAsync($"/api/series/import-recovery/{recoveryCase.Id}/retry", new { });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("searching for", body);
        Assert.DoesNotContain(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/series/import-recovery"));
        var jobs = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None);
        var search = Assert.Single(jobs, job => job.Source == "recovery-case.retry");
        Assert.Equal(LibrarySearchJobTypes.For("tv"), search.JobType);
        Assert.Contains("\"targetEntityId\":\"series-1\"", search.PayloadJson);
        Assert.True(File.Exists(download), "trying again never touches a downloaded file");
        Assert.Equal(library.Id, (await app.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single().Id);
    }

    [Fact]
    public async Task Try_again_on_a_case_that_names_no_download_says_so_and_leaves_the_case_open()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var recoveryCase = await app.Services.GetRequiredService<ISeriesCatalogRepository>().AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(Release, "grab-timeout", "Something went wrong.", "Try again.", null), CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync($"/api/series/import-recovery/{recoveryCase.Id}/retry", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("does not say which download", await response.Content.ReadAsStringAsync());
        Assert.Contains(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/series/import-recovery"));
    }

    [Fact]
    public async Task Import_original_releases_a_completed_hand_off_to_import_unprocessed_and_keeps_the_original_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (library, dispatchId, download) = await SeedAsync(app, "refine");
        var connection = await app.Services.GetRequiredService<IProcessorRepository>().CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "weir", "http://127.0.0.1:8585", "X-Api-Key", "secret", IsEnabled: true), CancellationToken.None);
        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var created = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "tv", "sabnzbd", "nzo-tv-1", Release, download, connection.Name), CancellationToken.None);
        var handoff = (await processors.UpdateProcessorHandoffAsync(
            created.Id, ProcessorHandoffStatuses.Completed, Path.Combine(app.DataRoot, "refined", Release + ".mkv"), null, null, CancellationToken.None))!;
        var recoveryCase = await AddCaseAsync(app, "processedFileIsLess", dispatchId);

        var response = await app.Client.PostAsJsonAsync($"/api/series/import-recovery/{recoveryCase.Id}/import-original", new { });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("original", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ProcessorHandoffStatuses.Released, (await processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))?.Status);
        Assert.DoesNotContain(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/series/import-recovery"));
        Assert.True(File.Exists(download), "the original is what gets imported, so it must still be there");
    }

    [Fact]
    public async Task Import_original_on_an_ordinary_download_failure_is_refused_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, dispatchId, download) = await SeedAsync(app, "ordinary");
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().MarkNeverDetectedAsync(dispatchId, "never showed it", CancellationToken.None);
        var recoveryCase = await AddCaseAsync(app, "grab-timeout", dispatchId);

        var response = await app.Client.PostAsJsonAsync($"/api/series/import-recovery/{recoveryCase.Id}/import-original", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/series/import-recovery"));
        Assert.True(File.Exists(download));
    }

    [Fact]
    public async Task Search_again_queues_a_replacement_search_and_never_blocklists_the_release_or_deletes_its_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, dispatchId, download) = await SeedAsync(app, "search-again");
        var recoveryCase = await AddCaseAsync(app, "processedFileIsLess", dispatchId);

        var response = await app.Client.PostAsJsonAsync($"/api/series/import-recovery/{recoveryCase.Id}/search-again", new { });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var queued = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(200, CancellationToken.None);
        Assert.Contains(queued, job => job.JobType == LibrarySearchJobTypes.For("tv"));
        using var blocked = JsonDocument.Parse(await app.Client.GetStringAsync("/api/blocked-releases"));
        Assert.Empty(blocked.RootElement.EnumerateArray());
        Assert.True(File.Exists(download));
        Assert.DoesNotContain(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/series/import-recovery"));
    }

    [Fact]
    public async Task Stop_wanting_ends_the_download_and_resolves_the_case_without_blocklisting()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, dispatchId, download) = await SeedAsync(app, "stop-wanting");
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().MarkNeverDetectedAsync(dispatchId, "never showed it", CancellationToken.None);
        var recoveryCase = await AddCaseAsync(app, "grab-timeout", dispatchId);

        var response = await app.Client.PostAsJsonAsync($"/api/series/import-recovery/{recoveryCase.Id}/stop-wanting", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/series/import-recovery"));
        using var blocked = JsonDocument.Parse(await app.Client.GetStringAsync("/api/blocked-releases"));
        Assert.Empty(blocked.RootElement.EnumerateArray());
        Assert.True(File.Exists(download), "stopping wanting a show never deletes what was downloaded");
    }

    [Fact]
    public async Task Deleting_a_case_removes_it_for_good_and_the_second_delete_is_404_without_touching_the_download()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, dispatchId, download) = await SeedAsync(app, "delete");
        var recoveryCase = await AddCaseAsync(app, "grab-timeout", dispatchId);

        var first = await app.Client.DeleteAsync($"/api/series/import-recovery/{recoveryCase.Id}");
        var second = await app.Client.DeleteAsync($"/api/series/import-recovery/{recoveryCase.Id}");

        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.DoesNotContain(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/series/import-recovery"));
        Assert.True(File.Exists(download));
    }

    [Fact]
    public async Task A_case_that_does_not_exist_is_404_on_every_action()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        foreach (var action in new[] { "retry", "stop-wanting", "import-original", "search-again" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync($"/api/series/import-recovery/nope/{action}", new { })).StatusCode);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(LibraryItem Library, string DispatchId, string DownloadFile)> SeedAsync(ApplicationTestHost app, string label)
    {
        var library = await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Shows", "tv", "library", Path.Combine(app.DataRoot, "tv"), Path.Combine(app.DataRoot, "downloads"), null,
                "refine-before-import", "Weir", null, null, null, true, true, true, 12, 24, 25),
            CancellationToken.None);
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads", label)).FullName;
        var download = Path.Combine(folder, Release + ".mkv");
        await File.WriteAllTextAsync(download, "the downloaded episode");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", "series-1", Release, "Fixture", "sabnzbd", "SABnzbd", "sent", null,
            cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(
            dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: "nzo-tv-1");
        return (library, dispatchId, download);
    }

    private static Task<SeriesImportRecoveryCase> AddCaseAsync(ApplicationTestHost app, string kind, string dispatchId)
        => app.Services.GetRequiredService<ISeriesCatalogRepository>().AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(
                Release, kind, "Something went wrong.", "Try again, import the original, or search for another release.",
                JsonSerializer.Serialize(new { DispatchId = dispatchId })),
            CancellationToken.None);
}
