using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1188: removing one file of a film or an episode (the film's Files tab, the series page) used to
/// delete it for good, or could not be done at all. A person's media only leaves through Deluno's
/// recycle bin, so it can be restored: the file goes to the bin, the title stays, and a restore puts
/// the file back and the title has its file again.
/// </summary>
public sealed class DeletingOneFileUsesTheRecycleBinTests
{
    [Fact]
    public async Task Deleting_a_films_file_moves_only_that_file_to_the_recycle_bin_and_a_restore_gives_it_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, "movies");
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "movies", "Nosferatu (1922)")).FullName;
        var file = Path.Combine(folder, "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv");
        var sidecar = Path.Combine(folder, "poster.jpg");
        await File.WriteAllTextAsync(file, "not really a film");
        await File.WriteAllTextAsync(sidecar, "poster");
        var movieId = await AddAsync(app, "movies", libraryId);
        Assert.True(await MovieHasFileAsync(app, movieId));

        var response = await DeleteAsync(app, $"/api/movies/{movieId}/files", new { path = file });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(sidecar), "only the named file leaves, not the folder around it");
        var item = Assert.Single(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
        Assert.False(item.IsDirectory);
        Assert.Equal(file, item.OriginalPath);
        Assert.True(File.Exists(item.RecyclePath), "the video is held in the recycle bin");
        Assert.False(await MovieHasFileAsync(app, movieId), "the title stays and shows no file");
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync($"/api/movies/{movieId}")).StatusCode);
        Assert.Contains("recycle bin", await app.Client.GetStringAsync("/api/activity?take=50"));

        var restore = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync());
        Assert.True(File.Exists(file), "the file is back where it was");
        Assert.True(await MovieHasFileAsync(app, movieId), "the title has its file again");
        Assert.True((await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(movieId, CancellationToken.None))!.Monitored,
            "a film that was watched before is watched again");
        Assert.Empty(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_an_episodes_file_moves_only_that_file_to_the_recycle_bin_and_a_restore_gives_it_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, "tv");
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "tv", "Nosferatu (1922)", "Season 01")).FullName;
        var first = Path.Combine(folder, "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv");
        var second = Path.Combine(folder, "Nosferatu.S01E02.1080p.WEB-DL.x264-HD4U.mkv");
        await File.WriteAllTextAsync(first, "episode one");
        await File.WriteAllTextAsync(second, "episode two");
        var seriesId = await AddAsync(app, "tv", libraryId);
        var episodes = await EpisodesAsync(app, seriesId);
        var one = Assert.Single(episodes, item => item.Number == 1);
        var two = Assert.Single(episodes, item => item.Number == 2);
        Assert.True(one.HasFile && two.HasFile, "both files were matched when the show was added");

        var response = await app.Client.DeleteAsync($"/api/series/episodes/{one.Id}/file");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second), "the other episode's file is untouched");
        var item = Assert.Single(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
        Assert.False(item.IsDirectory);
        Assert.Equal(first, item.OriginalPath);
        Assert.True(File.Exists(item.RecyclePath));
        var after = await EpisodesAsync(app, seriesId);
        Assert.False(Assert.Single(after, entry => entry.Number == 1).HasFile, "the episode shows no file");
        Assert.True(Assert.Single(after, entry => entry.Number == 2).HasFile);
        Assert.Contains("recycle bin", await app.Client.GetStringAsync("/api/activity?take=50"));

        var restore = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync());
        Assert.True(File.Exists(first), "the file is back where it was");
        var restored = Assert.Single(await EpisodesAsync(app, seriesId), entry => entry.Number == 1);
        Assert.True(restored.HasFile, "the episode has its file again");
        Assert.True(restored.Monitored, "an episode that was watched before is watched again");
        Assert.Empty(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_episode_with_no_file_is_not_found_and_nothing_is_moved()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, "tv");
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "tv", "Nosferatu (1922)", "Season 01")).FullName;
        var file = Path.Combine(folder, "Nosferatu.S01E01.1080p.WEB-DL.x264-HD4U.mkv");
        await File.WriteAllTextAsync(file, "episode one");
        var seriesId = await AddAsync(app, "tv", libraryId);
        var episode = Assert.Single(await EpisodesAsync(app, seriesId));

        Assert.Equal(HttpStatusCode.OK, (await app.Client.DeleteAsync($"/api/series/episodes/{episode.Id}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/series/episodes/{episode.Id}/file")).StatusCode);
        Assert.Single(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Episode(string Id, int Number, bool HasFile, bool Monitored);

    private static async Task<IReadOnlyList<Episode>> EpisodesAsync(ApplicationTestHost app, string seriesId)
    {
        var detail = await app.Services.GetRequiredService<ISeriesCatalogRepository>().GetInventoryDetailAsync(seriesId, CancellationToken.None);
        return detail!.Episodes.Select(item => new Episode(item.EpisodeId, item.EpisodeNumber, item.HasFile, item.Monitored)).ToArray();
    }

    private static async Task<bool> MovieHasFileAsync(ApplicationTestHost app, string id)
        => (await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(id, CancellationToken.None))?.HasFile ?? false;

    private static Task<HttpResponseMessage> DeleteAsync(ApplicationTestHost app, string url, object body)
        => app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url) { Content = JsonContent.Create(body) });

    private static async Task<string> AddAsync(ApplicationTestHost app, string mediaType, string libraryId)
    {
        var route = mediaType == "movies" ? "/api/movies/" : "/api/series/";
        object body = mediaType == "movies"
            ? new { title = "Nosferatu", releaseYear = 1922, monitored = true, libraryId, searchForMissingNow = false }
            : new { title = "Nosferatu", startYear = 1922, monitored = true, libraryId, searchForMissingNow = false };
        var response = await app.Client.PostAsJsonAsync(route, body);
        Assert.True(response.IsSuccessStatusCode, $"POST {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string mediaType)
    {
        var rootPath = Path.Combine(app.DataRoot, mediaType);
        Directory.CreateDirectory(rootPath);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = mediaType,
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
