using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A library's folders, checked end to end (#616).
///
/// <para><b>Why this file exists.</b> A download client, Deluno and a processor
/// each hold part of the chain, set in three places, and nothing checked that
/// they agreed. On the rig, Transmission saved TV to a folder Weir never
/// watched, and nothing on screen said so.</para>
/// </summary>
public sealed class FolderChainTests
{
    [Fact]
    public async Task A_client_saving_to_the_librarys_downloads_folder_matches_link_by_link()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: true);
        client.Result = Ready(scene.Downloads);

        var report = await ChainAsync(app, scene.LibraryId);

        Assert.True(report.GetProperty("allMatch").GetBoolean(), report.ToString());
        var links = report.GetProperty("clients")[0].GetProperty("links");
        Assert.Equal("ok", links[1].GetProperty("status").GetString());
        Assert.Contains("the folder the Films library and Weir expect", links[1].GetProperty("message").GetString());
        Assert.Contains(Steps(report), step => step == "ready");
    }

    [Fact]
    public async Task A_client_saving_somewhere_else_is_named_with_both_folders()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: true);
        var elsewhere = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Transmission")).FullName;
        client.Result = Ready(elsewhere);

        var report = await ChainAsync(app, scene.LibraryId);

        Assert.False(report.GetProperty("allMatch").GetBoolean());
        var folder = report.GetProperty("clients")[0].GetProperty("links")[1];
        Assert.Equal("problem", folder.GetProperty("status").GetString());
        var message = folder.GetProperty("message").GetString()!;
        Assert.Contains(elsewhere, message);
        Assert.Contains(scene.Downloads, message);
        Assert.Contains("will not be picked up", message);
    }

    [Fact]
    public async Task Without_a_processor_the_chain_has_no_ready_step()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: false);
        client.Result = Ready(scene.Downloads);

        var report = await ChainAsync(app, scene.LibraryId);

        Assert.False(report.GetProperty("usesProcessor").GetBoolean());
        Assert.DoesNotContain(Steps(report), step => step == "ready");
        Assert.True(report.GetProperty("allMatch").GetBoolean(), report.ToString());
    }

    [Fact]
    public async Task A_category_the_client_does_not_have_is_a_problem_in_the_clients_words()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: true);
        client.Result = new DownloadClientCategoryCheckResult("c", "Fake", "movies", DownloadClientCategoryStatuses.Missing,
            "Fake has no category named movies.", Supported: true, Found: false);

        var report = await ChainAsync(app, scene.LibraryId);

        var category = report.GetProperty("clients")[0].GetProperty("links")[0];
        Assert.Equal("problem", category.GetProperty("status").GetString());
        Assert.Equal("Fake has no category named movies.", category.GetProperty("message").GetString());
    }

    private sealed record Scene(string LibraryId, string Downloads);

    private static IEnumerable<string?> Steps(JsonElement report)
        => report.GetProperty("libraryLinks").EnumerateArray().Select(link => link.GetProperty("step").GetString());

    private static DownloadClientCategoryCheckResult Ready(string savePath)
        => new("c", "Fake", "movies", DownloadClientCategoryStatuses.Ready, $"Fake saves movies to {savePath}.", Supported: true, Found: true, SavePath: savePath);

    private static async Task<JsonElement> ChainAsync(ApplicationTestHost app, string libraryId)
    {
        var response = await app.Client.GetAsync($"/api/libraries/{libraryId}/folder-chain");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Scene> SceneAsync(ApplicationTestHost app, bool processor)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Completed", "Movies")).FullName;
        var ready = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Ready", "Movies")).FullName;
        var created = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = root,
            downloadsPath = downloads,
            importWorkflow = processor ? "refine-before-import" : "direct-import",
            processorName = processor ? "Weir" : null,
            processorOutputPath = processor ? ready : null
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var client = await app.Services.GetRequiredService<IConnectionsRepository>().CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fake", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        return new Scene(libraryId, downloads);
    }

    private static void Use(IServiceCollection services, FakeClient client)
    {
        services.RemoveAll<IDownloadClientRegistry>();
        services.AddSingleton<IDownloadClientRegistry>(new FakeRegistry(client));
    }

    private sealed class FakeRegistry(FakeClient client) : IDownloadClientRegistry
    {
        public IReadOnlyCollection<string> KnownProtocols => ["fakeclient"];

        public bool TryGet(string? protocol, out IDownloadClient found)
        {
            found = client;
            return true;
        }
    }

    private sealed class FakeClient : IDownloadClient
    {
        public DownloadClientCategoryCheckResult? Result { get; set; }

        public string Protocol => "fakeclient";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => Task.FromResult(Result!);

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken) => Task.FromResult<DownloadClientTelemetrySnapshot?>(null);

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null) => nativeStatus ?? "unknown";
    }
}
