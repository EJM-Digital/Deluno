using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Clear" on a stray item in a processor's output folder (System, Health): the only way Deluno ever removes anything there. It
/// removes exactly one item directly inside the folder the library reads processed files from, and nothing a library holds.
/// </summary>
public sealed class ProcessorOutputClearOnRealFilesTests
{
    [Fact]
    public async Task Clear_removes_one_stray_file_and_one_stray_folder_and_leaves_their_siblings_and_the_library_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);

        using var file = await ClearAsync(app, world.LibraryId, world.StrayFile);
        using var folder = await ClearAsync(app, world.LibraryId, world.StrayFolder);

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(HttpStatusCode.OK, folder.StatusCode);
        Assert.False(File.Exists(world.StrayFile));
        Assert.False(Directory.Exists(world.StrayFolder));
        Assert.True(File.Exists(world.Sibling), "another item in the output folder stays");
        Assert.True(File.Exists(world.LibraryFilm), "the library's film stays");
        var activity = await app.Client.GetStringAsync("/api/activity?take=100");
        Assert.Contains("Moved stray.mkv from the Films library's processor output folder to the recycle bin.", activity);
        Assert.Contains("Moved the folder Stray.Folder from the Films library's processor output folder to the recycle bin.", activity);
    }

    [Fact]
    public async Task Clear_refuses_anything_that_is_not_directly_inside_the_output_folder_and_deletes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var nested = Path.Combine(world.StrayFolder, "inside.mkv");
        var climbing = Path.Combine(world.Output, "..", "movies", "Film (2020)", "Film.2020.mkv");

        foreach (var path in new[] { nested, climbing, world.LibraryFilm, world.Output, world.Root })
        {
            using var response = await ClearAsync(app, world.LibraryId, path);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Deluno only removes an item directly inside a processor output folder this way.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        }

        Assert.True(File.Exists(nested));
        Assert.True(File.Exists(world.LibraryFilm));
        Assert.True(File.Exists(world.StrayFile));
        Assert.True(Directory.Exists(world.Output));
        Assert.True(Directory.Exists(world.Root));
    }

    [Fact]
    public async Task Clear_of_an_item_that_is_already_gone_or_for_a_library_that_is_not_there_is_404_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        File.Delete(world.StrayFile);

        using var gone = await ClearAsync(app, world.LibraryId, world.StrayFile);
        using var noLibrary = await ClearAsync(app, "no-such-library", world.Sibling);

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noLibrary.StatusCode);
        Assert.True(File.Exists(world.Sibling));
    }

    [Fact]
    public async Task Clear_needs_the_imports_scope()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var made = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Reader", scopes = "read" });
        var key = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var reader = app.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", key);

        using var response = await reader.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId = world.LibraryId, path = world.StrayFile });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(File.Exists(world.StrayFile));
    }

    /// <summary>
    /// Was a defect (#1318), now fixed: a library's processor output folder may be set to the folder that holds the
    /// library itself (nothing checks the two folders against each other when the library is saved), and Clear then accepts the
    /// library's own folder as "an item directly inside the output folder" and deletes it with everything in it, bypassing the
    /// recycle bin. Probed on this build: the answer was 200 "Deleted the folder movies ..." and the library folder, with its films,
    /// was gone from disk.
    /// </summary>
    [Fact]
    public async Task Clear_cannot_delete_a_library_root_even_when_the_output_folder_is_set_to_its_parent()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var media = Path.Combine(app.DataRoot, "media");
        var root = Path.Combine(media, "movies");
        var film = Path.Combine(root, "Film (2020)", "Film.2020.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(film)!);
        await File.WriteAllTextAsync(film, "the only copy of a film");
        var libraryId = await CreateLibraryAsync(app, root, output: media);

        using var response = await ClearAsync(app, libraryId, root);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(film), "a library's films never leave through Clear");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record World(string LibraryId, string Root, string Output, string StrayFile, string StrayFolder, string Sibling, string LibraryFilm);

    private static Task<HttpResponseMessage> ClearAsync(ApplicationTestHost app, string libraryId, string path)
        => app.Client.PostAsJsonAsync("/api/integrations/processors/output/delete", new { libraryId, path });

    private static async Task<World> WorldAsync(ApplicationTestHost app)
    {
        var root = Path.Combine(app.DataRoot, "movies");
        var output = Path.Combine(app.DataRoot, "refined");
        var film = Path.Combine(root, "Film (2020)", "Film.2020.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(film)!);
        Directory.CreateDirectory(output);
        var strayFile = Path.Combine(output, "stray.mkv");
        var strayFolder = Path.Combine(output, "Stray.Folder");
        var sibling = Path.Combine(output, "keep.mkv");
        Directory.CreateDirectory(strayFolder);
        foreach (var path in new[] { film, strayFile, sibling, Path.Combine(strayFolder, "inside.mkv") })
        {
            await File.WriteAllTextAsync(path, path);
        }

        var libraryId = await CreateLibraryAsync(app, root, output);
        return new World(libraryId, root, output, strayFile, strayFolder, sibling, film);
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string root, string output)
    {
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Films",
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            downloadsPath = (string?)null,
            qualityProfileId = (string?)null,
            importWorkflow = "refine-before-import",
            processorName = "Weir",
            processorOutputPath = output,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
