using Deluno.Companions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1135: an indexer or client is switched on in Library Routing however it is created (the
/// installer's connect step, the API, an import), by the server, on real repositories.
/// </summary>
public sealed class AutomaticRoutingTests
{
    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static async Task<(SqliteLibrariesRepository Libraries, SqliteConnectionsRepository Connections, LibraryItem Movies, LibraryItem Tv)> SetUpAsync(TestStorage storage)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, Clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var libraries = new SqliteLibrariesRepository(storage.Factory, Clock);
        var connections = new SqliteConnectionsRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));
        var movies = await CreateLibraryAsync(libraries, "Movies", "movies");
        var tv = await CreateLibraryAsync(libraries, "TV Shows", "tv");
        return (libraries, connections, movies, tv);
    }

    private static Task<LibraryItem> CreateLibraryAsync(SqliteLibrariesRepository libraries, string name, string mediaType)
        => libraries.CreateLibraryAsync(
            new CreateLibraryRequest(name, mediaType, "General", @"C:\Media\" + name, null, null, "auto", null, null, null, null, true, true, false, null, null, null),
            CancellationToken.None);

    private static CreateIndexerRequest Indexer(string scope, bool route = true)
        => new("Tracker " + scope, "torznab", "public", "https://" + scope + ".example.test", null, 1, "2000", "", scope, true, RouteToLibraries: route);

    private static CreateDownloadClientRequest Client(string? movies, string? tv, bool route = true)
        => new("Client", "qbittorrent", "localhost", 8080, null, null, null, movies, tv, null, 10, true, RouteToLibraries: route);

    [Fact]
    public async Task A_movies_indexer_is_switched_on_for_the_movie_library_only_and_a_both_indexer_for_every_library()
    {
        using var storage = TestStorage.Create();
        var (libraries, connections, movies, tv) = await SetUpAsync(storage);

        var moviesOnly = await connections.CreateIndexerAsync(Indexer("movies"), CancellationToken.None);
        var both = await connections.CreateIndexerAsync(Indexer("both"), CancellationToken.None);

        var movieRouting = await libraries.GetLibraryRoutingAsync(movies.Id, CancellationToken.None);
        var tvRouting = await libraries.GetLibraryRoutingAsync(tv.Id, CancellationToken.None);
        Assert.Equal([moviesOnly.Id, both.Id], movieRouting!.Sources.OrderBy(link => link.Priority).Select(link => link.IndexerId));
        Assert.Equal([both.Id], tvRouting!.Sources.Select(link => link.IndexerId));
    }

    [Fact]
    public async Task The_drawers_opt_out_leaves_a_new_indexer_and_client_unrouted()
    {
        using var storage = TestStorage.Create();
        var (libraries, connections, movies, tv) = await SetUpAsync(storage);

        await connections.CreateIndexerAsync(Indexer("both", route: false), CancellationToken.None);
        await connections.CreateDownloadClientAsync(Client("movies", "tv", route: false), CancellationToken.None);

        foreach (var library in new[] { movies, tv })
        {
            var routing = await libraries.GetLibraryRoutingAsync(library.Id, CancellationToken.None);
            Assert.Empty(routing!.Sources);
            Assert.Empty(routing.DownloadClients);
        }
    }

    [Fact]
    public async Task The_first_client_is_routed_to_both_libraries_and_a_second_client_leaves_them_alone()
    {
        using var storage = TestStorage.Create();
        var (libraries, connections, movies, tv) = await SetUpAsync(storage);

        var first = await connections.CreateDownloadClientAsync(Client("Movies", "TV"), CancellationToken.None);
        await connections.CreateDownloadClientAsync(Client("Movies", "TV"), CancellationToken.None);

        foreach (var library in new[] { movies, tv })
        {
            var routing = await libraries.GetLibraryRoutingAsync(library.Id, CancellationToken.None);
            Assert.Equal(first.Id, Assert.Single(routing!.DownloadClients).DownloadClientId);
        }
    }

    [Fact]
    public async Task A_client_with_only_a_tv_category_is_routed_to_the_tv_library_only()
    {
        using var storage = TestStorage.Create();
        var (libraries, connections, movies, tv) = await SetUpAsync(storage);

        var client = await connections.CreateDownloadClientAsync(Client(null, "TV"), CancellationToken.None);

        Assert.Empty((await libraries.GetLibraryRoutingAsync(movies.Id, CancellationToken.None))!.DownloadClients);
        Assert.Equal(client.Id, Assert.Single((await libraries.GetLibraryRoutingAsync(tv.Id, CancellationToken.None))!.DownloadClients).DownloadClientId);
    }

    [Fact]
    public async Task The_installers_connect_step_routes_the_clients_it_connects_to_both_libraries()
    {
        using var storage = TestStorage.Create();
        var (libraries, connections, movies, tv) = await SetUpAsync(storage);
        var connector = new DownloadClientConnector(connections, new HealthyTester());

        // The categories are the ones the installer creates (InstalledClientCategories), as the wirer passes them.
        var first = await connector.ConnectAsync(new DownloadClientWiring("qBittorrent", "qbittorrent", "localhost", 8081, null, null, "Movies", "TV"), 1, TimeSpan.Zero, CancellationToken.None);
        var second = await connector.ConnectAsync(new DownloadClientWiring("Deluge", "deluge", "localhost", 8112, null, "deluge", "Movies", "TV"), 1, TimeSpan.Zero, CancellationToken.None);

        Assert.True(first.Healthy && second.Healthy);
        foreach (var library in new[] { movies, tv })
        {
            var routing = await libraries.GetLibraryRoutingAsync(library.Id, CancellationToken.None);
            Assert.Equal(first.Client!.Id, Assert.Single(routing!.DownloadClients).DownloadClientId);
        }
    }

    private sealed class HealthyTester : IDownloadClientTester
    {
        public Task<(string HealthStatus, string Message, string? FailureCategory)> TestAsync(DownloadClientItem client, CancellationToken cancellationToken)
            => Task.FromResult<(string, string, string?)>(("healthy", "Connected.", null));
    }
}
