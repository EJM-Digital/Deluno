using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Platform;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Routes that look at, import from, or put back real folders and files: the folder check, importing what a library already holds,
/// and the recycle bin's settings and restore, each over a real temp folder and the real state on disk.
/// </summary>
public sealed class LibraryAndBinRoutesOnRealFilesTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    // ------------------------------------------------------------------ the folder check

    [Fact]
    public async Task The_folder_check_refuses_a_blank_path_and_reads_a_folder_a_file_and_a_missing_path_from_the_disk()
    {
        await using var world = await StartWorldAsync();
        var folder = Directory.CreateDirectory(Path.Combine(world.App.DataRoot, "to-check")).FullName;
        var file = Path.Combine(folder, "film.mkv");
        File.WriteAllBytes(file, [1, 2, 3]);
        var missing = Path.Combine(folder, "not-there");

        var blank = await world.App.Client.PostAsJsonAsync("/api/filesystem/path-diagnostics", new { path = "  " });
        var ofFolder = await (await world.App.Client.PostAsJsonAsync("/api/filesystem/path-diagnostics", new { path = folder })).Content.ReadFromJsonAsync<JsonElement>();
        var ofFile = await (await world.App.Client.PostAsJsonAsync("/api/filesystem/path-diagnostics", new { path = file })).Content.ReadFromJsonAsync<JsonElement>();
        var ofMissing = await (await world.App.Client.PostAsJsonAsync("/api/filesystem/path-diagnostics", new { path = missing })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Path is required.", await blank.Content.ReadAsStringAsync());
        Assert.True(ofFolder.GetProperty("exists").GetBoolean());
        Assert.True(ofFolder.GetProperty("isDirectory").GetBoolean());
        Assert.True(ofFolder.GetProperty("readable").GetBoolean());
        Assert.True(ofFolder.GetProperty("writable").GetBoolean());
        Assert.Empty(Directory.GetFiles(folder, ".deluno*"));
        Assert.True(ofFile.GetProperty("isFile").GetBoolean());
        Assert.True(ofFile.GetProperty("readable").GetBoolean());
        Assert.False(ofFile.GetProperty("isDirectory").GetBoolean());
        Assert.False(ofMissing.GetProperty("exists").GetBoolean());
        Assert.True(ofMissing.GetProperty("parentExists").GetBoolean());
        Assert.True(ofMissing.GetProperty("parentWritable").GetBoolean());
        Assert.False(ofMissing.GetProperty("writable").GetBoolean(), "a folder that is not there is not writable, only creatable");
        Assert.False(Directory.Exists(missing), "asking never creates the folder");
    }

    // ------------------------------------------------------------------ importing what a library already holds

    [Fact]
    public async Task Importing_what_a_library_holds_is_refused_in_plain_words_for_no_paths_an_unknown_library_or_a_root_that_is_gone()
    {
        await using var world = await StartWorldAsync();

        var noPaths = await world.App.Client.PostAsJsonAsync($"/api/libraries/{world.Library.Id}/import-existing/selected", new { sourcePaths = Array.Empty<string>() });
        var unknownSelected = await world.App.Client.PostAsJsonAsync("/api/libraries/no-such-library/import-existing/selected", new { sourcePaths = new[] { Path.Combine(world.Root, "x") } });
        var unknownStart = await world.App.Client.PostAsync("/api/libraries/no-such-library/import-existing", null);
        var unknownPreview = await world.App.Client.GetAsync("/api/libraries/no-such-library/import-existing/preview");
        Directory.Delete(world.Root, recursive: true);
        var rootGoneSelected = await world.App.Client.PostAsJsonAsync($"/api/libraries/{world.Library.Id}/import-existing/selected", new { sourcePaths = new[] { Path.Combine(world.Root, "x") } });
        var rootGoneStart = await world.App.Client.PostAsync($"/api/libraries/{world.Library.Id}/import-existing", null);
        var rootGonePreview = await world.App.Client.GetAsync($"/api/libraries/{world.Library.Id}/import-existing/preview");

        Assert.Equal(HttpStatusCode.BadRequest, noPaths.StatusCode);
        Assert.Contains("Select at least one file or folder to import.", await noPaths.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknownSelected.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownStart.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownPreview.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rootGoneSelected.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rootGoneStart.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rootGonePreview.StatusCode);
        Assert.Equal(0L, Convert.ToInt64(await world.ScalarAsync("jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'library.import.existing';")));
    }

    [Fact]
    public async Task Importing_a_selected_folder_with_nothing_importable_counts_it_skipped_and_says_so_in_Activity_while_a_real_film_is_brought_in()
    {
        await using var world = await StartWorldAsync();
        var film = Path.Combine(world.Root, "Sintel (2010)");
        Directory.CreateDirectory(film);
        File.WriteAllBytes(Path.Combine(film, "Sintel.2010.1080p.WEB-DL.x264-GRP.mkv"), [1, 2, 3, 4]);
        var empty = Directory.CreateDirectory(Path.Combine(world.Root, "Notes")).FullName;
        File.WriteAllText(Path.Combine(empty, "readme.txt"), "not a film");

        var response = await world.App.Client.PostAsJsonAsync($"/api/libraries/{world.Library.Id}/import-existing/selected", new { sourcePaths = new[] { film, empty } });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, result.GetProperty("requestedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("importedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("skippedCount").GetInt32());
        var issue = Assert.Single(result.GetProperty("issues").EnumerateArray());
        Assert.Equal("notImportable", issue.GetProperty("kind").GetString());
        Assert.Equal(empty, issue.GetProperty("sourcePath").GetString());
        var activity = await world.App.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, null, null, CancellationToken.None);
        var line = Assert.Single(activity, entry => entry.Category == "library.import.existing.selected");
        Assert.Contains("1", line.Message);
        Assert.Single(Directory.GetFiles(film));
        Assert.Single(Directory.GetFiles(empty));
        Assert.Equal(1L, Convert.ToInt64(await world.ScalarAsync("movies", "SELECT COUNT(*) FROM movie_entries;")));
    }

    // ------------------------------------------------------------------ the recycle bin

    [Fact]
    public async Task Saving_the_recycle_bin_settings_keeps_them_inside_their_limits_and_writes_them_to_the_state_on_disk()
    {
        await using var world = await StartWorldAsync();

        var tooSmall = await world.App.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 0, maxSizeMb = 0 });
        var tooBig = await world.App.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 9999, maxSizeMb = 99_999_999 });
        var fine = await world.App.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 14, maxSizeMb = 2048 });

        var low = await tooSmall.Content.ReadFromJsonAsync<JsonElement>();
        var high = await tooBig.Content.ReadFromJsonAsync<JsonElement>();
        var kept = await fine.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, low.GetProperty("retentionDays").GetInt32());
        Assert.Equal(1, low.GetProperty("maxSizeMb").GetInt64());
        Assert.Equal(365, high.GetProperty("retentionDays").GetInt32());
        Assert.Equal(10_000_000, high.GetProperty("maxSizeMb").GetInt64());
        Assert.Equal(14, kept.GetProperty("retentionDays").GetInt32());
        Assert.Equal(2048, kept.GetProperty("maxSizeMb").GetInt64());
        var again = await world.App.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/settings");
        Assert.Equal(14, again.GetProperty("retentionDays").GetInt32());
        var state = File.ReadAllText(Path.Combine(world.App.DataRoot, "recycle-bin", "state.json"));
        Assert.Contains("14", state);
    }

    [Fact]
    public async Task Restoring_an_item_that_is_not_there_or_whose_stored_copy_has_gone_answers_409_and_drops_the_dead_entry()
    {
        await using var world = await StartWorldAsync();
        var file = Path.Combine(world.Root, "Sintel (2010)", "Sintel.2010.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [1, 2, 3]);
        var stored = (await world.Bin.StoreReplacementAsync(world.Library, file, file, CancellationToken.None))!;
        File.Delete(stored.RecyclePath);

        var unknown = await world.App.Client.PostAsync("/api/recycle-bin/not-an-item/restore", null);
        var gone = await world.App.Client.PostAsync($"/api/recycle-bin/{stored.Id}/restore", null);

        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Contains("Recycle-bin item not found", await unknown.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
        Assert.Contains("no longer on disk", await gone.Content.ReadAsStringAsync());
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.False(File.Exists(file));
    }
}
