using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1098: Deluno using a connection (a title handed to a processor, a media server refresh sent, a Seerr
/// request received, a subtitle fetched, a list checked) is pushed as <c>ConnectionUsed</c>, with what the
/// connection has done since the person's midnight and (for a processor) how many titles it has right now.
/// </summary>
public sealed class LiveConnectionUsedTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-10-01T09:00:00Z"));
    private readonly RecordingRealtimeEventPublisher _realtime = new();
    private readonly ConnectionUseReporter _reporter;

    public LiveConnectionUsedTests()
    {
        var migrator = new SqliteDatabaseMigrator(_storage.Factory, _time);
        new JobsSchemaInitializer(_storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        new PlatformSchemaInitializer(_storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _reporter = new ConnectionUseReporter(_storage.Factory, new ConnectionDayStore(_storage.Factory), _time, _realtime);
    }

    public void Dispose() => _storage.Dispose();

    private IReadOnlyList<ConnectionUsedEvent> Pushed() => _realtime.LiveEventsOf<ConnectionUsedEvent>(LiveEventNames.ConnectionUsed);

    private async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = await _storage.Factory.OpenConnectionAsync(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> AddProcessorAsync(string name)
    {
        var id = Guid.NewGuid().ToString("N");
        await ExecuteAsync(DelunoDatabaseNames.Platform,
            $"INSERT INTO processor_connections (id, name, provider, submission_url, created_utc, updated_utc) VALUES ('{id}', '{name}', 'generic', 'http://p', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');");
        return id;
    }

    [Fact]
    public async Task A_media_server_refresh_is_recorded_and_pushed_with_a_count_that_grows_each_time()
    {
        await _reporter.ReportAsync(ConnectionKinds.MediaServer, "ms1", "Plex", CancellationToken.None);
        await _reporter.ReportAsync(ConnectionKinds.MediaServer, "ms1", "Plex", CancellationToken.None);
        await _reporter.ReportAsync(ConnectionKinds.MediaServer, "ms2", "Jellyfin", CancellationToken.None);

        var pushed = Pushed();
        Assert.Equal([1, 2, 1], pushed.Select(item => item.CountToday).ToArray());
        Assert.Equal(["Plex", "Plex", "Jellyfin"], pushed.Select(item => item.Name).ToArray());
        Assert.All(pushed, item =>
        {
            Assert.Equal(ConnectionKinds.MediaServer, item.Kind);
            Assert.Equal(0, item.Busy);
            Assert.Equal(_time.GetUtcNow().UtcDateTime.ToString("O"), item.AtUtc);
        });
        Assert.Equal("ms1", pushed[0].Id);
    }

    [Fact]
    public async Task A_subtitle_fetch_counts_per_provider()
    {
        await _reporter.ReportAsync(ConnectionKinds.Subtitles, "os", "OpenSubtitles", CancellationToken.None);
        await _reporter.ReportAsync(ConnectionKinds.Subtitles, "os", "OpenSubtitles", CancellationToken.None);

        var last = Pushed().Last();
        Assert.Equal(ConnectionKinds.Subtitles, last.Kind);
        Assert.Equal(2, last.CountToday);
    }

    [Fact]
    public async Task A_use_from_before_the_persons_midnight_is_not_counted_today()
    {
        await ExecuteAsync(DelunoDatabaseNames.Jobs,
            "INSERT INTO connection_uses (id, kind, connection_id, used_utc) VALUES ('old', 'mediaServer', 'ms1', '2026-09-20T09:00:00Z');");

        await _reporter.ReportAsync(ConnectionKinds.MediaServer, "ms1", "Plex", CancellationToken.None);

        Assert.Equal(1, Assert.Single(Pushed()).CountToday);
    }

    [Fact]
    public async Task Checking_a_list_pushes_how_many_lists_that_provider_is_watching_not_a_day_count()
    {
        await ExecuteAsync(DelunoDatabaseNames.Platform,
            """
            INSERT INTO intake_sources (id, name, provider, feed_url, media_type, is_enabled, created_utc, updated_utc) VALUES
              ('l1', 'Trending', 'trakt-trending', 'x', 'movies', 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'),
              ('l2', 'My watchlist', 'trakt', 'x', 'movies', 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'),
              ('l3', 'Switched off', 'trakt', 'x', 'movies', 0, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'),
              ('l4', 'TMDb list', 'tmdb', 'x', 'movies', 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            """);

        await _reporter.ReportAsync(ConnectionKinds.Lists, ListProviderFamilies.KeyFor("trakt-trending"), ListProviderFamilies.NameFor("trakt-trending"), CancellationToken.None);
        await _reporter.ReportAsync(ConnectionKinds.Lists, ListProviderFamilies.KeyFor("trakt-trending"), ListProviderFamilies.NameFor("trakt-trending"), CancellationToken.None);

        var pushed = Pushed();
        Assert.All(pushed, item =>
        {
            Assert.Equal("trakt", item.Id);
            Assert.Equal("Trakt", item.Name);
            Assert.Equal(2, item.CountToday);
        });
        Assert.Equal(2, pushed.Count);
    }

    [Fact]
    public async Task A_title_handed_to_a_processor_pushes_its_count_and_how_many_it_has_right_now_then_clears_when_it_finishes()
    {
        var weir = await AddProcessorAsync("Weir");
        var other = await AddProcessorAsync("FileFlows");
        var processors = new SqliteProcessorRepository(_storage.Factory, _time, TestSecretProtection.Create(_storage), _realtime, _reporter);

        var first = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("lib", "movies", "c1", "q1", "Dune.2024", "/downloads/Dune.2024", "Weir", ProcessorConnectionId: weir),
            CancellationToken.None);
        await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("lib", "movies", "c1", "q2", "Arrival.2016", "/downloads/Arrival.2016", "Weir", ProcessorConnectionId: weir),
            CancellationToken.None);
        await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("lib", "movies", "c1", "q3", "Heat.1995", "/downloads/Heat.1995", "FileFlows", ProcessorConnectionId: other),
            CancellationToken.None);

        var afterThree = Pushed();
        Assert.Equal([(1, 1), (2, 2), (1, 1)], afterThree.Select(item => (item.CountToday, item.Busy)).ToArray());
        Assert.Equal([weir, weir, other], afterThree.Select(item => item.Id).ToArray());
        Assert.Equal(["Weir", "Weir", "FileFlows"], afterThree.Select(item => item.Name).ToArray());
        Assert.All(afterThree, item => Assert.Equal(ConnectionKinds.Processor, item.Kind));

        // One finishes: the count of what it was handed today stays, what it has right now drops.
        await processors.UpdateProcessorHandoffAsync(first.Id, ProcessorHandoffStatuses.Completed, null, null, null, CancellationToken.None);

        var done = Pushed().Last();
        Assert.Equal(weir, done.Id);
        Assert.Equal(2, done.CountToday);
        Assert.Equal(1, done.Busy);
    }

    [Fact]
    public async Task A_seerr_request_received_pushes_the_servers_count_for_today()
    {
        var acquisitions = new SqliteSeerrAcquisitionsRepository(_storage.Factory, _time, _reporter);

        await acquisitions.RecordAsync("seerr1", "Seerr", "movie", "m1", "Sintel", 11, "added", null, "james", CancellationToken.None);
        await acquisitions.RecordAsync("seerr1", "Seerr", "movie", "m2", "Moon", 12, "added", null, "ben", CancellationToken.None);

        var pushed = Pushed();
        Assert.Equal([1, 2], pushed.Select(item => item.CountToday).ToArray());
        Assert.All(pushed, item =>
        {
            Assert.Equal(ConnectionKinds.Requests, item.Kind);
            Assert.Equal("seerr1", item.Id);
            Assert.Equal("Seerr", item.Name);
        });
    }

    [Fact]
    public async Task A_use_that_cannot_be_recorded_never_fails_the_thing_it_reports()
    {
        var broken = new ConnectionUseReporter(new BrokenFactory(), new ConnectionDayStore(_storage.Factory), _time, _realtime);

        await broken.ReportAsync(ConnectionKinds.MediaServer, "ms1", "Plex", CancellationToken.None);

        Assert.Empty(Pushed());
    }

    private sealed class BrokenFactory : IDelunoDatabaseConnectionFactory
    {
        public string GetDatabasePath(string databaseName) => throw new InvalidOperationException("The database is gone.");

        public ValueTask<System.Data.Common.DbConnection> OpenConnectionAsync(string databaseName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The database is gone.");

        public ValueTask<System.Data.Common.DbConnection> OpenReadOnlyConnectionAsync(string databaseName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The database is gone.");

        public long GetWriteGeneration(string databaseName) => 0;
    }
}
