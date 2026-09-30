using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1065: a film's Files tab lists and deletes only the film's own files wherever its folder is
/// shared with another title, and reads a subtitle's name the same way the subtitle scan does.
/// </summary>
public sealed class MovieFilesTabSharedFolderTests
{
    [Fact]
    public async Task A_flat_film_owns_every_subtitle_name_the_subtitle_scan_reads_and_no_look_alike()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Flat")).FullName;
        var library = await CreateLibraryAsync(app, "Flat", root);
        var alien = Path.Combine(root, "Alien.mkv");
        var own = new[]
        {
            Path.Combine(root, "Alien.en.hearing_impaired.srt"),
            Path.Combine(root, "Alien.foreign.srt"),
            Path.Combine(root, "Alien.English.srt")
        };
        var notOwn = Path.Combine(root, "Alien.Man.srt");
        foreach (var path in own.Append(alien).Append(notOwn))
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }

        var movieId = await ImportAsync(app, library, "Alien", 1979, alien);

        var listed = await ListAsync(app, movieId);
        foreach (var path in own)
        {
            Assert.Contains(listed, item => Same(item, path));
        }

        Assert.DoesNotContain(listed, item => Same(item, notOwn));
        Assert.False((await DeleteAsync(app, movieId, notOwn, library)).IsSuccessStatusCode);
        Assert.True(File.Exists(notOwn));
        Assert.True((await DeleteAsync(app, movieId, own[0], library)).IsSuccessStatusCode);
        Assert.False(File.Exists(own[0]));
    }

    [Fact]
    public async Task A_flat_film_owns_its_companion_written_without_the_quality_tag()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Flat")).FullName;
        var library = await CreateLibraryAsync(app, "Flat", root);
        var film = Path.Combine(root, "Movie (2020) - 1080p.mkv");
        var nfo = Path.Combine(root, "Movie (2020).nfo");
        var other = Path.Combine(root, "Other (2021).nfo");
        foreach (var path in new[] { film, nfo, other })
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }

        var movieId = await ImportAsync(app, library, "Movie", 2020, film);

        var listed = await ListAsync(app, movieId);
        Assert.Contains(listed, item => Same(item, nfo));
        Assert.DoesNotContain(listed, item => Same(item, other));
        Assert.True((await DeleteAsync(app, movieId, nfo, library)).IsSuccessStatusCode);
        Assert.False((await DeleteAsync(app, movieId, other, library)).IsSuccessStatusCode);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task A_film_in_a_subfolder_shared_with_another_film_lists_and_deletes_only_its_own_files()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var shared = Directory.CreateDirectory(Path.Combine(root, "Action")).FullName;
        var library = await CreateLibraryAsync(app, "Films", root);
        var heat = Path.Combine(shared, "Heat.mkv");
        var heatSubtitle = Path.Combine(shared, "Heat.en.srt");
        var ronin = Path.Combine(shared, "Ronin.mkv");
        var roninSubtitle = Path.Combine(shared, "Ronin.en.srt");
        var deep = Directory.CreateDirectory(Path.Combine(shared, "Extras")).FullName;
        var extra = Path.Combine(deep, "Ronin-behind.mkv");
        foreach (var path in new[] { heat, heatSubtitle, ronin, roninSubtitle, extra })
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }

        var heatId = await ImportAsync(app, library, "Heat", 1995, heat);
        await ImportAsync(app, library, "Ronin", 1998, ronin);

        var listed = await ListAsync(app, heatId);
        Assert.Contains(listed, item => Same(item, heatSubtitle));
        Assert.DoesNotContain(listed, item => Same(item, ronin) || Same(item, roninSubtitle) || Same(item, extra));

        foreach (var path in new[] { ronin, roninSubtitle, extra })
        {
            Assert.False((await DeleteAsync(app, heatId, path, library)).IsSuccessStatusCode);
            Assert.True(File.Exists(path));
        }

        Assert.True((await DeleteAsync(app, heatId, heatSubtitle, library)).IsSuccessStatusCode);
        Assert.False(File.Exists(heatSubtitle));
    }

    [Fact]
    public async Task A_film_whose_library_was_removed_does_not_list_the_rest_of_its_folder()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Gone")).FullName;
        var library = await CreateLibraryAsync(app, "Gone", root);
        var film = Path.Combine(root, "Heat (1995)", "Heat (1995).mkv");
        var stranger = Path.Combine(root, "Heat (1995)", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(film)!);
        foreach (var path in new[] { film, stranger })
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }

        var movieId = await ImportAsync(app, library, "Heat", 1995, film);
        var removed = await app.Client.DeleteAsync($"/api/libraries/{library}");
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());

        var listed = await ListAsync(app, movieId);
        Assert.DoesNotContain(listed, item => Same(item, stranger));
        Assert.False((await DeleteAsync(app, movieId, stranger, library)).IsSuccessStatusCode);
        Assert.True(File.Exists(stranger));
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static async Task<string[]> ListAsync(ApplicationTestHost app, string movieId)
    {
        var response = await app.Client.GetAsync($"/api/movies/{movieId}/files");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(item => item.GetProperty("path").GetString()!).ToArray();
    }

    private static Task<HttpResponseMessage> DeleteAsync(ApplicationTestHost app, string movieId, string path, string? libraryId)
        => app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{movieId}/files")
        {
            Content = JsonContent.Create(new { path, libraryId })
        });

    private static async Task<string> ImportAsync(ApplicationTestHost app, string library, string title, int year, string path)
    {
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(library, title, year, "covered", "x", "Bluray-1080p", "Bluray-1080p", true, path, 3, CancellationToken.None);
        return (await movies.ListAsync(CancellationToken.None)).First(item => item.Title == title).Id;
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
