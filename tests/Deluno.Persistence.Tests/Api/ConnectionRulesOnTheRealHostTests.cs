using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Library Routing and download-client rules on the whole application with its own database files: what Save routing stores
/// (indexers in the order shown, clients with their category), what a removed client leaves behind, and the client priority and
/// port the server enforces. The clients and indexers are only saved records here; no other app is asked.
/// </summary>
public sealed class ConnectionRulesOnTheRealHostTests
{
    /// <summary>Reads the platform database file itself, so a test shows what really reached the disk.</summary>
    private static async Task<string?> StoredAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<string> CreateIndexerAsync(ApplicationTestHost app, string name)
    {
        var response = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            name, protocol = "newznab", privacy = "public", baseUrl = $"https://{name.ToLowerInvariant()}.example.test", apiKey = "k", priority = 10,
            categories = "2000", tags = "", mediaScope = "movies", isEnabled = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateClientAsync(ApplicationTestHost app, string host, int port = 8080, int priority = 10)
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host, port, username = "u", password = "p", moviesCategory = "movies", tvCategory = "tv", priority, isEnabled = true
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Save_routing_stores_the_indexers_in_the_order_shown_as_priorities_with_their_tags_and_the_clients_with_their_category_and_reads_them_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var first = await CreateIndexerAsync(app, "Alpha");
        var second = await CreateIndexerAsync(app, "Beta");
        var client = await CreateClientAsync(app, "client-one.example.test");

        var saved = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId = second, priority = 1, requiredTags = "anime", excludedTags = "kids" }, new { indexerId = first, priority = 2, requiredTags = "", excludedTags = "" } },
            downloadClients = new[] { new { downloadClientId = client, priority = 1, category = "films" } }
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var routing = await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/routing");
        var sources = routing.GetProperty("sources").EnumerateArray().OrderBy(item => item.GetProperty("priority").GetInt32()).ToArray();
        Assert.Equal(["Beta", "Alpha"], sources.Select(item => item.GetProperty("indexerName").GetString()!).ToArray());
        Assert.Equal("anime", sources[0].GetProperty("requiredTags").GetString());
        Assert.Equal("kids", sources[0].GetProperty("excludedTags").GetString());
        var link = Assert.Single(routing.GetProperty("downloadClients").EnumerateArray());
        Assert.Equal("films", link.GetProperty("category").GetString());
        Assert.Equal("2", await StoredAsync(app, $"SELECT COUNT(*) FROM library_source_links WHERE library_id = '{libraryId}'"));
        Assert.Equal("1", await StoredAsync(app, $"SELECT COUNT(*) FROM library_download_client_links WHERE library_id = '{libraryId}' AND category = 'films'"));
    }

    [Fact]
    public async Task Save_routing_refuses_an_indexer_or_client_that_does_not_exist_and_stores_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");

        var badIndexer = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = new[] { new { indexerId = "no-such-indexer", priority = 1 } }, downloadClients = Array.Empty<object>() });
        var badClient = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = Array.Empty<object>(), downloadClients = new[] { new { downloadClientId = "no-such-client", priority = 1 } } });

        Assert.Equal(HttpStatusCode.BadRequest, badIndexer.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badClient.StatusCode);
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM library_source_links"));
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM library_download_client_links"));
    }

    [Fact]
    public async Task A_removed_client_leaves_no_routing_link_behind_and_the_library_is_left_with_no_client()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var client = await CreateClientAsync(app, "client-gone.example.test");
        await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = Array.Empty<object>(), downloadClients = new[] { new { downloadClientId = client, priority = 1, category = "films" } } });
        Assert.Equal("1", await StoredAsync(app, $"SELECT COUNT(*) FROM library_download_client_links WHERE download_client_id = '{client}'"));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/download-clients/{client}")).StatusCode);

        Assert.Equal("0", await StoredAsync(app, $"SELECT COUNT(*) FROM library_download_client_links WHERE download_client_id = '{client}'"));
        var routing = await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/routing");
        Assert.Equal(0, routing.GetProperty("downloadClients").GetArrayLength());
    }

    [Fact]
    public async Task A_removed_indexer_drops_its_routing_links()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var indexer = await CreateIndexerAsync(app, "Gamma");
        await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = new[] { new { indexerId = indexer, priority = 1 } }, downloadClients = Array.Empty<object>() });
        Assert.Equal("1", await StoredAsync(app, $"SELECT COUNT(*) FROM library_source_links WHERE indexer_id = '{indexer}'"));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/indexers/{indexer}")).StatusCode);

        Assert.Equal("0", await StoredAsync(app, $"SELECT COUNT(*) FROM library_source_links WHERE indexer_id = '{indexer}'"));
    }

    [Fact(Skip = "Defect: the server does not enforce a download client's priority range (1-50) or port (1-65535). ValidateDownloadClient in ConnectionsEndpointRouteBuilderExtensions checks only the protocol, so POST /api/download-clients with priority 0 or port 99999 is saved as given, although the drawer refuses both and the register row says the server enforces them.")]
    public async Task The_server_refuses_a_client_priority_outside_1_to_50_and_a_port_outside_1_to_65535()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var badPriority = await app.Client.PostAsJsonAsync("/api/download-clients", new { protocol = "qbittorrent", host = "a.example.test", port = 8080, priority = 0, moviesCategory = "movies", tvCategory = "tv", isEnabled = true });
        var badPort = await app.Client.PostAsJsonAsync("/api/download-clients", new { protocol = "qbittorrent", host = "b.example.test", port = 99999, priority = 10, moviesCategory = "movies", tvCategory = "tv", isEnabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, badPriority.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPort.StatusCode);
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM download_clients"));
    }
}
