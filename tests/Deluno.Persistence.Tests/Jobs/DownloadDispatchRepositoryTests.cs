using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.DownloadClients;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Jobs;

public sealed class DownloadDispatchRepositoryTests
{
    private static async Task InsertDispatchAsync(
        IDelunoDatabaseConnectionFactory connectionFactory,
        string dispatchId,
        string libraryId,
        string entityId,
        string releaseName,
        DateTimeOffset? createdUtc = null,
        bool replacementAuthorized = false,
        bool forceReplacementAuthorized = false,
        string? replacementExpectedPath = null)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            CancellationToken.None);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status,
                replacement_authorized, force_replacement_authorized, replacement_expected_path, created_utc
            ) VALUES (
                @id, @libraryId, 'movie', 'movie', @entityId, @releaseName,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'initial',
                @replacementAuthorized, @forceReplacementAuthorized, @replacementExpectedPath, @createdUtc
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

        var replacementAuthorizedParam = command.CreateParameter();
        replacementAuthorizedParam.ParameterName = "@replacementAuthorized";
        replacementAuthorizedParam.Value = replacementAuthorized ? 1 : 0;
        command.Parameters.Add(replacementAuthorizedParam);

        var forceReplacementAuthorizedParam = command.CreateParameter();
        forceReplacementAuthorizedParam.ParameterName = "@forceReplacementAuthorized";
        forceReplacementAuthorizedParam.Value = forceReplacementAuthorized ? 1 : 0;
        command.Parameters.Add(forceReplacementAuthorizedParam);

        var replacementExpectedPathParam = command.CreateParameter();
        replacementExpectedPathParam.ParameterName = "@replacementExpectedPath";
        replacementExpectedPathParam.Value = replacementExpectedPath is null ? DBNull.Value : replacementExpectedPath;
        command.Parameters.Add(replacementExpectedPathParam);

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

        await InsertDispatchAsync(
            storage.Factory,
            "dispatch-a",
            "movies-main",
            "movie-1",
            "Sintel.2010.1080p",
            replacementAuthorized: true,
            forceReplacementAuthorized: true,
            replacementExpectedPath: @"C:\Library\Movies\Sintel (2010)\Sintel.mkv");

        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var link = await store.FindRecentDispatchLinkAsync("qbittorrent-main", "Sintel.2010.1080p", CancellationToken.None);

        Assert.NotNull(link);
        Assert.Equal("dispatch-a", link.DispatchId);
        Assert.Equal("movie", link.EntityType);
        Assert.Equal("movie-1", link.EntityId);
        Assert.Equal("test-indexer", link.IndexerName);
        Assert.Equal("movies-main", link.LibraryId);
        Assert.True(link.ReplacementAuthorized);
        Assert.True(link.ForceReplacementAuthorized);
        Assert.Equal(@"C:\Library\Movies\Sintel (2010)\Sintel.mkv", link.ReplacementExpectedPath);
    }

    [Fact]
    public async Task Ordinary_and_legacy_dispatches_cannot_authorize_an_overwrite()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await InsertDispatchAsync(storage.Factory, "dispatch-first", "movies-main", "movie-1", "Sintel.2010.1080p");

        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var link = await store.FindRecentDispatchLinkAsync("qbittorrent-main", "Sintel.2010.1080p", CancellationToken.None);

        Assert.NotNull(link);
        Assert.False(link.ReplacementAuthorized);
        Assert.False(link.ForceReplacementAuthorized);
        Assert.Null(link.ReplacementExpectedPath);
    }

    [Fact]
    public async Task Force_authority_is_discarded_without_same_title_replacement_authority()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        await store.RecordDownloadDispatchAsync(
            "movies-main",
            "movies",
            "movie",
            "movie-1",
            "Sintel.2010.1080p",
            "test-indexer",
            "qbittorrent-main",
            "qBittorrent",
            "sent",
            null,
            replacementAuthorized: false,
            forceReplacementAuthorized: true);

        var link = await store.FindRecentDispatchLinkAsync("qbittorrent-main", "Sintel.2010.1080p", CancellationToken.None);

        Assert.NotNull(link);
        Assert.False(link.ReplacementAuthorized);
        Assert.False(link.ForceReplacementAuthorized);
        Assert.Null(link.ReplacementExpectedPath);
    }

    [Fact]
    public async Task Episode_scoped_replacement_manifest_survives_dispatch_persistence_in_canonical_order()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        await store.RecordDownloadDispatchAsync(
            "series-main",
            "tv",
            "season",
            "series-1:season:1",
            "Example.Show.S01.2160p",
            "test-indexer",
            "sab-main",
            "SABnzbd",
            "sent",
            null,
            replacementAuthorized: true,
            replacementTargets:
            [
                new DispatchReplacementTarget("episode-2", @"D:\TV\Example\S01E02.mkv"),
                new DispatchReplacementTarget("episode-1", @"D:\TV\Example\S01E01.mkv")
            ]);

        var link = await store.FindRecentDispatchLinkAsync("sab-main", "Example.Show.S01.2160p", CancellationToken.None);

        Assert.NotNull(link);
        Assert.True(link.ReplacementAuthorized);
        Assert.Null(link.ReplacementExpectedPath);
        var persistedTargets = Assert.IsAssignableFrom<IReadOnlyList<DispatchReplacementTarget>>(link.ReplacementTargets);
        Assert.Equal(
            ["episode-1", "episode-2"],
            persistedTargets.Select(target => target.EntityId).ToArray());
        Assert.Equal(@"D:\TV\Example\S01E01.mkv", persistedTargets[0].ExpectedPath);
    }

    /// <summary>
    /// The word a finished import is stored under, pinned.
    ///
    /// Every writer has always stored <c>imported</c>; three readers asked for
    /// <c>completed</c> and so matched nothing. The archive sweep therefore
    /// archived nothing and every imported dispatch stayed in the active set
    /// for good, and the dispatch metrics served zero successful imports —
    /// both individually plausible, neither ever compared. This is the
    /// comparison.
    /// </summary>
    [Fact]
    public async Task A_finished_import_is_stored_under_one_word_whichever_one_the_caller_used()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        await InsertDispatchAsync(storage.Factory, "dispatch-a", "movies-main", "movie-1", "Sintel.2010.1080p");
        await InsertDispatchAsync(storage.Factory, "dispatch-b", "movies-main", "movie-2", "Tears.2011.1080p");

        await repository.RecordImportOutcomeAsync("dispatch-a", "imported", @"C:\Library\Movies\Sintel.mkv", null, null, CancellationToken.None);
        // The other word, which nothing writes any more but an older caller might.
        await repository.RecordImportOutcomeAsync("dispatch-b", "completed", @"C:\Library\Movies\Tears.mkv", null, null, CancellationToken.None);

        var a = await repository.GetDispatchAsync("dispatch-a", CancellationToken.None);
        var b = await repository.GetDispatchAsync("dispatch-b", CancellationToken.None);

        Assert.Equal("imported", a!.ImportStatus);
        Assert.Equal("imported", b!.ImportStatus);
    }

    /// <summary>
    /// The dispatch metrics, read through the production query rather than a
    /// copy of it.
    ///
    /// This is the consequence that mattered. Because the readers asked for a
    /// word nothing wrote, <c>SuccessfulImports</c> served zero for every
    /// install that ever ran, and the sweep that retires a finished dispatch
    /// selected nothing — so every imported dispatch stayed in the working set
    /// the Transfers list, the metrics and the routing statistics all read.
    /// </summary>
    [Fact]
    public async Task A_finished_import_counts_as_a_successful_import()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        await InsertDispatchAsync(storage.Factory, "dispatch-a", "movies-main", "movie-1", "Sintel.2010.1080p");
        await InsertDispatchAsync(storage.Factory, "dispatch-b", "movies-main", "movie-2", "Tears.2011.1080p");
        await repository.RecordImportOutcomeAsync("dispatch-a", "imported", @"C:\Library\Movies\Sintel.mkv", null, null, CancellationToken.None);
        await repository.RecordImportOutcomeAsync("dispatch-b", "failed", null, "import-failed", "no matching title", CancellationToken.None);

        var metrics = await new SqliteDispatchMetricsRepository(storage.Factory, timeProvider)
            .GetMetricsAsync(CancellationToken.None);

        Assert.Equal(1, metrics.SuccessfulImports);
        Assert.Equal(1, metrics.FailedImports);
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
    public async Task RecordGrab_persists_the_client_external_id_before_polling()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(storage.Factory, dispatchId, "tv-main", "series-1", "Example.Show.S01E01");

        var recorded = await repository.RecordGrabAsync(
            dispatchId,
            "succeeded",
            200,
            "Release URL sent to SABnzbd.",
            null,
            "{\"status\":true,\"nzo_ids\":[\"native-sab-id-42\"]}",
            CancellationToken.None,
            externalId: "native-sab-id-42");

        Assert.Equal("native-sab-id-42", recorded.TorrentHashOrItemId);

        var nativeHistory = new DownloadClientHistoryItem(
            "native-sab-id-42",
            "sab-client",
            "SABnzbd",
            "sabnzbd",
            "tv",
            "Example Show S01E01",
            "Example.Show.S01E01",
            "tv",
            DownloadQueueStatuses.Completed,
            "SABnzbd",
            123,
            timeProvider.GetUtcNow(),
            null,
            HistorySource: "native",
            ExternalId: "native-sab-id-42");
        var snapshot = new DownloadClientTelemetrySnapshot(
            "sab-client",
            "SABnzbd",
            "sabnzbd",
            "http://sabnzbd.test",
            "healthy",
            null,
            new(true, true, true, true, false, true, "api-key"),
            new(0, 0, 1, 0, 0, 0, 0),
            [],
            [nativeHistory],
            timeProvider.GetUtcNow());

        var merged = DownloadClientTelemetryService.EnrichWithDispatchHistory(
            snapshot,
            [recorded with { DownloadClientId = "sab-client", DownloadClientName = "SABnzbd" }],
            timeProvider.GetUtcNow());

        Assert.Single(merged.History);
        Assert.Equal("native", merged.History[0].HistorySource);
        Assert.Equal("native-sab-id-42", merged.History[0].ExternalId);
    }

    [Fact]
    public async Task ReadDispatch_exposes_typed_grab_failure_and_keeps_legacy_rows_explainable()
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

        var failure = IntegrationFailureFactory.FromLegacy(
            "download-client",
            "qbittorrent-main",
            "qBittorrent",
            "grab",
            "auth",
            "The API key was rejected.",
            httpStatus: 401,
            code: "unauthorized");

        await repository.RecordGrabAsync(
            dispatchId,
            "failed",
            401,
            failure.Message,
            failure.Code,
            grabResponseJson: null,
            cancellationToken: CancellationToken.None,
            failure: failure);

        var retrieved = await repository.GetDispatchAsync(dispatchId, CancellationToken.None);

        Assert.NotNull(retrieved?.Failure);
        Assert.Equal(IntegrationFailureKind.Authentication, retrieved!.Failure!.Kind);
        Assert.Equal("qBittorrent", retrieved.Failure.ServiceName);
        Assert.Equal("unauthorized", retrieved.Failure.Code);
        Assert.Equal(401, retrieved.Failure.HttpStatus);
    }

    [Fact]
    public async Task ReadDispatch_derives_a_typed_failure_from_legacy_grab_columns()
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

        await repository.RecordGrabAsync(
            dispatchId,
            "failed",
            503,
            "The service is unavailable.",
            "unavailable",
            null,
            CancellationToken.None);

        var retrieved = await repository.GetDispatchAsync(dispatchId, CancellationToken.None);

        Assert.NotNull(retrieved?.Failure);
        Assert.Equal(IntegrationFailureKind.Unavailable, retrieved!.Failure!.Kind);
        Assert.Equal("unavailable", retrieved.Failure.Code);
        Assert.Equal(503, retrieved.Failure.HttpStatus);
    }

    [Fact]
    public async Task ReadDispatch_prefers_terminal_import_failure_over_stale_grab_failure_json()
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

        var grabFailure = IntegrationFailureFactory.FromLegacy(
            "download-client",
            "qbittorrent-main",
            "qBittorrent",
            "grab",
            "auth",
            "The API key was rejected.",
            httpStatus: 401,
            code: "unauthorized");
        await repository.RecordGrabAsync(
            dispatchId,
            "failed",
            401,
            grabFailure.Message,
            grabFailure.Code,
            JsonSerializer.Serialize(new { failure = grabFailure }),
            CancellationToken.None);

        await repository.RecordImportOutcomeAsync(
            dispatchId,
            "failed",
            null,
            "import-no-match",
            "The cleaned file did not match a catalogue item.",
            CancellationToken.None);

        var retrieved = await repository.GetDispatchAsync(dispatchId, CancellationToken.None);

        Assert.NotNull(retrieved?.Failure);
        Assert.Equal("Deluno import", retrieved!.Failure!.ServiceName);
        Assert.Equal("import-no-match", retrieved.Failure.Code);
        Assert.Equal(IntegrationFailureKind.RejectedAction, retrieved.Failure.Kind);
    }

    [Fact]
    public async Task RecordImportOutcome_persists_the_typed_client_failure_for_restart_safe_history()
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

        var failure = IntegrationFailureFactory.FromLegacy(
            "download-client",
            "sabnzbd-main",
            "SABnzbd",
            "download",
            "failed",
            "The download client rejected the job.",
            code: "client-reported-failure",
            externalId: "sab-job-42");

        await repository.RecordImportOutcomeAsync(
            dispatchId,
            "failed",
            null,
            failure.Code,
            failure.Message,
            CancellationToken.None,
            failure);

        var retrieved = await repository.GetDispatchAsync(dispatchId, CancellationToken.None);

        Assert.NotNull(retrieved?.Failure);
        Assert.Equal(failure, retrieved!.Failure);
        Assert.Equal("sab-job-42", retrieved.Failure.ExternalId);
        Assert.Equal(IntegrationFailureKind.RejectedAction, retrieved.Failure.Kind);
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
