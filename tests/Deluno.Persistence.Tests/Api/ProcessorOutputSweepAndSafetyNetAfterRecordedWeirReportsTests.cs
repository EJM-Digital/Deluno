using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What is left in a processor's output folder, after the report Weir really sent Deluno (tests/fixtures/recorded/deluno: a file
/// Weir could not read yet): the sweep clears the output of a hand-off whose original was imported instead, leaves the output of a
/// hand-off the person told to keep waiting, and never touches the download folder or an item no hand-off claims; the safety net
/// lists only what no hand-off is waiting for.
/// </summary>
public sealed class ProcessorOutputSweepAndSafetyNetAfterRecordedWeirReportsTests
{
    private const string Report = "events-failed-unreadable";

    private sealed record Scene(string LibraryId, string Downloads, string Output);

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    private static string ReleaseOf() => JsonNode.Parse(RecordedReplies.Deluno(Report).Exchanges[0].Request.Body!)!["releaseName"]!.GetValue<string>();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films", mediaType = "movies", rootPath = root, downloadsPath = downloads,
            importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return new Scene((await response.Content.ReadFromJsonAsync<LibraryItem>())!.Id, downloads, output);
    }

    /// <summary>A hand-off for the recorded release, and the very report Weir sent for it.</summary>
    private static async Task<ProcessorHandoffItem> ReportedAsync(ApplicationTestHost app, Scene scene)
    {
        var release = ReleaseOf();
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(scene.LibraryId, "movies", "qbittorrent", $"queue-{release}", release, Path.Combine(scene.Downloads, release), "Weir"),
            CancellationToken.None);
        var body = JsonNode.Parse(RecordedReplies.Deluno(Report).Exchanges[0].Request.Body!)!.AsObject();
        body["libraryId"] = scene.LibraryId;
        body["handoffId"] = handoff.Id;
        var posted = await app.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        return handoff;
    }

    private static string Leftover(Scene scene, string name)
    {
        var path = Path.Combine(scene.Output, name);
        File.WriteAllBytes(path, new byte[2048]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-30));
        return path;
    }

    private static async Task<int> SweepAsync(ApplicationTestHost app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ProcessorOutputSweep>().SweepAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<HealthCheckOutcome>> SafetyNetAsync(ApplicationTestHost app)
    {
        using var scope = app.Services.CreateScope();
        var check = scope.ServiceProvider.GetServices<IHealthCheck>().OfType<Deluno.Worker.HealthChecks.Checks.ProcessorOutputSafetyNetHealthCheck>().Single();
        return (await check.RunAsync(CancellationToken.None)).ToArray();
    }

    [Fact]
    public async Task Once_the_person_imports_the_original_the_processors_leftover_for_that_hand_off_is_cleared_and_nothing_else_is_touched()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await ReportedAsync(app, scene);
        var leftover = Leftover(scene, ReleaseOf());
        var stranger = Leftover(scene, "Some.Other.Film.1999.1080p-XYZ.mkv");
        var download = Path.Combine(scene.Downloads, ReleaseOf());
        await File.WriteAllBytesAsync(download, [1, 2, 3]);
        Assert.Equal(HttpStatusCode.Accepted, (await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original", content: null)).StatusCode);
        Assert.Equal(ProcessorHandoffStatuses.Released, (await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);

        var removed = await SweepAsync(app);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(leftover), "the processor's copy of a download whose original was imported will never be imported");
        Assert.True(File.Exists(stranger), "an item no hand-off claims is left to the safety net");
        Assert.True(File.Exists(download), "the download itself is not the processor's to clear");
        Assert.True(Directory.Exists(scene.Output), "only the hand-off's own item goes, never the output folder");
        var said = Assert.Single(await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, null, null, CancellationToken.None), line => line.Category == ProcessorOutputSweep.ActivityCategory);
        Assert.Contains("imported the original download instead", said.Message);
    }

    [Fact]
    public async Task While_the_person_keeps_waiting_for_Weir_its_output_is_never_swept_and_is_not_listed_as_a_stray()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await ReportedAsync(app, scene);
        var leftover = Leftover(scene, ReleaseOf());
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/keep-waiting", content: null)).StatusCode);

        var removed = await SweepAsync(app);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(leftover), "a hand-off that may still receive its output owns the item");
        Assert.DoesNotContain(await SafetyNetAsync(app), outcome => outcome.Message.Contains(ReleaseOf(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_in_the_output_folder_that_no_hand_off_is_waiting_for_is_listed_for_the_person_and_never_deleted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var stray = Leftover(scene, ReleaseOf());

        var listed = Assert.Single(await SafetyNetAsync(app));

        Assert.Contains(ReleaseOf(), listed.Message, StringComparison.Ordinal);
        Assert.Contains("clear it", listed.Fix, StringComparison.Ordinal);
        Assert.Equal(0, await SweepAsync(app));
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public async Task A_media_file_and_a_folder_of_scraps_are_both_listed_and_only_the_media_is_offered_for_import()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var film = Leftover(scene, "Stray.Film.2020.1080p.mkv");
        var scraps = Leftover(scene, "notes.nfo");

        var listed = await SafetyNetAsync(app);

        Assert.Equal(2, listed.Count);
        Assert.Contains("\"isMedia\":true", Assert.Single(listed, outcome => outcome.Message.Contains("Stray.Film.2020.1080p.mkv", StringComparison.Ordinal)).DetailsJson);
        var nfo = Assert.Single(listed, outcome => outcome.Message.Contains("notes.nfo", StringComparison.Ordinal));
        Assert.Contains("\"isMedia\":false", nfo.DetailsJson);
        Assert.Contains("not media", nfo.Message);
        Assert.True(File.Exists(film));
        Assert.True(File.Exists(scraps));
    }

    [Fact]
    public async Task A_file_the_processor_is_still_writing_is_not_listed_until_it_is_quiet()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var path = Path.Combine(scene.Output, "Still.Writing.2020.1080p.mkv");
        File.WriteAllBytes(path, new byte[2048]);

        var whileWriting = await SafetyNetAsync(app);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-30));
        var afterwards = await SafetyNetAsync(app);

        Assert.Empty(whileWriting);
        Assert.Contains("Still.Writing.2020.1080p.mkv", Assert.Single(afterwards).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_copy_of_a_film_the_library_holds_is_called_that_and_offers_keep_and_remove_instead_of_import()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var movies = app.Services.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>();
        var movie = await movies.AddAsync(new Deluno.Movies.Contracts.CreateMovieRequest("Pulp Fiction", 1994, null, true, null, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(movie.Id, scene.LibraryId, WantedStatuses.Covered, "Already in the library.", true, null, null, true, CancellationToken.None);
        Leftover(scene, "Pulp.Fiction.1994.1080p.mkv");

        var spare = Assert.Single(await SafetyNetAsync(app));

        Assert.Contains("second copy", spare.Message);
        Assert.Contains("Pulp Fiction (1994)", spare.Message);
        Assert.Contains("\"spareCopyOf\":\"Pulp Fiction (1994)\"", spare.DetailsJson);
    }

    [Fact]
    public async Task Clear_moves_a_listed_stray_item_to_the_recycle_bin_says_so_in_Activity_and_the_row_goes_with_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var stray = Leftover(scene, ReleaseOf());
        Assert.Single(await SafetyNetAsync(app));

        using var cleared = await app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = scene.LibraryId, path = stray });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False(File.Exists(stray));
        var inBin = Assert.Single(await app.Services.GetRequiredService<Deluno.Platform.IRecycleBinService>().ListAsync(CancellationToken.None));
        Assert.True(File.Exists(inBin.RecyclePath), "Clear never deletes outright: the item is in the recycle bin");
        Assert.Contains($"Moved {ReleaseOf()} from the Films library's processor output folder to the recycle bin.", await app.Client.GetStringAsync("/api/activity?take=100"));
        Assert.Empty(await SafetyNetAsync(app));
    }
}
