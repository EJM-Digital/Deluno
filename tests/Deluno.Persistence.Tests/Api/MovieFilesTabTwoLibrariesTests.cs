using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1065: the Files tab of a film kept in two libraries shows each library's file, and deleting one
/// goes through the per-library rule and never reaches outside that library's own title folder.
/// </summary>
public sealed class MovieFilesTabTwoLibrariesTests
{
    [Fact]
    public async Task The_Files_tab_lists_every_librarys_file_labelled_by_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await SeedAsync(app);

        var response = await app.Client.GetAsync($"/api/movies/{film.MovieId}/files");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var videos = document.RootElement.EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "video")
            .ToDictionary(item => item.GetProperty("libraryId").GetString()!, item => item.GetProperty("libraryName").GetString());

        Assert.Equal(2, videos.Count);
        Assert.Equal("Films HD", videos[film.HdLibraryId]);
        Assert.Equal("Films 4K", videos[film.FourKLibraryId]);
    }

    [Fact]
    public async Task Deleting_the_other_librarys_file_switches_off_only_that_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await SeedAsync(app);

        var response = await DeleteAsync(app, film.MovieId, film.FourKPath, film.FourKLibraryId);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(film.FourKPath));
        Assert.True(File.Exists(film.HdPath), "the other library's file is untouched");
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        Assert.Equal([film.FourKLibraryId], (await movies.ListNotWantedLibraryIdsAsync(film.MovieId, CancellationToken.None)).ToArray());
        Assert.True((await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!.Monitored);
        var remaining = await movies.ListLibraryFilesAsync(film.MovieId, CancellationToken.None);
        Assert.Equal(film.HdPath, Assert.Single(remaining).FilePath);
    }

    [Fact]
    public async Task Without_a_library_the_path_finds_its_own_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await SeedAsync(app);

        var response = await DeleteAsync(app, film.MovieId, film.HdPath, libraryId: null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        Assert.Equal([film.HdLibraryId], (await movies.ListNotWantedLibraryIdsAsync(film.MovieId, CancellationToken.None)).ToArray());
        Assert.True(File.Exists(film.FourKPath));
    }

    [Fact]
    public async Task A_path_outside_the_named_librarys_folder_is_refused_and_nothing_is_deleted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await SeedAsync(app);
        var outside = Path.Combine(app.DataRoot, "outside.txt");
        await File.WriteAllTextAsync(outside, "keep me");

        // Outside every library, and the other library's file named under this library's id.
        foreach (var (path, libraryId) in new (string, string?)[]
        {
            (outside, film.HdLibraryId),
            (outside, null),
            (film.FourKPath, film.HdLibraryId)
        })
        {
            var response = await DeleteAsync(app, film.MovieId, path, libraryId);
            Assert.False(response.IsSuccessStatusCode);
        }

        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(film.HdPath));
        Assert.True(File.Exists(film.FourKPath));
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        Assert.Empty(await movies.ListNotWantedLibraryIdsAsync(film.MovieId, CancellationToken.None));
    }

    [Fact]
    public async Task A_film_filed_flat_in_the_library_root_lists_and_deletes_only_its_own_files()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "FlatFilms")).FullName;
        var library = await CreateLibraryAsync(app, "Flat films", root);
        var heat = Path.Combine(root, "Heat (1995).mkv");
        var heatSubtitle = Path.Combine(root, "Heat (1995).en.srt");
        var ronin = Path.Combine(root, "Ronin (1998).mkv");
        var roninSubtitle = Path.Combine(root, "Ronin (1998).en.srt");
        foreach (var path in new[] { heat, heatSubtitle, ronin, roninSubtitle })
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        }

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(library, "Heat", 1995, "covered", "x", "Bluray-1080p", "Bluray-1080p", true, heat, 4, CancellationToken.None);
        var movieId = Assert.Single(await movies.ListAsync(CancellationToken.None)).Id;

        var response = await app.Client.GetAsync($"/api/movies/{movieId}/files");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var listed = document.RootElement.EnumerateArray().Select(item => item.GetProperty("path").GetString()!).ToArray();
        Assert.DoesNotContain(listed, path => path.Contains("Ronin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(listed, path => string.Equals(path, heat, StringComparison.OrdinalIgnoreCase));

        Assert.False((await DeleteAsync(app, movieId, ronin, library)).IsSuccessStatusCode);
        Assert.False((await DeleteAsync(app, movieId, roninSubtitle, library)).IsSuccessStatusCode);
        Assert.True(File.Exists(ronin));
        Assert.True(File.Exists(roninSubtitle));

        Assert.True((await DeleteAsync(app, movieId, heatSubtitle, library)).IsSuccessStatusCode);
        Assert.False(File.Exists(heatSubtitle));
    }

    private static Task<HttpResponseMessage> DeleteAsync(ApplicationTestHost app, string movieId, string path, string? libraryId)
        => app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{movieId}/files")
        {
            Content = JsonContent.Create(new { path, libraryId })
        });

    private sealed record Film(string MovieId, string HdLibraryId, string FourKLibraryId, string HdPath, string FourKPath);

    private static async Task<Film> SeedAsync(ApplicationTestHost app)
    {
        var hdRoot = Directory.CreateDirectory(Path.Combine(app.DataRoot, "FilmsHd")).FullName;
        var fourKRoot = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films4k")).FullName;
        var hdLibrary = await CreateLibraryAsync(app, "Films HD", hdRoot);
        var fourKLibrary = await CreateLibraryAsync(app, "Films 4K", fourKRoot);
        var hdPath = Path.Combine(hdRoot, "Heat (1995)", "Heat (1995) [Bluray-1080p].mkv");
        var fourKPath = Path.Combine(fourKRoot, "Heat (1995)", "Heat (1995) [Bluray-2160p].mkv");
        foreach (var path in new[] { hdPath, fourKPath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        }

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(hdLibrary, "Heat", 1995, "covered", "x", "Bluray-1080p", "Bluray-1080p", true, hdPath, 4, CancellationToken.None);
        var movieId = Assert.Single(await movies.ListAsync(CancellationToken.None)).Id;
        await movies.ImportExistingAsync(fourKLibrary, "Heat", 1995, "covered", "x", "Bluray-2160p", "Bluray-2160p", true, fourKPath, 4, CancellationToken.None, movieId: movieId);
        return new Film(movieId, hdLibrary, fourKLibrary, hdPath, fourKPath);
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string name, string rootPath)
    {
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType = "movies",
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
