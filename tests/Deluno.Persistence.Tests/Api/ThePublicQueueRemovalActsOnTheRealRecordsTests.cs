using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The public queue removal (API parity with Radarr and Sonarr, #1022): <c>DELETE /api/queue/{client}/{item}</c> and the bulk call,
/// with <c>removeFromClient</c>, <c>blocklist</c> and <c>skipRedownload</c>. Run over the whole application on real database
/// files, with a download Deluno really recorded sending, so what the flags do is read from the files and not from a stand-in
/// service. Only the download client itself is a stand-in: it writes down what it was asked.
/// </summary>
public sealed class ThePublicQueueRemovalActsOnTheRealRecordsTests
{
    private const string Release = "Some.Film.2024.1080p.WEB-DL-GROUP";

    private static string OnDisk(ApplicationTestHost app, string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, database)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private sealed class Scene
    {
        public required string ClientId { get; init; }
        public required string LibraryId { get; init; }
    }

    private static async Task<Scene> ClientAndLibraryAsync(ApplicationTestHost app)
    {
        var library = await ApiPayload.CreateAsync(app.Client, "/api/libraries", new
        {
            name = "Films", mediaType = "movies", purpose = "collection", rootPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName
        });
        var response = await app.Client.PostAsJsonAsync("/api/download-clients/", new
        {
            name = "Fake", protocol = "qbittorrent", host = "127.0.0.1", port = 8080, username = (string?)null, password = "secret",
            endpointUrl = (string?)null, moviesCategory = "movies", tvCategory = "tv", categoryTemplate = (string?)null, priority = 1, isEnabled = true
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new Scene { ClientId = document.RootElement.GetProperty("id").GetString()!, LibraryId = library };
    }

    /// <summary>Deluno's own record of having sent a release to the client, which the client now holds under <paramref name="itemId"/>.</summary>
    private static async Task SentAsync(ApplicationTestHost app, Scene scene, string itemId, string release, string? movieId = null)
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            scene.LibraryId, "movies", "movie", movieId ?? $"movie-{itemId}", release, "Indexer", scene.ClientId, "Fake", "sent", null);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordDetectionAsync(dispatchId, itemId, null, completed: false, CancellationToken.None);
    }

    private static void UseFake(IServiceCollection services, FakeClient client)
    {
        services.RemoveAll<IDownloadClientRegistry>();
        services.AddSingleton<IDownloadClientRegistry>(new FakeRegistry(client));
    }

    [Fact]
    public async Task By_default_the_download_and_its_files_are_removed_from_the_client_and_nothing_is_blocklisted()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => UseFake(services, client));
        var scene = await ClientAndLibraryAsync(app);
        await SentAsync(app, scene, "item-1", Release);

        var response = await app.Client.DeleteAsync($"/api/queue/{scene.ClientId}/item-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([("item-1", DownloadClientActions.DeleteWithData)], client.Calls);
        Assert.Equal("0", OnDisk(app, "jobs.db", "SELECT COUNT(*) FROM blocked_releases"));
        Assert.Equal("0", OnDisk(app, "jobs.db", "SELECT COUNT(*) FROM job_queue"));
    }

    [Fact]
    public async Task With_removeFromClient_false_the_client_is_sent_nothing_and_Deluno_only_stops_tracking_it()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => UseFake(services, client));
        var scene = await ClientAndLibraryAsync(app);
        await SentAsync(app, scene, "item-1", Release);

        var response = await app.Client.DeleteAsync($"/api/queue/{scene.ClientId}/item-1?removeFromClient=false");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(client.Calls);
        // Deluno says so in Activity, in words that promise the client was left alone.
        Assert.Equal("1", OnDisk(app, "jobs.db", "SELECT COUNT(*) FROM activity_events WHERE category = 'download.queue.removed' AND message LIKE '%untouched%'"));
    }

    [Fact]
    public async Task With_blocklist_true_the_release_is_refused_from_every_indexer_in_the_real_blocklist_and_the_files_still_go()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => UseFake(services, client));
        var scene = await ClientAndLibraryAsync(app);
        await SentAsync(app, scene, "item-1", Release);

        var response = await app.Client.DeleteAsync($"/api/queue/{scene.ClientId}/item-1?blocklist=true&skipRedownload=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([("item-1", DownloadClientActions.DeleteWithData)], client.Calls);
        Assert.Equal("1", OnDisk(app, "jobs.db", $"SELECT COUNT(*) FROM blocked_releases WHERE release_name = '{Release}'"));
        // skipRedownload: refused for good, and no new search is queued behind it.
        Assert.Equal("0", OnDisk(app, "jobs.db", "SELECT COUNT(*) FROM job_queue"));
    }

    [Fact]
    public async Task A_client_that_refuses_the_removal_is_a_400_with_why_and_nothing_is_blocklisted()
    {
        var client = new FakeClient { Refuse = true };
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => UseFake(services, client));
        var scene = await ClientAndLibraryAsync(app);
        await SentAsync(app, scene, "item-1", Release);

        var response = await app.Client.DeleteAsync($"/api/queue/{scene.ClientId}/item-1?blocklist=true");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("would not", await response.Content.ReadAsStringAsync());
        Assert.Equal("0", OnDisk(app, "jobs.db", "SELECT COUNT(*) FROM blocked_releases"));
    }

    [Fact]
    public async Task A_download_Deluno_never_sent_is_refused_with_409_and_the_client_is_not_called()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => UseFake(services, client));
        var scene = await ClientAndLibraryAsync(app);

        var single = await app.Client.DeleteAsync($"/api/queue/{scene.ClientId}/hand-added");

        Assert.Equal(HttpStatusCode.Conflict, single.StatusCode);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Bulk_removal_applies_the_same_flags_to_every_download_and_one_that_is_not_Delunos_stops_the_lot()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => UseFake(services, client));
        var scene = await ClientAndLibraryAsync(app);
        await SentAsync(app, scene, "item-1", Release);
        await SentAsync(app, scene, "item-2", "Other.Film.2023.1080p.WEB-DL-GROUP");

        using var refused = await SendBulkAsync(app, "/api/queue/bulk?blocklist=true",
            new { items = new[] { new { clientId = scene.ClientId, queueItemId = "item-1" }, new { clientId = scene.ClientId, queueItemId = "hand-added" } } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(client.Calls);
        Assert.Equal("0", OnDisk(app, "jobs.db", "SELECT COUNT(*) FROM blocked_releases"));

        using var done = await SendBulkAsync(app, "/api/queue/bulk?blocklist=true&skipRedownload=true",
            new { items = new[] { new { clientId = scene.ClientId, queueItemId = "item-1" }, new { clientId = scene.ClientId, queueItemId = "item-2" } } });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(["item-1", "item-2"], client.Calls.Select(call => call.Item).OrderBy(item => item).ToArray());
        Assert.Equal("2", OnDisk(app, "jobs.db", "SELECT COUNT(*) FROM blocked_releases"));
    }

    private static async Task<HttpResponseMessage> SendBulkAsync(ApplicationTestHost app, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url) { Content = JsonContent.Create(body) };
        return await app.Client.SendAsync(request);
    }

    private sealed class FakeRegistry(FakeClient client) : IDownloadClientRegistry
    {
        public IReadOnlyCollection<string> KnownProtocols => ["qbittorrent"];

        public bool TryGet(string? protocol, out IDownloadClient found)
        {
            found = client;
            return true;
        }
    }

    private sealed class FakeClient : IDownloadClient
    {
        public List<(string Item, string Action)> Calls { get; } = [];

        public bool Refuse { get; set; }

        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
        {
            Calls.Add((queueItemId, action));
            return Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, !Refuse, Refuse ? "The client would not do that." : "Done."));
        }

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(null);

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null) => nativeStatus ?? "unknown";
    }
}
