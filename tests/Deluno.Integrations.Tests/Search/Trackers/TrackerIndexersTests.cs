using System.Net.Http;
using System.Net.Sockets;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #606: an indexer row whose definition id no longer exists in the catalogue
/// (removed, renamed, or a data folder copied without its trackers directory)
/// must not crash a search, and must show a clear, clearable reason on the
/// Indexers screen and in health rather than a raw exception.
/// </summary>
public sealed class TrackerIndexersTests
{
    private static readonly TrackerDefinitionCatalog Catalog = new(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);

    [Fact]
    public async Task Connecting_an_indexer_whose_definition_is_gone_fails_with_a_plain_english_reason()
    {
        var indexers = new TrackerIndexers(Catalog, engine: null!);
        var indexer = CreateIndexer("indexer-1", "Old Tracker", definitionId: "long-removed-tracker");

        var exception = await Assert.ThrowsAsync<TrackerException>(() => indexers.ConnectAsync(indexer, CancellationToken.None));

        Assert.Contains("not installed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trackers folder", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remove this indexer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Testing_an_indexer_whose_definition_is_gone_reports_degraded_health_instead_of_throwing()
    {
        var indexers = new TrackerIndexers(Catalog, engine: null!);
        var indexer = CreateIndexer("indexer-1", "Old Tracker", definitionId: "long-removed-tracker");

        var (status, message, category) = await indexers.TestAsync(indexer, CancellationToken.None);

        Assert.Equal("degraded", status);
        Assert.Equal("configuration", category);
        Assert.Contains("not installed", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #971: a Cloudflare check turning every mirror away is its own kind of
    /// failure - a Cloudflare helper is needed, not a network fix - and must
    /// never be reported the same way as a genuine connectivity problem.
    /// </summary>
    [Fact]
    public async Task Testing_a_tracker_behind_a_cloudflare_check_reports_blocked_not_connectivity()
    {
        var engine = new Mock<ITrackerEngine>();
        engine.Setup(instance => instance.SearchAsync(It.IsAny<TrackerConnection>(), It.IsAny<TrackerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TrackerException(
                "1337x.to is behind a Cloudflare check that turns automated searches away. A Cloudflare helper (FlareSolverr or Byparr), set in Settings, gets Deluno past it; if one is set, it could not get through either.",
                new TrackerBlockedException("1337x.to is behind a Cloudflare check.")));
        var indexers = new TrackerIndexers(Catalog, engine.Object);
        var indexer = CreateIndexer("indexer-1", "1337x", definitionId: "1337x");

        var (status, message, category) = await indexers.TestAsync(indexer, CancellationToken.None);

        Assert.Equal("degraded", status);
        Assert.Equal("blocked", category);
        Assert.Contains("Cloudflare helper", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A genuinely unreachable tracker (Deluno's own request refused, timed
    /// out, or otherwise never answered) stays "connectivity", never
    /// "blocked" - the two need different fixes (#971).
    /// </summary>
    [Fact]
    public async Task Testing_a_tracker_that_is_genuinely_unreachable_still_reports_connectivity()
    {
        var engine = new Mock<ITrackerEngine>();
        engine.Setup(instance => instance.SearchAsync(It.IsAny<TrackerConnection>(), It.IsAny<TrackerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TrackerException(
                "None of the 2 addresses for 1337x answered. The last: 1337x.st could not be reached: Connection refused",
                new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused))));
        var indexers = new TrackerIndexers(Catalog, engine.Object);
        var indexer = CreateIndexer("indexer-1", "1337x", definitionId: "1337x");

        var (status, message, category) = await indexers.TestAsync(indexer, CancellationToken.None);

        Assert.Equal("unreachable", status);
        Assert.Equal("connectivity", category);
        Assert.Contains("1337x.st", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_indexer_that_never_says_which_tracker_it_is_fails_with_its_own_reason()
    {
        var indexers = new TrackerIndexers(Catalog, engine: null!);
        var indexer = CreateIndexer("indexer-1", "Nameless Tracker", definitionId: null);

        var exception = await Assert.ThrowsAsync<TrackerException>(() => indexers.ConnectAsync(indexer, CancellationToken.None));

        Assert.Contains("does not say which tracker it is", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // #748/#749 -- an indexer's own assigned proxy resolves onto the tracker
    // connection, for the Cloudflare helper's request to carry.

    [Fact]
    public async Task An_indexer_with_an_assigned_http_proxy_resolves_it_onto_the_connection()
    {
        var proxy = new ProxyItem(
            Id: "proxy-1", Name: "Home HTTP", ProxyType: ProxyTypes.Http, Address: "http://192.168.1.1:8080",
            Username: "user", Password: "pass", IsEnabled: true, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);
        var indexers = new TrackerIndexers(Catalog, engine: null!, connections: ConnectionsReturning([proxy]));
        var indexer = CreateIndexer("indexer-1", "1337x", definitionId: "1337x") with { ProxyId = "proxy-1" };

        var connection = await indexers.ConnectAsync(indexer, CancellationToken.None);

        Assert.NotNull(connection.HelperProxy);
        Assert.Equal("http://192.168.1.1:8080", connection.HelperProxy!.Url);
        Assert.Equal("user", connection.HelperProxy.Username);
        Assert.Equal("pass", connection.HelperProxy.Password);
    }

    [Fact]
    public async Task An_indexer_with_no_proxy_assigned_resolves_no_helper_proxy()
    {
        var indexers = new TrackerIndexers(Catalog, engine: null!, connections: ConnectionsReturning([]));
        var indexer = CreateIndexer("indexer-1", "1337x", definitionId: "1337x");

        var connection = await indexers.ConnectAsync(indexer, CancellationToken.None);

        Assert.Null(connection.HelperProxy);
    }

    [Fact]
    public async Task An_indexer_assigned_a_flaresolverr_proxy_resolves_no_helper_proxy()
    {
        // Assigning a FlareSolverr/Byparr instance means "route through this
        // other helper", not "tell the Cloudflare helper to nest one behind it".
        var proxy = new ProxyItem(
            Id: "proxy-1", Name: "Another FlareSolverr", ProxyType: ProxyTypes.FlareSolverr, Address: "http://127.0.0.1:8191",
            Username: null, Password: null, IsEnabled: true, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);
        var indexers = new TrackerIndexers(Catalog, engine: null!, connections: ConnectionsReturning([proxy]));
        var indexer = CreateIndexer("indexer-1", "1337x", definitionId: "1337x") with { ProxyId = "proxy-1" };

        var connection = await indexers.ConnectAsync(indexer, CancellationToken.None);

        Assert.Null(connection.HelperProxy);
    }

    [Fact]
    public async Task An_indexer_assigned_a_disabled_proxy_resolves_no_helper_proxy()
    {
        var proxy = new ProxyItem(
            Id: "proxy-1", Name: "Disabled", ProxyType: ProxyTypes.Http, Address: "http://192.168.1.1:8080",
            Username: null, Password: null, IsEnabled: false, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);
        var indexers = new TrackerIndexers(Catalog, engine: null!, connections: ConnectionsReturning([proxy]));
        var indexer = CreateIndexer("indexer-1", "1337x", definitionId: "1337x") with { ProxyId = "proxy-1" };

        var connection = await indexers.ConnectAsync(indexer, CancellationToken.None);

        Assert.Null(connection.HelperProxy);
    }

    private static IConnectionsRepository ConnectionsReturning(IReadOnlyList<ProxyItem> proxies)
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListProxiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(proxies);
        return repository.Object;
    }

    private static IndexerItem CreateIndexer(string id, string name, string? definitionId)
        => new(
            Id: id,
            Name: name,
            Protocol: "tracker",
            Privacy: "public",
            BaseUrl: string.Empty,
            ApiKey: null,
            Priority: 1,
            Categories: "2000,5000",
            Tags: "",
            MediaScope: "both",
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            ConsecutiveFailures: 0,
            RateLimitedUntilUtc: null,
            DisabledReason: null,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow)
        {
            DefinitionId = definitionId
        };
}
