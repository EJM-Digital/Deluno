using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The filesystem routes when the request is right (coverage register, area api): previews read real files and
/// change nothing, repairs change Deluno's records and never delete, and each says so in plain words.
/// </summary>
public sealed class FilesystemRoutesDoWhatTheySayTests
{
    private static Task<ApplicationTestHost> StartAsync()
        => ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));

    private static Task<FilesystemReconciliationRepairResult> RepairAsync(ApplicationTestHost app, string issueId, string action)
        => app.Services.GetRequiredService<IFilesystemReconciliationService>().RepairAsync(new FilesystemReconciliationRepairRequest(issueId, action), CancellationToken.None);

    private static async Task<JsonElement> PostOkAsync(ApplicationTestHost app, string url, object body)
    {
        var response = await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object PreviewBody(string sourcePath, string title = "Arrival", int year = 2016)
        => new { sourcePath, fileName = (string?)null, mediaType = "movies", title, year, genres = (string[]?)null, tags = (string[]?)null, studio = (string?)null, originalLanguage = (string?)null };

    private static async Task<string> DownloadAsync(ApplicationTestHost app, string name = "Arrival.2016.WEB.1080p.mkv")
    {
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads")).FullName;
        var path = Path.Combine(downloads, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }

    [Fact]
    public async Task A_manual_import_preview_names_the_destination_inside_the_library_and_moves_nothing()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var source = await DownloadAsync(app);

        var preview = await PostOkAsync(app, "/api/filesystem/import/preview", PreviewBody(source));

        Assert.True(preview.GetProperty("sourceExists").GetBoolean());
        Assert.True(preview.GetProperty("isSupportedMediaFile").GetBoolean());
        Assert.False(preview.GetProperty("destinationExists").GetBoolean());
        var destination = preview.GetProperty("destinationPath").GetString()!;
        Assert.StartsWith(root, destination, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Arrival", destination, StringComparison.Ordinal);
        Assert.Contains("2016", destination, StringComparison.Ordinal);
        Assert.True(File.Exists(source), "a preview never moves the source");
        Assert.False(File.Exists(destination), "and never writes the destination");
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task The_same_preview_asked_for_on_behalf_of_an_outside_app_answers_the_same_destination()
    {
        await using var app = await StartAsync();
        await ApiScenes.CreateLibraryAsync(app, "movies");
        var source = await DownloadAsync(app);

        var own = await PostOkAsync(app, "/api/filesystem/import/preview", PreviewBody(source));
        var external = await PostOkAsync(app, "/api/integrations/external/import-preview", PreviewBody(source));

        Assert.Equal(own.GetProperty("destinationPath").GetString(), external.GetProperty("destinationPath").GetString());
        Assert.True(external.GetProperty("sourceExists").GetBoolean());
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Scanning_a_folder_lists_each_video_file_with_a_guessed_title_and_year_and_its_own_preview()
    {
        await using var app = await StartAsync();
        await ApiScenes.CreateLibraryAsync(app, "movies");
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "to-sort")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, "Arrival.2016.WEB.1080p.mkv"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Dune.2021.WEB.2160p.mkv"), [3, 4]);
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "not a video");

        var scan = await PostOkAsync(app, "/api/filesystem/import/scan-folder", new { folderPath = folder, mediaType = "movies", libraryId = (string?)null, seriesId = (string?)null });

        var items = scan.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var arrival = items.Single(item => item.GetProperty("fileName").GetString()!.StartsWith("Arrival", StringComparison.Ordinal));
        Assert.Equal("Arrival", arrival.GetProperty("guessedTitle").GetString());
        Assert.Equal(2016, arrival.GetProperty("guessedYear").GetInt32());
        Assert.True(arrival.GetProperty("preview").GetProperty("sourceExists").GetBoolean());
        Assert.Empty(scan.GetProperty("issues").EnumerateArray());
        Assert.True(File.Exists(Path.Combine(folder, "Arrival.2016.WEB.1080p.mkv")), "scanning never moves a file");
    }

    [Fact]
    public async Task Scanning_a_folder_that_does_not_exist_or_holds_no_video_says_so()
    {
        await using var app = await StartAsync();
        var empty = Directory.CreateDirectory(Path.Combine(app.DataRoot, "nothing-here")).FullName;

        var missing = await app.Client.PostAsJsonAsync("/api/filesystem/import/scan-folder", new { folderPath = Path.Combine(app.DataRoot, "no-such-folder"), mediaType = "movies" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("That folder does not exist.", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var none = await PostOkAsync(app, "/api/filesystem/import/scan-folder", new { folderPath = empty, mediaType = "movies" });
        Assert.Empty(none.GetProperty("items").EnumerateArray());
        Assert.Contains("No supported video files were found", none.GetProperty("issues")[0].GetString(), StringComparison.Ordinal);

        var blank = await app.Client.PostAsJsonAsync("/api/filesystem/import/scan-folder", new { folderPath = " ", mediaType = "movies" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("A folder path is required.", await blank.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queuing_an_import_from_a_valid_preview_adds_one_import_job_and_moves_no_file_yet()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var source = await DownloadAsync(app);
        var before = await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'filesystem.import.execute';");

        var queued = await PostOkAsync(app, "/api/filesystem/import/jobs", new { preview = PreviewBody(source), transferMode = "copy", overwrite = false, allowCopyFallback = true });

        var jobId = queued.GetProperty("job").GetProperty("id").GetString()!;
        Assert.Equal(before + 1, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'filesystem.import.execute';"));
        Assert.Equal("queued", await ApiScenes.TextAsync(app, "jobs", $"SELECT status FROM job_queue WHERE id = '{jobId}';"));
        Assert.Contains(source.Replace("\\", "\\\\"), await ApiScenes.TextAsync(app, "jobs", $"SELECT payload_json FROM job_queue WHERE id = '{jobId}';") ?? string.Empty, StringComparison.Ordinal);
        Assert.True(File.Exists(source), "queuing an import leaves the file where it is until the job runs");
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Queuing_an_import_for_a_file_that_is_not_there_is_refused_and_queues_nothing()
    {
        await using var app = await StartAsync();
        await ApiScenes.CreateLibraryAsync(app, "movies");
        var before = await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'filesystem.import.execute';");

        var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/jobs", new
        {
            preview = PreviewBody(Path.Combine(app.DataRoot, "downloads", "Missing.File.mkv"), "Missing File", 2020),
            transferMode = "copy", overwrite = false, allowCopyFallback = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Import cannot be queued until the preview is valid.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'filesystem.import.execute';"));
    }

    [Fact]
    public async Task A_path_diagnosis_says_what_a_real_folder_and_a_missing_one_allow()
    {
        await using var app = await StartAsync();
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "diagnose-me")).FullName;

        var present = await PostOkAsync(app, "/api/filesystem/path-diagnostics", new { path = folder });
        Assert.True(present.GetProperty("exists").GetBoolean());
        Assert.True(present.GetProperty("readable").GetBoolean());
        Assert.True(present.GetProperty("writable").GetBoolean());
        Assert.Equal(Path.GetFullPath(folder), present.GetProperty("normalizedPath").GetString());

        var absent = await PostOkAsync(app, "/api/filesystem/path-diagnostics", new { path = Path.Combine(folder, "not-created", "deeper") });
        Assert.False(absent.GetProperty("exists").GetBoolean());
        Assert.False(absent.GetProperty("readable").GetBoolean());

        var blank = await app.Client.PostAsJsonAsync("/api/filesystem/path-diagnostics", new { path = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Path is required.", await blank.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder, "not-created")), "a diagnosis creates nothing");
    }

    [Fact]
    public async Task Rescanning_a_title_marks_a_file_that_is_gone_missing_and_lists_a_video_that_is_not_tracked_without_touching_it()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var gone = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId);
        var other = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, libraryId);
        var stray = Path.Combine(gone.FolderPath, "Dune (2021) extended.mkv");
        await File.WriteAllBytesAsync(stray, [9, 9]);
        File.Delete(gone.FilePath);

        var result = await PostOkAsync(app, "/api/filesystem/rescan-title", new { mediaType = "movies", titleId = gone.MovieId });

        Assert.Equal("changed", result.GetProperty("outcome").GetString());
        Assert.Equal(1, result.GetProperty("filesChecked").GetInt32());
        var missing = Assert.Single(result.GetProperty("missing").EnumerateArray());
        Assert.True(missing.GetProperty("markedMissing").GetBoolean());
        var untracked = Assert.Single(result.GetProperty("untracked").EnumerateArray());
        Assert.Equal(stray, untracked.GetProperty("path").GetString());
        Assert.True(File.Exists(stray), "an untracked video is only listed, never moved or deleted");
        Assert.True(File.Exists(other.FilePath), "other titles are not touched");
        var detail = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{gone.MovieId}");
        Assert.False(detail.GetProperty("monitored").GetBoolean(), "a file deleted outside Deluno stops being wanted");
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{other.MovieId}")).GetProperty("monitored").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/filesystem/rescan-title", new { mediaType = "movies", titleId = "no-such-title" })).StatusCode);
        var blank = await app.Client.PostAsJsonAsync("/api/filesystem/rescan-title", new { mediaType = "movies", titleId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Choose a title to rescan.", await blank.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_repair_service_marks_a_missing_file_missing_and_stops_wanting_the_title_and_deletes_nothing()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var gone = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId);
        var kept = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, libraryId);
        File.Delete(gone.FilePath);
        var report = await app.Services.GetRequiredService<IFilesystemReconciliationService>().ScanAsync(CancellationToken.None);
        var issue = Assert.Single(report.Issues, item => item.Kind == "missingTrackedFile");

        var repair = await RepairAsync(app, issue.Id, "mark-missing");

        Assert.True(repair.Repaired);
        Assert.Contains("Deluno did not delete any files", repair.Message, StringComparison.Ordinal);
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{gone.MovieId}")).GetProperty("monitored").GetBoolean());
        Assert.True(File.Exists(kept.FilePath));
        Assert.DoesNotContain((await app.Services.GetRequiredService<IFilesystemReconciliationService>().ScanAsync(CancellationToken.None)).Issues, item => item.Kind == "missingTrackedFile");
    }

    [Fact]
    public async Task The_repair_service_opens_a_review_case_for_it_and_leaves_the_file_alone()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId);
        var orphan = Path.Combine(await ApiScenes.LibraryRootAsync(app, libraryId), "Stray Film (2019)", "Stray Film (2019).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllBytesAsync(orphan, [7, 7, 7]);
        var report = await app.Services.GetRequiredService<IFilesystemReconciliationService>().ScanAsync(CancellationToken.None);
        var issue = Assert.Single(report.Issues, item => item.Kind == "orphanFile");

        var repair = await RepairAsync(app, issue.Id, "queue-import-review");

        Assert.True(repair.Repaired);
        Assert.Contains("recovery case was created", repair.Message, StringComparison.Ordinal);
        var cases = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/import-recovery");
        Assert.True(cases.GetRawText().Contains("Stray Film", StringComparison.Ordinal), cases.GetRawText());
        Assert.True(File.Exists(orphan), "reviewing is a decision for later; the file stays");
        Assert.True(File.Exists(film.FilePath));
    }

    [Fact]
    public async Task The_repair_service_moves_a_leftover_staging_file_to_the_recycle_bin_and_refuses_a_repair_that_does_not_apply()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId);
        var artifact = Path.Combine(await ApiScenes.LibraryRootAsync(app, libraryId), "Dune (2021)", "Dune (2021).mkv.deluno-stage-1234");
        await File.WriteAllBytesAsync(artifact, [5, 5]);
        var report = await app.Services.GetRequiredService<IFilesystemReconciliationService>().ScanAsync(CancellationToken.None);
        var issue = Assert.Single(report.Issues, item => item.Kind == "partialImportArtifact");

        var repair = await RepairAsync(app, issue.Id, "cleanup-artifact");

        Assert.True(repair.Repaired);
        Assert.Contains("recycle bin", repair.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(artifact), "the artifact left the library folder");
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin")).GetArrayLength() > 0, "and is held in the recycle bin");

        var wrongAction = await RepairAsync(app, issue.Id, "mark-missing");
        Assert.False(wrongAction.Repaired);
        Assert.Contains("not supported", wrongAction.Message, StringComparison.Ordinal);
        var blank = await app.Client.PostAsJsonAsync("/api/filesystem/reconciliation/repair", new { issueId = "", action = "" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Fact]
    public async Task Confirming_that_the_files_of_a_library_that_looked_unreachable_are_really_gone_marks_each_missing_and_unmonitors_it()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var films = new List<ApiScenes.Film>();
        for (var i = 0; i < 24; i++)
        {
            films.Add(await ApiScenes.FilmOnDiskAsync(app, $"Film {i:00}", 2000 + i, libraryId));
        }

        // 22 of the 24 are deleted outside Deluno: a share dropping out looks the same, so Deluno changes nothing and says so.
        foreach (var film in films.Take(22))
        {
            File.Delete(film.FilePath);
        }

        var check = await PostOkAsync(app, "/api/filesystem/file-check", new { });
        Assert.Equal(0, check.GetProperty("markedMissing").GetInt32());
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{films[0].MovieId}")).GetProperty("monitored").GetBoolean(), "nothing was changed while the library looked unreachable");

        var gone = await PostOkAsync(app, $"/api/filesystem/libraries/{libraryId}/files-gone", new { });

        Assert.Equal(22, gone.GetProperty("markedGone").GetInt32());
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{films[0].MovieId}")).GetProperty("monitored").GetBoolean());
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{films[23].MovieId}")).GetProperty("monitored").GetBoolean(), "a film whose file is still there is left wanted");
        Assert.True(File.Exists(films[23].FilePath));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/filesystem/libraries/no-such-library/files-gone", new { })).StatusCode);
    }

    [Fact]
    public async Task Marking_a_films_file_with_another_quality_changes_the_recorded_quality_and_says_so_in_Activity()
    {
        await using var app = await StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, quality: "WEB 720p");

        var response = await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/files/quality", new { quality = "Remux 2160p" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Remux 2160p", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quality").GetString());
        Assert.Equal("Remux 2160p", (await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}")).GetProperty("currentQuality").GetString());
        Assert.Contains("Dune's file was marked as Remux 2160p.", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);
        Assert.True(File.Exists(film.FilePath), "the video file itself is unchanged");
        var blank = await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/files/quality", new { quality = " " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Choose a quality before saving.", await blank.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marking_a_subtitle_with_another_language_renames_the_file_beside_the_film_to_that_language()
    {
        await using var app = await StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app);
        var original = Path.Combine(film.FolderPath, "Dune (2021) [Bluray-1080p].en.srt");
        await File.WriteAllTextAsync(original, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");

        var response = await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/files/language", new { path = original, language = "fr", hearingImpaired = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var renamed = body.GetProperty("path").GetString()!;
        Assert.EndsWith(".fr.srt", renamed, StringComparison.Ordinal);
        Assert.True(File.Exists(renamed), "the subtitle now carries the new language");
        Assert.False(File.Exists(original), "under its old name it is gone");
        Assert.Contains("Hello", await File.ReadAllTextAsync(renamed), StringComparison.Ordinal);
        Assert.True(File.Exists(film.FilePath), "the video is untouched");
        var blank = await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/files/language", new { path = renamed, language = "" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        var outside = await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/files/language", new { path = Path.Combine(app.DataRoot, "elsewhere.srt"), language = "de" });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Contains("under this title's own folder", await outside.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact(Skip = "Defect: POST /api/filesystem/reconciliation/repair refuses every issue id Deluno hands out. An issue id is the issue's details encoded (kind, media type, library id, title id, path), 274 to 282 characters for a short path like D:/Films/Dune (2021)/Dune.mkv, and the request pipeline caps a member named issueId at 200 characters: 400 'The issue id is too long. It can be at most 200 characters.' The title page's Review button (apps/web/src/components/app/title-folder-actions.tsx) posts such an id.")]
    public async Task Repairing_through_the_route_with_an_id_Deluno_issued_repairs_the_issue()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var gone = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId);
        await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, libraryId);
        File.Delete(gone.FilePath);
        var report = await app.Services.GetRequiredService<IFilesystemReconciliationService>().ScanAsync(CancellationToken.None);
        var issue = Assert.Single(report.Issues, item => item.Kind == "missingTrackedFile");

        var repair = await PostOkAsync(app, "/api/filesystem/reconciliation/repair", new { issueId = issue.Id, action = "mark-missing" });

        Assert.True(repair.GetProperty("repaired").GetBoolean());
    }
}
