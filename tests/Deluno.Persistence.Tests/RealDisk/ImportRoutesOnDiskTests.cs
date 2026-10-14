using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The import routes (preview, scan a folder, queue an import, delete season-pack leftovers) over real folders and real
/// files on a real host. Only the media probe is scripted by file name, because the bytes written are not real video.
/// </summary>
public sealed class ImportRoutesOnDiskTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var probe = new ScriptedProbe(path =>
        {
            var name = Path.GetFileName(path);
            if (name.Contains("corrupt", StringComparison.OrdinalIgnoreCase)) return ScriptedProbe.Failed("moov atom not found");
            if (name.Contains("novideo", StringComparison.OrdinalIgnoreCase)) return ScriptedProbe.Good() with { VideoStreams = [] };
            if (name.Contains("sample", StringComparison.OrdinalIgnoreCase)) return ScriptedProbe.Good(seconds: 90);
            return ScriptedProbe.Good();
        });
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(probe));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static object PreviewBody(string source, string title = "Sintel", int year = 2010)
        => new { sourcePath = source, mediaType = "movies", title, year, genres = Array.Empty<string>(), tags = Array.Empty<string>() };

    private static object JobBody(string source, bool overwrite = false)
        => new { preview = PreviewBody(source), transferMode = "copy", overwrite, allowCopyFallback = true };

    private static Task<HttpResponseMessage> QueueAsync(RealDiskLibrary world, string source, bool overwrite = false)
        => world.App.Client.PostAsJsonAsync("/api/filesystem/import/jobs", JobBody(source, overwrite));

    // ---------------------------------------------------------------- queueing an import

    [Fact]
    public async Task A_valid_preview_queues_exactly_one_import_job_whose_payload_is_the_request_and_returns_the_preview()
    {
        await using var world = await StartWorldAsync();
        var source = world.WriteDownload("Sintel.2010.WEB.1080p.mkv");

        var response = await QueueAsync(world, source);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(source, document.RootElement.GetProperty("preview").GetProperty("sourcePath").GetString());
        var jobs = (await world.App.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None))
            .Where(job => job.JobType == "filesystem.import.execute").ToArray();
        var job = Assert.Single(jobs);
        Assert.Equal(document.RootElement.GetProperty("job").GetProperty("id").GetString(), job.Id);
        using var payload = JsonDocument.Parse(job.PayloadJson!);
        Assert.Equal(source, payload.RootElement.GetProperty("Preview").GetProperty("SourcePath").GetString());
        Assert.Empty(RealDiskLibrary.FilesUnder(world.Root));
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData("missing", "Sintel.2010.WEB.1080p.mkv")]
    [InlineData("failed probe", "Sintel.corrupt.mkv")]
    [InlineData("no video stream", "Sintel.novideo.mkv")]
    [InlineData("runtime under 120 s", "Sintel.sample.mkv")]
    [InlineData("unsupported type", "Sintel.2010.nfo")]
    public async Task An_import_whose_preview_is_not_valid_is_refused_400_and_nothing_is_queued(string reason, string fileName)
    {
        await using var world = await StartWorldAsync();
        var source = reason == "missing" ? Path.Combine(world.Downloads, fileName) : world.WriteDownload(fileName);

        var response = await QueueAsync(world, source);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Import cannot be queued until the preview is valid", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(await world.App.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None), job => job.JobType == "filesystem.import.execute");
    }

    [Fact]
    public async Task An_import_onto_a_file_that_already_exists_is_refused_unless_overwrite_is_chosen_and_a_file_onto_itself_is_always_refused()
    {
        await using var world = await StartWorldAsync();
        var (_, existing) = await world.ImportFirstCopyAsync("Sintel", 2010, "Sintel.2010.WEB.1080p");
        var again = world.WriteDownload("Sintel.2010.WEB.1080p.mkv", fill: 0x22);
        var jobs = world.App.Services.GetRequiredService<IJobQueueRepository>();

        var refused = await QueueAsync(world, again);
        var onItself = await QueueAsync(world, existing, overwrite: true);
        var allowed = await QueueAsync(world, again, overwrite: true);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, onItself.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Single(await jobs.ListAsync(100, CancellationToken.None), job => job.JobType == "filesystem.import.execute");
        Assert.All(File.ReadAllBytes(existing), value => Assert.Equal(0x11, value));
    }

    [Fact]
    public async Task An_import_asked_for_without_a_preview_or_with_a_tv_file_the_library_cannot_match_is_refused_in_plain_words()
    {
        await using var world = await StartWorldAsync();
        var episode = world.WriteDownload("Unknown.Show.S01E01.1080p.WEB.mkv");

        var empty = await world.App.Client.PostAsJsonAsync("/api/filesystem/import/jobs", new { transferMode = "copy" });
        var unknownShow = await world.App.Client.PostAsJsonAsync("/api/filesystem/import/jobs", new
        {
            preview = new { sourcePath = episode, mediaType = "tv", genres = Array.Empty<string>(), tags = Array.Empty<string>() },
            transferMode = "copy", overwrite = false, allowCopyFallback = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("An import needs a preview", await empty.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, unknownShow.StatusCode);
        Assert.Contains("Help Deluno match it", await unknownShow.Content.ReadAsStringAsync());
        Assert.DoesNotContain(await world.App.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None), job => job.JobType == "filesystem.import.execute");
    }

    // ---------------------------------------------------------------- scanning a folder

    [Fact]
    public async Task Scanning_a_folder_answers_400_for_a_blank_or_nonsense_path_404_for_a_missing_one_and_says_so_for_an_empty_one()
    {
        await using var world = await StartWorldAsync();
        var empty = Directory.CreateDirectory(Path.Combine(world.App.DataRoot, "empty-folder")).FullName;

        var blank = await world.App.Client.PostAsJsonAsync("/api/filesystem/import/scan-folder", new { folderPath = "  ", mediaType = "movies" });
        var nonsense = await world.App.Client.PostAsJsonAsync("/api/filesystem/import/scan-folder", new { folderPath = "bad\0path", mediaType = "movies" });
        var missing = await world.App.Client.PostAsJsonAsync("/api/filesystem/import/scan-folder", new { folderPath = Path.Combine(world.App.DataRoot, "no-such-folder"), mediaType = "movies" });
        var nothing = await world.App.Client.PostAsJsonAsync("/api/filesystem/import/scan-folder", new { folderPath = empty, mediaType = "movies" });

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("A folder path is required", await blank.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("That folder does not exist", await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, nothing.StatusCode);
        Assert.Contains("No supported video files were found in this folder.", await nothing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Scanning_a_folder_of_two_films_returns_each_files_guess_and_a_full_preview_and_touches_nothing()
    {
        await using var world = await StartWorldAsync();
        var folder = Directory.CreateDirectory(Path.Combine(world.App.DataRoot, "to-import")).FullName;
        var first = Path.Combine(folder, "Sintel.2010.WEB.1080p.mkv");
        var second = Path.Combine(folder, "Nosferatu.1922.BluRay.1080p.mkv");
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(second, [4, 5, 6]);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a video");

        var response = await world.App.Client.PostAsJsonAsync("/api/filesystem/import/scan-folder", new { folderPath = folder, mediaType = "movies" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var sintel = Assert.Single(items, item => item.GetProperty("fileName").GetString() == "Sintel.2010.WEB.1080p.mkv");
        Assert.Equal(2010, sintel.GetProperty("guessedYear").GetInt32());
        Assert.True(sintel.GetProperty("preview").GetProperty("sourceExists").GetBoolean());
        Assert.StartsWith(world.Root, sintel.GetProperty("preview").GetProperty("destinationPath").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, new FileInfo(first).Length);
        Assert.Empty(RealDiskLibrary.FilesUnder(world.Root));
    }

    // ---------------------------------------------------------------- the external preview

    [Fact]
    public async Task The_external_import_preview_answers_the_same_destination_as_the_screens_own_preview_for_a_real_file()
    {
        await using var world = await StartWorldAsync();
        var source = world.WriteDownload("Sintel.2010.WEB.1080p.mkv");

        var screen = await (await world.App.Client.PostAsJsonAsync("/api/filesystem/import/preview", PreviewBody(source))).Content.ReadFromJsonAsync<JsonElement>();
        var external = await (await world.App.Client.PostAsJsonAsync("/api/integrations/external/import-preview", PreviewBody(source))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(external.GetProperty("sourceExists").GetBoolean());
        Assert.Equal(screen.GetProperty("destinationPath").GetString(), external.GetProperty("destinationPath").GetString());
        Assert.StartsWith(world.Root, external.GetProperty("destinationPath").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(external.GetProperty("isSupportedMediaFile").GetBoolean());
        Assert.Equal("succeeded", external.GetProperty("mediaProbe").GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------- season-pack leftovers

    private static Task<HttpResponseMessage> DeleteLeftoversAsync(RealDiskLibrary world, params string[] paths)
        => world.App.Client.PostAsJsonAsync("/api/filesystem/import/season-pack/leftovers/delete", new { paths });

    [Fact]
    public async Task Deleting_season_pack_leftovers_treats_a_file_already_gone_as_nothing_to_do_and_collapses_duplicates()
    {
        await using var world = await StartWorldAsync();
        var leftover = world.WriteDownload("Show.S01.extras.mkv");
        var gone = Path.Combine(world.Downloads, "Show.S01.already-gone.mkv");

        var response = await DeleteLeftoversAsync(world, leftover, leftover, leftover.ToUpperInvariant(), gone);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body.GetProperty("deleted").GetInt32());
        Assert.Empty(body.GetProperty("refused").EnumerateArray());
        Assert.False(File.Exists(leftover));
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(leftover, item.OriginalPath);
    }

    [Fact]
    public async Task Deleting_season_pack_leftovers_refuses_a_locked_file_and_a_path_outside_the_download_folders_and_touches_neither()
    {
        await using var world = await StartWorldAsync();
        var locked = world.WriteDownload("Show.S01.locked.mkv");
        var stranger = Path.Combine(world.App.DataRoot, "elsewhere", "Other.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(stranger)!);
        File.WriteAllText(stranger, "not in a download folder");
        var climbing = Path.Combine(world.Downloads, "..", "elsewhere", "Other.mkv");
        using var block = DeleteBlock.TryApply(locked);
        if (block is null)
        {
            return; // this account can delete anything (for example root in a container), so a delete cannot be made to fail
        }

        var response = await DeleteLeftoversAsync(world, locked, stranger, climbing);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        block.Dispose();

        Assert.Equal(0, body.GetProperty("deleted").GetInt32());
        Assert.Equal(3, body.GetProperty("refused").GetArrayLength());
        Assert.True(File.Exists(locked));
        Assert.Equal("not in a download folder", File.ReadAllText(stranger));
    }

    // Was a defect (#1318): the route deleted with File.Delete and trusted the library's downloads folder, so a downloads folder
    // that is its own root let it delete a film outright. Now it does nothing while a folder overlaps (#1325).
    [Fact]
    public async Task Deleting_season_pack_leftovers_never_deletes_a_file_that_is_inside_a_library_root()
    {
        await using var world = await StartWorldAsync();
        var inside = Path.Combine(world.Root, "Show (2000)", "Season 01", "Show.S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, "a person's episode");
        // The library's downloads folder is its own root: the save refuses it (#1325) ...
        var update = await world.App.Client.PutAsJsonAsync($"/api/libraries/{world.Library.Id}", new { name = world.Library.Name, rootPath = world.Root, downloadsPath = world.Root });
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);

        // ... and an install that saved it before still has it, so the old layout is written straight in.
        await world.App.Services.GetRequiredService<ILibrariesRepository>().UpdateLibraryDetailsAsync(
            world.Library.Id, new UpdateLibraryDetailsRequest(null, null, world.Root), CancellationToken.None);

        var response = await DeleteLeftoversAsync(world, inside);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(inside), "library media stays where it is");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_season_pack_leftovers_refuses_a_file_in_a_downloads_folder_that_sits_inside_the_library_root()
    {
        await using var world = await StartWorldAsync();
        var downloadsInside = Path.Combine(world.Root, "_downloads");
        var inside = Path.Combine(downloadsInside, "Show.S01.extras.mkv");
        Directory.CreateDirectory(downloadsInside);
        File.WriteAllText(inside, "a person's file");
        var update = await world.App.Client.PutAsJsonAsync($"/api/libraries/{world.Library.Id}", new { name = world.Library.Name, rootPath = world.Root, downloadsPath = downloadsInside });
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());

        var response = await DeleteLeftoversAsync(world, inside);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("deleted").GetInt32());
        Assert.Single(body.GetProperty("refused").EnumerateArray());
        Assert.True(File.Exists(inside));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }
}
