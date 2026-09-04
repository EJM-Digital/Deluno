using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Removing one film, and one show.
///
/// <para><b>Why this file exists.</b> #421. Movies and shows were the only two
/// entities in Deluno with no single-item DELETE — twenty-eight others have one,
/// including a movie's own import-recovery case. Removing one film went through
/// <c>POST /api/movies/bulk</c> with an array of one.</para>
///
/// <para>The cost was the status code rather than the awkwardness: a removal
/// that failed entirely returned <c>200</c> with <c>successCount: 0</c>, so a
/// caller checking the response status was told it had worked. Every other
/// entity answers <c>404</c>.</para>
///
/// <para>Both media kinds are held to the same contract here, deliberately. A
/// media manager should not answer the same question two different ways
/// depending on whether the thing has episodes in it.</para>
/// </summary>
public sealed class SingleTitleRemovalTests
{
    [Fact]
    public async Task Removing_a_film_that_is_not_there_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.DeleteAsync("/api/movies/00000000000000000000000000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Removing_a_show_that_is_not_there_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.DeleteAsync("/api/series/00000000000000000000000000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Removing_a_film_takes_it_out_of_the_catalogue_and_saying_it_twice_does_not_work()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, "Films", "movies");

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId,
            "Arrival",
            2016,
            "covered",
            "It is on disk.",
            "WEB 1080p",
            "WEB 1080p",
            true,
            false,
            Path.Combine(app.DataRoot, "films", "Arrival (2016)", "Arrival (2016).mkv"),
            1024,
            CancellationToken.None);

        var created = Assert.Single(await movies.ListAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/movies/{created.Id}")).StatusCode);
        Assert.Empty(await movies.ListAsync(CancellationToken.None));

        // The second attempt has nothing left to remove, and says so rather than
        // reporting a success that did not happen.
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/movies/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Removing_a_show_takes_it_out_of_the_catalogue_and_saying_it_twice_does_not_work()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "Shows", "tv");

        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var created = await series.AddAsync(
            new CreateSeriesRequest(Title: "Severance", StartYear: 2022, ImdbId: "tt11280740"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/series/{created.Id}")).StatusCode);
        Assert.Empty(await series.ListAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/series/{created.Id}")).StatusCode);
    }

    /// <summary>
    /// The removal dialog's two questions reach the route as query parameters,
    /// because a DELETE body is poorly supported by clients. Leaving the file on
    /// disk is the default, so an accidental removal costs a re-add rather than
    /// the media.
    /// </summary>
    [Fact]
    public async Task Removing_a_film_leaves_the_file_alone_unless_asked()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, "Films", "movies");

        var titleFolder = Path.Combine(app.DataRoot, "films", "Arrival (2016)");
        Directory.CreateDirectory(titleFolder);
        var file = Path.Combine(titleFolder, "Arrival (2016).mkv");
        await File.WriteAllTextAsync(file, "not really a film", CancellationToken.None);

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId, "Arrival", 2016, "covered", "It is on disk.",
            "WEB 1080p", "WEB 1080p", true, false, file, 1024, CancellationToken.None);
        var created = Assert.Single(await movies.ListAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/movies/{created.Id}")).StatusCode);

        Assert.Empty(await movies.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(file), "the catalogue record goes; the file stays unless deleteFiles was asked for");
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string name, string mediaType)
    {
        var root = Path.Combine(app.DataRoot, mediaType == "movies" ? "films" : "shows");
        Directory.CreateDirectory(root);

        var libraries = app.Services.GetRequiredService<ILibrariesRepository>();
        var library = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: name,
                MediaType: mediaType,
                Purpose: "collection",
                RootPath: root,
                DownloadsPath: null,
                QualityProfileId: null,
                ImportWorkflow: "copy",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null,
                AutoSearchEnabled: false,
                MissingSearchEnabled: false,
                UpgradeSearchEnabled: false,
                SearchIntervalHours: null,
                RetryDelayHours: null,
                MaxItemsPerRun: null),
            CancellationToken.None);
        return library.Id;
    }
}
