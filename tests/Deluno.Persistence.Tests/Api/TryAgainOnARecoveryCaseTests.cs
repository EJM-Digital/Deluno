using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Try again on a Transfers recovery case (#558).
///
/// <para>The button posted to <c>/api/{movies|series}/import-recovery/{id}/retry</c>,
/// and nothing answered it: every press returned a 404 and the screen said
/// "That import could not be tried again". No test pressed it. These do, and
/// they check each kind of case does its own thing.</para>
/// </summary>
public sealed class TryAgainOnARecoveryCaseTests
{
    private const string Release = "Lost.Film.2010.1080p.WEB-DL-DELUNO";

    [Fact]
    public async Task A_download_the_client_never_showed_is_ended_and_its_title_searched_for_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-lost", Release, "Fixture", "client-1", "SABnzbd", "sent", null,
            cancellationToken: CancellationToken.None);
        await dispatches.MarkNeverDetectedAsync(dispatchId, "SABnzbd accepted this download and then never showed it.", CancellationToken.None);
        var recoveryCase = await AddCaseAsync(app, "grab-timeout", JsonSerializer.Serialize(new { DispatchId = dispatchId }));

        var response = await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{recoveryCase.Id}/retry", new { });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("searching for", body);
        Assert.DoesNotContain(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/movies/import-recovery"));
        // The lost download is over, so it no longer holds its title off the work list.
        Assert.Null(await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None));
        var search = Assert.Single(
            await jobs.ListAsync(50, CancellationToken.None),
            job => job.Source == "recovery-case.retry");
        Assert.Equal(LibrarySearchJobTypes.For("movies"), search.JobType);
        Assert.Contains("\"targetEntityId\":\"movie-lost\"", search.PayloadJson);
    }

    [Fact]
    public async Task A_failed_import_is_run_again_and_nothing_else_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-import", Release, "Fixture", "client-1", "SABnzbd", "sent", null,
            cancellationToken: CancellationToken.None);
        var thisImport = await FailedImportAsync(app, dispatchId);
        var otherImport = await FailedImportAsync(app, "some-other-download");
        var recoveryCase = await AddCaseAsync(app, "import-failed", JsonSerializer.Serialize(new { DispatchId = dispatchId }));

        var response = await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{recoveryCase.Id}/retry", new { });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("importing", body);
        var all = await jobs.ListAsync(50, CancellationToken.None);
        Assert.Equal("queued", all.Single(job => job.Id == thisImport).Status);
        Assert.Equal("dead-letter", all.Single(job => job.Id == otherImport).Status);
    }

    [Fact]
    public async Task A_failed_import_that_is_gone_says_so_and_leaves_the_case_open()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-gone", Release, "Fixture", "client-1", "SABnzbd", "sent", null,
            cancellationToken: CancellationToken.None);
        var recoveryCase = await AddCaseAsync(app, "import-failed", JsonSerializer.Serialize(new { DispatchId = dispatchId }));

        var response = await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{recoveryCase.Id}/retry", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("no longer has a failed import", await response.Content.ReadAsStringAsync());
        Assert.Contains(recoveryCase.Id, await ApiPayload.ListIdsAsync(app.Client, "/api/movies/import-recovery"));
    }

    [Fact]
    public async Task A_case_that_names_no_download_says_there_is_nothing_to_try_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var recoveryCase = await AddCaseAsync(app, "grab-timeout", detailsJson: null);

        var response = await app.Client.PostAsJsonAsync($"/api/movies/import-recovery/{recoveryCase.Id}/retry", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("does not say which download", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_case_that_does_not_exist_is_not_found_on_either_side()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/movies/import-recovery/nope/retry", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/series/import-recovery/nope/retry", new { })).StatusCode);
    }

    private static async Task<LibraryItem> CreateLibraryAsync(ApplicationTestHost app)
        => await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(app.DataRoot, "movies"), Path.Combine(app.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25),
            CancellationToken.None);

    private static Task<MovieImportRecoveryCase> AddCaseAsync(ApplicationTestHost app, string kind, string? detailsJson)
        => app.Services.GetRequiredService<IMovieCatalogRepository>().AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(Release, kind, "Something went wrong.", "Try again.", detailsJson),
            CancellationToken.None);

    private static async Task<string> FailedImportAsync(ApplicationTestHost app, string dispatchId)
    {
        var job = await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "filesystem.import.execute",
                Source: "download-client",
                PayloadJson: JsonSerializer.Serialize(new { DispatchId = dispatchId, ReleaseName = Release }),
                RelatedEntityType: null,
                RelatedEntityId: null),
            CancellationToken.None);

        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE job_queue SET status = 'dead-letter', last_error = 'The import failed.', completed_utc = $at WHERE id = $id;";
        foreach (var (name, value) in new (string, object)[] { ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", job.Id) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
        return job.Id;
    }
}
