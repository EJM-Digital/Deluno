using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1250 (Weir #956): two connections share one download folder, and the processor refuses a second hand-off for a file
/// another connection's pass owns. It reports failed, failureClass not_taken, no disposition, sourceRemoved false.
/// Nothing is wrong with the release and the processor holds nothing, so Deluno sends it again after a back-off, and only
/// after a few tries says, once and clearably, that another connection keeps taking this file.
/// </summary>
public sealed class AHandOffWeirDidNotTakeBecauseAnotherConnectionOwnsTheFileTests
{
    private const string Release = "Shared.Folder.2021.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    // The reply Weir sends today (Weir #956): failed, not_taken, no disposition, source not removed.
    private static Task<HttpResponseMessage> NotTakenAsync(ApplicationTestHost app, string libraryId, string handoffId)
        => app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId,
            handoffId,
            status = "failed",
            failureClass = "not_taken",
            sourceRemoved = false,
            message = "Weir is already processing this file for another connection, so it did not take this hand-off."
        });

    public static TheoryData<string, string, string> Clients => new()
    {
        { "transmission-1", "Transmission", "hash-shared" },
        { "sabnzbd", "SABnzbd", "nzo-shared" }
    };

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_first_hand_off_that_was_not_taken_waits_to_be_sent_again_and_raises_nothing(string client, string clientName, string item)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, client, clientName, item);

        var response = await NotTakenAsync(app, libraryId, handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        // Waiting to be sent again, not a failure and not something a person is asked about.
        Assert.Equal(ProcessorHandoffStatuses.Waiting, stored.Status);
        Assert.Equal("Weir is busy with this file for another connection; Deluno will send it again shortly.", stored.FailureMessage);
        Assert.False(ProcessorHandoffStatuses.NeedsPerson(stored.Status, stored.FailureMessage));

        await AssertNothingRaisedOrTouchedAsync(app, handoff);
        Assert.Single(await ActivityAsync(app, handoff.Id), line => line.Category == "processing.not-taken");

        // The same report again (Weir resends an event) is not a second try.
        Assert.Equal(HttpStatusCode.Accepted, (await NotTakenAsync(app, libraryId, handoff.Id)).StatusCode);
        Assert.Single(await ActivityAsync(app, handoff.Id), line => line.Category == "processing.not-taken");
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_hand_off_not_taken_and_then_accepted_on_a_retry_settles_normally(string client, string clientName, string item)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, client, clientName, item);
        await NotTakenAsync(app, libraryId, handoff.Id);

        // The planner's next send is accepted, and Weir then reports the file cleaned.
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Started, null, null, null, CancellationToken.None);
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var file = Path.Combine(output, Release + ".mkv");
        await File.WriteAllBytesAsync(file, new byte[2048]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));
        var done = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new { libraryId, handoffId = handoff.Id, status = "completed", outputPath = file });

        Assert.Equal(HttpStatusCode.Accepted, done.StatusCode);
        var stored = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored.Status);
        Assert.False(ProcessorHandoffStatuses.NeedsPerson(stored.Status, stored.FailureMessage), stored.FailureMessage);
        await AssertNothingRaisedOrTouchedAsync(app, handoff);
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_hand_off_not_taken_every_time_becomes_one_clearable_problem_naming_the_shared_folder(string client, string clientName, string item)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, client, clientName, item);

        // Three sends again, each answered not taken (sent and refused: Submitted, then the report).
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await NotTakenAsync(app, libraryId, handoff.Id)).StatusCode);
            var waiting = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
            Assert.Equal(ProcessorHandoffStatuses.Waiting, waiting.Status);
            await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await NotTakenAsync(app, libraryId, handoff.Id)).StatusCode);

        var stored = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Failed, stored.Status);
        Assert.True(ProcessorHandoffStatuses.NeedsPerson(stored.Status, stored.FailureMessage));
        Assert.Contains("another connection", stored.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("download folder", stored.FailureMessage);
        Assert.DoesNotContain("not_taken", stored.FailureMessage);

        // One problem, said once, and still nothing held, blocklisted or touched.
        var lines = await ActivityAsync(app, handoff.Id);
        Assert.Single(lines, line => line.Category == "processing.not-taken.gave-up");
        Assert.Equal(3, lines.Count(line => line.Category == "processing.not-taken"));
        await AssertNothingRaisedOrTouchedAsync(app, handoff);

        // It clears from the screen like any other problem.
        var dismissed = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/dismiss", new { });
        Assert.True(dismissed.IsSuccessStatusCode, await dismissed.Content.ReadAsStringAsync());
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_not_taken_report_for_a_download_that_was_removed_stays_closed(string client, string clientName, string item)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, client, clientName, item);
        // What removing the download does to its hand-off (#1216).
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, "You removed it.", CancellationToken.None);

        var response = await NotTakenAsync(app, libraryId, handoff.Id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored.Status);
        Assert.Equal("You removed it.", stored.FailureMessage);
        Assert.DoesNotContain(await ActivityAsync(app, handoff.Id), line => line.Category.StartsWith("processing.not-taken", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_hand_off_waiting_to_be_sent_again_is_still_with_the_processor_so_removing_the_download_closes_it(string client, string clientName, string item)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, client, clientName, item);
        await NotTakenAsync(app, libraryId, handoff.Id);

        // Removal closes a hand-off in any state that is still with the processor; one waiting to be sent again is one.
        var waiting = (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.True(ProcessorHandoffStatuses.IsWithProcessor(waiting.Status));
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, "Deluno removed it.", CancellationToken.None);

        // A late not-taken report neither reopens it nor counts as a try.
        Assert.Equal(HttpStatusCode.Accepted, (await NotTakenAsync(app, libraryId, handoff.Id)).StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_held_report_still_raises_its_case()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoff) = await SceneAsync(app, "transmission-1", "Transmission", "hash-shared");

        var held = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId, handoffId = handoff.Id, status = "failed", failureClass = "execution", disposition = "held", sourceRemoved = false, message = "Could not clean it."
        });

        Assert.Equal(HttpStatusCode.Accepted, held.StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Failed, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.NotEmpty((await app.Services.GetRequiredService<IMovieCatalogRepository>().GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
    }

    private static async Task AssertNothingRaisedOrTouchedAsync(ApplicationTestHost app, ProcessorHandoffItem handoff)
    {
        // No held or other recovery case, and nothing blocklisted.
        Assert.Empty((await app.Services.GetRequiredService<IMovieCatalogRepository>().GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        using var blocked = JsonDocument.Parse(await (await app.Client.GetAsync("/api/blocked-releases")).Content.ReadAsStringAsync());
        Assert.Empty(blocked.RootElement.EnumerateArray());

        // The download itself is untouched: its send is still open.
        var open = await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .FindOpenDispatchesByHashAsync(handoff.ClientId, handoff.QueueItemId, CancellationToken.None);
        Assert.NotEmpty(open);
    }

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<IReadOnlyList<ActivityEventItem>> ActivityAsync(ApplicationTestHost app, string handoffId)
        => await app.Services.GetRequiredService<IActivityFeedRepository>()
            .ListActivityAsync(100, "processor-handoff", handoffId, CancellationToken.None);

    private static async Task<(string LibraryId, ProcessorHandoffItem Handoff)> SceneAsync(ApplicationTestHost app, string client, string clientName, string item)
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

        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Shared Folder", releaseYear = 2021, monitored = true });
        added.EnsureSuccessStatusCode();
        var film = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var dispatchId = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", film, Release, "Indexer", client, clientName, "queued", null,
            cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: item);

        var source = Directory.CreateDirectory(Path.Combine(downloads, Release)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, Release + ".mkv"), new byte[64]);
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "movies", client, item, Release, source, "Weir"),
            CancellationToken.None);
        await Repository(app).UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        return (library.Id, handoff);
    }
}
