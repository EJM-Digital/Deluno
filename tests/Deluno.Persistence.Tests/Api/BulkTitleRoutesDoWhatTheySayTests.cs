using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The bulk routes of both engines when the request is right (coverage register, area api): what a batch
/// changes in the real database, and where it moves real folders and files.
/// </summary>
public sealed class BulkTitleRoutesDoWhatTheySayTests
{
    public static TheoryData<string> Kinds => new() { "movies", "series" };

    private static string IdsKey(string kind) => kind == "movies" ? "movieIds" : "seriesIds";

    private static async Task<(string LibraryId, string TitleId, string FilePath, string FolderPath)> TitleAsync(
        ApplicationTestHost app, string kind, string? libraryId = null, string? folderName = null)
    {
        if (kind == "movies")
        {
            var film = await ApiScenes.FilmOnDiskAsync(app, libraryId: libraryId, folderName: folderName);
            return (film.LibraryId, film.MovieId, film.FilePath, film.FolderPath);
        }

        var show = await ApiScenes.ShowOnDiskAsync(app, libraryId: libraryId, folderName: folderName);
        return (show.LibraryId, show.SeriesId, show.FilePath, show.FolderPath);
    }

    private static async Task<JsonElement> PostAsync(ApplicationTestHost app, string url, object body)
    {
        var response = await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<bool> MonitoredAsync(ApplicationTestHost app, string kind, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>($"/api/{kind}/{id}")).GetProperty("monitored").GetBoolean();

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_bulk_monitoring_change_applies_to_every_chosen_title_and_reports_each(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, titleId, filePath, _) = await TitleAsync(app, kind);
        Assert.True(await MonitoredAsync(app, kind, titleId));

        var result = await PostAsync(app, $"/api/{kind}/bulk", new Dictionary<string, object?>
        {
            [IdsKey(kind)] = new[] { titleId, "no-such-title" },
            ["operation"] = "monitoring",
            ["monitored"] = false
        });

        Assert.Equal(2, result.GetProperty("totalProcessed").GetInt32());
        Assert.Equal(1, result.GetProperty("successCount").GetInt32());
        Assert.Equal(1, result.GetProperty("failureCount").GetInt32());
        Assert.False(await MonitoredAsync(app, kind, titleId));
        var missing = result.GetProperty("results").EnumerateArray().Single(item => !item.GetProperty("succeeded").GetBoolean());
        Assert.Contains("not found", missing.GetProperty("errorMessage").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(filePath), "changing what is wanted never touches a file");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_bulk_remove_with_delete_files_takes_the_title_and_its_folder_off_the_disk_into_the_recycle_bin(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, titleId, filePath, folder) = await TitleAsync(app, kind);
        Assert.True(File.Exists(filePath));

        var result = await PostAsync(app, $"/api/{kind}/bulk", new Dictionary<string, object?>
        {
            [IdsKey(kind)] = new[] { titleId },
            ["operation"] = "remove",
            ["deleteFiles"] = true
        });

        Assert.Equal(1, result.GetProperty("successCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/{kind}/{titleId}")).StatusCode);
        Assert.False(File.Exists(filePath), "the file left the library");
        Assert.False(Directory.Exists(folder), "so did the now-empty title folder");
        var bin = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin");
        Assert.True(bin.GetArrayLength() > 0, "the file is held in the recycle bin and can be restored");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_bulk_remove_without_delete_files_forgets_the_title_and_leaves_its_files_where_they_are(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, titleId, filePath, _) = await TitleAsync(app, kind);

        var result = await PostAsync(app, $"/api/{kind}/bulk", new Dictionary<string, object?>
        {
            [IdsKey(kind)] = new[] { titleId },
            ["operation"] = "remove"
        });

        Assert.Equal(1, result.GetProperty("successCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/{kind}/{titleId}")).StatusCode);
        Assert.True(File.Exists(filePath), "removing a title without asking for its files to go never deletes them");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_bulk_call_with_nothing_chosen_or_an_unknown_operation_is_refused_with_the_reason(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, titleId, _, _) = await TitleAsync(app, kind);

        var none = await app.Client.PostAsJsonAsync($"/api/{kind}/bulk", new Dictionary<string, object?> { [IdsKey(kind)] = Array.Empty<string>(), ["operation"] = "monitoring", ["monitored"] = false });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Select at least one", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var unknown = await PostAsync(app, $"/api/{kind}/bulk", new Dictionary<string, object?> { [IdsKey(kind)] = new[] { titleId }, ["operation"] = "explode" });
        Assert.Equal(0, unknown.GetProperty("successCount").GetInt32());
        Assert.Contains("Unknown operation: explode", unknown.GetProperty("results")[0].GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
        Assert.True(await MonitoredAsync(app, kind, titleId));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Bulk_tags_add_replace_and_remove_change_exactly_the_tags_asked_for(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, titleId, _, _) = await TitleAsync(app, kind);

        async Task<string[]> TagsAsync() => (await app.Client.GetFromJsonAsync<JsonElement>($"/api/{kind}/{titleId}/tags"))
            .EnumerateArray().Select(tag => tag.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();

        var added = await PostAsync(app, $"/api/{kind}/bulk/tags", new Dictionary<string, object?> { [IdsKey(kind)] = new[] { titleId }, ["tags"] = "kids, favourite", ["mode"] = "add" });
        Assert.Equal(1, added.GetProperty("updated").GetInt32());
        Assert.Equal(["favourite", "kids"], await TagsAsync());

        await PostAsync(app, $"/api/{kind}/bulk/tags", new Dictionary<string, object?> { [IdsKey(kind)] = new[] { titleId }, ["tags"] = "kids", ["mode"] = "remove" });
        Assert.Equal(["favourite"], await TagsAsync());

        await PostAsync(app, $"/api/{kind}/bulk/tags", new Dictionary<string, object?> { [IdsKey(kind)] = new[] { titleId }, ["tags"] = "classic", ["mode"] = "replace" });
        Assert.Equal(["classic"], await TagsAsync());

        var refused = await app.Client.PostAsJsonAsync($"/api/{kind}/bulk/tags", new Dictionary<string, object?> { [IdsKey(kind)] = new[] { titleId }, ["tags"] = "x", ["mode"] = "swap" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Mode must be add, remove or replace.", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(["classic"], await TagsAsync());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_bulk_search_asks_for_one_search_in_the_library_of_each_wanted_title(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, kind == "movies" ? "movies" : "tv");
        string titleId;
        if (kind == "movies")
        {
            var added = await PostAsync(app, "/api/movies", new { title = "Missing Film", releaseYear = 2020, monitored = true, libraryId });
            titleId = added.GetProperty("id").GetString()!;
        }
        else
        {
            var added = await PostAsync(app, "/api/series", new { title = "Missing Show", startYear = 2020, monitored = true, libraryId });
            titleId = added.GetProperty("id").GetString()!;
        }

        var result = await PostAsync(app, $"/api/{kind}/bulk/search", new Dictionary<string, object?> { [IdsKey(kind)] = new[] { titleId } });

        Assert.Equal(1, result.GetProperty("searchesTriggered").GetInt32());
        Assert.Equal(1, result.GetProperty("libraryCount").GetInt32());
        var queued = Assert.Single(await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None), job => job.RelatedEntityId == titleId && job.JobType.EndsWith(".search.manual", StringComparison.Ordinal));
        Assert.Contains(libraryId, queued.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM library_automation_state WHERE search_requested = 1;"));

        var refused = await app.Client.PostAsJsonAsync($"/api/{kind}/bulk/search", new Dictionary<string, object?> { [IdsKey(kind)] = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Choose at least one", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reassigning_films_with_move_files_moves_the_whole_title_folder_and_every_path_follows()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var fromId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var toId = await ApiScenes.CreateLibraryAsync(app, "movies", "Kids", "Kids");
        var film = await ApiScenes.FilmOnDiskAsync(app, libraryId: fromId);
        var extra = Path.Combine(film.FolderPath, "Dune.en.srt");
        await File.WriteAllTextAsync(extra, "subtitle");
        var toRoot = await ApiScenes.LibraryRootAsync(app, toId);

        var result = await PostAsync(app, "/api/movies/bulk/reassign-library",
            new { movieIds = new[] { film.MovieId }, fromLibraryId = fromId, toLibraryId = toId, moveFiles = true });

        Assert.Equal(1, result.GetProperty("reassigned").GetInt32());
        Assert.Equal(1, result.GetProperty("filesMoved").GetInt32());
        var newFolder = Path.Combine(toRoot, Path.GetFileName(film.FolderPath));
        Assert.True(File.Exists(Path.Combine(newFolder, Path.GetFileName(film.FilePath))), "the video is in the new library");
        Assert.True(File.Exists(Path.Combine(newFolder, "Dune.en.srt")), "the subtitle beside it travelled too");
        Assert.False(Directory.Exists(film.FolderPath), "nothing is left under the old library");
        var detail = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}");
        Assert.Equal(Path.Combine(newFolder, Path.GetFileName(film.FilePath)), detail.GetProperty("filePath").GetString());
        Assert.Equal(toId, detail.GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task Reassigning_films_without_move_files_changes_the_library_and_leaves_every_file_where_it_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var fromId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var toId = await ApiScenes.CreateLibraryAsync(app, "movies", "Kids", "Kids");
        var film = await ApiScenes.FilmOnDiskAsync(app, libraryId: fromId);

        var result = await PostAsync(app, "/api/movies/bulk/reassign-library",
            new { movieIds = new[] { film.MovieId }, fromLibraryId = fromId, toLibraryId = toId });

        Assert.Equal(1, result.GetProperty("reassigned").GetInt32());
        Assert.Equal(0, result.GetProperty("filesMoved").GetInt32());
        Assert.True(File.Exists(film.FilePath), "the file stays in the old folder");
        Assert.Equal(toId, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}")).GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task Reassigning_shows_changes_their_library_and_leaves_the_folders_where_they_are()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var fromId = await ApiScenes.CreateLibraryAsync(app, "tv", "Shows", "Shows");
        var toId = await ApiScenes.CreateLibraryAsync(app, "tv", "Kids Shows", "Kids Shows");
        var show = await ApiScenes.ShowOnDiskAsync(app, libraryId: fromId);

        var result = await PostAsync(app, "/api/series/bulk/reassign-library",
            new { seriesIds = new[] { show.SeriesId }, fromLibraryId = fromId, toLibraryId = toId });

        Assert.Equal(1, result.GetProperty("reassigned").GetInt32());
        Assert.True(File.Exists(show.FilePath), "this route changes the record only");
        Assert.Equal(toId, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}")).GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task Reassigning_shows_moves_the_whole_show_folder_into_the_new_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var fromId = await ApiScenes.CreateLibraryAsync(app, "tv", "Shows", "Shows");
        var toId = await ApiScenes.CreateLibraryAsync(app, "tv", "Kids Shows", "Kids Shows");
        var show = await ApiScenes.ShowOnDiskAsync(app, libraryId: fromId);
        var extra = Path.Combine(show.FolderPath, "Season 01", "The Wire S01E01.en.srt");
        await File.WriteAllTextAsync(extra, "subtitle");
        var toRoot = await ApiScenes.LibraryRootAsync(app, toId);

        var result = await PostAsync(app, "/api/series/bulk/reassign-library",
            new { seriesIds = new[] { show.SeriesId }, fromLibraryId = fromId, toLibraryId = toId, moveFiles = true });

        Assert.Equal(1, result.GetProperty("reassigned").GetInt32());
        Assert.Equal(1, result.GetProperty("filesMoved").GetInt32());
        Assert.Empty(result.GetProperty("moveFailures").EnumerateArray());
        var newFolder = Path.Combine(toRoot, Path.GetFileName(show.FolderPath));
        Assert.False(Directory.Exists(show.FolderPath), "the folder left the old library");
        Assert.True(Directory.Exists(newFolder));
        var newFile = Path.Combine(newFolder, Path.GetRelativePath(show.FolderPath, show.FilePath));
        Assert.True(File.Exists(newFile), "the episode is in the new library");
        Assert.True(File.Exists(Path.Combine(newFolder, "Season 01", "The Wire S01E01.en.srt")), "the subtitle beside it travelled too");
        Assert.Equal(newFile, await ApiScenes.TextAsync(app, "series", "SELECT file_path FROM episode_entries;"));
        Assert.Equal(toId, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}")).GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task Reassigning_shows_refuses_a_move_whose_target_folder_already_exists_and_loses_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var fromId = await ApiScenes.CreateLibraryAsync(app, "tv", "Shows", "Shows");
        var toId = await ApiScenes.CreateLibraryAsync(app, "tv", "Kids Shows", "Kids Shows");
        var show = await ApiScenes.ShowOnDiskAsync(app, libraryId: fromId);
        var toRoot = await ApiScenes.LibraryRootAsync(app, toId);
        var occupied = Path.Combine(toRoot, Path.GetFileName(show.FolderPath));
        Directory.CreateDirectory(occupied);
        var theirs = Path.Combine(occupied, "keep.txt");
        await File.WriteAllTextAsync(theirs, "another title's file");

        var result = await PostAsync(app, "/api/series/bulk/reassign-library",
            new { seriesIds = new[] { show.SeriesId }, fromLibraryId = fromId, toLibraryId = toId, moveFiles = true });

        Assert.Equal(0, result.GetProperty("reassigned").GetInt32());
        Assert.Equal(0, result.GetProperty("filesMoved").GetInt32());
        Assert.Equal("The Wire", Assert.Single(result.GetProperty("moveFailures").EnumerateArray()).GetString());
        Assert.Contains("already exists", result.GetProperty("moveFailureReasons")[0].GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(show.FilePath), "the show's own files stay put");
        Assert.Equal("another title's file", await File.ReadAllTextAsync(theirs));
        Assert.Single(Directory.EnumerateFileSystemEntries(occupied));
        Assert.Equal(show.FilePath, await ApiScenes.TextAsync(app, "series", "SELECT file_path FROM episode_entries;"));
        Assert.Equal(fromId, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}")).GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task Reassigning_several_shows_moves_the_ones_that_can_move_and_names_the_one_that_cannot()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var fromId = await ApiScenes.CreateLibraryAsync(app, "tv", "Shows", "Shows");
        var toId = await ApiScenes.CreateLibraryAsync(app, "tv", "Kids Shows", "Kids Shows");
        var wire = await ApiScenes.ShowOnDiskAsync(app, title: "The Wire", libraryId: fromId);
        var treme = await ApiScenes.ShowOnDiskAsync(app, title: "Treme", year: 2010, libraryId: fromId);
        var toRoot = await ApiScenes.LibraryRootAsync(app, toId);
        Directory.CreateDirectory(Path.Combine(toRoot, "Treme"));

        var result = await PostAsync(app, "/api/series/bulk/reassign-library",
            new { seriesIds = new[] { wire.SeriesId, treme.SeriesId }, fromLibraryId = fromId, toLibraryId = toId, moveFiles = true });

        Assert.Equal(1, result.GetProperty("reassigned").GetInt32());
        Assert.Equal(1, result.GetProperty("filesMoved").GetInt32());
        Assert.Equal("Treme", Assert.Single(result.GetProperty("moveFailures").EnumerateArray()).GetString());
        Assert.True(File.Exists(Path.Combine(toRoot, "The Wire", "Season 01", "The Wire S01E01.mkv")));
        Assert.True(File.Exists(treme.FilePath));
        Assert.Equal(toId, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{wire.SeriesId}")).GetProperty("libraryId").GetString());
        Assert.Equal(fromId, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{treme.SeriesId}")).GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task Reassigning_shows_puts_the_folder_back_when_the_records_cannot_follow()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var fromId = await ApiScenes.CreateLibraryAsync(app, "tv", "Shows", "Shows");
        var toId = await ApiScenes.CreateLibraryAsync(app, "tv", "Kids Shows", "Kids Shows");
        var show = await ApiScenes.ShowOnDiskAsync(app, libraryId: fromId);
        await ApiScenes.ExecuteAsync(app, "series",
            "CREATE TRIGGER refuse_library_change BEFORE UPDATE OF library_id ON series_wanted_state BEGIN SELECT RAISE(ABORT, 'database is locked'); END;");

        var result = await PostAsync(app, "/api/series/bulk/reassign-library",
            new { seriesIds = new[] { show.SeriesId }, fromLibraryId = fromId, toLibraryId = toId, moveFiles = true });

        Assert.Equal(0, result.GetProperty("reassigned").GetInt32());
        Assert.True(File.Exists(show.FilePath), "the folder was put back where it was");
        Assert.False(Directory.Exists(Path.Combine(await ApiScenes.LibraryRootAsync(app, toId), Path.GetFileName(show.FolderPath))));
        Assert.Equal(show.FilePath, await ApiScenes.TextAsync(app, "series", "SELECT file_path FROM episode_entries;"));
        Assert.Equal(fromId, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}")).GetProperty("libraryId").GetString());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Both_reassign_routes_take_the_same_options_and_answer_in_the_same_shape(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var mediaType = kind == "movies" ? "movies" : "tv";
        var fromId = await ApiScenes.CreateLibraryAsync(app, mediaType, "From", "From");
        var toId = await ApiScenes.CreateLibraryAsync(app, mediaType, "To", "To");
        var (_, titleId, filePath, folderPath) = await TitleAsync(app, kind, fromId);
        var toRoot = await ApiScenes.LibraryRootAsync(app, toId);
        var body = new Dictionary<string, object?> { [IdsKey(kind)] = new[] { titleId }, ["fromLibraryId"] = fromId, ["toLibraryId"] = toId };

        var recordOnly = await PostAsync(app, $"/api/{kind}/bulk/reassign-library", body);
        Assert.Equal(["filesMoved", "moveFailures", "reassigned"], recordOnly.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(0, recordOnly.GetProperty("filesMoved").GetInt32());
        Assert.True(File.Exists(filePath), "left alone unless asked");

        body["fromLibraryId"] = toId;
        body["toLibraryId"] = fromId;
        await PostAsync(app, $"/api/{kind}/bulk/reassign-library", body);
        body["fromLibraryId"] = fromId;
        body["toLibraryId"] = toId;
        body["moveFiles"] = true;
        var moved = await PostAsync(app, $"/api/{kind}/bulk/reassign-library", body);

        var names = moved.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("filesMoved", names);
        Assert.Contains("moveFailures", names);
        Assert.Contains("moveFailureReasons", names);
        Assert.Equal(1, moved.GetProperty("filesMoved").GetInt32());
        Assert.False(Directory.Exists(folderPath));
        Assert.True(Directory.Exists(Path.Combine(toRoot, Path.GetFileName(folderPath))));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Applying_renames_renames_the_title_folder_on_disk_and_every_stored_path_follows(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, titleId, filePath, oldFolder) = await TitleAsync(app, kind, folderName: "old folder name");
        Assert.True(File.Exists(filePath));

        var result = await PostAsync(app, $"/api/{kind}/bulk/rename-apply", new Dictionary<string, object?> { [IdsKey(kind)] = new[] { titleId } });

        Assert.Equal(1, result.GetProperty("renamedCount").GetInt32());
        Assert.Empty(result.GetProperty("failures").EnumerateArray());
        var renamed = result.GetProperty("renamed")[0];
        var newName = renamed.GetProperty("to").GetString()!;
        Assert.Equal("old folder name", renamed.GetProperty("from").GetString());
        var newFolder = Path.Combine(Path.GetDirectoryName(oldFolder)!, newName);
        Assert.False(Directory.Exists(oldFolder), "the old folder name is gone");
        Assert.True(Directory.Exists(newFolder), "the folder carries the template's name");
        var relative = Path.GetRelativePath(oldFolder, filePath);
        Assert.True(File.Exists(Path.Combine(newFolder, relative)), "the file moved along with its folder");

        var again = await PostAsync(app, $"/api/{kind}/bulk/rename-apply", new Dictionary<string, object?> { [IdsKey(kind)] = new[] { titleId } });
        Assert.Equal(0, again.GetProperty("renamedCount").GetInt32());
        Assert.Empty(again.GetProperty("failures").EnumerateArray());
    }
}
