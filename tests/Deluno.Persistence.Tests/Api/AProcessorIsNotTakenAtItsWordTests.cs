using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What a processor reports is a claim, not a fact (#573).
///
/// <para><b>Why this file exists.</b> A completed event names the hand-off and
/// the file as two separate claims, and Deluno used to check them
/// independently: the hand-off properly, the file only for being somewhere
/// under the library's output folder. So a processor could report hand-off A
/// and hand over another release's file, and Deluno would import it, rename it
/// to A's catalogue title, and mark A completed — the wrong film in the library
/// under the right name. A report could also reopen a hand-off that had already
/// ended, replay itself into a second import, or use a word Deluno did not
/// understand and be recorded as "accepted".</para>
/// </summary>
public sealed class AProcessorIsNotTakenAtItsWordTests
{
    private const string Ours = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
    private const string Theirs = "Big.Buck.Bunny.2008.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public async Task A_file_that_is_another_downloads_is_refused_rather_than_imported_under_this_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        await HandoffAsync(app, scene, Theirs);

        var response = await ReportAsync(app, scene, Ours, "completed", Path.Combine(scene.Output, Theirs, $"{Theirs}.mkv"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var said = await response.Content.ReadAsStringAsync();
        Assert.Contains(Theirs, said);
        Assert.Contains($"not {Ours}", said);
    }

    [Fact]
    public async Task A_file_in_the_downloads_own_folder_is_accepted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        var response = await ReportAsync(app, scene, Ours, "completed", Path.Combine(scene.Output, Ours, $"{Ours}.mkv"));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A processor that writes somewhere Deluno cannot recognise still gets its
    /// file imported, because it did name the hand-off and no other download
    /// claims that file. Only a file that demonstrably belongs elsewhere is
    /// refused.
    /// </summary>
    [Fact]
    public async Task A_file_that_names_no_download_at_all_is_still_this_ones()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        var response = await ReportAsync(app, scene, Ours, "completed", Path.Combine(scene.Output, "scratch", "out.mkv"));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_report_for_a_hand_off_that_already_ended_is_too_late()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, Ours);
        await Repository(app).UpdateProcessorHandoffAsync(
            handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, "You stopped waiting.", CancellationToken.None);

        var response = await ReportAsync(app, scene, Ours, "completed", Path.Combine(scene.Output, Ours, $"{Ours}.mkv"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("too late", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_same_completion_reported_twice_imports_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var output = Path.Combine(scene.Output, Ours, $"{Ours}.mkv");

        var first = await ReportAsync(app, scene, Ours, "completed", output);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        var second = await ReportAsync(app, scene, Ours, "completed", output);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Contains("alreadyHandled", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_status_Deluno_does_not_understand_is_refused_rather_than_read_as_accepted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, Ours);

        var response = await ReportAsync(app, scene, Ours, "halfway", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("did not understand", await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Waiting, stored?.Status);
    }

    private sealed record Scene(string LibraryId, string Downloads, string Output);

    private static IProcessorRepository Repository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
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
        var library = await response.Content.ReadFromJsonAsync<LibraryItem>();
        return new Scene(library!.Id, downloads, output);
    }

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, string releaseName)
        => await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                scene.LibraryId,
                "movies",
                "sabnzbd",
                $"nzo-{releaseName}",
                releaseName,
                Path.Combine(scene.Downloads, releaseName),
                "Weir"),
            CancellationToken.None);

    private static async Task<HttpResponseMessage> ReportAsync(
        ApplicationTestHost app,
        Scene scene,
        string releaseName,
        string status,
        string? outputPath)
    {
        var handoff = await HandoffAsync(app, scene, releaseName);
        return await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status,
            outputPath,
            message = status == "failed" ? "Something went wrong." : null
        });
    }
}
