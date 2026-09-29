using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Persistence.Tests.Platform;

public sealed class DownloadClientPersistenceTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<SqliteConnectionsRepository> CreateRepositoryAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
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

    // ── Use SSL / URL Base (#804) ─────────────────────────────────────────────

    [Fact]
    public async Task CreateDownloadClientAsync_persists_use_ssl_and_url_base()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateDownloadClientAsync(
            BaseCreateRequest() with { UseSsl = true, UrlBase = "/sabnzbd/" },
            CancellationToken.None);

        Assert.True(created.UseSsl);
        Assert.Equal("/sabnzbd/", created.UrlBase);

        var reloaded = Assert.Single(await repo.ListDownloadClientsAsync(CancellationToken.None));
        Assert.True(reloaded.UseSsl);
        Assert.Equal("/sabnzbd/", reloaded.UrlBase);
    }

    [Fact]
    public async Task CreateDownloadClientAsync_defaults_use_ssl_to_false_and_url_base_to_null()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        Assert.False(created.UseSsl);
        Assert.Null(created.UrlBase);
    }

    [Fact]
    public async Task UpdateDownloadClientAsync_can_turn_use_ssl_on_and_set_url_base()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, null, UseSsl: true, UrlBase: "sabnzbd"),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.True(updated.UseSsl);
        Assert.Equal("sabnzbd", updated.UrlBase);
    }

    [Fact]
    public async Task UpdateDownloadClientAsync_null_use_ssl_and_url_base_preserve_existing_values()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(
            BaseCreateRequest() with { UseSsl = true, UrlBase = "sabnzbd" },
            CancellationToken.None);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(Name: "Renamed", null, null, null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.True(updated.UseSsl);
        Assert.Equal("sabnzbd", updated.UrlBase);
    }

    // ── Add paused (#805) ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDownloadClientAsync_defaults_add_paused_to_false()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        Assert.False(created.AddPaused);
    }

    [Fact]
    public async Task CreateDownloadClientAsync_persists_add_paused()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest() with { AddPaused = true }, CancellationToken.None);

        Assert.True(created.AddPaused);
        var reloaded = Assert.Single(await repo.ListDownloadClientsAsync(CancellationToken.None));
        Assert.True(reloaded.AddPaused);
    }

    [Fact]
    public async Task UpdateDownloadClientAsync_can_turn_add_paused_on()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, null, AddPaused: true),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.True(updated.AddPaused);
    }

    [Fact]
    public async Task UpdateDownloadClientAsync_null_add_paused_preserves_existing_value()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest() with { AddPaused = true }, CancellationToken.None);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(Name: "Renamed again", null, null, null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.True(updated.AddPaused);
    }

    // ── CreateDownloadClientAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CreateDownloadClientAsync_persists_client_with_correct_field_values()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        Assert.Equal($"qBittorrent on {Deluno.Contracts.DelunoHostName.Current}", created.Name);
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
    public async Task CreateDownloadClientAsync_rejects_an_unknown_protocol_instead_of_rewriting_it()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => repo.CreateDownloadClientAsync(
            BaseCreateRequest() with { Protocol = "nonsense" }, CancellationToken.None));

        Assert.Contains("supported download client protocol", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("qbittorrent", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task Download_client_path_mappings_are_scoped_to_the_clients_host_and_can_be_removed()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var first = await repo.CreateDownloadClientAsync(BaseCreateRequest("qBittorrent") with { Host = "torrent-box" }, CancellationToken.None);
        var second = await repo.CreateDownloadClientAsync(BaseCreateRequest("SABnzbd") with { Host = "usenet-box" }, CancellationToken.None);

        var mapping = await repo.CreateDownloadClientPathMappingAsync(
            first.Id,
            new CreateDownloadClientPathMappingRequest("/downloads/complete", "D:\\Downloads\\complete", true, 10),
            CancellationToken.None);
        await repo.CreateDownloadClientPathMappingAsync(
            second.Id,
            new CreateDownloadClientPathMappingRequest("/sab/complete", "E:\\Usenet", true, 10),
            CancellationToken.None);

        Assert.NotNull(mapping);
        // Different hosts: each client sees only its own mapping (#809).
        var firstMappings = await repo.ListDownloadClientPathMappingsAsync(first.Id, CancellationToken.None);
        Assert.Single(firstMappings);
        Assert.Equal("/downloads/complete", firstMappings[0].RemotePath);
        Assert.Equal("D:\\Downloads\\complete", firstMappings[0].LocalPath);

        var removed = await repo.DeleteDownloadClientPathMappingAsync(first.Id, mapping!.Id, CancellationToken.None);
        Assert.True(removed);
        Assert.Empty(await repo.ListDownloadClientPathMappingsAsync(first.Id, CancellationToken.None));
        Assert.Single(await repo.ListDownloadClientPathMappingsAsync(second.Id, CancellationToken.None));
    }

    /// <summary>
    /// #809: a mapping added under one client is visible and reusable for a
    /// second client sharing the same host — Radarr/Sonarr's own "one global
    /// list keyed by host" behaviour, instead of every client re-entering the
    /// same remote-to-local translation for itself.
    /// </summary>
    [Fact]
    public async Task Download_client_path_mapping_added_for_one_client_applies_to_a_second_client_sharing_its_host()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var first = await repo.CreateDownloadClientAsync(BaseCreateRequest("qBittorrent") with { Host = "nas.local" }, CancellationToken.None);
        var second = await repo.CreateDownloadClientAsync(BaseCreateRequest("Transmission") with { Host = "NAS.LOCAL" }, CancellationToken.None);

        var mapping = await repo.CreateDownloadClientPathMappingAsync(
            first.Id,
            new CreateDownloadClientPathMappingRequest("/downloads/complete", "D:\\Downloads\\complete", true, 10),
            CancellationToken.None);
        Assert.NotNull(mapping);

        var secondMappings = await repo.ListDownloadClientPathMappingsAsync(second.Id, CancellationToken.None);

        var shared = Assert.Single(secondMappings);
        Assert.Equal(mapping!.Id, shared.Id);
        Assert.Equal("/downloads/complete", shared.RemotePath);
        Assert.Equal("D:\\Downloads\\complete", shared.LocalPath);
    }

    /// <summary>
    /// #809: the shared Remote Path Mappings settings screen lists every
    /// mapping across every host in one call — GET /api/download-clients/path-mappings
    /// passes no client id through to exactly this same repository method.
    /// A mapping must appear once even though two clients share its host, not
    /// once per client that happens to match it.
    /// </summary>
    [Fact]
    public async Task ListDownloadClientPathMappingsAsync_with_no_client_id_lists_every_mapping_once_across_every_host()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var first = await repo.CreateDownloadClientAsync(BaseCreateRequest("qBittorrent") with { Host = "nas.local" }, CancellationToken.None);
        // Shares the host with `first` on purpose: proves a mapping shows up
        // once, not once per client that matches it.
        await repo.CreateDownloadClientAsync(BaseCreateRequest("Transmission") with { Host = "nas.local" }, CancellationToken.None);
        var third = await repo.CreateDownloadClientAsync(BaseCreateRequest("SABnzbd") with { Host = "usenet-box" }, CancellationToken.None);

        await repo.CreateDownloadClientPathMappingAsync(
            first.Id,
            new CreateDownloadClientPathMappingRequest("/downloads/complete", "D:\\Downloads\\complete", true, 10),
            CancellationToken.None);
        await repo.CreateDownloadClientPathMappingAsync(
            third.Id,
            new CreateDownloadClientPathMappingRequest("/sab/complete", "E:\\Usenet", true, 10),
            CancellationToken.None);

        var all = await repo.ListDownloadClientPathMappingsAsync(null, CancellationToken.None);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, item => item.RemotePath == "/downloads/complete" && item.Host == "nas.local");
        Assert.Contains(all, item => item.RemotePath == "/sab/complete" && item.Host == "usenet-box");
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
        Assert.Equal("qBittorrent on 192.168.1.100", updated.Name);
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

    // ── protocol catalogue (#292) ─────────────────────────────────────────────

    /// <summary>
    /// The API now refuses to write a protocol nothing can dispatch to, but
    /// storage still has to read back a row a previous version wrote, or an
    /// upgrade would silently rewrite somebody's connection.
    /// </summary>
    [Theory]
    [InlineData("torrent")]
    [InlineData("usenet")]
    [InlineData("custom")]
    public async Task Legacy_protocols_remain_loadable(string protocol)
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest() with { Protocol = protocol }, CancellationToken.None);

        Assert.Equal(protocol, created.Protocol);
        Assert.Equal(protocol, Assert.Single(await repo.ListDownloadClientsAsync(CancellationToken.None)).Protocol);
    }

    [Fact]
    public async Task An_unknown_protocol_is_refused_by_storage()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => repo.CreateDownloadClientAsync(BaseCreateRequest() with { Protocol = "carrier-pigeon" }, CancellationToken.None));

        // The message names what a user may actually choose, not the wider set
        // storage will still read back.
        Assert.Contains("Supported protocols: deluge, nzbget, qbittorrent, rtorrent, sabnzbd, transmission, utorrent.", error.Message, StringComparison.Ordinal);
    }

    // ── Save path / location (#811) ─────────────────────────────────────────

    [Fact]
    public async Task CreateDownloadClientAsync_persists_the_save_path_and_defaults_it_to_null()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var withLocation = await repo.CreateDownloadClientAsync(BaseCreateRequest() with { Location = @"D:\Downloads\Movies" }, CancellationToken.None);
        Assert.Equal(@"D:\Downloads\Movies", withLocation.Location);
        var reloaded = Assert.Single(await repo.ListDownloadClientsAsync(CancellationToken.None), item => item.Id == withLocation.Id);
        Assert.Equal(@"D:\Downloads\Movies", reloaded.Location);

        var withoutLocation = await repo.CreateDownloadClientAsync(BaseCreateRequest(name: "qBittorrent 2"), CancellationToken.None);
        Assert.Null(withoutLocation.Location);
    }

    // ── Content layout (#999) ───────────────────────────────────────────────

    [Fact]
    public async Task A_client_keeps_the_clients_own_content_layout_until_one_is_chosen_and_the_choice_persists()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var plain = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);
        Assert.Equal("default", plain.ContentLayout);

        var chosen = await repo.CreateDownloadClientAsync(BaseCreateRequest(name: "qBittorrent 2") with { ContentLayout = "no-subfolder" }, CancellationToken.None);
        Assert.Equal("no-subfolder", chosen.ContentLayout);
        Assert.Equal("no-subfolder", Assert.Single(await repo.ListDownloadClientsAsync(CancellationToken.None), item => item.Id == chosen.Id).ContentLayout);

        // An edit that does not mention it leaves it alone; naming one changes it.
        var untouched = await repo.UpdateDownloadClientAsync(
            chosen.Id, new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, null, Location: @"E:\T"), CancellationToken.None);
        Assert.Equal("no-subfolder", untouched!.ContentLayout);
        var changed = await repo.UpdateDownloadClientAsync(
            chosen.Id, new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, null, ContentLayout: "subfolder"), CancellationToken.None);
        Assert.Equal("subfolder", changed!.ContentLayout);
        Assert.Equal("subfolder", Assert.Single(await repo.ListDownloadClientsAsync(CancellationToken.None), item => item.Id == chosen.Id).ContentLayout);
    }

    [Fact]
    public async Task UpdateDownloadClientAsync_can_set_and_clear_the_save_path()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateDownloadClientAsync(BaseCreateRequest(), CancellationToken.None);

        var updated = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, null, Location: @"E:\Torrents"),
            CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal(@"E:\Torrents", updated.Location);

        var reopened = Assert.Single(await repo.ListDownloadClientsAsync(CancellationToken.None), item => item.Id == created.Id);
        Assert.Equal(@"E:\Torrents", reopened.Location);

        var cleared = await repo.UpdateDownloadClientAsync(
            created.Id,
            new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, null, ClearLocation: true),
            CancellationToken.None);
        Assert.NotNull(cleared);
        Assert.Null(cleared.Location);
    }
}
