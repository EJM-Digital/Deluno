using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A release a person sends by hand to a film: the download is recorded as started by "manual", keeps the client's own id for
/// it, is replacement-authorised only when the film already has a file (and forced only when the person forced it), takes the
/// film off the work list as downloading, and writes the search-history row that says whether it was forced. The client itself
/// is not under test here, so it answers "sent" and gives an id.
/// </summary>
public sealed class AHandSentGrabIsRecordedAsManualTests
{
    [Fact]
    public async Task A_sent_grab_for_a_film_without_a_file_is_recorded_as_manual_with_the_clients_id_and_no_replacement_authority()
    {
        await using var app = await StartAsync();
        var scene = await SceneAsync(app, filmHasAFile: false);

        using var response = await GrabAsync(app, scene, force: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dispatch = Assert.Single((await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .QueryDispatchesAsync(new DispatchQueryFilter { ClientId = scene.ClientId }, new DispatchPaginationOptions(), CancellationToken.None)).Items);
        Assert.Equal("manual", dispatch.StartedBy);
        Assert.Equal("You asked", dispatch.StartedByLabel);
        Assert.Equal("hash-from-the-client", dispatch.TorrentHashOrItemId);
        Assert.Equal((false, false), await AuthorityAsync(app, dispatch.Id));
        Assert.Equal("downloading", (await app.Services.GetRequiredService<IMovieCatalogRepository>().GetMovieWantedStateAsync(scene.MovieId, scene.LibraryId, CancellationToken.None))!.WantedStatus);
        var history = Assert.Single(await app.Services.GetRequiredService<IMovieCatalogRepository>().ListSearchHistoryAsync(CancellationToken.None), row => row.MovieId == scene.MovieId);
        Assert.Equal("manual-grab", history.TriggerKind);
    }

    [Fact]
    public async Task A_sent_grab_for_a_film_that_has_a_file_is_replacement_authorised_and_a_forced_one_is_marked_forced()
    {
        await using var app = await StartAsync();
        var scene = await SceneAsync(app, filmHasAFile: true);

        using var ordinary = await GrabAsync(app, scene, force: false);
        using var forced = await GrabAsync(app, scene, force: true);

        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        var dispatches = (await app.Services.GetRequiredService<IDownloadDispatchesRepository>()
            .QueryDispatchesAsync(new DispatchQueryFilter { ClientId = scene.ClientId }, new DispatchPaginationOptions(), CancellationToken.None)).Items
            .OrderBy(item => item.CreatedUtc).ToArray();
        Assert.Equal(2, dispatches.Length);
        Assert.Equal((true, false), await AuthorityAsync(app, dispatches[0].Id));
        Assert.Equal((true, true), await AuthorityAsync(app, dispatches[1].Id));
        Assert.All(dispatches, dispatch => Assert.Equal("manual", dispatch.StartedBy));
        var kinds = (await app.Services.GetRequiredService<IMovieCatalogRepository>().ListSearchHistoryAsync(CancellationToken.None))
            .Where(row => row.MovieId == scene.MovieId).Select(row => row.TriggerKind).ToArray();
        Assert.Contains("manual-grab", kinds);
        Assert.Contains("manual-force-grab", kinds);
    }

    private sealed record Scene(string MovieId, string LibraryId, string ClientId, string IndexerId);

    private static Task<ApplicationTestHost> StartAsync()
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(new SentGrabService());
        });

    private static Task<HttpResponseMessage> GrabAsync(ApplicationTestHost app, Scene scene, bool force)
        => app.Client.PostAsJsonAsync($"/api/movies/{scene.MovieId}/grab", new
        {
            releaseName = force ? "Example.Film.2024.1080p.WEB-DL-FORCED" : "Example.Film.2024.1080p.WEB-DL-GRP",
            indexerId = scene.IndexerId,
            indexerName = "Fixture torznab",
            downloadUrl = "https://indexer.invalid/download/1",
            candidateQuality = "WEB 1080p",
            force,
            duplicateAction = force ? "send-as-well" : null
        });

    private static async Task<(bool Replacement, bool Force)> AuthorityAsync(ApplicationTestHost app, string dispatchId)
    {
        await using var connection = await app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>()
            .OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT replacement_authorized, force_replacement_authorized FROM download_dispatches WHERE id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = dispatchId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (Convert.ToInt32(reader.GetValue(0)) == 1, Convert.ToInt32(reader.GetValue(1)) == 1);
    }

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, bool filmHasAFile)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Fixture torznab", "torznab", "public", "http://127.0.0.1:1/", "fixture-key", 10, "2000", "", "movies", true), CancellationToken.None);
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Connected.", null, 5, CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId = indexer.Id, priority = 1 } },
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        string movieId;
        if (filmHasAFile)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "Example Film (2024)")).FullName;
            var path = Path.Combine(folder, "Example Film (2024) - 720p.mkv");
            await File.WriteAllBytesAsync(path, new byte[4096]);
            await movies.ImportExistingAsync(
                libraryId, "Example Film", 2024,
                wantedStatus: "upgrade", wantedReason: "Below the cutoff", currentQuality: "WEB 720p", targetQuality: "WEB 1080p",
                qualityCutoffMet: false, filePath: path, fileSizeBytes: 4096, cancellationToken: CancellationToken.None);
            movieId = (await movies.ListAsync(CancellationToken.None)).First(item => item.Title == "Example Film").Id;
        }
        else
        {
            var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Example Film", releaseYear = 2024, monitored = true });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        return new Scene(movieId, libraryId, client.Id, indexer.Id);
    }

    private sealed class SentGrabService : IDownloadClientGrabService
    {
        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the client.") { ExternalId = "hash-from-the-client" });
    }
}
