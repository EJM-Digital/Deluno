using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1222: a download client's success rate counts only sends with a final outcome that is the client's doing, and
/// only recent ones, so an old run of failures ages out and a send that was never the client's fault never counts.
/// </summary>
public sealed class ASuccessRateCountsOnlyRecentFinalOutcomesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");

    private enum Kind
    {
        Imported,
        ClientFailed,
        InFlight,
        RemovedByPerson,
        RemovedByStrike,
        Contained
    }

    [Fact]
    public async Task An_old_run_of_failures_ages_out_of_the_rate()
    {
        using var storage = TestStorage.Create();
        var (store, service) = await CreateAsync(storage);

        // Five failures 40 days ago, then four imports this week.
        for (var i = 0; i < 5; i++)
        {
            await SendAsync(storage, store, "deluge", Kind.ClientFailed, Now.AddDays(-40).AddMinutes(i));
        }

        for (var i = 0; i < 4; i++)
        {
            await SendAsync(storage, store, "deluge", Kind.Imported, Now.AddDays(-3).AddMinutes(i));
        }

        var record = await service.GetDownloadClientRecordAsync("deluge", CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal(4, record!.Counted);
        Assert.Equal(4, record.Imported);
        Assert.Equal(1.0, record.Rate);
        Assert.Equal(30, record.WindowDays);
    }

    [Fact]
    public async Task Only_the_last_twenty_counted_sends_make_the_rate()
    {
        using var storage = TestStorage.Create();
        var (store, service) = await CreateAsync(storage);

        // Ten failures a fortnight ago, then twenty imports since: the failures fall outside the last twenty.
        for (var i = 0; i < 10; i++)
        {
            await SendAsync(storage, store, "deluge", Kind.ClientFailed, Now.AddDays(-14).AddMinutes(i));
        }

        for (var i = 0; i < 20; i++)
        {
            await SendAsync(storage, store, "deluge", Kind.Imported, Now.AddDays(-2).AddMinutes(i));
        }

        var record = await service.GetDownloadClientRecordAsync("deluge", CancellationToken.None);

        Assert.Equal(20, record!.Counted);
        Assert.Equal(1.0, record.Rate);
    }

    [Fact]
    public async Task Sends_still_in_flight_or_removed_by_a_person_or_a_strike_or_contained_never_count()
    {
        using var storage = TestStorage.Create();
        var (store, service) = await CreateAsync(storage);
        await SendAsync(storage, store, "deluge", Kind.Imported, Now.AddDays(-2));
        await SendAsync(storage, store, "deluge", Kind.Imported, Now.AddDays(-2).AddMinutes(1));
        await SendAsync(storage, store, "deluge", Kind.ClientFailed, Now.AddDays(-2).AddMinutes(2));
        foreach (var kind in new[] { Kind.InFlight, Kind.RemovedByPerson, Kind.RemovedByStrike, Kind.Contained })
        {
            for (var i = 0; i < 4; i++)
            {
                await SendAsync(storage, store, "deluge", kind, Now.AddDays(-1).AddMinutes(i));
            }
        }

        var record = await service.GetDownloadClientRecordAsync("deluge", CancellationToken.None);

        // Three counted sends (two imported, one failed in the client); sixteen others are not the client's doing.
        Assert.Equal(3, record!.Counted);
        Assert.Equal(2, record.Imported);
        Assert.Equal(0.6667, record.Rate);
    }

    [Fact]
    public async Task A_client_with_only_uncounted_sends_has_no_record_and_so_is_never_passed_over()
    {
        using var storage = TestStorage.Create();
        var (store, service) = await CreateAsync(storage);
        foreach (var kind in new[] { Kind.InFlight, Kind.RemovedByPerson, Kind.RemovedByStrike, Kind.Contained })
        {
            for (var i = 0; i < 5; i++)
            {
                await SendAsync(storage, store, "transmission", kind, Now.AddHours(-i - 1));
            }
        }

        Assert.Null(await service.GetDownloadClientRecordAsync("transmission", CancellationToken.None));
        Assert.Null(await service.GetDownloadClientSuccessRateAsync("transmission", CancellationToken.None));
    }

    [Fact]
    public async Task The_record_carries_when_the_client_last_had_a_send_for_the_cool_off()
    {
        using var storage = TestStorage.Create();
        var (store, service) = await CreateAsync(storage);
        for (var i = 0; i < 4; i++)
        {
            await SendAsync(storage, store, "deluge", Kind.ClientFailed, Now.AddDays(-5).AddMinutes(i));
        }

        await SendAsync(storage, store, "deluge", Kind.InFlight, Now.AddHours(-2));

        var record = await service.GetDownloadClientRecordAsync("deluge", CancellationToken.None);

        Assert.Equal(0.0, record!.Rate);
        Assert.Equal(Now.AddHours(-2), record.LastSendUtc);
    }

    [Fact]
    public async Task The_indexer_rate_follows_the_same_counting_rules()
    {
        using var storage = TestStorage.Create();
        var (store, service) = await CreateAsync(storage);
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(storage, store, "qbit", Kind.Imported, Now.AddDays(-1).AddMinutes(i));
            await SendAsync(storage, store, "qbit", Kind.RemovedByPerson, Now.AddDays(-1).AddMinutes(i));
            await SendAsync(storage, store, "qbit", Kind.InFlight, Now.AddDays(-1).AddMinutes(i));
        }

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(1.0, snapshot.IndexerSuccessRates["Golden feed"]);
    }

    private static async Task<(SqliteJobStore Store, IntelligentRoutingService Service)> CreateAsync(TestStorage storage)
    {
        var time = new FixedTimeProvider(Now);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var store = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        return (store, new IntelligentRoutingService(storage.Factory, time));
    }

    private static async Task SendAsync(TestStorage storage, SqliteJobStore store, string clientId, Kind kind, DateTimeOffset at)
    {
        var id = await store.RecordDownloadDispatchAsync(
            "library-1", "movie", "movie", "movie-" + Guid.NewGuid().ToString("n"), "Release." + Guid.NewGuid().ToString("n"),
            "Golden feed", clientId, clientId, "sent", null, cancellationToken: CancellationToken.None);

        var (status, grab, importStatus, importCode, grabCode) = kind switch
        {
            Kind.Imported => ("sent", "succeeded", "imported", (string?)null, (string?)null),
            Kind.ClientFailed => ("sent", "succeeded", "failed", "client-reported-failure", (string?)null),
            Kind.InFlight => ("sent", "succeeded", "pending", (string?)null, (string?)null),
            Kind.RemovedByPerson => ("archived", "succeeded", (string?)null, (string?)null, (string?)null),
            Kind.RemovedByStrike => ("sent", "succeeded", "failed", "health-remediation-applied", (string?)null),
            _ => ("archived", "succeeded", (string?)null, "handed-over-elsewhere", (string?)null)
        };

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_dispatches
            SET status = @status, grab_status = @grab, import_status = @importStatus,
                import_failure_code = @importCode, grab_failure_code = @grabCode,
                decision_score = 50, created_utc = @at, grab_attempted_utc = @at,
                import_completed_utc = CASE WHEN @importStatus IN ('imported', 'failed') THEN @at ELSE NULL END
            WHERE id = @id
            """;
        Add(command, "@id", id);
        Add(command, "@status", status);
        Add(command, "@grab", grab);
        Add(command, "@importStatus", importStatus);
        Add(command, "@importCode", importCode);
        Add(command, "@grabCode", grabCode);
        Add(command, "@at", at.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
