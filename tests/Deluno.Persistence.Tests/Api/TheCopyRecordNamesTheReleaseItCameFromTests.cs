using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Coverage register, area movie-detail: the film page's Destination tab asks <c>GET /api/movies/{id}/copy</c> what the copy on disk
/// is. For a copy imported through a recorded grab it answers in the grab's own words: the release, the indexer and the download
/// client, and when it was grabbed. (The record kept with the file at import is proven in ImportPipelineServiceTests, which reads the
/// provenance event.) Real host, real SQLite files, a real file on disk.
/// </summary>
public sealed class TheCopyRecordNamesTheReleaseItCameFromTests
{
    private const string Release = "Dune.2021.1080p.BluRay.x264-GRP";

    [Fact]
    public async Task A_film_imported_from_a_grab_answers_with_the_release_the_indexer_and_the_client_it_came_from()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            film.LibraryId, "movies", "movie", film.MovieId, Release, "Fixture Indexer",
            "client-1", "qBittorrent", "sent", null, clientExternalId: "hash-dune", cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordImportOutcomeAsync(dispatchId, "imported", film.FilePath, null, null, CancellationToken.None);

        var copy = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/copy");

        Assert.Equal(film.MovieId, copy.GetProperty("movieId").GetString());
        Assert.Equal(film.FilePath, copy.GetProperty("filePath").GetString());
        Assert.Equal(Release, copy.GetProperty("releaseName").GetString());
        Assert.Equal("Fixture Indexer", copy.GetProperty("indexerName").GetString());
        Assert.Equal("qBittorrent", copy.GetProperty("downloadClientName").GetString());
        Assert.NotEqual(JsonValueKind.Null, copy.GetProperty("grabbedUtc").ValueKind);
        Assert.True(File.Exists(film.FilePath));
    }

    [Fact]
    public async Task A_film_that_came_from_no_grab_answers_with_no_release_rather_than_a_guess()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016);

        var copy = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/copy");

        Assert.Equal(film.FilePath, copy.GetProperty("filePath").GetString());
        Assert.Equal(JsonValueKind.Null, copy.GetProperty("releaseName").ValueKind);
        Assert.Equal(JsonValueKind.Null, copy.GetProperty("indexerName").ValueKind);
        Assert.Equal(JsonValueKind.Null, copy.GetProperty("downloadClientName").ValueKind);
    }

    [Fact]
    public async Task A_film_that_is_not_there_has_no_copy_record()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/movies/no-such-film/copy")).StatusCode);
    }
}
