using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #919: "processedFileIsLess" is offered three real actions -- Import the
/// original, Run the processor again, and Search for another release -- not
/// just words in the case's own recommendation. Checkpoint A already proved
/// the release good, so none of these three ever blocklists it.
/// </summary>
public sealed class ProcessorFaultRecoveryActionsTests
{
    private const string Release = "Interstellar.2014.WEB.1080p-DELUNO";

    [Fact]
    public async Task Retry_runs_the_processor_again_on_a_hand_off_it_already_completed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (library, dispatchId) = await SeedAsync(app);
        var connection = await CreateConnectionAsync(app);
        var handoff = await CreateCompletedHandoffAsync(app, library, connection.Name);
        var recoveryCase = await AddCaseAsync(app, dispatchId);

        var response = await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{recoveryCase.Id}/retry", new { });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("run", body, StringComparison.OrdinalIgnoreCase);

        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var updated = await processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Waiting, updated?.Status);
        Assert.DoesNotContain(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/movies/import-recovery"));
    }

    [Fact]
    public async Task Import_original_releases_the_hand_off_to_import_unprocessed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (library, dispatchId) = await SeedAsync(app);
        var connection = await CreateConnectionAsync(app);
        var handoff = await CreateCompletedHandoffAsync(app, library, connection.Name);
        var recoveryCase = await AddCaseAsync(app, dispatchId);

        var response = await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{recoveryCase.Id}/import-original", new { });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("original", body, StringComparison.OrdinalIgnoreCase);

        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var updated = await processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Released, updated?.Status);
    }

    [Fact]
    public async Task Search_again_queues_a_replacement_search_and_never_blocklists_the_release()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (library, dispatchId) = await SeedAsync(app);
        var recoveryCase = await AddCaseAsync(app, dispatchId);

        var response = await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{recoveryCase.Id}/search-again", new { });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);

        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var queued = await jobs.ListAsync(200, CancellationToken.None);
        Assert.Contains(queued, job => job.JobType == LibrarySearchJobTypes.For("movies"));

        var blocked = await app.Client.GetAsync("/api/blocked-releases");
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Empty(blockedBody.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Retry_on_an_ordinary_download_failure_is_unaffected_by_the_processor_fault_case()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-ordinary", Release, "Fixture", "client-1", "SABnzbd", "sent", null,
            cancellationToken: CancellationToken.None);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.MarkNeverDetectedAsync(dispatchId, "SABnzbd accepted this download and then never showed it.", CancellationToken.None);
        var recoveryCase = await app.Services.GetRequiredService<IMovieCatalogRepository>().AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(Release, "grab-timeout", "Something went wrong.", "Try again.", JsonSerializer.Serialize(new { DispatchId = dispatchId })),
            CancellationToken.None);

        // Import the original and search-again are only for processedFileIsLess.
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{recoveryCase.Id}/import-original", new { })).StatusCode);
    }

    private static async Task<(LibraryItem Library, string DispatchId)> SeedAsync(ApplicationTestHost app)
    {
        var library = await CreateLibraryAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-interstellar", Release, "Fixture", "sabnzbd", "SABnzbd", "sent", null,
            cancellationToken: CancellationToken.None);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: "nzo-fault-1");
        return (library, dispatchId);
    }

    private static async Task<LibraryItem> CreateLibraryAsync(ApplicationTestHost app)
        => await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(app.DataRoot, "movies"), Path.Combine(app.DataRoot, "downloads"), null,
                "refine-before-import", "Weir", null, null, null, true, true, true, 12, 24, 25),
            CancellationToken.None);

    private static async Task<ProcessorConnectionItem> CreateConnectionAsync(ApplicationTestHost app)
        => await app.Services.GetRequiredService<IProcessorRepository>().CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "weir", "http://127.0.0.1:8585", "X-Api-Key", "secret", IsEnabled: true),
            CancellationToken.None);

    private static async Task<ProcessorHandoffItem> CreateCompletedHandoffAsync(ApplicationTestHost app, LibraryItem library, string processorName)
    {
        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var created = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                library.Id, "movies", "sabnzbd", "nzo-fault-1", Release,
                Path.Combine(app.DataRoot, "downloads", Release, Release + ".mkv"), processorName),
            CancellationToken.None);
        var completed = await processors.UpdateProcessorHandoffAsync(
            created.Id, ProcessorHandoffStatuses.Completed, Path.Combine(app.DataRoot, "refined", Release + ".mkv"), null, null, CancellationToken.None);
        return completed!;
    }

    private static Task<MovieImportRecoveryCase> AddCaseAsync(ApplicationTestHost app, string dispatchId)
        => app.Services.GetRequiredService<IMovieCatalogRepository>().AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(
                Release, "processedFileIsLess", "The processed file runs 1h 02m; the download it was made from runs 2h 46m.",
                "Import the original, run the processor again, or search for another release.",
                JsonSerializer.Serialize(new { DispatchId = dispatchId })),
            CancellationToken.None);
}
