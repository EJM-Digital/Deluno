using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Applying a bulk rename (both engines) when something is wrong: each refusal is said in words against the title, one
/// Activity line sums the batch up, and a title already named as asked is skipped, not failed (coverage register, area engines).
/// </summary>
public sealed class TheBulkRenameApplyRefusesInWordsTests
{
    public static TheoryData<string> Kinds => new() { "movies", "series" };

    private static string IdsKey(string kind) => kind == "movies" ? "movieIds" : "seriesIds";

    private static string ActivityCategory(string kind) => kind == "movies" ? "library.movie.renamed" : "library.series.renamed";

    private static async Task<(string TitleId, string FolderPath)> TitleAsync(ApplicationTestHost app, string kind, string? libraryId = null, string? folderName = null)
    {
        if (kind == "movies")
        {
            var film = await ApiScenes.FilmOnDiskAsync(app, libraryId: libraryId, folderName: folderName);
            return (film.MovieId, film.FolderPath);
        }

        var show = await ApiScenes.ShowOnDiskAsync(app, libraryId: libraryId, folderName: folderName);
        return (show.SeriesId, show.FolderPath);
    }

    private static async Task<HttpResponseMessage> ApplyAsync(ApplicationTestHost app, string kind, string[] ids, string? template = "Renamed Folder")
        => await app.Client.PostAsJsonAsync($"/api/{kind}/bulk/rename-apply", new Dictionary<string, object?> { [IdsKey(kind)] = ids, ["template"] = template });

    private static async Task<JsonElement> ApplyOkAsync(ApplicationTestHost app, string kind, string[] ids, string? template = "Renamed Folder")
    {
        var response = await ApplyAsync(app, kind, ids, template);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<long> ActivityLinesAsync(ApplicationTestHost app, string kind)
        => await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM activity_events WHERE category = '{ActivityCategory(kind)}';");

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Choosing_nothing_is_refused_as_a_validation_problem_and_writes_no_activity(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await ApplyAsync(app, kind, []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(kind == "movies" ? "Choose at least one movie to rename." : "Choose at least one series to rename.", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await ActivityLinesAsync(app, kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_title_that_no_longer_exists_is_named_as_a_failure_and_the_batch_is_summed_up_in_one_activity_line(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var result = await ApplyOkAsync(app, kind, ["no-such-title"]);

        Assert.Equal(0, result.GetProperty("renamedCount").GetInt32());
        var failure = Assert.Single(result.GetProperty("failures").EnumerateArray());
        Assert.Equal(kind == "movies" ? "This title no longer exists." : "This series no longer exists.", failure.GetProperty("reason").GetString());
        Assert.Equal(1, await ActivityLinesAsync(app, kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_folder_that_already_has_the_new_name_is_refused_and_nothing_moves(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (id, folder) = await TitleAsync(app, kind, folderName: "old folder name");
        var taken = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(folder)!, "Renamed Folder")).FullName;

        var result = await ApplyOkAsync(app, kind, [id]);

        Assert.Equal(0, result.GetProperty("renamedCount").GetInt32());
        var failure = Assert.Single(result.GetProperty("failures").EnumerateArray());
        Assert.Equal("A folder named \"Renamed Folder\" already exists.", failure.GetProperty("reason").GetString());
        Assert.True(Directory.Exists(folder));
        Assert.True(Directory.Exists(taken));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_folder_the_system_will_not_rename_is_said_so_in_words_and_left_where_it_is(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (id, folder) = await TitleAsync(app, kind, folderName: "old folder name");
        var parent = Path.GetDirectoryName(folder)!;
        FileStream? held = null;
        UnixFileMode? parentMode = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // An open file inside the folder is a sharing violation for the rename.
                held = new FileStream(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).First(), FileMode.Open, FileAccess.Read, FileShare.None);
            }
            else
            {
                parentMode = File.GetUnixFileMode(parent);
                File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                if (Environment.UserName == "root")
                {
                    return; // root ignores the permission, so there is nothing to refuse
                }
            }

            var result = await ApplyOkAsync(app, kind, [id]);

            Assert.Equal(0, result.GetProperty("renamedCount").GetInt32());
            var failure = Assert.Single(result.GetProperty("failures").EnumerateArray());
            Assert.Equal("The folder could not be renamed on disk.", failure.GetProperty("reason").GetString());
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            held?.Dispose();
            if (parentMode is { } mode && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(parent, mode);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_folder_already_named_as_asked_is_skipped_not_failed_and_a_mixed_batch_is_one_activity_line(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (renamedId, _) = await TitleAsync(app, kind, folderName: "old folder name");

        var first = await ApplyOkAsync(app, kind, [renamedId]);
        Assert.Equal(1, first.GetProperty("renamedCount").GetInt32());

        // Same title, same template: already named correctly, so skipped. The missing id is the one failure.
        var second = await ApplyOkAsync(app, kind, [renamedId, "no-such-title"]);

        Assert.Equal(0, second.GetProperty("renamedCount").GetInt32());
        Assert.Single(second.GetProperty("failures").EnumerateArray());
        Assert.Equal(2, await ActivityLinesAsync(app, kind));
        var latest = await ApiScenes.TextAsync(app, "jobs", $"SELECT message FROM activity_events WHERE category = '{ActivityCategory(kind)}' ORDER BY created_utc DESC, rowid DESC LIMIT 1;");
        Assert.Equal("0 " + (kind == "movies" ? "movie" : "series") + " folders renamed; 1 could not be renamed.", latest);
    }
}
