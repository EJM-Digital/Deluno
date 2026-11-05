using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Microsoft.Extensions.Logging;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>Rules and words that a first round of client-rule tests let slip.</summary>
public sealed class ClientRulesSurvivorsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static DownloadClientItem Client(string name = "My Client", string protocol = "qbittorrent")
        => new("c1", name, protocol, "example.invalid", 8080, null, null, null, false, null, "movies", "tv", null, 1, true, "healthy", null, null, null, Now, Now, Now);

    private static IntegrationFailureKind Unreachable => IntegrationFailureFactory.FromLegacy("download-client", "c1", "x", "y", "unreachable", "m").Kind;

    // ---- category move and pause guard: unreachable is its own kind

    [Fact]
    public async Task A_category_move_to_a_client_that_cannot_answer_is_unreachable_both_before_and_after()
    {
        var before = await ClientCategoryMove.RunAsync(Client(), "h", "seeding", "qBit", () => Task.FromResult(new ClientCategoryReading(ClientHas.Unknown, null)),
            _ => Task.FromResult(new DownloadClientActionResult("c1", "h", "x", true, "ok")));
        var reads = 0;
        var after = await ClientCategoryMove.RunAsync(Client(), "h", "seeding", "qBit",
            () => Task.FromResult(++reads == 1 ? new ClientCategoryReading(ClientHas.Yes, "movies") : new ClientCategoryReading(ClientHas.Unknown, null)),
            _ => Task.FromResult(new DownloadClientActionResult("c1", "h", "x", true, "ok")));

        Assert.Equal(Unreachable, before.Failure!.Kind);
        Assert.Equal(Unreachable, after.Failure!.Kind);
    }

    [Fact]
    public async Task A_pause_on_a_client_that_cannot_answer_is_unreachable_both_before_and_after()
    {
        var send = () => Task.FromResult(new DownloadClientActionResult("c1", "h", "pause", true, "ok"));
        var before = await ClientPauseGuard.RunAsync(Client(), "h", "pause", "qBit", () => Task.FromResult(new ClientPauseReading(ClientHas.Unknown, false)), send);
        var reads = 0;
        var after = await ClientPauseGuard.RunAsync(Client(), "h", "pause", "qBit",
            () => Task.FromResult(++reads == 1 ? new ClientPauseReading(ClientHas.Yes, false) : new ClientPauseReading(ClientHas.Unknown, false)), send,
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(1));

        Assert.Equal(Unreachable, before.Failure!.Kind);
        Assert.Equal(Unreachable, after.Failure!.Kind);
    }

    // ---- pause guard timings

    /// <summary>Runs the guard against a client that never changes, with a delay that records each pause and returns at once, so the
    /// tests add up the pauses instead of timing a real wait (#1506).</summary>
    private static async Task<(int Reads, List<TimeSpan> Pauses)> NeverChanges(TimeSpan window, TimeSpan poll)
    {
        var reads = 0;
        var pauses = new List<TimeSpan>();
        await ClientPauseGuard.RunAsync(Client(), "h", "pause", "qBit",
            () => { reads++; return Task.FromResult(new ClientPauseReading(ClientHas.Yes, false)); },
            () => Task.FromResult(new DownloadClientActionResult("c1", "h", "pause", true, "ok")),
            window, poll,
            delay: (pause, _) => { pauses.Add(pause); return Task.CompletedTask; });
        return (reads, pauses);
    }

    private static TimeSpan Total(IEnumerable<TimeSpan> pauses) => pauses.Aggregate(TimeSpan.Zero, (sum, pause) => sum + pause);

    [Fact]
    public async Task The_wait_lasts_the_window_asked_for_and_polls_often_enough()
    {
        var (reads, pauses) = await NeverChanges(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));

        Assert.Equal(TimeSpan.FromMilliseconds(150), Total(pauses));
        Assert.True(reads >= 5, reads.ToString());
    }

    [Fact]
    public async Task A_poll_longer_than_the_window_never_waits_past_the_window()
    {
        var (_, pauses) = await NeverChanges(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(5));

        Assert.Equal([TimeSpan.FromMilliseconds(30)], pauses);
    }

    [Fact]
    public async Task The_last_poll_is_cut_short_to_what_is_left_of_the_window()
    {
        var (_, pauses) = await NeverChanges(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(150));

        Assert.Equal([TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(50)], pauses);
    }

    [Fact]
    public async Task With_no_window_given_the_default_two_seconds_is_waited()
    {
        var (reads, pauses) = await NeverChanges(default, TimeSpan.FromMilliseconds(20));

        Assert.Equal(ClientPauseGuard.SettleWindow, Total(pauses));
        Assert.True(reads >= 20, reads.ToString());
    }

    // ---- credentials

    [Fact]
    public void A_refused_grab_carries_the_typed_auth_failure_for_that_client()
    {
        var grab = ClientCredentials.GrabFailure(Client(), new DownloadClientGrabRequest("Release", "http://x/y", "movies", "movies", null, null, null), "no");

        Assert.Equal(IntegrationFailureFactory.FromLegacy("download-client", "c1", "My Client", "grab", "auth", "no"), grab.Failure);
    }

    // ---- save folders

    [Fact]
    public void A_bare_drive_letter_is_windows_style()
    {
        Assert.Equal(ClientSaveFolders.PathStyle.Windows, ClientSaveFolders.StyleOf("C:"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Running_in_a_container_is_read_from_the_dotnet_variable(string? value, bool expected)
    {
        var before = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", value);
            Assert.Equal(expected, ClientSaveFolders.RunningInContainer());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", before);
        }
    }

    private static LibraryItem Library(string id, string downloads)
        => ((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with { Id = id, MediaType = "movies", DownloadsPath = downloads };

    [Fact]
    public async Task A_library_routed_among_several_clients_is_routed_to_each_of_them()
    {
        var repo = new Mock<ILibrariesRepository>();
        repo.Setup(r => r.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library("l1", "/data/a")]);
        repo.Setup(r => r.GetLibraryRoutingAsync("l1", It.IsAny<CancellationToken>())).ReturnsAsync(new LibraryRoutingSnapshot("l1", "n", [],
            [new LibraryDownloadClientLinkItem("a", "l1", "other", "o", 1, Now, Now), new LibraryDownloadClientLinkItem("b", "l1", "c1", "c", 2, Now, Now)]));
        var map = new DownloadClientPathMappingItem("m", "c1", "/dl", "/data", true, 1, Now, Now);

        var resolved = await ClientSaveFolders.ResolveAsync(repo.Object, Client(), "movies", "movies", [map], CancellationToken.None);

        Assert.Equal("/dl/a", resolved!.ClientFolder);
    }

    [Fact]
    public async Task A_sibling_folder_that_only_shares_the_start_of_a_mapped_one_is_not_covered()
    {
        var repo = new Mock<ILibrariesRepository>();
        repo.Setup(r => r.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library("l1", "/database/x")]);
        repo.Setup(r => r.GetLibraryRoutingAsync("l1", It.IsAny<CancellationToken>())).ReturnsAsync(new LibraryRoutingSnapshot("l1", "n", [],
            [new LibraryDownloadClientLinkItem("a", "l1", "c1", "c", 1, Now, Now)]));
        var map = new DownloadClientPathMappingItem("m", "c1", "/data", "/data", true, 1, Now, Now);

        var resolved = await ClientSaveFolders.ResolveAsync(repo.Object, Client(), "movies", "movies", [map], CancellationToken.None);

        Assert.Null(resolved!.ClientFolder);
    }

    [Fact]
    public void A_remote_side_with_no_separator_at_all_is_joined_with_a_backslash()
    {
        var map = new DownloadClientPathMappingItem("m", "c1", "downloads", "/data", true, 1, Now, Now);

        Assert.Equal("downloads\\movies", ClientSaveFolders.ToClientPath("/data/movies", [map]));
    }

    // ---- cache schema initializer logs

    private sealed class Lines : ILogger<CacheSchemaInitializer>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add($"{logLevel}: {formatter(state, exception)}");
    }

    [Fact]
    public async Task The_cache_database_says_where_it_is_once_migrated()
    {
        var factory = new Mock<IDelunoDatabaseConnectionFactory>();
        factory.Setup(f => f.GetDatabasePath(DelunoDatabaseNames.Cache)).Returns("/data/cache.db");
        var lines = new Lines();

        await new CacheSchemaInitializer(factory.Object, new Mock<IDelunoDatabaseMigrator>().Object, lines).StartAsync(CancellationToken.None);

        Assert.Equal(["Information: Cache database migrations are current at /data/cache.db."], lines.Messages);
    }
}
