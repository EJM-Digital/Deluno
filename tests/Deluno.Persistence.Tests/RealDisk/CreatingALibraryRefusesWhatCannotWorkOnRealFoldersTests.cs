using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Creating a library on the real application with real folders and files: each thing that cannot work is refused in
/// plain words naming the field, a path that is a file is called a file, and nothing is stored for a refused request.
/// Refs #1296.
/// </summary>
public sealed class CreatingALibraryRefusesWhatCannotWorkOnRealFoldersTests
{
    private static async Task<string[]> ErrorsAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").GetProperty(field).EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    [Fact]
    public async Task A_library_with_no_name_no_folder_or_a_kind_that_is_not_movies_or_tv_is_refused_with_the_exact_words()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;

        Assert.Equal(["Give this library a name."], await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/libraries", new { name = " ", mediaType = "movies", rootPath = root }), "name"));
        Assert.Equal(["Choose a folder for this library."], await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = "" }), "rootPath"));
        Assert.Equal(["Choose Movies or TV Shows."], await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "music", rootPath = root }), "mediaType"));
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray());
    }

    [Theory]
    [InlineData("rootPath", "this library lives in")]
    [InlineData("downloadsPath", "downloads arrive in")]
    [InlineData("processorOutputPath", "your processor writes to")]
    public async Task A_folder_that_is_not_there_or_is_a_file_is_refused_naming_which_it_is(string field, string whatItIsFor)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Real")).FullName;
        var missing = Path.Combine(app.DataRoot, "Not-there");
        var file = Path.Combine(app.DataRoot, "a-file.txt");
        await File.WriteAllTextAsync(file, "not a folder");

        object Body(string path) => field switch
        {
            "rootPath" => new { name = "Films", mediaType = "movies", rootPath = path },
            "downloadsPath" => new { name = "Films", mediaType = "movies", rootPath = folder, downloadsPath = path },
            _ => new { name = "Films", mediaType = "movies", rootPath = folder, processorOutputPath = path }
        };

        var gone = await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/libraries", Body(missing)), field);
        var isFile = await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/libraries", Body(file)), field);

        Assert.Equal([$"{missing} does not exist. Create it first, or choose the folder {whatItIsFor}."], gone);
        Assert.Equal([$"{file} is a file, not a folder. Choose the folder {whatItIsFor}."], isFile);
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray());
        Assert.True(File.Exists(file) && Directory.Exists(folder), "the folders and files are left as they were");
    }

    [Fact]
    public async Task A_library_with_real_folders_is_made_and_tv_shows_is_accepted_as_a_way_to_say_tv()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;

        var response = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Shows", mediaType = "TV Shows", rootPath = root });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray());
        Assert.True(Directory.Exists(root));
    }
}
