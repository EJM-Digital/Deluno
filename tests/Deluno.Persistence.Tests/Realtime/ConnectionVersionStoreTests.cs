using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>#1117: the version each connection reported is kept, replaced by the next reading, and never wiped by a read that found nothing.</summary>
public sealed class ConnectionVersionStoreTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-10-03T09:00:00Z"));
    private readonly ConnectionVersionStore _store;

    public ConnectionVersionStoreTests()
    {
        new JobsSchemaInitializer(_storage.Factory, new SqliteDatabaseMigrator(_storage.Factory, _time), NullLogger<JobsSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new ConnectionVersionStore(_storage.Factory, _time);
    }

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task A_version_is_stored_with_its_project_and_time()
    {
        await _store.RecordAsync(ConnectionKinds.DownloadClient, "qb1", "qbittorrent", "4.6.7", CancellationToken.None);

        var row = Assert.Single(await _store.ListAsync(CancellationToken.None));
        Assert.Equal((ConnectionKinds.DownloadClient, "qb1", "qbittorrent", "4.6.7"), (row.Kind, row.Id, row.Project, row.Version));
        Assert.Equal(_time.GetUtcNow(), row.ReadUtc);
    }

    [Fact]
    public async Task The_next_reading_replaces_the_version_and_keeps_the_project()
    {
        await _store.RecordAsync(ConnectionKinds.MediaServer, "pl1", "plex", "1.43.3.10540", CancellationToken.None);
        await _store.RecordAsync(ConnectionKinds.MediaServer, "pl1", null, "1.43.4.10903", CancellationToken.None);

        var row = Assert.Single(await _store.ListAsync(CancellationToken.None));
        Assert.Equal("1.43.4.10903", row.Version);
        Assert.Equal("plex", row.Project);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_read_that_found_nothing_changes_nothing(string? version)
    {
        await _store.RecordAsync(ConnectionKinds.Requests, "se1", "seerr", "3.5.0", CancellationToken.None);
        await _store.RecordAsync(ConnectionKinds.Requests, "se1", "seerr", version, CancellationToken.None);
        await _store.RecordAsync(ConnectionKinds.Requests, "se2", "seerr", version, CancellationToken.None);

        var row = Assert.Single(await _store.ListAsync(CancellationToken.None));
        Assert.Equal("se1", row.Id);
        Assert.Equal("3.5.0", row.Version);
    }

    [Fact]
    public async Task The_same_id_in_two_kinds_is_two_connections()
    {
        await _store.RecordAsync(ConnectionKinds.Indexer, "1", "prowlarr", "2.6.5.5623", CancellationToken.None);
        await _store.RecordAsync(ConnectionKinds.DownloadClient, "1", "sabnzbd", "5.1.3", CancellationToken.None);

        Assert.Equal(2, (await _store.ListAsync(CancellationToken.None)).Count);
    }
}
