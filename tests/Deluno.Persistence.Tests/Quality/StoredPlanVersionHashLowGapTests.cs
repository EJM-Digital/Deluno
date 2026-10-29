using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// A stored media plan version is trusted only while its content still matches
/// the hash written with it (#1329): a row edited outside Deluno is refused, not read.
/// </summary>
public sealed class StoredPlanVersionHashLowGapTests
{
    [Fact]
    public async Task A_version_row_edited_outside_Deluno_is_refused_when_read_and_an_untouched_one_is_read_back()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T03:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteQualityRepository(storage.Factory, clock);
        var plan = await repository.CreatePolicySetAsync(
            new CreatePolicySetRequest("Original name", "movies", null, null, null, null, null, true, true, null),
            CancellationToken.None);

        var intact = await repository.GetMediaPlanVersionAsync(plan.Id, 1, CancellationToken.None);
        Assert.Equal("Original name", intact!.Snapshot.Name);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE media_plan_versions SET snapshot_json = replace(snapshot_json, 'Original name', 'Edited by hand');";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var read = await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetMediaPlanVersionAsync(plan.Id, 1, CancellationToken.None));
        Assert.Contains("version 1 has an invalid content hash", read.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ListMediaPlanVersionsAsync(plan.Id, CancellationToken.None));
    }
}
