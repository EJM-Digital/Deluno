using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Deleting a library, a download client or a processor ends the work it was
/// doing (#567, #568, #569).
///
/// <para><b>Why this file exists.</b> Each delete used to remove one row and
/// nothing else. The poller has no idea a client or library has gone, so it
/// went on working through downloads that could never be detected and raising a
/// fresh recovery case for each; hand-offs went on waiting for a processor that
/// could no longer be asked, and after the library's limit — six hours by
/// default — each one both timed out and raised a case naming a processor
/// somebody had deliberately deleted. Every one of those is counted under Needs
/// attention.</para>
/// </summary>
public sealed class DeletingSomethingEndsWhatItWasDoingTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public async Task Deleting_a_processor_drops_the_files_it_still_has_and_settles_their_cases()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var connection = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "weir", "http://127.0.0.1:9347/submit", null, null, IsEnabled: true),
            CancellationToken.None);
        var handoff = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("library-1", "movies", "sabnzbd", "nzo-1", Release, @"C:\Downloads\Tears", "Weir"),
            CancellationToken.None);
        await processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        var raised = await RaiseCaseAsync(app, $$"""{"libraryId":"library-1","handoffId":"{{handoff.Id}}","releaseName":"{{Release}}"}""");

        var response = await app.Client.DeleteAsync($"/api/integrations/processors/connections/{connection.Id}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var ended = await processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, ended?.Status);
        Assert.Contains("Weir was removed from Deluno", ended?.FailureMessage ?? string.Empty);
        Assert.DoesNotContain(raised, await OpenCaseIdsAsync(app));
    }

    [Fact]
    public async Task Deleting_a_download_client_archives_its_downloads_and_settles_their_cases()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await AddClientAsync(app);
        var dispatchId = await RecordDispatchAsync(app, client.Id, client.Name, "library-1");
        var raised = await RaiseCaseAsync(app, $$"""{"DispatchId":"{{dispatchId}}","ReleaseName":"{{Release}}"}""");

        var response = await app.Client.DeleteAsync($"/api/download-clients/{client.Id}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .GetDispatchIncludingArchivedAsync(dispatchId, CancellationToken.None);
        Assert.Equal("archived", stored?.Status);
        Assert.DoesNotContain(raised, await OpenCaseIdsAsync(app));
    }

    [Fact]
    public async Task Deleting_a_library_cancels_its_work_and_settles_the_cases_about_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await AddLibraryAsync(app);
        var client = await AddClientAsync(app);
        var dispatchId = await RecordDispatchAsync(app, client.Id, client.Name, library);
        var raised = await RaiseCaseAsync(app, $$"""{"DispatchId":"{{dispatchId}}"}""");

        var response = await app.Client.DeleteAsync($"/api/libraries/{library}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .GetDispatchIncludingArchivedAsync(dispatchId, CancellationToken.None);
        Assert.Equal("archived", stored?.Status);
        Assert.DoesNotContain(raised, await OpenCaseIdsAsync(app));
    }

    /// <summary>
    /// A download that finished is still one of the client's downloads: a
    /// successful import archives its dispatch at once, and a case raised about
    /// one of those is still open and still counted.
    /// </summary>
    [Fact]
    public async Task A_case_about_a_finished_download_settles_with_its_client_too()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await AddClientAsync(app);
        var dispatchId = await RecordDispatchAsync(app, client.Id, client.Name, "library-1");
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.ArchiveDispatchAsync(dispatchId, "Imported.", CancellationToken.None);
        var raised = await RaiseCaseAsync(app, $$"""{"DispatchId":"{{dispatchId}}"}""");

        await app.Client.DeleteAsync($"/api/download-clients/{client.Id}");

        Assert.DoesNotContain(raised, await OpenCaseIdsAsync(app));
    }

    /// <summary>
    /// A health record ends when the client lists the download again and the
    /// finding is gone. A deleted client is never asked again, so its records
    /// would sit until the 90-day retention with no screen able to clear them.
    /// </summary>
    [Fact]
    public async Task What_was_recorded_about_a_deleted_clients_downloads_is_forgotten()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var going = await AddClientAsync(app, "Going");
        var staying = await AddClientAsync(app, "Staying");
        var health = app.Services.GetRequiredService<IDownloadHealthRepository>();
        foreach (var client in new[] { going, staying })
        {
            await health.RecordDownloadHealthObservationsAsync(
                client.Id,
                ["item-1"],
                [new DownloadHealthObservation(client.Id, "item-1", Release, DownloadHealthStrikes.ClientStalled, "warning", "No throughput for 30 minutes.")],
                CancellationToken.None);
        }

        await app.Client.DeleteAsync($"/api/download-clients/{going.Id}");

        var left = await health.ListDownloadHealthRecordsAsync(100, CancellationToken.None);
        Assert.DoesNotContain(left, record => record.ClientId == going.Id);
        Assert.Contains(left, record => record.ClientId == staying.Id);
    }

    /// <summary>Another client's download is not this deletion's to end.</summary>
    [Fact]
    public async Task Only_the_deleted_clients_downloads_are_archived()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var going = await AddClientAsync(app, "Going");
        var staying = await AddClientAsync(app, "Staying");
        var keptId = await RecordDispatchAsync(app, staying.Id, staying.Name, "library-1");

        await app.Client.DeleteAsync($"/api/download-clients/{going.Id}");

        var stored = await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .GetDispatchIncludingArchivedAsync(keptId, CancellationToken.None);
        Assert.NotEqual("archived", stored?.Status);
    }

    private static async Task<string> RaiseCaseAsync(ApplicationTestHost app, string detailsJson)
    {
        var raised = await app.Services.GetRequiredService<IMovieCatalogRepository>().AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(Release, "import-timeout", "The file never arrived.", "Check the client.", detailsJson),
            CancellationToken.None);
        return raised.Id;
    }

    private static async Task<string[]> OpenCaseIdsAsync(ApplicationTestHost app)
    {
        var summary = await app.Services.GetRequiredService<IMovieCatalogRepository>()
            .GetImportRecoverySummaryAsync(CancellationToken.None);
        return [.. summary.RecentCases.Select(item => item.Id)];
    }

    private static async Task<DownloadClientItem> AddClientAsync(ApplicationTestHost app, string name = "SABnzbd")
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            name,
            protocol = "sabnzbd",
            baseUrl = "http://127.0.0.1:8085",
            apiKey = "k",
            isEnabled = true
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DownloadClientItem>())!;
    }

    private static async Task<string> AddLibraryAsync(ApplicationTestHost app)
    {
        // Real folders, because a library will not be added pointing at one that
        // is not there.
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = root,
            downloadsPath = downloads
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        return body!.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> RecordDispatchAsync(
        ApplicationTestHost app,
        string clientId,
        string clientName,
        string libraryId)
        => await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId,
            "movies",
            "movie",
            "m1",
            Release,
            "Proof Newznab",
            clientId,
            clientName,
            "sent",
            null,
            cancellationToken: CancellationToken.None);
}
