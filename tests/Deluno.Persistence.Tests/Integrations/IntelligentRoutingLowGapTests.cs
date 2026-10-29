using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>Low-severity rows (#1329) of the routing record a download client earns from its own sends, on real SQLite databases.</summary>
public sealed class IntelligentRoutingLowGapTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");

    private static async Task<(SqliteJobStore Store, IntelligentRoutingService Service, FixedTimeProvider Time)> CreateAsync(TestStorage storage)
    {
        var time = new FixedTimeProvider(Now);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var store = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        return (store, new IntelligentRoutingService(storage.Factory, time), time);
    }

    /// <summary>One send that reached a final outcome that is the client's doing: imported, or failed in the client.</summary>
    private static async Task SendAsync(TestStorage storage, SqliteJobStore store, string clientId, bool imported, DateTimeOffset at)
    {
        var id = await store.RecordDownloadDispatchAsync(
            "library-1", "movie", "movie", "movie-" + Guid.NewGuid().ToString("n"), "Release." + Guid.NewGuid().ToString("n"),
            "Golden feed", clientId, clientId, "sent", null, cancellationToken: CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_dispatches
            SET status = 'sent', grab_status = 'succeeded', import_status = @importStatus,
                import_failure_code = @importCode, decision_score = 50, created_utc = @at, grab_attempted_utc = @at,
                import_completed_utc = @at
            WHERE id = @id
            """;
        Add(command, "@id", id);
        Add(command, "@importStatus", imported ? "imported" : "failed");
        Add(command, "@importCode", imported ? null : "client-reported-failure");
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

    [Fact]
    public async Task A_client_with_fewer_than_three_counted_sends_has_no_record_at_all_and_the_third_one_gives_it_one()
    {
        using var storage = TestStorage.Create();
        var (store, service, time) = await CreateAsync(storage);

        await SendAsync(storage, store, "deluge", imported: false, Now.AddDays(-2));
        await SendAsync(storage, store, "deluge", imported: false, Now.AddDays(-2).AddMinutes(1));

        Assert.Null(await service.GetDownloadClientRecordAsync("deluge", CancellationToken.None));
        Assert.Null(await service.GetDownloadClientSuccessRateAsync("deluge", CancellationToken.None));

        await SendAsync(storage, store, "deluge", imported: true, Now.AddDays(-2).AddMinutes(2));
        time.Advance(TimeSpan.FromMinutes(10));

        var record = await service.GetDownloadClientRecordAsync("deluge", CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(3, record!.Counted);
    }

    [Fact]
    public async Task The_snapshot_is_kept_for_ten_minutes_and_worked_out_again_after_that()
    {
        using var storage = TestStorage.Create();
        var (store, service, time) = await CreateAsync(storage);
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(storage, store, "deluge", imported: true, Now.AddDays(-2).AddMinutes(i));
        }

        var first = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(3, first.DownloadClientRecords!["deluge"].Counted);

        await SendAsync(storage, store, "deluge", imported: false, Now.AddDays(-1));
        time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Same(first, await service.GetSnapshotAsync(CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(2));
        var later = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.NotSame(first, later);
        Assert.Equal(4, later.DownloadClientRecords!["deluge"].Counted);
    }

    [Fact]
    public async Task Anomalies_answer_a_count_of_zero_and_an_empty_list_on_a_quiet_install()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var body = await app.Client.GetFromJsonAsync<JsonElement>("/api/intelligent-routing/anomalies");

        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("anomalies").ValueKind);
        Assert.Equal(0, body.GetProperty("anomalies").GetArrayLength());
    }

    [Fact]
    public async Task Preferences_come_from_imported_sends_only_the_most_imported_quality_the_average_format_score_and_the_top_five_release_groups()
    {
        using var storage = TestStorage.Create();
        var (store, service, _) = await CreateAsync(storage);
        // (quality, score, group, imported): WEB 1080p imported 3 times, Bluray 2160p twice, both failed ones must not count.
        (string Quality, int Score, string Group, bool Imported)[] sends =
        [
            ("WEB 1080p", 10, "ALPHA", true), ("WEB 1080p", 20, "ALPHA", true), ("WEB 1080p", 30, "ALPHA", true),
            ("Bluray 2160p", 40, "BRAVO", true), ("Bluray 2160p", 50, "BRAVO", true),
            ("DVD", 900, "ZULU", false), ("DVD", 900, "ZULU", false), ("DVD", 900, "ZULU", false), ("DVD", 900, "ZULU", false),
            ("HDTV 720p", 0, "CHARLIE", true), ("SDTV", 0, "DELTA", true), ("WEB 720p", 0, "ECHO", true), ("DVD-R", 0, "FOXTROT", true)
        ];
        foreach (var (quality, score, group, imported) in sends)
        {
            await SendAsync(storage, store, "deluge", imported, Now.AddDays(-1));
            await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE download_dispatches SET decision_quality = @q, decision_custom_format_score = @s, decision_release_group = @g WHERE id = (SELECT id FROM download_dispatches ORDER BY created_utc DESC, rowid DESC LIMIT 1)";
            Add(command, "@q", quality);
            Add(command, "@s", score);
            Add(command, "@g", group);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var preferences = (await service.GetSnapshotAsync(CancellationToken.None)).Preferences;

        Assert.Equal("WEB 1080p", preferences.PreferredQuality);
        // Imported scores: 10+20+30+40+50 and four zeroes, over nine sends; the failed 900s are left out.
        Assert.Equal(Math.Round(150 / 9.0, 2), preferences.AverageCustomFormatScore);
        Assert.Equal(5, preferences.PreferredReleaseGroups.Count);
        Assert.Equal(["ALPHA", "BRAVO"], preferences.PreferredReleaseGroups.Take(2));
        Assert.DoesNotContain("ZULU", preferences.PreferredReleaseGroups);
    }
}
