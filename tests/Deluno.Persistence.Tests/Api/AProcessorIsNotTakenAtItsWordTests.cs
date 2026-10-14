using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

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
///
/// <para>#1239: a report Weir really sent (tests/fixtures/recorded/deluno/*/events-*) is posted from its recording wherever one matches the case; a case with no real report stays
/// inline on purpose, and tests/fixtures/recorded/MISSING.md lists it with the Weir state that would produce it.</para>
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

    /// <summary>
    /// #891: today Deluno would only ever surface this via the hand-off's
    /// ordinary stall timeout, up to a week later. It is told on Activity as
    /// soon as the misplaced report arrives instead.
    /// </summary>
    [Fact]
    public async Task A_file_claimed_by_another_hand_off_is_told_on_activity_immediately()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        await HandoffAsync(app, scene, Theirs);
        var handoff = await HandoffAsync(app, scene, Ours);

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = Path.Combine(scene.Output, Theirs, $"{Theirs}.mkv")
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var activity = await ActivityFeed(app).ListActivityAsync(20, "processor-handoff", handoff.Id, CancellationToken.None);
        var told = Assert.Single(activity, item => item.Category == "processing.output.misplaced");
        Assert.Contains(Ours, told.Message);
        Assert.Contains(Theirs, told.Message);

        // A second identical misreport (the processor retrying with the same
        // broken path) does not tell twice.
        var second = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = Path.Combine(scene.Output, Theirs, $"{Theirs}.mkv")
        });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var activityAfterRetry = await ActivityFeed(app).ListActivityAsync(20, "processor-handoff", handoff.Id, CancellationToken.None);
        Assert.Single(activityAfterRetry, item => item.Category == "processing.output.misplaced");
    }

    /// <summary>
    /// #891: an output path outside the library's configured clean output
    /// folder -- a wrong path mapping, most likely -- is told right away
    /// rather than waiting for the stall timeout.
    /// </summary>
    [Fact]
    public async Task An_output_path_outside_the_configured_folder_is_told_on_activity_immediately()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, Ours);
        var elsewhere = Path.Combine(scene.Downloads, Ours, $"{Ours}.mkv");

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = elsewhere
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var activity = await ActivityFeed(app).ListActivityAsync(20, "processor-handoff", handoff.Id, CancellationToken.None);
        var told = Assert.Single(activity, item => item.Category == "processing.output.misplaced");
        Assert.Contains(Ours, told.Message);
        Assert.Contains("outside", told.Message);
    }

    /// <summary>Weir's real report of a finished Usenet job (events-completed: the film inside a folder named for the job, #1239).</summary>
    [Fact]
    public async Task A_file_in_the_downloads_own_folder_is_accepted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        var (_, response) = await RecordedReportAsync(app, scene, "events-completed", 2, Ours);

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

    /// <summary>Weir's real report for a hand-off Deluno had stopped waiting for (events-too-late, #1239).</summary>
    [Fact]
    public async Task A_report_for_a_hand_off_that_already_ended_is_too_late()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var handoff = await HandoffAsync(app, scene, Ours);
        await Repository(app).UpdateProcessorHandoffAsync(
            handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, "You stopped waiting.", CancellationToken.None);

        var response = await RecordedPostAsync(app, scene, handoff, "events-too-late", 0, Ours);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("too late", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_same_completion_reported_twice_imports_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var (handoff, first) = await RecordedReportAsync(app, scene, "events-completed", 2, Ours);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        var second = await RecordedPostAsync(app, scene, handoff, "events-completed", 2, Ours);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Contains("alreadyHandled", await second.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// #619, live on the rig: Weir reported each episode of a pack as its own
    /// completion of one hand-off. The second is refused, and says why in a way
    /// a processor author can act on.
    /// </summary>
    [Fact]
    public async Task A_second_completion_of_a_completed_hand_off_is_refused_and_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var (handoff, first) = await RecordedReportAsync(app, scene, "events-completed", 2, Ours);
        Assert.True(first.IsSuccessStatusCode);

        // The same report again, naming another file: Weir's real body with its output changed to a second part.
        var again = RecordedWeirReports.Body(RecordedWeirReports.Exchange("events-completed", 2), scene.LibraryId, handoff.Id, scene.Output, Ours);
        var part2 = Path.Combine(scene.Output, Ours, $"{Ours}.part2.mkv");
        again["outputPath"] = part2;
        again["outputFiles"] = new JsonArray(JsonValue.Create(part2));
        var second = await RecordedWeirReports.PostAsync(app.Client, again);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("already completed", body);
        Assert.Contains("OutputFiles", body);
    }

    /// <summary>#619: a file the processor names that Deluno cannot find is said, not passed over.</summary>
    [Fact]
    public async Task Output_files_the_processor_names_but_are_not_there_are_reported()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, Ours)).FullName;
        var present = Path.Combine(folder, $"{Ours}.mkv");
        await File.WriteAllBytesAsync(present, new byte[1024]);
        var handoff = await HandoffAsync(app, scene, Ours);

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = folder,
            outputFiles = new[] { present, Path.Combine(folder, $"{Ours}.extra.mkv") }
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Contains("1 of its 2 output files", stored?.FailureMessage);
        Assert.Contains($"{Ours}.extra.mkv", stored?.FailureMessage);
    }

    /// <summary>
    /// #619, live on the rig: the Pioneer One season pack. A folder hand-off's
    /// completion, naming every episode in OutputFiles, is imported as the one
    /// pack the existing season-pack import already knows how to place - not as
    /// six separate completions of the same hand-off.
    /// </summary>
    [Fact]
    public async Task A_folder_hand_off_with_several_output_files_imports_as_one_pack()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await TvSceneAsync(app);
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, "Pioneer.One.S01")).FullName;
        var episode1 = Path.Combine(folder, "Pioneer.One.S01E01.mkv");
        var episode2 = Path.Combine(folder, "Pioneer.One.S01E02.mkv");
        await File.WriteAllBytesAsync(episode1, new byte[4096]);
        await File.WriteAllBytesAsync(episode2, new byte[4096]);
        var handoff = await HandoffAsync(app, scene, "Pioneer.One.S01");

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = folder,
            outputFiles = new[] { episode1, episode2 }
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored?.Status);
        Assert.Null(stored?.FailureMessage);
        Assert.NotNull(stored?.ImportJobId);

        // One pack import, not one job per episode: the existing season-pack
        // import path is reused for the whole folder.
        var jobs = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None);
        var importJob = Assert.Single(jobs, job => job.Id == stored!.ImportJobId);
        var payload = JsonSerializer.Deserialize<ImportExecuteRequest>(importJob.PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(folder, payload.Preview.SourcePath);
    }

    /// <summary>#619: a single-file hand-off also carries OutputFiles, with one item, and still works. Weir's real report (events-completed: the file flat in its output folder, #1239).</summary>
    [Fact]
    public async Task A_single_file_hand_off_with_one_output_file_still_works()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var (handoff, response) = await RecordedReportAsync(app, scene, "events-completed", 0, Ours, writeFiles: true);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored?.Status);
        Assert.Null(stored?.FailureMessage);
        Assert.NotNull(stored?.ImportJobId);
    }

    /// <summary>An older processor that has not been updated for OutputFiles keeps working exactly as before.</summary>
    [Fact]
    public async Task A_completion_with_no_output_files_from_an_older_processor_still_works()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        var response = await ReportAsync(app, scene, Ours, "completed", Path.Combine(scene.Output, Ours, $"{Ours}.mkv"));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// #619: a processor that only finished some of a hand-off's files reports
    /// failed, with a message giving the counts and OutputFiles listing the
    /// files that did succeed. Those files still belong in the library.
    /// </summary>
    [Fact]
    public async Task A_partial_failure_imports_what_succeeded_and_reports_the_count()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await TvSceneAsync(app);
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, "Pioneer.One.S01")).FullName;
        var succeeded = Path.Combine(folder, "Pioneer.One.S01E01.mkv");
        await File.WriteAllBytesAsync(succeeded, new byte[4096]);
        var handoff = await HandoffAsync(app, scene, "Pioneer.One.S01");

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "failed",
            outputPath = folder,
            message = "1 of 2 episodes processed; 1 failed: S01E02.",
            outputFiles = new[] { succeeded }
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"failed\"", body);

        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Failed, stored?.Status);
        Assert.NotNull(stored?.ImportJobId);
        Assert.Contains("1 of 2 episodes processed; 1 failed", stored?.FailureMessage);
        Assert.Contains("imported", stored?.FailureMessage, StringComparison.OrdinalIgnoreCase);

        var jobs = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None);
        var importJob = Assert.Single(jobs, job => job.Id == stored!.ImportJobId);
        var payload = JsonSerializer.Deserialize<ImportExecuteRequest>(importJob.PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(succeeded, payload.Preview.SourcePath);
    }

    /// <summary>
    /// Golden path rc.30.8: a release holds a small extra (a 12 MB featurette) that the processor leaves out for
    /// being under its own size floor. The film was delivered, so the hand-off succeeded; the extra is named in
    /// one plain line instead of failing the whole hand-off.
    /// </summary>
    [Fact]
    public async Task A_small_extra_the_processor_left_out_does_not_fail_a_delivered_hand_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        const string release = "Nosferatu.1922.1080p.BluRay.x265-GRP";
        var source = Directory.CreateDirectory(Path.Combine(scene.Downloads, release, "Featurettes")).Parent!.FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "Nosferatu.1922.1080p.BluRay.x265-GRP.mkv"), new byte[200_000]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Featurettes", "Gallery.mkv"), new byte[2_000]);
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, release)).FullName;
        var film = Path.Combine(folder, "Nosferatu.1922.1080p.BluRay.x265-GRP.mkv");
        await File.WriteAllBytesAsync(film, new byte[190_000]);
        var handoff = await HandoffAsync(app, scene, release);

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "failed",
            outputPath = folder,
            message = "Skipped because this file is 12.1 MB, under the 50 MB minimum.",
            outputFiles = new[] { film }
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored?.Status);
        Assert.NotNull(stored?.ImportJobId);
        Assert.Contains("Gallery.mkv", stored?.FailureMessage);
        Assert.Contains("main video was delivered", stored?.FailureMessage);
    }

    /// <summary>Weir's real report for a film with one skipped extra: outputFiles, no outputPath (golden path rc.30.9).</summary>
    [Fact]
    public async Task A_failed_report_that_names_its_output_files_but_no_output_path_is_judged_by_its_main_video()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        const string release = "Nosferatu.1922.1080p.BluRay.x265-GRP";
        var source = Directory.CreateDirectory(Path.Combine(scene.Downloads, release, "Featurettes")).Parent!.FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, release + ".mkv"), new byte[200_000]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Featurettes", "Trailer.mkv"), new byte[90_000]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Featurettes", "Gallery.mkv"), new byte[2_000]);
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, release, "Featurettes")).Parent!.FullName;
        var film = Path.Combine(folder, release + ".mkv");
        var trailer = Path.Combine(folder, "Featurettes", "Trailer.mkv");
        await File.WriteAllBytesAsync(film, new byte[190_000]);
        await File.WriteAllBytesAsync(trailer, new byte[80_000]);
        var handoff = await HandoffAsync(app, scene, release);

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "failed",
            message = "It could not process 1 file: Gallery.mkv. Skipped because this file is 12.1 MB, under the 50 MB minimum.",
            outputFiles = new[] { film, trailer }
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored?.Status);
        Assert.NotNull(stored?.ImportJobId);
        Assert.Equal(folder, stored?.OutputPath);
    }

    [Fact]
    public async Task A_later_failed_report_for_one_small_extra_does_not_undo_a_completed_hand_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        const string release = "Nosferatu.1922.1080p.BluRay.x265-GRP";
        var source = Directory.CreateDirectory(Path.Combine(scene.Downloads, release, "Featurettes")).Parent!.FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "Nosferatu.1922.1080p.BluRay.x265-GRP.mkv"), new byte[200_000]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Featurettes", "Gallery.mkv"), new byte[2_000]);
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, release)).FullName;
        var film = Path.Combine(folder, "Nosferatu.1922.1080p.BluRay.x265-GRP.mkv");
        await File.WriteAllBytesAsync(film, new byte[190_000]);
        var handoff = await HandoffAsync(app, scene, release);
        var done = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new { libraryId = scene.LibraryId, handoffId = handoff.Id, status = "completed", outputPath = folder });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());

        var late = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "failed",
            message = "Skipped because this file is 12.1 MB, under the 50 MB minimum."
        });

        Assert.True(late.IsSuccessStatusCode, await late.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored?.Status);
    }

    [Fact]
    public async Task A_main_video_the_processor_failed_still_fails_the_hand_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await TvSceneAsync(app);
        const string release = "Pioneer.One.S01";
        var source = Directory.CreateDirectory(Path.Combine(scene.Downloads, release)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "Pioneer.One.S01E01.mkv"), new byte[100_000]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Pioneer.One.S01E02.mkv"), new byte[100_000]);
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, release)).FullName;
        var done = Path.Combine(folder, "Pioneer.One.S01E01.mkv");
        await File.WriteAllBytesAsync(done, new byte[90_000]);
        var handoff = await HandoffAsync(app, scene, release);

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "failed",
            outputPath = folder,
            message = "1 of 2 episodes processed; 1 failed: S01E02.",
            outputFiles = new[] { done }
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Failed, stored?.Status);
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

    private sealed record Scene(string LibraryId, string Downloads, string Output, string MediaType = "movies");

    private static IProcessorRepository Repository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static IActivityFeedRepository ActivityFeed(ApplicationTestHost app)
        => app.Services.GetRequiredService<IActivityFeedRepository>();

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

    /// <summary>A TV library, for the folder-hand-off/season-pack cases (#619).</summary>
    private static async Task<Scene> TvSceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "TvDownloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "TvRefined")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Shows",
            mediaType = "tv",
            rootPath = root,
            downloadsPath = downloads,
            importWorkflow = "refine-before-import",
            processorName = "Weir",
            processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = await response.Content.ReadFromJsonAsync<LibraryItem>();
        return new Scene(library!.Id, downloads, output, "tv");
    }

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene, string releaseName)
        => await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                scene.LibraryId,
                scene.MediaType,
                "sabnzbd",
                $"nzo-{releaseName}",
                releaseName,
                Path.Combine(scene.Downloads, releaseName),
                "Weir"),
            CancellationToken.None);

    /// <summary>
    /// The hand-off for <paramref name="release"/> and Weir's recorded report (#1239) for it: the recorded body with this test's
    /// library, hand-off, output folder and release name, everything else as Weir sent it.
    /// </summary>
    private static async Task<(ProcessorHandoffItem Handoff, HttpResponseMessage Response)> RecordedReportAsync(
        ApplicationTestHost app, Scene scene, string state, int exchange, string release, bool writeFiles = false)
    {
        var handoff = await HandoffAsync(app, scene, release);
        return (handoff, await RecordedPostAsync(app, scene, handoff, state, exchange, release, writeFiles));
    }

    private static Task<HttpResponseMessage> RecordedPostAsync(
        ApplicationTestHost app, Scene scene, ProcessorHandoffItem handoff, string state, int exchange, string release, bool writeFiles = false)
    {
        var body = RecordedWeirReports.Body(RecordedWeirReports.Exchange(state, exchange), scene.LibraryId, handoff.Id, scene.Output, release);
        if (writeFiles)
        {
            foreach (var file in RecordedWeirReports.OutputFilesOf(body))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, new byte[1024]);
            }
        }

        return RecordedWeirReports.PostAsync(app.Client, body);
    }

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
