using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// The Health rows for indexers, media servers, subtitle providers and Seerr (coverage register, engines) read the verdict a connection
/// test stored: healthy is an OK row, anything else a Problem row that names the connection, says why in plain words and says what to
/// do (a saved key that cannot be read, a rate limit with its time, an untested connection); a switched-off connection has no row.
/// </summary>
public sealed class IndexerAndExternalServiceHealthRowsReadTheStoredVerdictTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

    private static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static IndexerItem Indexer(string name, string status, string? message = null, bool enabled = true) =>
        Blank<IndexerItem>() with
        {
            Id = name.ToLowerInvariant(), Name = name, Protocol = "torznab", IsEnabled = enabled, HealthStatus = status,
            LastHealthMessage = message, LastHealthTestUtc = Now.AddHours(-1), CreatedUtc = Now.AddDays(-3)
        };

    private static async Task<IReadOnlyList<HealthCheckOutcome>> RunIndexersAsync(params IndexerItem[] indexers)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(indexers);
        return await new IndexerHealthCheck(connections.Object, new FixedClock(Now)).RunAsync(CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task A_healthy_indexer_is_an_ok_row_and_a_switched_off_one_has_none()
    {
        var outcomes = await RunIndexersAsync(Indexer("YTS", "healthy"), Indexer("Dormant", "unreachable", enabled: false));

        var row = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Ok, row.Status);
        Assert.Equal("indexer:yts", row.Id);
        Assert.Equal("YTS is reachable.", row.Message);
    }

    [Fact]
    public async Task An_indexer_whose_saved_key_cannot_be_read_says_so_and_asks_for_the_key_again()
    {
        var outcomes = await RunIndexersAsync(Indexer("YTS", "unreachable", "No key.") with { SecretUnreadable = true });

        var row = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Problem, row.Status);
        Assert.Equal("YTS: No key.", row.Message);
        Assert.Contains("stored on another computer", row.Cause);
        Assert.Contains("enter its key again and save", row.Fix);
    }

    [Fact]
    public async Task A_rate_limited_indexer_names_the_time_it_is_limited_until()
    {
        var until = DateTimeOffset.UtcNow.AddHours(2);

        var row = Assert.Single(await RunIndexersAsync(Indexer("YTS", "rateLimited") with { RateLimitedUntilUtc = until }));

        Assert.Equal(HealthCheckStatuses.Problem, row.Status);
        Assert.Equal("YTS is not answering.", row.Message);
        Assert.Contains($"rate-limited Deluno until {until:u}", row.Cause);
        Assert.Equal("Open Connections and test YTS.", row.Fix);
    }

    [Fact]
    public async Task An_indexer_that_has_not_been_confirmed_says_so_and_asks_to_be_tested()
    {
        var row = Assert.Single(await RunIndexersAsync(Indexer("YTS", "unknown")));

        Assert.Equal(HealthCheckStatuses.Problem, row.Status);
        Assert.Equal("Deluno could not confirm YTS's last connection test.", row.Cause);
        Assert.Equal("Open Connections and test YTS.", row.Fix);
    }

    [Fact]
    public async Task Media_servers_subtitle_providers_and_Seerr_each_get_a_row_from_their_stored_status_and_disabled_ones_get_none()
    {
        var servers = new Mock<IMediaServerRepository>();
        servers.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Blank<MediaServerConnection>() with { Id = "m1", Name = "Plex Den", IsEnabled = true, HealthStatus = "healthy" },
            Blank<MediaServerConnection>() with { Id = "m2", Name = "Emby Study", IsEnabled = false, HealthStatus = "unreachable" }
        ]);
        var subtitles = new Mock<ISubtitleProviderRepository>();
        subtitles.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Blank<SubtitleProviderConnection>() with { Id = "s1", Name = "OpenSubtitles", IsEnabled = true, HealthStatus = "unreachable", LastHealthMessage = "Login refused." }
        ]);
        var seerr = new Mock<ISeerrRepository>();
        seerr.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Blank<SeerrConnection>() with { Id = "r1", Name = "Seerr", IsEnabled = true, HealthStatus = "unknown" }
        ]);

        var outcomes = await new ExternalServiceHealthCheck(servers.Object, subtitles.Object, seerr.Object).RunAsync(CancellationToken.None);

        Assert.Equal(3, outcomes.Count);
        var plex = outcomes.Single(row => row.Id == "media-server:m1");
        Assert.Equal(HealthCheckStatuses.Ok, plex.Status);
        Assert.Equal("Plex Den is reachable.", plex.Message);
        var subtitle = outcomes.Single(row => row.Id == "subtitle-provider:s1");
        Assert.Equal(HealthCheckCategories.SubtitleProvider, subtitle.Category);
        Assert.Equal(HealthCheckStatuses.Problem, subtitle.Status);
        Assert.Equal("OpenSubtitles: Login refused.", subtitle.Message);
        Assert.Equal("Open Connections and test OpenSubtitles.", subtitle.Fix);
        var seerrRow = outcomes.Single(row => row.Id == "seerr:r1");
        Assert.Equal(HealthCheckCategories.Seerr, seerrRow.Category);
        Assert.Equal("Seerr is not answering.", seerrRow.Message);
        Assert.Equal("Deluno could not confirm Seerr's last test.", seerrRow.Cause);
    }
}
