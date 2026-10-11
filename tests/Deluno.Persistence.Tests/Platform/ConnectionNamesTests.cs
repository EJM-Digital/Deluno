using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Notifications.Contracts;
using Deluno.Notifications.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1076: a connection's name is worked out ("qBittorrent on RIG", "nzbgeek.info",
/// "Trakt: my-list"), never typed. Two of a kind on one host tell themselves apart by
/// port, then a suffix; this machine's own address is its hostname; an API client's
/// name is ignored; and renaming changes nothing anything is linked by.
/// </summary>
public sealed class ConnectionNamesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T02:00:00Z");
    private static readonly string Machine = DelunoHostName.Current;

    private static async Task<(TestStorage Storage, FixedTimeProvider Clock)> StartAsync()
    {
        var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (storage, clock);
    }

    private static CreateDownloadClientRequest Client(string? name, string protocol, string host, int port)
        => new(name, protocol, host, port, null, null, null, null, null, null, 1, true);

    private static UpdateDownloadClientRequest ClientUpdate(string? name = null, string? host = null)
        => new(name, null, host, null, null, null, null, null, null, null, null, null);

    private static CreateIndexerRequest Indexer(string? name, string baseUrl, string? definitionId = null)
        => new(name, "newznab", "public", baseUrl, "key", 10, "2000,5000", null, "both", true) { DefinitionId = definitionId };

    [Fact]
    public void A_name_is_the_app_and_the_host_and_a_machines_own_address_is_its_hostname()
    {
        Assert.Equal("Plex on nas.lan", ConnectionNames.AppOnHost("Plex", "http://nas.lan:32400/web"));
        Assert.Equal("Plex on nas.lan", ConnectionNames.AppOnHost("Plex", "nas.lan"));
        Assert.Equal(32400, ConnectionNames.PortOf("http://nas.lan:32400"));
        Assert.Null(ConnectionNames.PortOf("https://nas.lan"));
        foreach (var local in new[] { "localhost", "127.0.0.1", "http://127.0.0.1:8080", "::1", "http://[::1]:8080/", "http://LOCALHOST:8096" })
        {
            Assert.Equal($"Jellyfin on {Machine}", ConnectionNames.AppOnHost("Jellyfin", local));
        }

        Assert.Equal("Seerr", ConnectionNames.AppOnHost("Seerr", null));
        Assert.Equal("nzbgeek.info", ConnectionNames.SiteName("https://api.nzbgeek.info/api"));
        Assert.Equal("1337x.to", ConnectionNames.SiteName("https://www.1337x.to/"));
        Assert.Equal("Trakt: my-list", ConnectionNames.ImportList("trakt", "https://trakt.tv/users/me/lists/my-list"));
        Assert.Equal("qBittorrent", ConnectionNames.DownloadClientApp("qbittorrent"));
    }

    [Fact]
    public void A_clash_takes_the_port_then_a_short_suffix()
    {
        Assert.Equal("qBittorrent on RIG", ConnectionNames.Unique("qBittorrent on RIG", 8080, "id1", []));
        Assert.Equal("qBittorrent on RIG:8081", ConnectionNames.Unique("qBittorrent on RIG", 8081, "id2", ["qbittorrent on rig"]));
        var third = ConnectionNames.Unique("qBittorrent on RIG", 8081, "abcdef12", ["qBittorrent on RIG", "qBittorrent on RIG:8081"]);
        Assert.Equal("qBittorrent on RIG:8081 (ef12)", third);
        Assert.Equal("Discord on discord.com (2)", ConnectionNames.Unique("Discord on discord.com", null, "", ["Discord on discord.com"]));

        var all = ConnectionNames.AssignAll([("a", "qBittorrent on RIG", 8080), ("b", "qBittorrent on RIG", 8081), ("c", "SABnzbd on RIG", 8085)]);
        Assert.Equal("qBittorrent on RIG:8080", all["a"]);
        Assert.Equal("qBittorrent on RIG:8081", all["b"]);
        Assert.Equal("SABnzbd on RIG", all["c"]);
    }

    [Fact]
    public async Task A_download_client_is_named_after_its_app_and_host_and_a_typed_name_is_ignored()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteConnectionsRepository(storage.Factory, clock, TestSecretProtection.Create(storage));

        var first = await repository.CreateDownloadClientAsync(Client("My favourite client", "qbittorrent", "192.168.1.51", 8080), CancellationToken.None);
        var second = await repository.CreateDownloadClientAsync(Client("Another", "qbittorrent", "192.168.1.51", 8081), CancellationToken.None);
        var local = await repository.CreateDownloadClientAsync(Client(null, "sabnzbd", "localhost", 8085), CancellationToken.None);
        var loopback = await repository.CreateDownloadClientAsync(Client(null, "nzbget", "127.0.0.1", 6789), CancellationToken.None);

        Assert.Equal("qBittorrent on 192.168.1.51", first.Name);
        Assert.Equal("qBittorrent on 192.168.1.51:8081", second.Name);
        Assert.Equal($"SABnzbd on {Machine}", local.Name);
        Assert.Equal($"NZBGet on {Machine}", loopback.Name);

        // Saving with a name from an API client neither fails nor renames it.
        var saved = await repository.UpdateDownloadClientAsync(first.Id, ClientUpdate(name: "Renamed by a script"), CancellationToken.None);
        Assert.Equal("qBittorrent on 192.168.1.51", saved!.Name);
        Assert.Equal("qBittorrent on 192.168.1.51:8081", (await repository.UpdateDownloadClientAsync(second.Id, ClientUpdate(), CancellationToken.None))!.Name);

        // Moving it to another host renames it.
        var moved = await repository.UpdateDownloadClientAsync(first.Id, ClientUpdate(host: "rig.lan"), CancellationToken.None);
        Assert.Equal("qBittorrent on rig.lan", moved!.Name);
    }

    [Fact]
    public async Task An_indexer_is_named_after_its_site_or_its_definition()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteConnectionsRepository(
            storage.Factory, clock, TestSecretProtection.Create(storage), new FakeDefinitionNames());

        var geek = await repository.CreateIndexerAsync(Indexer("Anything typed", "https://api.nzbgeek.info"), CancellationToken.None);
        var again = await repository.CreateIndexerAsync(Indexer(null, "https://api.nzbgeek.info"), CancellationToken.None);
        var tracker = await repository.CreateIndexerAsync(Indexer(null, "https://1337x.to", "1337x"), CancellationToken.None);

        // A name given is kept (#1248); one not given is the site, and a second of the same site tells itself apart.
        Assert.Equal("Anything typed", geek.Name);
        Assert.Equal("nzbgeek.info", again.Name);
        Assert.Equal("1337x", tracker.Name);

        var renamedByApi = await repository.UpdateIndexerAsync(
            geek.Id, new UpdateIndexerRequest("Nope", null, null, null, null, null, null, null, null, null), CancellationToken.None);
        Assert.Equal("Nope", renamedByApi!.Name);
    }

    [Fact]
    public async Task A_media_server_and_a_seerr_server_are_named_after_their_app_and_host()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var protector = TestSecretProtection.Create(storage);
        var servers = new SqliteMediaServerRepository(storage.Factory, clock, protector);
        var seerr = new SqliteSeerrRepository(storage.Factory, clock, protector);

        var plex = await servers.SaveAsync(null, new SaveMediaServerRequest("plex", "Living room", "http://nas.lan:32400", "token", true), CancellationToken.None);
        var jelly = await servers.SaveAsync(null, new SaveMediaServerRequest("jellyfin", null, "http://localhost:8096", "token", true), CancellationToken.None);
        var jelly2 = await servers.SaveAsync(null, new SaveMediaServerRequest("jellyfin", null, "http://127.0.0.1:8097", "token", true), CancellationToken.None);
        Assert.Equal("Plex on nas.lan", plex!.Name);
        Assert.Equal($"Jellyfin on {Machine}", jelly!.Name);
        Assert.Equal($"Jellyfin on {Machine}:8097", jelly2!.Name);
        Assert.Equal("Plex on nas.lan", (await servers.SaveAsync(plex.Id, new SaveMediaServerRequest("plex", "Changed", "http://nas.lan:32400", null, true), CancellationToken.None))!.Name);

        var request = await seerr.SaveAsync(null, new SaveSeerrRequest("Requests", "http://request.lan:5055", "key", true), CancellationToken.None);
        Assert.Equal("Seerr on request.lan", request!.Name);
    }

    [Fact]
    public async Task A_notification_connection_is_named_after_its_provider_and_host()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteNotificationRepository(storage.Factory, clock);

        var discord = await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Home", "https://discord.com/api/webhooks/1/x", "all", true, NotificationProviders.Discord), CancellationToken.None);
        var second = await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("Home", "https://discord.com/api/webhooks/2/y", "all", true, NotificationProviders.Discord), CancellationToken.None);
        var local = await repository.CreateNotificationWebhookAsync(
            new CreateNotificationWebhookRequest("HA", "http://localhost:8123/hook", "all", true), CancellationToken.None);

        Assert.Equal("Discord on discord.com", discord.Name);
        Assert.NotEqual(discord.Name, second.Name);
        Assert.StartsWith("Discord on discord.com (", second.Name);
        Assert.Equal($"Webhook on {Machine}", local.Name);

        var updated = await repository.UpdateNotificationWebhookAsync(
            discord.Id, new UpdateNotificationWebhookRequest("Zzz", "https://discord.com/api/webhooks/1/x", "all", true), CancellationToken.None);
        Assert.Equal("Discord on discord.com", updated!.Name);
    }

    [Fact]
    public async Task A_processor_uses_the_machine_name_it_publishes_else_the_host_of_its_address()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var processors = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));

        var published = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "generic-webhook", "http://192.168.1.60:8181/hook", null, null, true) { MachineName = "MEDIA-BOX" },
            CancellationToken.None);
        var fallback = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "fileflows-webhook", "http://ff.lan:5000/hook", null, null, true), CancellationToken.None);
        var local = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("x", "generic-webhook", "http://127.0.0.1:8181/hook", null, null, true), CancellationToken.None);

        Assert.Equal("Processor on MEDIA-BOX", published.Name);
        Assert.Equal("FileFlows on ff.lan", fallback.Name);
        Assert.Equal($"Processor on {Machine}", local.Name);

        // Saving without a new machine name, at the same address, keeps the name it had.
        var saved = await processors.UpdateProcessorConnectionAsync(
            published.Id, new UpdateProcessorConnectionRequest("A different name", null, null, null, null, true), CancellationToken.None);
        Assert.Equal("Processor on MEDIA-BOX", saved!.Name);
    }

    [Fact]
    public async Task A_processor_that_says_its_own_name_is_listed_under_it_and_keeps_it_when_saved_again()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var processors = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));

        var named = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "generic-webhook", "http://192.168.1.60:8181/hook", null, null, true) { MachineName = "MEDIA-BOX", ProductName = "Weir" },
            CancellationToken.None);
        Assert.Equal("Weir on MEDIA-BOX", named.Name);

        // Saved again when it did not say its name this time: the name it was listed under stays.
        var saved = await processors.UpdateProcessorConnectionAsync(
            named.Id, new UpdateProcessorConnectionRequest(null, null, null, null, null, true) { MachineName = "MEDIA-BOX" }, CancellationToken.None);
        Assert.Equal("Weir on MEDIA-BOX", saved!.Name);
    }

    [Theory]
    [InlineData("http://localhost:9117/api/v2.0/indexers/1337x/results/torznab/", "1337x")]
    [InlineData("http://192.168.1.5:9117/api/v2.0/indexers/yts/results/torznab", "yts")]
    [InlineData("http://jackett:9117/api/v2.0/indexers/all/results/torznab/", null)]
    [InlineData("http://localhost:9696/12/api", null)]
    [InlineData("http://prowlarr.lan:9696/7/api/", null)]
    [InlineData("https://api.nzbgeek.info", null)]
    public void An_indexer_behind_jackett_or_prowlarr_is_named_after_its_tracker_not_the_shared_host(string address, string? tracker)
    {
        Assert.Equal(tracker, ConnectionNames.AggregatedTrackerOf(address));
        Assert.Equal(tracker ?? ConnectionNames.SiteName(address), ConnectionNames.SiteName(address));
    }

    [Fact]
    public async Task Indexers_served_by_one_jackett_and_one_prowlarr_each_get_their_own_tracker_name()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteConnectionsRepository(storage.Factory, clock, TestSecretProtection.Create(storage));

        var one = await repository.CreateIndexerAsync(Indexer(null, "http://localhost:9117/api/v2.0/indexers/1337x/results/torznab/"), CancellationToken.None);
        var two = await repository.CreateIndexerAsync(Indexer(null, "http://localhost:9117/api/v2.0/indexers/eztv/results/torznab/"), CancellationToken.None);
        var three = await repository.CreateIndexerAsync(Indexer(null, "http://localhost:9696/4/api"), CancellationToken.None);
        var four = await repository.CreateIndexerAsync(Indexer(null, "http://localhost:9696/9/api"), CancellationToken.None);

        Assert.Equal("1337x", one.Name);
        Assert.Equal("eztv", two.Name);
        Assert.Equal(ConnectionNames.SiteName("http://localhost:9696/4/api"), three.Name);
        Assert.StartsWith(ConnectionNames.SiteName("http://localhost:9696/9/api"), four.Name);
        Assert.NotEqual(three.Name, four.Name);
    }

    [Fact]
    public async Task An_indexer_on_this_computer_is_named_after_its_address_never_the_computers_name()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteConnectionsRepository(storage.Factory, clock, TestSecretProtection.Create(storage));

        var local = await repository.CreateIndexerAsync(Indexer(null, "http://localhost:5000/api"), CancellationToken.None);
        var loopback = await repository.CreateIndexerAsync(Indexer(null, "http://127.0.0.1:5001/api"), CancellationToken.None);

        Assert.Equal("localhost", local.Name);
        Assert.StartsWith("127.0.0.1", loopback.Name);
        Assert.NotEqual(Machine, local.Name);
        Assert.NotEqual(Machine, loopback.Name);
    }

    [Fact]
    public async Task A_name_typed_for_an_indexer_is_kept_through_later_saves_and_a_blank_one_is_named_from_its_address()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteConnectionsRepository(storage.Factory, clock, TestSecretProtection.Create(storage));

        var typed = await repository.CreateIndexerAsync(Indexer("My private tracker", "https://tracker.example.test/api"), CancellationToken.None);
        var blank = await repository.CreateIndexerAsync(Indexer("  ", "https://www.other.example.test/api"), CancellationToken.None);
        // A name sent through the API is kept just the same (#1248).
        var given = await repository.CreateIndexerAsync(Indexer("Given by a client", "https://third.example.test/api"), CancellationToken.None);
        Assert.Equal("Given by a client", given.Name);
        Assert.Equal("My private tracker", typed.Name);
        Assert.Equal("other.example.test", blank.Name);

        // A save that sends the current name back (the form does, every time) changes nothing; another save keeps it.
        var resaved = await repository.UpdateIndexerAsync(typed.Id, new UpdateIndexerRequest("My private tracker", null, null, null, null, 20, null, null, null, null), CancellationToken.None);
        Assert.Equal("My private tracker", resaved!.Name);
        var listed = (await repository.ListIndexersAsync(CancellationToken.None)).Single(item => item.Id == typed.Id);
        Assert.Equal("My private tracker", listed.Name);

        // Typing another name renames it, and that one is kept too.
        var renamed = await repository.UpdateIndexerAsync(typed.Id, new UpdateIndexerRequest("Renamed tracker", null, null, null, null, null, null, null, null, null), CancellationToken.None);
        Assert.Equal("Renamed tracker", renamed!.Name);
        var again = await repository.UpdateIndexerAsync(typed.Id, new UpdateIndexerRequest(null, null, null, null, null, 30, null, null, null, null), CancellationToken.None);
        Assert.Equal("Renamed tracker", again!.Name);
    }

    [Fact]
    public async Task An_import_list_is_named_after_its_provider_and_list()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteIntakeRepository(storage.Factory, clock);

        var list = await repository.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest("Weekend movies", "trakt", "https://trakt.tv/users/me/lists/my-list", "movie", null, null, null, null, null, null, null, null, 6, true, true),
            CancellationToken.None);
        Assert.Equal("Trakt: my-list", list.Name);

        var updated = await repository.UpdateIntakeSourceAsync(
            list.Id,
            new UpdateIntakeSourceRequest("Renamed", "trakt", "https://trakt.tv/users/me/lists/other", "movie", null, null, null, null, null, null, null, null, 6, true, true),
            CancellationToken.None);
        Assert.Equal("Trakt: other", updated!.Name);
    }

    [Fact]
    public async Task The_upgrade_gives_existing_connections_the_name_they_would_get_today()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteConnectionsRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var one = await repository.CreateDownloadClientAsync(Client(null, "qbittorrent", "localhost", 8080), CancellationToken.None);
        var two = await repository.CreateDownloadClientAsync(Client(null, "qbittorrent", "127.0.0.1", 8081), CancellationToken.None);
        var sab = await repository.CreateDownloadClientAsync(Client(null, "sabnzbd", "192.168.1.51", 8085), CancellationToken.None);
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            await using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE download_clients SET name = 'Old typed name ' || id;";
            await stale.ExecuteNonQueryAsync();

            await using var transaction = await connection.BeginTransactionAsync();
            await new V0133ConnectionNamesFollowTheirAddress().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        var names = (await repository.ListDownloadClientsAsync(CancellationToken.None)).ToDictionary(item => item.Id, item => item.Name);
        Assert.Equal($"qBittorrent on {Machine}:8080", names[one.Id]);
        Assert.Equal($"qBittorrent on {Machine}:8081", names[two.Id]);
        Assert.Equal("SABnzbd on 192.168.1.51", names[sab.Id]);
    }

    [Fact]
    public async Task After_the_upgrade_a_tracker_definition_indexer_takes_its_automatic_name_at_start_up_and_it_is_stable()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteConnectionsRepository(
            storage.Factory, clock, TestSecretProtection.Create(storage), new FakeDefinitionNames());
        var tracker = await repository.CreateIndexerAsync(Indexer(null, "https://1337x.to", "1337x"), CancellationToken.None);
        var jackett = await repository.CreateIndexerAsync(Indexer(null, "http://localhost:9117/api/v2.0/indexers/yts/results/torznab/"), CancellationToken.None);
        await LegacyConnectionNames.SetAsync(storage.Factory, "indexer_sources", tracker.Id, "My typed tracker");
        await LegacyConnectionNames.SetAsync(storage.Factory, "indexer_sources", jackett.Id, Machine);

        Assert.Equal(2, await repository.ApplyAutomaticIndexerNamesAsync(CancellationToken.None));

        var names = (await repository.ListIndexersAsync(CancellationToken.None)).ToDictionary(item => item.Id, item => item.Name);
        Assert.Equal("1337x", names[tracker.Id]);
        Assert.Equal("yts", names[jackett.Id]);
        Assert.Equal(0, await repository.ApplyAutomaticIndexerNamesAsync(CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static IndexerItem Aggregated(string baseUrl, string apiKey = "abc")
        => new(
            "id-1", "x", "torznab", "public", baseUrl, apiKey, 10, "2000", "", "both", true, "unknown", null, null, null, null, 0, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Prowlarr_is_asked_for_the_indexers_name_with_its_own_key_and_a_url_base_is_kept()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":3,\"name\":\"NZBgeek\",\"protocol\":\"usenet\"}")
        });
        using var client = new HttpClient(handler);

        var name = await AggregatorIndexerNames.DiscoverAsync(Aggregated("http://media.lan:9696/prowlarr/3/api", "prow-key"), client, CancellationToken.None);

        Assert.Equal("NZBgeek", name);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://media.lan:9696/prowlarr/api/v1/indexer/3", request.RequestUri!.ToString());
        Assert.Equal("prow-key", request.Headers.GetValues("X-Api-Key").Single());

        var plain = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"YTS\"}") });
        using var plainClient = new HttpClient(plain);
        await AggregatorIndexerNames.DiscoverAsync(Aggregated("http://media.lan:9696/9/api"), plainClient, CancellationToken.None);
        Assert.Equal("http://media.lan:9696/api/v1/indexer/9", Assert.Single(plain.Requests).RequestUri!.ToString());
    }

    [Theory]
    [InlineData("Jackett", null)]
    [InlineData("1337x (Jackett)", "1337x (Jackett)")]
    public async Task Jackett_gives_its_trackers_title_from_the_caps_answer_but_never_just_Jackett(string title, string? expected)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($"<caps><server title=\"{title}\" /><searching /></caps>")
        });
        using var client = new HttpClient(handler);

        var name = await AggregatorIndexerNames.DiscoverAsync(
            Aggregated("http://localhost:9117/api/v2.0/indexers/1337x/results/torznab/"), client, CancellationToken.None);

        Assert.Equal(expected, name);
        Assert.Contains("t=caps", Assert.Single(handler.Requests).RequestUri!.Query);
    }


    [Fact]
    public async Task A_redirect_is_no_name_and_is_never_followed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://elsewhere.example/api/v1/indexer/3") }
        });
        using var client = new HttpClient(handler);

        Assert.Null(await AggregatorIndexerNames.DiscoverAsync(Aggregated("http://media.lan:9696/3/api", "prow-key"), client, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_ordinary_newznab_site_with_a_digits_api_path_is_not_taken_for_prowlarr()
    {
        // Not Prowlarr's answer: no name, and the address-derived name is the site's host, never "Prowlarr indexer N".
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[1,2]") }));
        Assert.Null(await AggregatorIndexerNames.DiscoverAsync(Aggregated("https://usenet.example.com/5/api"), client, CancellationToken.None));
        Assert.Equal("usenet.example.com", ConnectionNames.SiteName("https://usenet.example.com/5/api"));
    }

    [Fact]
    public void Only_a_changed_address_or_key_is_worth_asking_the_aggregator_again()
    {
        var before = Aggregated("http://media.lan:9696/3/api", "key");
        Assert.False(AggregatorIndexerNames.LookupNeeded(before, before with { IsEnabled = false, Priority = 50 }));
        Assert.True(AggregatorIndexerNames.LookupNeeded(before, before with { ApiKey = "other" }));
        Assert.True(AggregatorIndexerNames.LookupNeeded(before, before with { BaseUrl = "http://media.lan:9696/4/api" }));
    }
    [Fact]
    public async Task A_failing_or_unreachable_aggregator_leaves_no_name_and_never_throws()
    {
        using var refused = new HttpClient(new StubHandler(_ => throw new HttpRequestException("refused")));
        using var broken = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));
        using var notJson = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html/>") }));
        var item = Aggregated("http://localhost:9696/3/api");

        Assert.Null(await AggregatorIndexerNames.DiscoverAsync(item, refused, CancellationToken.None));
        Assert.Null(await AggregatorIndexerNames.DiscoverAsync(item, broken, CancellationToken.None));
        Assert.Null(await AggregatorIndexerNames.DiscoverAsync(item, notJson, CancellationToken.None));
        Assert.Null(await AggregatorIndexerNames.DiscoverAsync(Aggregated("https://api.nzbgeek.info"), refused, CancellationToken.None));
    }

    [Fact]
    public async Task A_name_the_aggregator_gave_is_kept_by_the_start_up_pass_and_dropped_when_the_address_changes()
    {
        var (storage, clock) = await StartAsync();
        using var _ = storage;
        var repository = new SqliteConnectionsRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var one = await repository.CreateIndexerAsync(Indexer(null, "http://localhost:9696/3/api"), CancellationToken.None);
        Assert.Equal(ConnectionNames.SiteName("http://localhost:9696/3/api"), one.Name);

        var named = await repository.SetDiscoveredIndexerNameAsync(one.Id, "NZBgeek", CancellationToken.None);
        Assert.Equal("NZBgeek", named!.Name);

        // The start-up pass makes no network call and keeps what was learned.
        Assert.Equal(0, await repository.ApplyAutomaticIndexerNamesAsync(CancellationToken.None));
        Assert.Equal("NZBgeek", (await repository.ListIndexersAsync(CancellationToken.None)).Single().Name);

        // Saving again at the same address keeps it; pointing it at another indexer goes back to the address's name.
        var same = await repository.UpdateIndexerAsync(one.Id, new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null), CancellationToken.None);
        Assert.Equal("NZBgeek", same!.Name);
        var moved = await repository.UpdateIndexerAsync(one.Id, new UpdateIndexerRequest(null, null, null, "http://localhost:9696/4/api", null, null, null, null, null, null), CancellationToken.None);
        Assert.Equal(ConnectionNames.SiteName("http://localhost:9696/4/api"), moved!.Name);
        Assert.Equal(0, await repository.ApplyAutomaticIndexerNamesAsync(CancellationToken.None));
    }

    private sealed class FakeDefinitionNames : ITrackerDefinitionNames
    {
        public string? DisplayName(string definitionId) => definitionId == "1337x" ? "1337x" : null;
    }
}
