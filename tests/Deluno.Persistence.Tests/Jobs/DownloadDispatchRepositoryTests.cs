using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;

namespace Deluno.Persistence.Tests.Jobs;

public sealed class DownloadDispatchRepositoryTests
{
    private static async Task InsertDispatchAsync(
        IDelunoDatabaseConnectionFactory connectionFactory,
        string dispatchId,
        string libraryId,
        string entityId,
        string releaseName,
        DateTimeOffset? createdUtc = null)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            CancellationToken.None);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, created_utc
            ) VALUES (
                @id, @libraryId, 'movie', 'movie', @entityId, @releaseName,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'initial', @createdUtc
            )
            """;

        var idParam = command.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = dispatchId;
        command.Parameters.Add(idParam);

        var libParam = command.CreateParameter();
        libParam.ParameterName = "@libraryId";
        libParam.Value = libraryId;
        command.Parameters.Add(libParam);

        var entityParam = command.CreateParameter();
        entityParam.ParameterName = "@entityId";
        entityParam.Value = entityId;
        command.Parameters.Add(entityParam);

        var nameParam = command.CreateParameter();
        nameParam.ParameterName = "@releaseName";
        nameParam.Value = releaseName;
        command.Parameters.Add(nameParam);

        var createdUtcParam = command.CreateParameter();
        createdUtcParam.ParameterName = "@createdUtc";
        createdUtcParam.Value = (createdUtc ?? DateTimeOffset.UtcNow).ToString("O");
        command.Parameters.Add(createdUtcParam);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// The sharing pass reads this link to find both the site whose rule applies
    /// and the library the release landed in — the second of which decides
    /// whether the download client's copy costs any disk at all (#288). Losing
    /// either makes Deluno charge a hardlinked download full price on the
    /// dashboard, or apply the wrong site's sharing rule to it.
    /// </summary>
    [Fact]
    public async Task The_dispatch_link_carries_the_source_and_the_library_it_was_grabbed_for()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await InsertDispatchAsync(storage.Factory, "dispatch-a", "movies-main", "movie-1", "Sintel.2010.1080p");

        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var link = await store.FindRecentDispatchLinkAsync("qbittorrent-main", "Sintel.2010.1080p", CancellationToken.None);

        Assert.NotNull(link);
        Assert.Equal("dispatch-a", link.DispatchId);
        Assert.Equal("movie", link.EntityType);
        Assert.Equal("movie-1", link.EntityId);
        Assert.Equal("test-indexer", link.IndexerName);
        Assert.Equal("movies-main", link.LibraryId);
    }

    [Fact]
    public async Task QueryDispatches_uses_a_keyset_token_when_a_newer_dispatch_arrives_mid_walk()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var createdUtc = DateTimeOffset.Parse("2026-04-28T04:00:00Z");
        var originalIds = new[] { "dispatch-a", "dispatch-b", "dispatch-c" };
        foreach (var id in originalIds)
        {
            await InsertDispatchAsync(storage.Factory, id, "movies-main", id, id, createdUtc);
        }

        var filter = new DispatchQueryFilter();
        var firstPage = await repository.QueryDispatchesAsync(
            filter,
            new DispatchPaginationOptions { PageSize = 2 },
            CancellationToken.None);

        Assert.NotNull(firstPage.NextPageToken);
        await InsertDispatchAsync(
            storage.Factory,
            "newer-dispatch",
            "movies-main",
            "newer",
            "newer",
            createdUtc.AddMinutes(1));

        var secondPage = await repository.QueryDispatchesAsync(
            filter,
            new DispatchPaginationOptions { PageSize = 2, PageToken = firstPage.NextPageToken },
            CancellationToken.None);

        var walkedIds = firstPage.Items.Concat(secondPage.Items).Select(item => item.Id).ToArray();
        Assert.Equal(originalIds.OrderByDescending(id => id), walkedIds);
        Assert.DoesNotContain("newer-dispatch", walkedIds);
        Assert.Null(secondPage.NextPageToken);
    }

    [Fact]
    public async Task RecordGrab_persists_grab_outcome_with_timeline_event()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);

        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(storage.Factory, dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        // First verify the insert worked
        var beforeGrab = await repository.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.NotNull(beforeGrab);

        var grabResult = await repository.RecordGrabAsync(
            dispatchId: dispatchId,
            grabStatus: "succeeded",
            grabResponseCode: 200,
            grabMessage: "Release grabbed successfully",
            grabFailureCode: null,
            grabResponseJson: """{"item_id":"12345"}""",
            cancellationToken: CancellationToken.None);

        Assert.NotNull(grabResult);
        Assert.Equal("succeeded", grabResult.GrabStatus);
        Assert.NotNull(grabResult.GrabAttemptedUtc);
        Assert.Equal(200, grabResult.GrabResponseCode);
        Assert.Equal("Release grabbed successfully", grabResult.GrabMessage);
        Assert.Null(grabResult.GrabFailureCode);

        // Verify we can retrieve it again
        var retrieved = await repository.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.NotNull(retrieved);
        Assert.Equal("succeeded", retrieved.GrabStatus);

        var timeline = await repository.GetDispatchTimelineAsync(dispatchId, CancellationToken.None);
        // Timeline should have a grab event (could be grab_succeeded or grab_failed based on status)
        Assert.NotEmpty(timeline);
    }

    [Fact]
    public async Task RecordDetection_updates_detected_utc()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);

        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(storage.Factory, dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        await repository.RecordDetectionAsync(
            dispatchId: dispatchId,
            torrentHashOrItemId: "abc123def456",
            downloadedBytes: 4700000000,
            cancellationToken: CancellationToken.None);

        var retrieved = await repository.GetDispatchAsync(dispatchId, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved.DetectedUtc);
        Assert.Equal("abc123def456", retrieved.TorrentHashOrItemId);
        Assert.Equal(4700000000, retrieved.DownloadedBytes);
    }

    [Fact]
    public async Task RecordImportOutcome_persists_import_status_and_path()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);

        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(storage.Factory, dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        var importResult = await repository.RecordImportOutcomeAsync(
            dispatchId: dispatchId,
            importStatus: "imported",
            importedFilePath: "/library/movies/Test Movie (2024)/TestMovie2024.mkv",
            importFailureCode: null,
            importFailureMessage: null,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(importResult);
        Assert.Equal("imported", importResult.ImportStatus);
        Assert.Equal("/library/movies/Test Movie (2024)/TestMovie2024.mkv", importResult.ImportedFilePath);
        Assert.Null(importResult.ImportFailureCode);
        Assert.NotNull(importResult.ImportCompletedUtc);
    }

    [Fact]
    public async Task QueryDispatches_with_filters_returns_matching_items()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);

        var successId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(storage.Factory, successId, "movies-main", "123", "Test.Movie.2024.1080p");

        await repository.RecordGrabAsync(
            dispatchId: successId,
            grabStatus: "succeeded",
            grabResponseCode: 200,
            grabMessage: "ok",
            grabFailureCode: null,
            grabResponseJson: null,
            cancellationToken: CancellationToken.None);

        var failedId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(storage.Factory, failedId, "movies-main", "456", "Another.Movie.2024.1080p");

        await repository.RecordGrabAsync(
            dispatchId: failedId,
            grabStatus: "failed",
            grabResponseCode: 403,
            grabMessage: "release not available",
            grabFailureCode: "not_available",
            grabResponseJson: null,
            cancellationToken: CancellationToken.None);

        var filter = new DispatchQueryFilter { GrabStatus = "succeeded" };
        var pagination = new DispatchPaginationOptions { PageSize = 50, PageToken = null };
        var (results, _) = await repository.QueryDispatchesAsync(filter, pagination, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(successId, results[0].Id);
        Assert.Equal("succeeded", results[0].GrabStatus);
    }

    [Fact]
    public async Task FindUnresolvedDispatches_returns_grabs_not_detected()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);

        var unresolvedId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(storage.Factory, unresolvedId, "movies-main", "123", "Test.Movie.2024.1080p");

        await repository.RecordGrabAsync(
            dispatchId: unresolvedId,
            grabStatus: "succeeded",
            grabResponseCode: 200,
            grabMessage: "ok",
            grabFailureCode: null,
            grabResponseJson: null,
            cancellationToken: CancellationToken.None);

        var resolvedId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(storage.Factory, resolvedId, "movies-main", "456", "Another.Movie.2024.1080p");

        await repository.RecordGrabAsync(
            dispatchId: resolvedId,
            grabStatus: "succeeded",
            grabResponseCode: 200,
            grabMessage: "ok",
            grabFailureCode: null,
            grabResponseJson: null,
            cancellationToken: CancellationToken.None);

        await repository.RecordDetectionAsync(
            dispatchId: resolvedId,
            torrentHashOrItemId: "hash",
            downloadedBytes: 1000000,
            cancellationToken: CancellationToken.None);

        var unresolvedList = await repository.FindUnresolvedDispatchesAsync(
            minAgeMinutes: 0,
            clientId: null,
            limit: 100,
            cancellationToken: CancellationToken.None);

        Assert.Single(unresolvedList);
        Assert.Equal(unresolvedId, unresolvedList[0].Id);
    }

    [Fact]
    public async Task ArchiveDispatch_soft_deletes_dispatch()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);

        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(storage.Factory, dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        var filter = new DispatchQueryFilter();
        var pagination = new DispatchPaginationOptions { PageSize = 50, PageToken = null };
        var (before, _) = await repository.QueryDispatchesAsync(filter, pagination, CancellationToken.None);

        Assert.Single(before);

        await repository.ArchiveDispatchAsync(dispatchId, "test_cleanup", CancellationToken.None);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT archived_utc FROM download_dispatches WHERE id = @id;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = dispatchId;
            command.Parameters.Add(parameter);
            Assert.NotNull(await command.ExecuteScalarAsync(CancellationToken.None));
        }

        var (after, _) = await repository.QueryDispatchesAsync(filter, pagination, CancellationToken.None);

        Assert.Empty(after);
    }
}
