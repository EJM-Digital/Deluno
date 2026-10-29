using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// PUT /api/libraries/{id}: a name and an existing folder are required, a body that is not an object is refused, an
/// unknown library is a 404, and a good edit renames and re-points the library (coverage register, area engines).
/// </summary>
public sealed class EditingALibraryRefusesWhatCannotBeSavedTests
{
    [Fact]
    public async Task A_blank_name_a_missing_folder_and_a_body_that_is_not_an_object_are_refused_in_words()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, id);

        var noName = await app.Client.PutAsJsonAsync($"/api/libraries/{id}", new { name = "  ", rootPath = root });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Contains("Give this library a name.", await noName.Content.ReadAsStringAsync());

        var noFolder = await app.Client.PutAsJsonAsync($"/api/libraries/{id}", new { name = "Films", rootPath = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noFolder.StatusCode);
        Assert.Contains("Choose a folder for this library.", await noFolder.Content.ReadAsStringAsync());

        var gone = await app.Client.PutAsJsonAsync($"/api/libraries/{id}", new { name = "Films", rootPath = Path.Combine(root, "no-such-folder") });
        Assert.Equal(HttpStatusCode.BadRequest, gone.StatusCode);

        using var notAnObject = new StringContent("[1,2]", Encoding.UTF8, "application/json");
        var array = await app.Client.PutAsync($"/api/libraries/{id}", notAnObject);
        Assert.Equal(HttpStatusCode.BadRequest, array.StatusCode);
        Assert.Contains("Send the library's details as an object.", await array.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_library_is_a_404_and_a_good_edit_renames_and_re_points_the_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await ApiScenes.CreateLibraryAsync(app, "movies");
        var elsewhere = Directory.CreateDirectory(Path.Combine(app.DataRoot, "moved-films")).FullName;

        var unknown = await app.Client.PutAsJsonAsync("/api/libraries/no-such-library", new { name = "Films", rootPath = elsewhere });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var edited = await app.Client.PutAsJsonAsync($"/api/libraries/{id}", new { name = "Renamed films", rootPath = elsewhere });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed films", body.GetProperty("name").GetString());
        Assert.Equal(elsewhere, body.GetProperty("rootPath").GetString());
        Assert.Equal(await ApiScenes.LibraryRootAsync(app, id), elsewhere);
    }
}
