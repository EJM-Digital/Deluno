using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1186: a processor says the download was gone before it could clean it (failed, source_gone, sourceRemoved
/// false, no disposition). Nothing can be tried again and nothing is the release's fault, so the report is closed
/// when Deluno removed the download itself, and otherwise asks a person only for another copy or a dismiss.
/// </summary>
public sealed class AProcessorReportThatTheDownloadWasGoneTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
    private const string Client = "sabnzbd";
    private const string Item = "nzo-1";

    [Fact]
    public async Task A_download_Deluno_did_not_remove_shows_one_plain_row_with_no_try_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, archived: false);

        var response = await ReportAsync(app, libraryId, handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.SourceGone, stored!.Status);
        Assert.Equal("The download was gone before Weir could clean it.", stored.FailureMessage);
        Assert.True(ProcessorHandoffStatuses.NeedsPerson(stored.Status, stored.FailureMessage));
        Assert.Contains(ProcessorHandoffStatuses.SourceGone, ProcessorHandoffStatuses.WentWrong);

        // Never offered Try again, nor the original.
        Assert.False(ProcessorHandoffStatuses.CanReleaseOriginal(stored.Status));
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/retry", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original", new { })).StatusCode);

        // One Activity line, and nothing blocklisted.
        Assert.Single(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone");
        Assert.Empty(await BlockedAsync(app));

        // The same report again does nothing new.
        var again = await ReportAsync(app, libraryId, handoff.Id);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Single(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone");
    }

    [Fact]
    public async Task A_download_Deluno_removed_itself_settles_with_a_line_and_no_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, archived: true);

        var response = await ReportAsync(app, libraryId, handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored!.Status);
        var settled = Assert.Single(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone.settled");
        Assert.Contains("Deluno removed that download itself", settled.Message);
        Assert.DoesNotContain(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone");
        Assert.Empty(await BlockedAsync(app));
    }

    [Fact]
    public async Task Searching_for_another_copy_searches_without_blocklisting_and_clears_the_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, archived: false);
        await ReportAsync(app, libraryId, handoff.Id);

        var search = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", new { });

        Assert.True(search.IsSuccessStatusCode, await search.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.Contains("did not blocklist", body.RootElement.GetProperty("message").GetString());
        Assert.Empty(await BlockedAsync(app));
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored!.Status);
        var jobs = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job => job.JobType == LibrarySearchJobTypes.Movies && job.PayloadJson!.Contains("movie-1"));
    }

    [Fact]
    public async Task Dismissing_the_row_clears_it_and_blocklists_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, archived: false);
        await ReportAsync(app, libraryId, handoff.Id);

        var dismissed = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/dismiss", new { });

        Assert.True(dismissed.IsSuccessStatusCode, await dismissed.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored!.Status);
        Assert.Empty(await BlockedAsync(app));
    }

    [Fact]
    public async Task A_report_for_a_handoff_already_completed_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, archived: false);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Completed, null, null, null, CancellationToken.None);

        var response = await ReportAsync(app, libraryId, handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Completed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
    }

    private static async Task<HttpResponseMessage> ReportAsync(ApplicationTestHost app, string libraryId, string handoffId)
        => await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId,
            handoffId,
            status = "failed",
            failureClass = "source_gone",
            sourceRemoved = false,
            message = "The download is no longer there, so Weir had nothing to do."
        });

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<IReadOnlyList<ActivityEventItem>> ActivityAsync(ApplicationTestHost app, string handoffId)
        => await app.Services.GetRequiredService<IActivityFeedRepository>()
            .ListActivityAsync(50, "processor-handoff", handoffId, CancellationToken.None);

    private static async Task<JsonElement[]> BlockedAsync(ApplicationTestHost app)
    {
        var blocked = await app.Client.GetAsync("/api/blocked-releases");
        using var body = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        return body.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static async Task<(string LibraryId, ProcessorHandoffItem Handoff)> SceneAsync(ApplicationTestHost app, bool archived)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
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
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;

        var dispatchId = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-1", Release, "Indexer", Client, "SABnzbd", "queued", null,
            cancellationToken: CancellationToken.None);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: Item);
        if (archived)
        {
            await dispatches.ArchiveDispatchAsync(dispatchId, "Gave up on this release and removed it from the client.", CancellationToken.None);
        }

        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "movies", Client, Item, Release, Path.Combine(downloads, Release), "Weir"),
            CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        return (library.Id, handoff);
    }
}
