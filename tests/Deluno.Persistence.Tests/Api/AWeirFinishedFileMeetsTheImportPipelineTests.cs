using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What Deluno does with a file Weir hands back, end to end: the report is the one the real Weir sent
/// (tests/fixtures/recorded/deluno/*/events-completed, posted to the real route), the route queues the import, and the
/// queued job's payload is run through the real import pipeline over real files in a real temp folder - which is what the
/// worker does with the job. Only ffprobe is answered by a script (it is another program: the test says what it reads in
/// the download and in Weir's file, so the comparison between them is the thing under test).
/// </summary>
public sealed class AWeirFinishedFileMeetsTheImportPipelineTests
{
    private sealed class ScriptedProbe : IMediaProbeService
    {
        public MediaProbeInfo Original { get; set; } = Film();

        public MediaProbeInfo Processed { get; set; } = Film();

        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(path.Contains($"{Path.DirectorySeparatorChar}Refined{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? Processed : Original);
    }

    private static MediaProbeInfo Film(double seconds = 7200, int audioTracks = 1, int videoTracks = 1, int width = 1920, int height = 1080, string audioLanguage = "eng")
        => new(
            Status: "succeeded", Tool: "test", Message: null, DurationSeconds: seconds, Container: "matroska", Bitrate: 12_000_000,
            VideoStreams: Enumerable.Range(0, videoTracks).Select(i => new MediaVideoStreamInfo(i, "h264", "High", width, height, "yuv420p", 23.976, 10_000_000, "eng")).ToArray(),
            AudioStreams: Enumerable.Range(0, audioTracks).Select(i => new MediaAudioStreamInfo(i + videoTracks, "eac3", null, 6, "5.1", 48_000, 640_000, audioLanguage)).ToArray(),
            SubtitleStreams: []);

    private sealed record Weir(
        ApplicationTestHost App,
        string LibraryId,
        string Downloads,
        string Output,
        string LibraryRoot,
        string Release,
        string OriginalFile,
        string[] OutputFiles,
        ImportExecuteRequest Queued);

    /// <summary>Weir's recorded finished report for a hand-off Deluno holds, posted, and the import it queued, ready to run.</summary>
    private static async Task<Weir> WeirFinishesAsync(ScriptedProbe probe, int recordedReport = 0, Action<IServiceCollection>? more = null)
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IMediaProbeService>(probe));
            more?.Invoke(services);
        });
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var created = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films", mediaType = "movies", rootPath = root, downloadsPath = downloads,
            importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = output
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var libraryId = (await created.Content.ReadFromJsonAsync<LibraryItem>())!.Id;

        // Report 0 is a loose single file, 1 a release folder, 2 a Usenet job's folder: the three shapes Weir really sent.
        var recorded = RecordedWeirReports.Exchange("events-completed", recordedReport);
        var release = RecordedWeirReports.ReleaseOf(recorded);
        var downloadFolder = Directory.CreateDirectory(Path.Combine(downloads, release)).FullName;
        var original = Path.Combine(downloadFolder, release + ".mkv");
        await File.WriteAllBytesAsync(original, new byte[4096]);
        var handoff = await app.Services.GetRequiredService<IProcessorRepository>().EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(libraryId, "movies", "qbittorrent", $"queue-{release}", release, downloadFolder, "Weir"),
            CancellationToken.None);

        var body = RecordedWeirReports.Body(recorded, libraryId, handoff.Id, output);
        var outputFiles = RecordedWeirReports.OutputFilesOf(body);
        foreach (var file in outputFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, new byte[2048]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-30));
        }

        var reported = await RecordedWeirReports.PostAsync(app.Client, body);
        Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);
        var jobId = JsonNode.Parse(await reported.Content.ReadAsStringAsync())!["importJobId"]!.GetValue<string>();
        var job = await app.Services.GetRequiredService<IJobQueueRepository>().GetAsync(jobId, CancellationToken.None);
        var queued = JsonSerializer.Deserialize<ImportExecuteRequest>(job!.PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return new Weir(app, libraryId, downloads, output, root, release, original, outputFiles, queued);
    }

    private static Task<ImportPipelineResult> RunAsync(Weir weir)
        => weir.App.Services.GetRequiredService<IImportPipelineService>().ExecuteAsync(weir.Queued, CancellationToken.None);

    [Theory]
    [InlineData("runs only 3m", "shorter")]
    [InlineData("has no audio", "no-audio")]
    public async Task A_file_Weir_hands_back_that_lost_something_is_refused_its_broken_copy_deleted_and_the_download_it_came_from_kept(string said, string loss)
    {
        var probe = new ScriptedProbe
        {
            Original = Film(),
            Processed = loss switch
            {
                "shorter" => Film(seconds: 180),
                _ => Film(audioTracks: 0)
            }
        };
        var weir = await WeirFinishesAsync(probe);
        await using var _app = weir.App;

        var result = await RunAsync(weir);

        Assert.False(result.Succeeded);
        Assert.Contains(loss == "shorter" ? "3m 0s" : said, result.Message);
        Assert.Contains("Deluno did not import it.", result.Message);
        Assert.All(weir.OutputFiles, file => Assert.False(File.Exists(file), "the processor's broken copy is deleted"));
        Assert.True(File.Exists(weir.OriginalFile), "the download it was made from is kept");
        Assert.Empty(Directory.GetFiles(weir.LibraryRoot, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("smaller")]
    [InlineData("lower-resolution")]
    [InlineData("other-language")]
    public async Task A_file_Weir_hands_back_that_is_smaller_a_lower_resolution_or_in_another_language_when_none_is_required_still_imports(string kind)
    {
        var probe = new ScriptedProbe
        {
            Original = Film(audioTracks: 5),
            Processed = kind switch
            {
                "smaller" => Film(audioTracks: 1),
                "lower-resolution" => Film(width: 1280, height: 720, audioTracks: 5),
                _ => Film(audioTracks: 5, audioLanguage: "fra")
            }
        };
        var weir = await WeirFinishesAsync(probe);
        await using var _app = weir.App;

        var result = await RunAsync(weir);

        Assert.True(result.Succeeded, result.Message);
        var placed = Assert.Single(Directory.GetFiles(weir.LibraryRoot, "*.mkv", SearchOption.AllDirectories));
        Assert.StartsWith(weir.LibraryRoot, placed);
    }

    [Fact]
    public async Task A_file_Weir_hands_back_is_imported_and_its_output_folder_is_swept_while_a_sibling_hand_offs_output_and_the_download_are_left()
    {
        var weir = await WeirFinishesAsync(new ScriptedProbe(), recordedReport: 1);
        await using var _app = weir.App;
        var sibling = Directory.CreateDirectory(Path.Combine(weir.Output, "Another.Release.2020.1080p.WEB-DL-DELUNO")).FullName;
        var siblingFile = Path.Combine(sibling, "Another.Release.2020.1080p.WEB-DL-DELUNO.mkv");
        await File.WriteAllBytesAsync(siblingFile, new byte[1024]);
        var releaseFolder = Path.GetDirectoryName(weir.OutputFiles[0])!;
        await File.WriteAllTextAsync(Path.Combine(releaseFolder, "leftover.nfo"), "left by the processor");

        var result = await RunAsync(weir);

        Assert.True(result.Succeeded, result.Message);
        Assert.Single(Directory.GetFiles(weir.LibraryRoot, "*.mkv", SearchOption.AllDirectories));
        Assert.All(weir.OutputFiles, file => Assert.False(File.Exists(file), "the processor's copy of the imported file is gone"));
        Assert.False(Directory.Exists(releaseFolder), "with everything else it left for this hand-off");
        Assert.True(Directory.Exists(weir.Output), "the output root itself stays");
        Assert.True(File.Exists(siblingFile), "a sibling hand-off's output is never touched");
        Assert.True(File.Exists(weir.OriginalFile), "the download is not the processor's to tidy");
    }

    [Fact]
    public async Task A_loose_file_Weir_hands_back_loses_only_its_own_leftovers_when_imported()
    {
        var weir = await WeirFinishesAsync(new ScriptedProbe(), recordedReport: 0);
        await using var _app = weir.App;
        var file = weir.OutputFiles[0];
        Assert.Equal(weir.Output, Path.GetDirectoryName(file));
        var ownLeftover = file + ".partial";
        var unrelated = Path.Combine(weir.Output, "Someone.Elses.Film.2019.1080p-DELUNO.mkv");
        await File.WriteAllTextAsync(ownLeftover, "never finished renaming away");
        await File.WriteAllBytesAsync(unrelated, new byte[1024]);

        var result = await RunAsync(weir);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(File.Exists(file), "the processor's copy of the imported file is gone");
        Assert.False(File.Exists(ownLeftover), "and its own temporary-suffix leftover");
        Assert.True(File.Exists(unrelated), "a file of another hand-off in the shared folder is left alone");
        Assert.True(Directory.Exists(weir.Output));
    }

    // DEFECT (found by this test, 15 Oct 2026): the absolute "No video stream was detected" guard in ImportPipelineService.ExecuteAsync answers first, so a processed file that
    // lost all its video is refused with that sentence, the "has no video, and the download it was made from does" check in WhatTheProcessorLostAsync is never reached, and the
    // broken processed copy is NOT deleted (the bin step runs only for a loss).
    [Fact]
    public async Task A_file_Weir_hands_back_with_no_video_is_refused_and_its_broken_copy_deleted_while_the_download_is_kept()
    {
        var weir = await WeirFinishesAsync(new ScriptedProbe { Original = Film(), Processed = Film(videoTracks: 0) });
        await using var _app = weir.App;

        var result = await RunAsync(weir);

        Assert.False(result.Succeeded);
        Assert.Contains("has no video, and the download it was made from does", result.Message);
        Assert.All(weir.OutputFiles, file => Assert.False(File.Exists(file), "the processor's broken copy is deleted"));
        Assert.True(File.Exists(weir.OriginalFile));
    }

    [Theory]
    [InlineData(true, "auto", "hardlink")]
    [InlineData(false, "auto", "copy")]
    [InlineData(true, "", "hardlink")]
    [InlineData(true, "no-such-mode", "hardlink")]
    public async Task The_mode_auto_a_blank_or_an_unknown_mode_resolves_to_what_the_preview_prefers_hardlink_when_hardlinks_are_on_on_one_drive_otherwise_copy(bool useHardlinks, string requested, string used)
    {
        var weir = await WeirFinishesAsync(new ScriptedProbe());
        await using var _app = weir.App;
        var settings = (await weir.App.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        settings["useHardlinks"] = useHardlinks;
        Assert.True((await weir.App.Client.PutAsJsonAsync("/api/settings", settings)).IsSuccessStatusCode);
        Assert.Equal("auto", weir.Queued.TransferMode, ignoreCase: true);

        var result = await RunAsync(weir with { Queued = weir.Queued with { TransferMode = requested } });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(used, result.Response!.TransferModeUsed);
        Assert.Equal(used, result.Response.Preview.PreferredTransferMode);
    }
}
