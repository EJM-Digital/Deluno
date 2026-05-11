using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class DownloadClientPersistenceTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<SqlitePlatformSettingsRepository> CreateRepositoryAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
    }

    private static CreateDownloadClientRequest BaseCreateRequest(string name = "qBittorrent") =>
        new(Name: name,
            Protocol: "qbittorrent",
            Host: "localhost",
            Port: 8080,
            Username: "admin",
            Password: "password",
            EndpointUrl: null,
            MoviesCategory: "movies",
            TvCategory: "tv",
            CategoryTemplate: null,
            Priority: 1,
            IsEnabled: true);

    // ── CreateDownloadClientAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CreateDownloadClientAsync_persists_client_with_correct_field_values()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        Assert.Equal("qBittorrent", created.Name);
        Assert.Equal("qbittorrent", created.Protocol);
        Assert.Equal("localhost", created.Host);
        Assert.Equal(8080, created.Port);
        Assert.Equal("admin", created.Username);
        Assert.Equal("movies", created.MoviesCategory);
        Assert.Equal("tv", created.TvCategory);
        Assert.Equal(1, created.Priority);
        Assert.True(created.IsEnabled);
        Assert.Equal("untested", created.HealthStatus);
    }

    [Fact]
    public async Task CreateDownloadClientAsync_disabled_client_starts_with_disabled_health_status()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateDownloadClientAsync(
            BaseCreateRequest() with { IsEnabled = false },
            CancellationToken.None);

        Assert.Equal("disabled", created.HealthStatus);
        Assert.False(created.IsEnabled);
    }

    [Fact]
    public async Task CreateDownloadClientAsync_multiple_clients_are_all_listed()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        await repo.CreateDownloadClientAsync(BaseCreateRequest("Client A"), CancellationToken.None);
        await repo.CreateDownloadClientAsync(BaseCreateRequest("Client B"), CancellationToken.None);

        var list = await repo.ListDownloadClientsAsync(CancellationToken.None);
        Assert.Equal(2, list.Count);
    }

    // ── UpdateDownloadClientAsync — null/patch semantics ──────────────────────

    [Fact]
    public async Task UpdateDownloadClientAsync_returns_null_for_unknown_id()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var result = await repo.UpdateDownloadClientAsync(
            "nonexistent-id",
            new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateDownloadClientAsync_null_fields_preserve_all_existing_values()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(created.Name, updated.Name);
        Assert.Equal(created.Protocol, updated.Protocol);
        Assert.Equal(created.Host, updated.Host);
        Assert.Equal(created.Port, updated.Port);
        Assert.Equal(created.Username, updated.Username);
        Assert.Equal(created.MoviesCategory, updated.MoviesCategory);
        Assert.Equal(created.TvCategory, updated.TvCategory);
        Assert.Equal(created.Priority, updated.Priority);
        Assert.Equal(created.IsEnabled, updated.IsEnabled);
    }

    [Fact]
    public async Task UpdateDownloadClientAsync_provided_fields_overwrite_existing_values()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(
                Name: "Updated Client",
                Protocol: null,
                Host: "192.168.1.100",
                Port: 9090,
                Username: null,
                Password: null,
                EndpointUrl: null,
                MoviesCategory: "radarr-movies",
                TvCategory: null,
                CategoryTemplate: null,
                Priority: null,
                IsEnabled: null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Updated Client", updated.Name);
        Assert.Equal("192.168.1.100", updated.Host);
        Assert.Equal(9090, updated.Port);
        Assert.Equal("radarr-movies", updated.MoviesCategory);
        Assert.Equal("tv", updated.TvCategory);  // preserved
        Assert.Equal("qbittorrent", updated.Protocol);  // preserved
    }

    [Fact]
    public async Task UpdateDownloadClientAsync_port_below_one_keeps_existing_port()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(
            BaseCreateRequest() with { Port = 8080 },
            CancellationToken.None);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(null, null, null, Port: 0, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(8080, updated.Port);
    }

    // ── UpdateDownloadClientAsync — enable/disable health reset ───────────────

    [Fact]
    public async Task UpdateDownloadClientAsync_enabling_disabled_client_resets_health_to_untested()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(
            BaseCreateRequest() with { IsEnabled = false },
            CancellationToken.None);
        Assert.Equal("disabled", created.HealthStatus);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, IsEnabled: true),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.True(updated.IsEnabled);
        Assert.Equal("untested", updated.HealthStatus);
    }

    [Fact]
    public async Task UpdateDownloadClientAsync_disabling_enabled_client_keeps_existing_health_status()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);
        await repo.UpdateDownloadClientHealthAsync(created.Id, "healthy", "Connected.", null, 30, CancellationToken.None);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, IsEnabled: false),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.False(updated.IsEnabled);
        Assert.Equal("healthy", updated.HealthStatus);
    }

    // ── DeleteDownloadClientAsync ─────────────────────────────────────────────

    [Fact]
    public async Task DeleteDownloadClientAsync_removes_client_from_list()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        var deleted = await repo.DeleteDownloadClientAsync(created.Id, CancellationToken.None);

        Assert.True(deleted);
        var list = await repo.ListDownloadClientsAsync(CancellationToken.None);
        Assert.Empty(list);
    }

    [Fact]
    public async Task DeleteDownloadClientAsync_returns_false_for_unknown_id()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var deleted = await repo.DeleteDownloadClientAsync("nonexistent-id", CancellationToken.None);

        Assert.False(deleted);
    }
}
