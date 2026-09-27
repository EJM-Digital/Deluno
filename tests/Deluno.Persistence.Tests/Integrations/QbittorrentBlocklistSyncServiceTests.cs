using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #822: the content blocklist is pushed into every enabled qBittorrent
/// client's "Excluded file names" preference only when the sync switch is on
/// and the blocklist's own content actually changed since the last push.
/// </summary>
public sealed class QbittorrentBlocklistSyncServiceTests
{
    private static async Task<(SqliteDownloadHealthRepository Health, SqliteConnectionsRepository Connections, TestStorage Storage)> CreateAsync()
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (
            new SqliteDownloadHealthRepository(storage.Factory, time),
            new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage)),
            storage);
    }

    private static QbittorrentBlocklistSyncService CreateService(IServiceProvider services)
        => new(NullLogger<QbittorrentBlocklistSyncService>.Instance, new SingleScopeFactory(services), TimeProvider.System);

    [Fact]
    public async Task Sync_switched_off_never_calls_the_client()
    {
        var (health, connections, storage) = await CreateAsync();
        using var _ = storage;
        await health.SaveContentBlocklistSettingsAsync(ContentBlocklistSettings.Default with { Enabled = true, SyncToQbittorrentEnabled = false }, CancellationToken.None);
        await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("qB", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var client = new FakeQbittorrentClient();
        var services = BuildServices(health, connections, client);

        await CreateService(services).SyncAsync(CancellationToken.None);

        Assert.Empty(client.Pushes);
    }

    [Fact]
    public async Task Sync_switched_on_pushes_the_effective_patterns_to_every_enabled_qbittorrent_client()
    {
        var (health, connections, storage) = await CreateAsync();
        using var _ = storage;
        await health.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, SyncToQbittorrentEnabled = true, UseBuiltInPatterns = false, CustomPatterns = ["*.exe"] },
            CancellationToken.None);
        await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("qB", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var client = new FakeQbittorrentClient();
        var services = BuildServices(health, connections, client);

        await CreateService(services).SyncAsync(CancellationToken.None);

        var push = Assert.Single(client.Pushes);
        Assert.Equal(["*.exe"], push);
        var states = await health.ListQbittorrentBlocklistSyncStatesAsync(CancellationToken.None);
        Assert.True(Assert.Single(states.Values).Succeeded);
    }

    [Fact]
    public async Task Sync_skips_a_client_whose_last_successful_push_already_matches_the_current_blocklist()
    {
        var (health, connections, storage) = await CreateAsync();
        using var _ = storage;
        await health.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, SyncToQbittorrentEnabled = true, UseBuiltInPatterns = false, CustomPatterns = ["*.exe"] },
            CancellationToken.None);
        var created = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("qB", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var client = new FakeQbittorrentClient();
        var services = BuildServices(health, connections, client);
        await CreateService(services).SyncAsync(CancellationToken.None);
        Assert.Single(client.Pushes);

        // Same blocklist content, second pass: nothing new to push.
        await CreateService(services).SyncAsync(CancellationToken.None);

        Assert.Single(client.Pushes);
    }

    [Fact]
    public async Task Sync_pushes_again_once_the_blocklist_content_changes()
    {
        var (health, connections, storage) = await CreateAsync();
        using var _ = storage;
        await health.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, SyncToQbittorrentEnabled = true, UseBuiltInPatterns = false, CustomPatterns = ["*.exe"] },
            CancellationToken.None);
        await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("qB", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var client = new FakeQbittorrentClient();
        var services = BuildServices(health, connections, client);
        await CreateService(services).SyncAsync(CancellationToken.None);

        await health.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, SyncToQbittorrentEnabled = true, UseBuiltInPatterns = false, CustomPatterns = ["*.exe", "*.scr"] },
            CancellationToken.None);
        await CreateService(services).SyncAsync(CancellationToken.None);

        Assert.Equal(2, client.Pushes.Count);
        Assert.Equal(["*.exe", "*.scr"], client.Pushes[1]);
    }

    private static IServiceProvider BuildServices(
        IDownloadHealthRepository health,
        IConnectionsRepository connections,
        FakeQbittorrentClient client)
    {
        var services = new ServiceCollection();
        services.AddSingleton(health);
        services.AddSingleton(connections);
        services.AddSingleton<IDownloadClientRegistry>(new DownloadClientRegistry([client]));
        return services.BuildServiceProvider();
    }

    /// <summary>A scope factory that always hands back the same provider - fine for a test with no scoped lifetimes to isolate.</summary>
    private sealed class SingleScopeFactory(IServiceProvider services) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new NonDisposingScope(services);

        private sealed class NonDisposingScope(IServiceProvider services) : IServiceScope
        {
            public IServiceProvider ServiceProvider => services;
            public void Dispose() { }
        }
    }

    private sealed class FakeQbittorrentClient : IDownloadClient, IQbittorrentBlocklistSyncClient
    {
        public List<IReadOnlyList<string>> Pushes { get; } = [];

        public string Protocol => "qbittorrent";
        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, false, true, true, true, true, "form");

        public Task<(bool Succeeded, string? Message)> PushExcludedFileNamesAsync(DownloadClientItem client, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
        {
            Pushes.Add(patterns);
            return Task.FromResult<(bool, string?)>((true, null));
        }

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);
        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>(null);
        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null) => nativeStatus ?? "unknown";
    }
}
