using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1186 on the report the real Weir 1.0.0-rc.13 sent Deluno when it was handed a download that had been deleted
/// (tests/fixtures/recorded/deluno/1.0.0-rc.30.30/events-failed-source-gone): failureClass source_gone, sourceRemoved false, no files.
/// Deluno closes it with one line when it removed the download itself, otherwise it asks a person only for another copy or a dismiss,
/// and nothing is blocklisted either way.
/// </summary>
public sealed class AWeirReportThatTheDownloadWasGoneTests
{
    private const string State = "events-failed-source-gone";
    private const string Client = "sabnzbd";
    private const string Item = "nzo-gone-1";

    private static string Release => RecordedWeirReports.ReleaseOf(RecordedWeirReports.Exchange(State));

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<IReadOnlyList<ActivityEventItem>> ActivityAsync(ApplicationTestHost app, string handoffId)
        => await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", handoffId, CancellationToken.None);

    private static async Task<int> BlockedCountAsync(ApplicationTestHost app)
        => JsonDocument.Parse(await app.Client.GetStringAsync("/api/blocked-releases")).RootElement.GetArrayLength();

    private static async Task<(string LibraryId, string Output)> LibraryAsync(ApplicationTestHost app)
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
        return (library.Id, output);
    }

    private static async Task<string> DispatchAsync(ApplicationTestHost app, string libraryId, string entityId, string item, bool archived)
    {
        var dispatchId = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId, "movies", "movie", entityId, Release, "Indexer", Client, "SABnzbd", "queued", null, cancellationToken: CancellationToken.None);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: item);
        if (archived)
        {
            await dispatches.ArchiveDispatchAsync(dispatchId, "Gave up on this release and removed it from the client.", CancellationToken.None);
        }

        return dispatchId;
    }

    private static async Task<ProcessorHandoffItem> SubmittedHandoffAsync(ApplicationTestHost app, string libraryId, string item)
    {
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(libraryId, "movies", Client, item, Release, Path.Combine(app.DataRoot, "Downloads", Release), "Weir"),
            CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        return handoff;
    }

    private static async Task<(string LibraryId, string Output, ProcessorHandoffItem Handoff)> SceneAsync(ApplicationTestHost app, bool archived)
    {
        var (libraryId, output) = await LibraryAsync(app);
        await DispatchAsync(app, libraryId, "movie-1", Item, archived);
        return (libraryId, output, await SubmittedHandoffAsync(app, libraryId, Item));
    }

    private static async Task<HttpResponseMessage> ReportAsync(ApplicationTestHost app, string libraryId, string output, string handoffId)
        => await RecordedWeirReports.PostAsync(app.Client, RecordedWeirReports.Body(RecordedWeirReports.Exchange(State), libraryId, handoffId, output));

    [Fact]
    public async Task A_download_Deluno_did_not_remove_is_one_plain_row_that_asks_only_for_another_copy_and_the_answer_is_the_one_Weir_was_given()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, output, handoff) = await SceneAsync(app, archived: false);
        var recordedReply = RecordedWeirReports.Exchange(State).Reply;

        var response = await ReportAsync(app, libraryId, output, handoff.Id);

        Assert.Equal(RecordedWeirReports.RecordedStatus(RecordedWeirReports.Exchange(State)), response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonDocument.Parse(recordedReply.Body!).RootElement.GetProperty("status").GetString(), answer.GetProperty("status").GetString());
        Assert.False(answer.GetProperty("settled").GetBoolean());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.SourceGone, stored!.Status);
        Assert.Equal("The download was gone before Weir could clean it.", stored.FailureMessage);
        Assert.True(ProcessorHandoffStatuses.NeedsPerson(stored.Status, stored.FailureMessage));
        Assert.False(ProcessorHandoffStatuses.CanReleaseOriginal(stored.Status));
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/retry", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original", new { })).StatusCode);
        Assert.Single(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone");
        Assert.Equal(0, await BlockedCountAsync(app));

        // Weir sends it again (as it does until answered): no second line.
        Assert.Equal(HttpStatusCode.Accepted, (await ReportAsync(app, libraryId, output, handoff.Id)).StatusCode);
        Assert.Single(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone");
    }

    [Fact]
    public async Task A_download_Deluno_removed_itself_is_settled_with_one_line_and_leaves_no_row_for_a_person()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, output, handoff) = await SceneAsync(app, archived: true);

        var response = await ReportAsync(app, libraryId, output, handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored!.Status);
        var settled = Assert.Single(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone.settled");
        Assert.Contains("Deluno had already closed that download", settled.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone");
        Assert.Equal(0, await BlockedCountAsync(app));
    }

    [Fact]
    public async Task Searching_for_another_copy_after_the_report_searches_without_blocklisting_and_dismissing_it_blocklists_nothing_either()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, output, handoff) = await SceneAsync(app, archived: false);
        await ReportAsync(app, libraryId, output, handoff.Id);

        var search = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/search-again", new { });

        Assert.True(search.IsSuccessStatusCode, await search.Content.ReadAsStringAsync());
        Assert.Contains("did not blocklist", (await search.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, await BlockedCountAsync(app));
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        var jobs = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job => job.JobType == LibrarySearchJobTypes.Movies && job.PayloadJson!.Contains("movie-1"));

        var second = await SceneSecondAsync(app, libraryId);
        await ReportAsync(app, libraryId, output, second.Id);
        var dismissed = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{second.Id}/dismiss", new { });
        Assert.True(dismissed.IsSuccessStatusCode, await dismissed.Content.ReadAsStringAsync());
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(second.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, await BlockedCountAsync(app));
    }

    [Fact]
    public async Task A_report_for_a_hand_off_that_already_finished_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, output, handoff) = await SceneAsync(app, archived: false);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Completed, null, null, null, CancellationToken.None);

        var response = await ReportAsync(app, libraryId, output, handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Completed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.DoesNotContain(await ActivityAsync(app, handoff.Id), item => item.Category.StartsWith("processing.source-gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_title_that_already_holds_the_file_that_download_was_for_is_settled_with_one_line_and_leaves_no_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, output) = await LibraryAsync(app);
        var handoff = await SubmittedHandoffAsync(app, libraryId, Item);
        // The film arrives in its folder after the hand-off was made (a copy of the same release, imported another way).
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films", "Gprecgonecgyh02 (2020)")).FullName;
        File.WriteAllText(Path.Combine(folder, Release), "not really a film");
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Gprecgonecgyh02", releaseYear = 2020, monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.True((await app.Services.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>().GetByIdAsync(movieId, CancellationToken.None))!.HasFile, "the film holds its file");
        await DispatchAsync(app, libraryId, movieId, Item, archived: false);

        var response = await ReportAsync(app, libraryId, output, handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        var settled = Assert.Single(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone.settled");
        Assert.Contains("The title is in the library already", settled.Message, StringComparison.Ordinal);
        Assert.Equal(0, await BlockedCountAsync(app));
    }

    [Fact]
    public async Task A_gone_download_is_judged_by_its_latest_send_not_by_an_older_archived_send_of_the_same_release()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, output) = await LibraryAsync(app);
        // Search for another copy sent the same release again: the first send was closed, the second is the one that went missing.
        await DispatchAsync(app, libraryId, "movie-1", Item, archived: true);
        await Task.Delay(TimeSpan.FromMilliseconds(1100)); // reason: the two sends must carry different created times, which are kept to the second
        await DispatchAsync(app, libraryId, "movie-1", Item, archived: false);
        var handoff = await SubmittedHandoffAsync(app, libraryId, Item);

        var response = await ReportAsync(app, libraryId, output, handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.SourceGone, stored!.Status);
        Assert.Single(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone");
        Assert.DoesNotContain(await ActivityAsync(app, handoff.Id), item => item.Category == "processing.source-gone.settled");
    }

    private static async Task<ProcessorHandoffItem> SceneSecondAsync(ApplicationTestHost app, string libraryId)
    {
        var other = Release.Replace("GOLDEN", "GOLDEN2", StringComparison.Ordinal);
        var downloads = Path.Combine(app.DataRoot, "Downloads");
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(libraryId, "movies", Client, "nzo-gone-2", other, Path.Combine(downloads, other), "Weir"),
            CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        return handoff;
    }
}
