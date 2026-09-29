using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1067: "Rename folder" on a title page (and the bulk rename tools) must only
/// ever rename the folder that is that one title's own. Before, a movie whose
/// file sat straight in the library folder renamed the whole library folder,
/// and a show with a single season on disk renamed "Season 01" to the show's
/// name. Driven through the real preview and apply routes, so the preview and
/// the apply are held to the same answer.
/// </summary>
public sealed class ARenameNeverTouchesAFolderThatIsNotTheTitlesOwnTests
{
    [Fact]
    public async Task A_movie_filed_straight_in_the_library_folder_has_no_folder_to_rename()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var libraryId = await CreateLibraryAsync(app, "movies", root);
        var dune = await AddMovieAsync(app, libraryId, "Dune", 2021, Path.Combine(root, "Dune.mkv"));
        var arrival = await AddMovieAsync(app, libraryId, "Arrival", 2016, Path.Combine(root, "Arrival.mkv"));

        var preview = await GetPreviewAsync(app, "movies", dune);
        Assert.Equal("flat-layout", preview.GetProperty("outcome").GetString());
        Assert.False(preview.GetProperty("willChange").GetBoolean());
        Assert.Contains("no folder to rename", preview.GetProperty("message").GetString(), StringComparison.Ordinal);

        var apply = await ApplyAsync(app, "movies", "movieIds", dune, arrival);
        Assert.Equal(0, apply.GetProperty("renamedCount").GetInt32());
        Assert.Equal(2, apply.GetProperty("failures").GetArrayLength());

        // The library folder and both files are exactly where they were.
        Assert.True(File.Exists(Path.Combine(root, "Dune.mkv")));
        Assert.True(File.Exists(Path.Combine(root, "Arrival.mkv")));
    }

    [Fact]
    public async Task A_movie_folder_that_holds_another_titles_file_is_left_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var libraryId = await CreateLibraryAsync(app, "movies", root);
        var dune = await AddMovieAsync(app, libraryId, "Dune", 2021, Path.Combine(root, "Mixed", "Dune.mkv"));
        await AddMovieAsync(app, libraryId, "Arrival", 2016, Path.Combine(root, "Mixed", "Arrival.mkv"));

        var preview = await GetPreviewAsync(app, "movies", dune);
        Assert.Equal("shared-folder", preview.GetProperty("outcome").GetString());
        Assert.False(preview.GetProperty("willChange").GetBoolean());

        var apply = await ApplyAsync(app, "movies", "movieIds", dune);
        Assert.Equal(0, apply.GetProperty("renamedCount").GetInt32());
        Assert.True(Directory.Exists(Path.Combine(root, "Mixed")));
    }

    [Fact]
    public async Task A_movie_in_its_own_folder_is_still_renamed_and_the_preview_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var libraryId = await CreateLibraryAsync(app, "movies", root);
        var dune = await AddMovieAsync(app, libraryId, "Dune", 2021, Path.Combine(root, "dune old", "Dune.mkv"));

        var preview = await GetPreviewAsync(app, "movies", dune);
        Assert.Equal("will-rename", preview.GetProperty("outcome").GetString());
        Assert.Equal("dune old", preview.GetProperty("currentFolder").GetString());

        var apply = await ApplyAsync(app, "movies", "movieIds", dune);
        Assert.Equal(1, apply.GetProperty("renamedCount").GetInt32());
        Assert.False(Directory.Exists(Path.Combine(root, "dune old")));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task A_show_with_one_season_on_disk_renames_the_show_folder_never_Season_01()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var libraryId = await CreateLibraryAsync(app, "tv", root);
        var episode = Path.Combine(root, "the wire old", "Season 01", "The Wire S01E01.mkv");
        var wire = await AddSeriesAsync(app, libraryId, "The Wire", 2002, episode);

        var preview = await GetPreviewAsync(app, "series", wire);
        Assert.Equal("will-rename", preview.GetProperty("outcome").GetString());
        Assert.Equal("the wire old", preview.GetProperty("currentFolder").GetString());

        var apply = await ApplyAsync(app, "series", "seriesIds", wire);
        Assert.Equal(1, apply.GetProperty("renamedCount").GetInt32());

        // "Season 01" keeps its name, and only the show folder changed.
        Assert.False(Directory.Exists(Path.Combine(root, "Season 01")));
        Assert.False(Directory.Exists(Path.Combine(root, "the wire old")));
        var renamed = Assert.Single(Directory.GetDirectories(root));
        Assert.True(File.Exists(Path.Combine(renamed, "Season 01", "The Wire S01E01.mkv")));
    }

    [Fact]
    public async Task A_show_whose_episodes_sit_straight_in_the_library_folder_has_no_folder_to_rename()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var libraryId = await CreateLibraryAsync(app, "tv", root);
        var wire = await AddSeriesAsync(app, libraryId, "The Wire", 2002, Path.Combine(root, "The Wire S01E01.mkv"));

        var preview = await GetPreviewAsync(app, "series", wire);
        Assert.Equal("flat-layout", preview.GetProperty("outcome").GetString());
        Assert.False(preview.GetProperty("willChange").GetBoolean());

        var apply = await ApplyAsync(app, "series", "seriesIds", wire);
        Assert.Equal(0, apply.GetProperty("renamedCount").GetInt32());
        Assert.Equal(1, apply.GetProperty("failures").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(root, "The Wire S01E01.mkv")));
    }

    private static async Task<JsonElement> GetPreviewAsync(ApplicationTestHost app, string engine, string id)
    {
        var response = await app.Client.GetAsync($"/api/{engine}/{id}/rename-preview");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> ApplyAsync(ApplicationTestHost app, string engine, string idsProperty, params string[] ids)
    {
        var response = await app.Client.PostAsJsonAsync(
            $"/api/{engine}/bulk/rename-apply",
            new Dictionary<string, object?> { [idsProperty] = ids });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string> AddMovieAsync(ApplicationTestHost app, string libraryId, string title, int year, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId, title, year,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: filePath,
            fileSizeBytes: 4,
            cancellationToken: CancellationToken.None);
        return (await movies.ListAsync(CancellationToken.None)).Single(movie => movie.Title == title).Id;
    }

    private static async Task<string> AddSeriesAsync(ApplicationTestHost app, string libraryId, string title, int year, string episodePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(episodePath)!);
        await File.WriteAllBytesAsync(episodePath, [1, 2, 3, 4]);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            libraryId, title, year,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: null,
            fileSizeBytes: null,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: episodePath, FileSizeBytes: 4)],
            cancellationToken: CancellationToken.None);
        return (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == title).Id;
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string mediaType, string rootPath)
    {
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = mediaType == "tv" ? "Shows" : "Films",
            mediaType,
            purpose = "collection",
            rootPath,
            downloadsPath = (string?)null,
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
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
