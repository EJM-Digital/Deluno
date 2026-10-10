using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #1215: a send Deluno contains (another copy was handed over) is closed on the server with its reason, so it leaves
/// Activity's In progress and stays out of it when its client entry later goes. Damagedfilm's second send stayed in
/// progress for 45 minutes because the state lived only in the live client reading.
/// </summary>
public sealed class AContainedSendIsClosedForGoodTests
{
    private const string Release = "Damagedfilm.2019.1080p.WEB-DL.x264-GOLDEN";

    [Fact]
    public async Task A_contained_send_is_archived_with_its_reason_and_a_moving_send_is_left_alone()
    {
        using var storage = TestStorage.Create();
        var (jobs, dispatches) = await CreateAsync(storage);
        var contained = await SendAsync(jobs, dispatches, "qb", "hash-contained");
        var moving = await SendAsync(jobs, dispatches, "qb", "hash-moving");

        var telemetry = Overview(Item("qb", "hash-contained", DownloadQueueStatuses.HandedOverElsewhere), Item("qb", "hash-moving", DownloadQueueStatuses.Downloading));
        var closed = await WorkPlanner.CloseContainedSendsAsync(telemetry, dispatches, CancellationToken.None);

        Assert.Equal(1, closed);
        var stored = await dispatches.GetDispatchIncludingArchivedAsync(contained, CancellationToken.None);
        Assert.Equal("archived", stored!.Status);
        Assert.Equal("handed-over-elsewhere", stored.ImportFailureCode);
        Assert.Equal("Not needed: another copy was handed over", stored.ImportFailureMessage);
        Assert.Null(stored.ImportStatus);
        var stillMoving = await dispatches.GetDispatchIncludingArchivedAsync(moving, CancellationToken.None);
        Assert.Equal("sent", stillMoving!.Status);
        Assert.Null(stillMoving.ImportFailureCode);
    }

    [Fact]
    public async Task A_contained_send_stays_closed_after_its_client_entry_is_gone()
    {
        using var storage = TestStorage.Create();
        var (jobs, dispatches) = await CreateAsync(storage);
        var contained = await SendAsync(jobs, dispatches, "qb", "hash-contained");
        await WorkPlanner.CloseContainedSendsAsync(Overview(Item("qb", "hash-contained", DownloadQueueStatuses.HandedOverElsewhere)), dispatches, CancellationToken.None);

        // The copy leaves the client (seeding removal, a person removes it): nothing reads it any more.
        var closedAgain = await WorkPlanner.CloseContainedSendsAsync(Overview(), dispatches, CancellationToken.None);

        Assert.Equal(0, closedAgain);
        var stored = await dispatches.GetDispatchIncludingArchivedAsync(contained, CancellationToken.None);
        Assert.Equal("archived", stored!.Status);
        Assert.Equal("handed-over-elsewhere", stored.ImportFailureCode);
        Assert.Empty(await dispatches.FindOpenDispatchesByHashAsync("qb", "hash-contained", CancellationToken.None));
    }

    [Fact]
    public async Task A_send_that_was_imported_is_never_closed_as_not_needed()
    {
        using var storage = TestStorage.Create();
        var (jobs, dispatches) = await CreateAsync(storage);
        var imported = await SendAsync(jobs, dispatches, "qb", "hash-imported");
        await dispatches.RecordImportOutcomeAsync(imported, "imported", "/library/x.mkv", null, null, CancellationToken.None);

        var closed = await WorkPlanner.CloseContainedSendsAsync(Overview(Item("qb", "hash-imported", DownloadQueueStatuses.HandedOverElsewhere)), dispatches, CancellationToken.None);

        Assert.Equal(0, closed);
        Assert.Equal("sent", (await dispatches.GetDispatchIncludingArchivedAsync(imported, CancellationToken.None))!.Status);
    }

    private static async Task<(SqliteJobStore Jobs, SqliteDownloadDispatchesRepository Dispatches)> CreateAsync(TestStorage storage)
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-10T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        return (new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches), dispatches);
    }

    private static async Task<string> SendAsync(SqliteJobStore jobs, SqliteDownloadDispatchesRepository dispatches, string clientId, string hash)
    {
        var id = await jobs.RecordDownloadDispatchAsync(
            "library-1", "movie", "movie", "movie-1", Release, "Golden feed", clientId, "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(id, hash, null, completed: false, CancellationToken.None);
        return id;
    }

    private static DownloadQueueItem Item(string clientId, string id, string status)
        => new(id, clientId, "qBittorrent", "qbittorrent", "movie", Release, Release, "movies", status,
            50, 1, 60, 1000, 500, 0, "Golden feed", null, DateTimeOffset.UnixEpoch);

    private static DownloadTelemetryOverview Overview(params DownloadQueueItem[] queue)
        => new(
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
            [new DownloadClientTelemetrySnapshot("qb", "qBittorrent", "qbittorrent", null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "qbittorrent"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0), queue, [], DateTimeOffset.UnixEpoch)],
            DateTimeOffset.UnixEpoch);
}
