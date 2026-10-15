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

    /// <summary>#1328: a client category folder that is, holds or sits inside a library root is a problem on the chain, said before anything is removed, and it leads the folder line.</summary>
    [Theory]
    [InlineData("inside")]
    [InlineData("is")]
    [InlineData("holds")]
    public async Task A_client_folder_that_is_holds_or_sits_inside_a_library_root_is_a_problem_in_plain_words(string relation)
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: false);
        var films = Path.Combine(app.DataRoot, "Films");
        var saves = relation switch { "inside" => Directory.CreateDirectory(Path.Combine(films, "Downloads")).FullName, "is" => films, _ => app.DataRoot };
        client.Result = Ready(saves);

        var report = await ChainAsync(app, scene.LibraryId);

        var link = Link(report, "in-library");
        Assert.Equal("problem", link.GetProperty("status").GetString());
        Assert.Contains("inside the Films library, or holds it", link.GetProperty("message").GetString());
        Assert.Contains("somewhere outside every library", link.GetProperty("message").GetString());
        Assert.False(report.GetProperty("allMatch").GetBoolean());
    }

    [Fact]
    public async Task A_client_folder_outside_every_library_draws_no_inside_a_library_line()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: true);
        client.Result = Ready(scene.Downloads);

        var report = await ChainAsync(app, scene.LibraryId);

        Assert.DoesNotContain(report.GetProperty("clients")[0].GetProperty("links").EnumerateArray(), link => link.GetProperty("step").GetString() == "in-library");
    }

    [Fact]
    public async Task Where_it_downloads_is_checked_apart_from_where_it_finishes()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: true);
        var incomplete = Path.Combine(Path.GetTempPath(), "deluno-incomplete-ok");
        client.Result = Ready(scene.Downloads, incomplete);

        var report = await ChainAsync(app, scene.LibraryId);

        var link = Link(report, "incomplete");
        Assert.Equal("ok", link.GetProperty("status").GetString());
        Assert.Contains("keeps", link.GetProperty("message").GetString());
        Assert.Equal(incomplete, report.GetProperty("clients")[0].GetProperty("incompleteFolder").GetString());
        Assert.True(report.GetProperty("allMatch").GetBoolean(), report.ToString());
    }

    [Theory]
    [InlineData("", "the same as")]
    [InlineData("partial", "inside")]
    public async Task An_in_progress_folder_that_is_or_sits_inside_the_finished_folder_is_flagged(string below, string how)
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: true);
        client.Result = Ready(scene.Downloads, below.Length == 0 ? scene.Downloads : Path.Combine(scene.Downloads, below));

        var report = await ChainAsync(app, scene.LibraryId);

        var link = Link(report, "incomplete");
        Assert.Equal("problem", link.GetProperty("status").GetString());
        Assert.Contains(how, link.GetProperty("message").GetString());
        Assert.Contains("Half-finished files", link.GetProperty("message").GetString());
        Assert.False(report.GetProperty("allMatch").GetBoolean());
    }

    [Fact]
    public async Task A_client_that_does_not_say_where_it_downloads_is_listed_as_not_known()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: true);
        client.Result = Ready(scene.Downloads) with { IncompletePath = null };

        var report = await ChainAsync(app, scene.LibraryId);

        Assert.Equal("unknown", Link(report, "incomplete").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_library_with_no_processor_expects_a_folder_in_the_singular()
    {
        var client = new FakeClient();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: false);
        client.Result = Ready(scene.Downloads);

        var report = await ChainAsync(app, scene.LibraryId);

        var message = report.GetProperty("clients")[0].GetProperty("links")[1].GetProperty("message").GetString();
        Assert.Contains("the folder the Films library expects.", message);
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

    [Fact]
    public async Task A_client_that_never_answers_is_reported_as_not_answering_and_the_check_still_finishes()
    {
        var client = new FakeClient { Hang = true };
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            Use(services, client);
            services.RemoveAll<IFolderChainService>();
            services.AddScoped<IFolderChainService>(provider => new FolderChainService(
                provider.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>(),
                provider.GetRequiredService<IConnectionsRepository>(),
                provider.GetRequiredService<IDownloadClientRegistry>(),
                stepTimeout: TimeSpan.FromMilliseconds(300)));
        });
        var scene = await SceneAsync(app, processor: false);

        var started = DateTimeOffset.UtcNow;
        var report = await ChainAsync(app, scene.LibraryId);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.False(report.GetProperty("allMatch").GetBoolean());
        var category = report.GetProperty("clients")[0].GetProperty("links")[0];
        Assert.Equal("unknown", category.GetProperty("status").GetString());
        Assert.Contains("did not answer", category.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_client_that_honours_its_token_and_is_cancelled_by_the_timeout_is_reported_as_not_answering_not_a_500()
    {
        var client = new FakeClient { HangHonouringToken = true };
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            Use(services, client);
            services.RemoveAll<IFolderChainService>();
            services.AddScoped<IFolderChainService>(provider => new FolderChainService(
                provider.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>(),
                provider.GetRequiredService<IConnectionsRepository>(),
                provider.GetRequiredService<IDownloadClientRegistry>(),
                stepTimeout: TimeSpan.FromMilliseconds(300)));
        });
        var scene = await SceneAsync(app, processor: false);

        var report = await ChainAsync(app, scene.LibraryId);

        var category = report.GetProperty("clients")[0].GetProperty("links")[0];
        Assert.Equal("unknown", category.GetProperty("status").GetString());
        Assert.Contains("did not answer within", category.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Only_the_callers_own_cancellation_propagates_from_a_step()
    {
        using var caller = new CancellationTokenSource();
        var work = FolderChainService.WithTimeoutAsync<int>(
            async token => { await Task.Delay(TimeSpan.FromMinutes(2), token); return 1; }, // reason: a step that never finishes; only the cancellation or the timeout under test ends it
            TimeSpan.FromMinutes(1),
            caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);

        await Assert.ThrowsAsync<TimeoutException>(() => FolderChainService.WithTimeoutAsync<int>(
            async token => { await Task.Delay(TimeSpan.FromMinutes(2), token); return 1; }, // reason: a step that never finishes; only the cancellation or the timeout under test ends it
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_client_that_throws_is_reported_in_plain_words_not_as_a_failed_check()
    {
        var client = new FakeClient { Throw = true };
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Use(services, client));
        var scene = await SceneAsync(app, processor: false);

        var report = await ChainAsync(app, scene.LibraryId);

        var category = report.GetProperty("clients")[0].GetProperty("links")[0];
        Assert.Equal("unknown", category.GetProperty("status").GetString());
        Assert.Contains("could not be checked", category.GetProperty("message").GetString());
    }

    private sealed record Scene(string LibraryId, string Downloads);

    private static IEnumerable<string?> Steps(JsonElement report)
        => report.GetProperty("libraryLinks").EnumerateArray().Select(link => link.GetProperty("step").GetString());

    /// <summary>A client that finishes in <paramref name="savePath"/> and (unless told otherwise) keeps downloads in progress in a folder of its own (#1164).</summary>
    private static DownloadClientCategoryCheckResult Ready(string savePath, string? incompletePath = null)
        => new("c", "Fake", "movies", DownloadClientCategoryStatuses.Ready, $"Fake saves movies to {savePath}.", Supported: true, Found: true, SavePath: savePath,
            IncompletePath: incompletePath ?? Path.Combine(Path.GetTempPath(), "deluno-incomplete-" + Guid.NewGuid().ToString("N")));

    private static JsonElement Link(JsonElement report, string step)
        => report.GetProperty("clients")[0].GetProperty("links").EnumerateArray().First(link => link.GetProperty("step").GetString() == step);

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

        public bool Hang { get; set; }

        public bool HangHonouringToken { get; set; }

        public bool Throw { get; set; }

        public async Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
        {
            if (Throw) throw new InvalidOperationException("boom");
            // Ignores the token on purpose: the check must not depend on the client honouring it.
            if (Hang) await Task.Delay(TimeSpan.FromMinutes(2), CancellationToken.None); // reason: a client that hangs and ignores its token; the step timeout under test must cut it off
            // Honours the token, like a real HTTP client: throws TaskCanceledException when the step timeout fires.
            if (HangHonouringToken) await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken); // reason: a client that hangs but honours its token; the step timeout under test must cut it off
            return Result!;
        }

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken) => Task.FromResult<DownloadClientTelemetrySnapshot?>(null);

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null) => nativeStatus ?? "unknown";
    }
}
