using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Delete leftover files" on an imported season pack: the files that matched no episode are deleted once the person confirms,
/// exactly those and only under a configured download folder. A library file, a path that climbs out of the download folder and
/// a file that is already gone are never deleted or counted.
/// </summary>
public sealed class SeasonPackLeftoversOnRealFilesTests
{
    [Fact]
    public async Task Delete_removes_exactly_the_leftover_files_named_and_writes_one_activity_line_listing_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);

        using var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/season-pack/leftovers/delete", new { paths = new[] { world.Sample, world.Extra } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("deleted").GetInt32());
        Assert.Empty(body.GetProperty("refused").EnumerateArray());
        Assert.False(File.Exists(world.Sample));
        Assert.False(File.Exists(world.Extra));
        Assert.True(File.Exists(world.Episode), "the episode that did match is not a leftover");
        Assert.True(File.Exists(world.OtherPack), "another download's file is not touched");
        var activity = await app.Client.GetStringAsync("/api/activity?take=100");
        Assert.Contains("Moved 2 leftover file(s) to the recycle bin from a season pack", activity);
        Assert.Contains(Path.GetFileName(world.Sample), activity);
        Assert.Contains(Path.GetFileName(world.Extra), activity);
    }

    [Fact]
    public async Task A_file_outside_every_download_folder_is_refused_and_stays()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var climbing = Path.Combine(world.Downloads, "Show.S01", "..", "..", "movies", "Film (2020)", "Film.2020.mkv");

        using var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/season-pack/leftovers/delete", new { paths = new[] { world.LibraryFilm, climbing, world.DataFile } });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("deleted").GetInt32());
        Assert.Equal(3, body.GetProperty("refused").GetArrayLength());
        Assert.True(File.Exists(world.LibraryFilm), "library media is never a leftover");
        Assert.True(File.Exists(world.DataFile));
        Assert.DoesNotContain("leftover file(s)", await app.Client.GetStringAsync("/api/activity?take=100"));
    }

    [Fact]
    public async Task A_file_that_is_already_gone_is_not_counted_or_refused_and_writes_no_activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        File.Delete(world.Sample);

        using var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/season-pack/leftovers/delete", new { paths = new[] { world.Sample } });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("deleted").GetInt32());
        Assert.Empty(body.GetProperty("refused").EnumerateArray());
        Assert.DoesNotContain("leftover file(s)", await app.Client.GetStringAsync("/api/activity?take=100"));
    }

    [Fact]
    public async Task Choosing_no_file_is_a_plain_400_and_deletes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);

        using var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/season-pack/leftovers/delete", new { paths = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Choose at least one leftover file to delete.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("paths")[0].GetString());
        Assert.True(File.Exists(world.Sample));
    }

    /// <summary>
    /// Was a defect (#1318), now fixed: <c>/api/filesystem/import/season-pack/leftovers/delete</c> trusts every library's
    /// download folder as safe to delete from, and nothing stops a download folder from holding another library's root. Library
    /// creation accepts it (200), and the leftovers route then deletes a film straight out of the other library: the answer was
    /// <c>{"deleted":1,"refused":[]}</c> and the file was gone from disk, not in the recycle bin. The orphan-folder remover refuses
    /// anything inside a library root; this route does not.
    /// </summary>
    [Fact]
    public async Task A_download_folder_that_holds_a_library_cannot_be_used_to_delete_that_librarys_media()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = Path.Combine(app.DataRoot, "media", "movies");
        var tv = Path.Combine(app.DataRoot, "media", "tv");
        var film = Path.Combine(movies, "Film (2020)", "Film.2020.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(film)!);
        Directory.CreateDirectory(tv);
        await File.WriteAllTextAsync(film, "the only copy of a film");
        await CreateLibraryAsync(app, "Films", "movies", movies, downloadsPath: null);
        await CreateLibraryAsync(app, "Shows", "tv", tv, downloadsPath: Path.Combine(app.DataRoot, "media"));

        using var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/season-pack/leftovers/delete", new { paths = new[] { film } });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("deleted").GetInt32());
        Assert.True(File.Exists(film), "library media never leaves through a leftovers delete");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record World(string Downloads, string Episode, string Sample, string Extra, string OtherPack, string LibraryFilm, string DataFile);

    private static async Task CreateLibraryAsync(ApplicationTestHost app, string name, string mediaType, string rootPath, string? downloadsPath)
    {
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType,
            purpose = "collection",
            rootPath,
            downloadsPath,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        created.EnsureSuccessStatusCode();
    }

    private static async Task<World> WorldAsync(ApplicationTestHost app)
    {
        var movies = Path.Combine(app.DataRoot, "movies");
        var tv = Path.Combine(app.DataRoot, "tv");
        var downloads = Path.Combine(app.DataRoot, "downloads");
        var film = Path.Combine(movies, "Film (2020)", "Film.2020.mkv");
        var pack = Path.Combine(downloads, "Show.S01");
        var other = Path.Combine(downloads, "Other.Pack.S02");
        Directory.CreateDirectory(Path.GetDirectoryName(film)!);
        Directory.CreateDirectory(tv);
        Directory.CreateDirectory(pack);
        Directory.CreateDirectory(other);
        var episode = Path.Combine(pack, "Show.S01E01.mkv");
        var sample = Path.Combine(pack, "sample.mkv");
        var extra = Path.Combine(pack, "Behind.The.Scenes.mkv");
        var otherPack = Path.Combine(other, "Other.S02E01.mkv");
        var dataFile = Path.Combine(app.DataRoot, "precious.txt");
        foreach (var file in new[] { film, episode, sample, extra, otherPack, dataFile })
        {
            await File.WriteAllTextAsync(file, file);
        }

        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Shows",
            mediaType = "tv",
            purpose = "collection",
            rootPath = tv,
            downloadsPath = downloads,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        created.EnsureSuccessStatusCode();
        return new World(downloads, episode, sample, extra, otherPack, film, dataFile);
    }
}
