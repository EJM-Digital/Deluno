using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Check library files now" on real files: the route runs the shared library file check and answers how many tracked files were
/// not on disk (markedMissing) and how many library roots it could not reach (unreachableRoots). A film whose file was deleted is
/// counted and the others are left alone; a root that is gone is counted as unreachable and nothing under it is marked.
/// </summary>
public sealed class TheFileCheckRouteReportsWhatItMarkedAndWhatItCouldNotReachTests
{
    private static async Task<JsonElement> CheckAsync(ApplicationTestHost app)
    {
        using var response = await app.Client.PostAsync("/api/filesystem/file-check", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_deleted_file_is_counted_as_marked_missing_and_the_library_that_is_there_is_not_unreachable()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var kept = await ApiScenes.FilmOnDiskAsync(app, "Kept Film", 2001, libraryId);
        var other = await ApiScenes.FilmOnDiskAsync(app, "Other Film", 2002, libraryId);
        var lost = await ApiScenes.FilmOnDiskAsync(app, "Lost Film", 2003, libraryId);
        File.Delete(lost.FilePath);

        var check = await CheckAsync(app);

        Assert.Equal(1, check.GetProperty("markedMissing").GetInt32());
        Assert.Equal(0, check.GetProperty("unreachableRoots").GetInt32());
        Assert.True(File.Exists(kept.FilePath));
        Assert.True(File.Exists(other.FilePath));
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{kept.MovieId}")).GetProperty("monitored").GetBoolean());
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{lost.MovieId}")).GetProperty("monitored").GetBoolean());
    }

    [Fact]
    public async Task A_library_root_that_is_gone_is_counted_as_unreachable_and_nothing_under_it_is_marked_missing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shelf")).FullName;
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", root);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Shelf Film", 2004, libraryId);
        Directory.Delete(root, recursive: true);

        var check = await CheckAsync(app);

        Assert.Equal(1, check.GetProperty("unreachableRoots").GetInt32());
        Assert.Equal(0, check.GetProperty("markedMissing").GetInt32());
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}")).GetProperty("monitored").GetBoolean());
    }

    [Fact]
    public async Task The_route_needs_a_sign_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.CreateAnonymousClient().PostAsync("/api/filesystem/file-check", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
