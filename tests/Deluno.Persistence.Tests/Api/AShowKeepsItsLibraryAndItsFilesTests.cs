using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A show is in one library (#530): taking it out of that library is refused in plain words, whether it holds files there or not, and the
/// files on disk are where they were. Run on a real show on disk. "Want it again" is a film's switch only; a show has no such route.
/// </summary>
public sealed class AShowKeepsItsLibraryAndItsFilesTests
{
    [Fact]
    public async Task Taking_a_show_out_of_the_library_that_holds_its_files_is_refused_and_every_file_stays_where_it_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var removed = await app.Client.DeleteAsync($"/api/series/{show.SeriesId}/libraries/{show.LibraryId}");

        Assert.Equal(HttpStatusCode.Conflict, removed.StatusCode);
        var message = (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.Contains("This show has a file in Shows.", message);
        Assert.True(File.Exists(show.FilePath));
        var libraries = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/libraries");
        Assert.Equal(show.LibraryId, Assert.Single(libraries.EnumerateArray()).GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task Taking_a_show_with_no_file_out_of_its_only_library_is_refused_and_a_library_it_is_not_in_or_a_show_that_is_gone_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await ApiScenes.CreateLibraryAsync(app, "tv");
        var other = await ApiScenes.CreateLibraryAsync(app, "tv", folder: "Anime", name: "Anime");
        var added = await app.Client.PostAsJsonAsync("/api/series/", new { title = "Severance", startYear = 2022, monitored = true, libraryId = library });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var only = await app.Client.DeleteAsync($"/api/series/{id}/libraries/{library}");

        Assert.Equal(HttpStatusCode.Conflict, only.StatusCode);
        Assert.Contains("is the only library this show is in", await only.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/series/{id}/libraries/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/series/no-such-show/libraries/{library}")).StatusCode);
        Assert.True(Directory.Exists(await ApiScenes.LibraryRootAsync(app, library)));
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "series", $"SELECT COUNT(*) FROM series_wanted_state WHERE series_id = '{id}'"));
    }

    [Fact]
    public async Task Wanting_a_show_again_in_a_library_is_not_offered_because_a_show_is_in_one_library_only()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var response = await app.Client.PostAsync($"/api/series/{show.SeriesId}/libraries/{show.LibraryId}/want-again", content: null);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, response.StatusCode.ToString());
        Assert.True(File.Exists(show.FilePath));
    }
}
