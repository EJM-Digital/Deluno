using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Download this title again", asked of Deluno by another tool through the external route: the library file goes to the recycle
/// bin on the real disk, the film is marked missing, one search is queued and the answer says so (202); a path Deluno does not
/// track is a 404; a tracked file that cannot be moved is a 409 and nothing is searched. Real host, real files, real databases.
/// </summary>
public sealed class ADownloadAgainAskedByAnotherToolTests
{
    [Fact]
    public async Task A_tracked_file_goes_to_the_bin_the_film_is_marked_missing_and_a_search_is_queued()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (movieId, libraryId, path) = await FilmWithAFileAsync(app);

        using var response = await app.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path, reason = "Keep Japanese audio.", tool = "Weir" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(movieId, body.GetProperty("titleId").GetString());
        Assert.Equal("movie", body.GetProperty("mediaKind").GetString());
        Assert.True(body.GetProperty("fileRemoved").GetBoolean());
        Assert.Contains("Weir asked Deluno to download Example Film again: Keep Japanese audio.", body.GetProperty("message").GetString());
        var jobId = body.GetProperty("searchJobId").GetString()!;
        var job = await app.Services.GetRequiredService<IJobQueueRepository>().GetAsync(jobId, CancellationToken.None);
        Assert.Equal("external-redownload", job!.Source);
        Assert.False(File.Exists(path));
        Assert.Single(await app.Services.GetRequiredService<Deluno.Platform.IRecycleBinService>().ListAsync(CancellationToken.None));
        var wanted = await app.Services.GetRequiredService<IMovieCatalogRepository>().GetMovieWantedStateAsync(movieId, libraryId, CancellationToken.None);
        Assert.False(wanted!.HasFile);
    }

    [Fact]
    public async Task A_path_Deluno_does_not_track_is_a_404_and_touches_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, _, path) = await FilmWithAFileAsync(app);
        var other = Path.Combine(Path.GetDirectoryName(path)!, "not-in-the-library.mkv");
        await File.WriteAllBytesAsync(other, new byte[16]);

        using var response = await app.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path = other });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("does not track a library file at", await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_tracked_file_that_cannot_be_moved_is_a_409_and_nothing_is_searched()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, _, path) = await FilmWithAFileAsync(app);
        File.Delete(path); // Deluno still tracks it, but there is nothing to move.

        using var response = await app.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("so nothing was searched", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None), job => job.Source == "external-redownload");
    }

    [Fact]
    public async Task No_path_is_a_400()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<(string MovieId, string LibraryId, string Path)> FilmWithAFileAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var folder = Directory.CreateDirectory(Path.Combine(root, "Example Film (2024)")).FullName;
        var path = Path.Combine(folder, "Example Film (2024) - 1080p.mkv");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId, "Example Film", 2024,
            wantedStatus: "covered", wantedReason: "Imported", currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: true, filePath: path, fileSizeBytes: 4096, cancellationToken: CancellationToken.None);
        var movieId = (await movies.ListAsync(CancellationToken.None)).First(item => item.Title == "Example Film").Id;
        return (movieId, libraryId, path);
    }
}
