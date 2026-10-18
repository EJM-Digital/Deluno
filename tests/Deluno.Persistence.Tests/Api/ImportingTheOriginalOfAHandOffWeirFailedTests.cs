using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Import original" on a hand-off the processor did not finish (#511), against what Weir really reported: a hand-off that Weir's
/// own failure report (tests/fixtures/recorded/deluno, events-failed-*) leaves failed is released so its untouched download is
/// imported, Deluno answers as it was recorded answering (reply-import-original-accepted: 202, the hand-off now released), one
/// Activity line says so, and nothing is deleted or blocklisted. Pressing it twice is refused, as recorded.
/// </summary>
public sealed class ImportingTheOriginalOfAHandOffWeirFailedTests
{
    private const string Client = "qbittorrent";
    private const string Item = "queue-1";

    [Theory]
    [InlineData("events-failed-unreadable")]
    [InlineData("events-failed-under-minimum")]
    public async Task A_hand_off_Weir_reported_failed_is_released_to_import_its_original_and_Deluno_answers_as_it_was_recorded_answering(string weirsReport)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff, source, output) = await SceneAsync(app);
        var body = RecordedWeirReports.Body(RecordedWeirReports.Exchange(weirsReport), libraryId, handoff.Id, output, "Weir.Failed.Film.2020.1080p.WEB-DL.x264-GOLDEN");
        var report = await RecordedWeirReports.PostAsync(app.Client, body);
        Assert.Equal(RecordedWeirReports.RecordedStatus(RecordedWeirReports.Exchange(weirsReport)), report.StatusCode);
        var failed = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.True(ProcessorHandoffStatuses.CanReleaseOriginal(failed!.Status), failed.Status);

        var recorded = RecordedReplies.Deluno("reply-import-original-accepted").Exchanges[0].Reply;
        using var response = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original", content: null);

        Assert.Equal((HttpStatusCode)recorded.Status, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(recorded.Json()["status"]!.GetValue<string>(), answer.GetProperty("status").GetString());
        Assert.Equal(ProcessorHandoffStatuses.Released, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Equal(
            recorded.Json().AsObject().Select(property => property.Key).OrderBy(name => name).ToArray(),
            answer.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());

        // One Activity line says the original will be imported unprocessed; nothing is blocklisted and the download is not touched.
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", handoff.Id, CancellationToken.None);
        var line = Assert.Single(activity, item => item.Category == "processing.released");
        Assert.Contains("will import the original", line.Message, StringComparison.Ordinal);
        Assert.Contains("without processing", line.Message, StringComparison.Ordinal);
        Assert.Empty(await app.Services.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(source, "Weir.Failed.Film.2020.1080p.WEB-DL.x264-GOLDEN.mkv")), "the original is left where it is for the import");

        // Pressed again, it is refused as recorded, with no second line.
        var notYet = RecordedReplies.Deluno("reply-import-original-not-yet").Exchanges[0].Reply;
        using var again = await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original", content: null);
        Assert.Equal((HttpStatusCode)notYet.Status, again.StatusCode);
        activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", handoff.Id, CancellationToken.None);
        Assert.Single(activity, item => item.Category == "processing.released");
    }

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<(string LibraryId, ProcessorHandoffItem Handoff, string Source, string Output)> SceneAsync(ApplicationTestHost app)
    {
        const string release = "Weir.Failed.Film.2020.1080p.WEB-DL.x264-GOLDEN";
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films", mediaType = "movies", rootPath = root, downloadsPath = downloads,
            importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;
        var source = Directory.CreateDirectory(Path.Combine(downloads, release)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, release + ".mkv"), new byte[4096]);

        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(library.Id, "movies", "movie", "movie-1", release, "Indexer", Client, "qBittorrent", "queued", null, cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: Item);
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(new CreateProcessorHandoffRequest(library.Id, "movies", Client, Item, release, source, "Weir"), CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        return (library.Id, handoff, source, output);
    }
}
