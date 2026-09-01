using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Infrastructure.Storage;
using Deluno.Contracts;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

public sealed class MediaStateRepositoryTests
{
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Shared_store_preserves_wanted_state_behaviour_for_both_media_kinds(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var now = new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        await InitializeSchemaAsync(storage, timeProvider, kind);

        var mediaId = await AddMediaAsync(storage, timeProvider, kind);
        var repository = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        await repository.EnsureWantedStateAsync(
            kind,
            mediaId,
            "main",
            "missing",
            "No accepted file exists.",
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        var summary = await repository.GetWantedSummaryAsync(kind, CancellationToken.None);
        var item = Assert.Single(summary.RecentItems);
        Assert.Equal(1, summary.TotalWanted);
        Assert.Equal(mediaId, item.Id);
        Assert.Equal("missing", item.WantedStatus);

        Assert.Single(await repository.ListEligibleWantedAsync(
            kind,
            "main",
            take: 10,
            now,
            ignoreRetryWindow: false,
            CancellationToken.None));

        var deferredUntil = now.AddHours(4);
        Assert.True(await repository.DeferWantedSearchAsync(
            kind,
            mediaId,
            "main",
            deferredUntil,
            CancellationToken.None));
        Assert.Empty(await repository.ListEligibleWantedAsync(
            kind,
            "main",
            take: 10,
            now,
            ignoreRetryWindow: false,
            CancellationToken.None));

        Assert.True(await repository.SkipNextWantedSearchAsync(
            kind,
            mediaId,
            "main",
            CancellationToken.None));
        Assert.True(await repository.ConsumeSkipNextWantedSearchAsync(
            kind,
            mediaId,
            "main",
            CancellationToken.None));
        Assert.False(await repository.ConsumeSkipNextWantedSearchAsync(
            kind,
            mediaId,
            "main",
            CancellationToken.None));

        var metrics = await repository.GetDailyMetricsAsync(
            kind,
            DateOnly.FromDateTime(now.UtcDateTime),
            DateOnly.FromDateTime(now.UtcDateTime),
            CancellationToken.None);
        Assert.Equal(1, metrics.TitlesAdded[now.ToString("yyyy-MM-dd")]);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Shared_store_lists_explicit_wanted_ids_outside_recent_summary_window(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var now = new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        await InitializeSchemaAsync(storage, timeProvider, kind);

        var repository = new SqliteMediaStateRepository(storage.Factory, timeProvider);
        var mediaIds = new List<string>();
        for (var index = 0; index < 30; index++)
        {
            var mediaId = await AddMediaAsync(storage, timeProvider, kind, index);
            mediaIds.Add(mediaId);
            await repository.EnsureWantedStateAsync(
                kind,
                mediaId,
                "main",
                "missing",
                "No accepted file exists.",
                hasFile: false,
                currentQuality: null,
                targetQuality: "WEB 1080p",
                qualityCutoffMet: false,
                CancellationToken.None);
        }

        var summary = await repository.GetWantedSummaryAsync(kind, CancellationToken.None);
        Assert.Equal(25, summary.RecentItems.Count);
        var outsideSummaryId = mediaIds.First(id => summary.RecentItems.All(item => item.Id != id));

        var selected = await repository.ListWantedByIdsAsync(
            kind,
            [outsideSummaryId],
            CancellationToken.None);

        var item = Assert.Single(selected);
        Assert.Equal(outsideSummaryId, item.Id);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Shared_store_reads_import_recovery_summary_for_both_media_kinds(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
            await repository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(
                    "Movie",
                    "quality",
                    "Below cutoff",
                    "Review quality"),
                CancellationToken.None);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
            await repository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(
                    "Series",
                    "unmatched",
                    "No match",
                    "Review the title"),
                CancellationToken.None);
        }

        var summary = await new SqliteMediaStateRepository(storage.Factory, timeProvider)
            .GetImportRecoverySummaryAsync(kind, CancellationToken.None);

        Assert.Equal(1, summary.OpenCount);
        Assert.Single(summary.RecentCases);
        Assert.Equal(kind == MediaKind.Movie ? 1 : 0, summary.QualityCount);
        Assert.Equal(kind == MediaKind.Series ? 1 : 0, summary.UnmatchedCount);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Engine_repositories_route_metadata_updates_through_shared_store(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);
        var shared = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, shared);
            var movie = await repository.AddAsync(
                new CreateMovieRequest("Before", 2026, "tt0000003"),
                CancellationToken.None);

            var updated = await repository.UpdateMetadataAsync(
                    new MediaMetadataUpdate(
                        movie.Id,
                        "tmdb",
                        "123",
                        "After",
                        "Updated overview",
                        "poster.jpg",
                        "backdrop.jpg",
                        8.5,
                        "Drama",
                        "https://example.test/movie",
                        "tt0000004",
                        "{\"ratings\":[]}"),
                    CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("Before", updated.Title);
            Assert.Equal("Updated overview", updated.Overview);
            Assert.Equal("tt0000004", updated.ImdbId);
            Assert.Equal("tmdb", updated.MetadataProvider);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, shared);
            var series = await repository.AddAsync(
                new CreateSeriesRequest("Before", 2026, "tt0000005"),
                CancellationToken.None);

            var updated = await repository.UpdateMetadataAsync(
                    new MediaMetadataUpdate(
                        series.Id,
                        "tmdb",
                        "456",
                        "After",
                        "Updated overview",
                        "poster.jpg",
                        "backdrop.jpg",
                        8.5,
                        "Drama",
                        "https://example.test/series",
                        "tt0000006",
                        "{\"ratings\":[]}"),
                    CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("Before", updated.Title);
            Assert.Equal("Updated overview", updated.Overview);
            Assert.Equal("tt0000006", updated.ImdbId);
            Assert.Equal("tmdb", updated.MetadataProvider);
        }
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Reviewed_metadata_identity_update_replaces_title_and_year(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);
        var shared = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, shared);
            var movie = await repository.AddAsync(new CreateMovieRequest("Before", 1984, null), CancellationToken.None);
            var update = new MediaMetadataUpdate(
                movie.Id, "tmdb", "603", null, null, null, null, null, null, null,
                "tt0133093", null, Title: "The Matrix", Year: 1999);

            var updated = await repository.UpdateMetadataAsync(update, CancellationToken.None);

            Assert.Equal("The Matrix", updated!.Title);
            Assert.Equal(1999, updated.ReleaseYear);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, shared);
            var series = await repository.AddAsync(new CreateSeriesRequest("Before", 1984, null), CancellationToken.None);
            var update = new MediaMetadataUpdate(
                series.Id, "tmdb", "1396", null, null, null, null, null, null, null,
                "tt0903747", null, Title: "Breaking Bad", Year: 2008);

            var updated = await repository.UpdateMetadataAsync(update, CancellationToken.None);

            Assert.Equal("Breaking Bad", updated!.Title);
            Assert.Equal(2008, updated.StartYear);
        }
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Engine_repositories_route_add_through_shared_store(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);
        var shared = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, shared);
            var created = await repository.AddAsync(
                new CreateMovieRequest(
                    "  Created movie  ",
                    2026,
                    "tt0000010",
                    Monitored: false,
                    MetadataProvider: "tmdb",
                    MetadataProviderId: "101",
                    OriginalTitle: "Created film",
                    Overview: "Created overview",
                    Rating: 8.5,
                    Genres: "Drama",
                    MetadataJson: "{\"source\":\"test\"}"),
                CancellationToken.None);
            var duplicate = await repository.AddAsync(
                new CreateMovieRequest("created movie", 2026, "tt0000099"),
                CancellationToken.None);

            Assert.Equal(created.Id, duplicate.Id);
            Assert.Equal("Created movie", created.Title);
            Assert.False(created.Monitored);
            Assert.Equal("tmdb", created.MetadataProvider);
            Assert.Equal("101", created.MetadataProviderId);
            Assert.Equal("Created overview", created.Overview);
            Assert.Equal(8.5, created.Rating);

            Assert.True(await repository.UpdateReleaseDatesAsync(
                created.Id,
                new DateOnly(2026, 8, 22),
                new DateOnly(2026, 8, 23),
                null,
                CancellationToken.None));

            var reloaded = await repository.GetByIdAsync(created.Id, CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Equal(new DateOnly(2026, 8, 22), reloaded.InCinemasDate);
            Assert.Equal(new DateOnly(2026, 8, 23), reloaded.DigitalReleaseDate);
            Assert.Null(reloaded.PhysicalReleaseDate);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, shared);
            var created = await repository.AddAsync(
                new CreateSeriesRequest(
                    "  Created series  ",
                    2026,
                    "tt0000011",
                    Monitored: false,
                    MetadataProvider: "tmdb",
                    MetadataProviderId: "102",
                    OriginalTitle: "Created show",
                    Overview: "Created overview",
                    Rating: 8.5,
                    Genres: "Drama",
                    MetadataJson: "{\"source\":\"test\"}"),
                CancellationToken.None);
            var duplicate = await repository.AddAsync(
                new CreateSeriesRequest("created series", 2026, "tt0000099"),
                CancellationToken.None);

            Assert.Equal(created.Id, duplicate.Id);
            Assert.Equal("Created series", created.Title);
            Assert.False(created.Monitored);
            Assert.Equal("tmdb", created.MetadataProvider);
            Assert.Equal("102", created.MetadataProviderId);
            Assert.Equal("Created overview", created.Overview);
            Assert.Equal(8.5, created.Rating);
        }
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Engine_repositories_route_existing_import_through_shared_store(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);
        var shared = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, shared);
            var created = await repository.ImportExistingBatchAsync(
                "library-movies",
                [
                    new ExistingMovieImportRequest(
                        "Imported movie",
                        2016,
                        "covered",
                        "Imported from disk.",
                        "Bluray-1080p",
                        "Bluray-1080p",
                        QualityCutoffMet: true,
                        UnmonitorWhenCutoffMet: true,
                        @"D:\Media\Imported movie (2016)\Imported.movie.2016.1080p.BluRay.x264-GROUP.mkv",
                        1024)
                ],
                CancellationToken.None);

            Assert.Equal(1, created);
            var item = Assert.Single(
                (await repository.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
            Assert.Equal("Imported movie", item.Title);
            Assert.True(item.HasFile);
            Assert.True(item.Monitored);
            Assert.Equal("H.264", item.VideoCodec);
            Assert.Equal("GROUP", item.ReleaseGroup);

            var streamed = new List<MediaTrackedFileItem>();
            await foreach (var tracked in shared.StreamTrackedFilesAsync(
                               MediaKind.Movie,
                               "library-movies",
                               CancellationToken.None))
            {
                streamed.Add(tracked);
            }

            Assert.Single(streamed);
            Assert.Equal(item.Id, streamed[0].MediaId);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, shared);
            var created = await repository.ImportExistingBatchAsync(
                "library-series",
                [
                    new ExistingSeriesImportRequest(
                        "Imported series",
                        2019,
                        "covered",
                        "Imported from disk.",
                        "WEB-1080p",
                        "WEB-1080p",
                        QualityCutoffMet: false,
                        UnmonitorWhenCutoffMet: true,
                        @"D:\Media\Imported series (2019)\Imported.series.S01E01.1080p.WEB-DL.mkv",
                        2048,
                        [
                            new ImportedEpisodeItem(1, 1, true, @"D:\Media\Imported series\S01E01.mkv", 1024),
                            new ImportedEpisodeItem(1, 2, true, @"D:\Media\Imported series\S01E02.mkv", 1024)
                        ])
                ],
                CancellationToken.None);

            Assert.Equal(1, created);
            var series = Assert.Single(await repository.ListAsync(CancellationToken.None));
            var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
            Assert.NotNull(detail);
            Assert.True(series.Monitored);
            Assert.Equal(2, detail.EpisodeCount);
            Assert.Equal(2, detail.ImportedEpisodeCount);

            var streamed = new List<MediaTrackedFileItem>();
            await foreach (var tracked in shared.StreamTrackedFilesAsync(
                               MediaKind.Series,
                               "library-series",
                               CancellationToken.None))
            {
                streamed.Add(tracked);
            }

            Assert.Single(streamed);
            Assert.Equal(series.Id, streamed[0].MediaId);
        }
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Shared_existing_import_persists_the_immutable_preference_snapshot_for_the_actual_media_id(
        MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 31, 3, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, clock, kind);
        var shared = new SqliteMediaStateRepository(storage.Factory, clock);
        var snapshot = new PreferenceEvaluationSnapshot(
            MediaId: string.Empty,
            LibraryId: "library-import-proof",
            FileIdentity: "preference-file/v1:import-proof",
            FilePath: @"D:\Media\Import proof.mkv",
            FileSizeBytes: 4096,
            PlanId: "quality-profile/import-proof",
            PlanVersion: "1",
            PlanHash: "0123456789abcdef",
            Facts: [],
            Evaluation: new PreferenceEvaluation(
                "quality-profile/import-proof",
                "1",
                "0123456789abcdef",
                PreferenceEvaluationStatus.MeetsPlan,
                hardGatesPassed: true,
                targetsMet: true,
                families: [],
                reasons: []),
            MatchedRuleIds: [],
            EvaluatedUtc: clock.GetUtcNow(),
            Source: "existing-import");

        string mediaId;
        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, clock, shared);
            var created = await repository.ImportExistingBatchAsync(
                "library-import-proof",
                [new ExistingMovieImportRequest(
                    "Import proof movie",
                    2026,
                    "covered",
                    "Imported from disk.",
                    "WEB-1080p",
                    "WEB-1080p",
                    QualityCutoffMet: true,
                    UnmonitorWhenCutoffMet: false,
                    snapshot.FilePath,
                    snapshot.FileSizeBytes,
                    snapshot)],
                CancellationToken.None);
            Assert.Equal(1, created);
            mediaId = Assert.Single((await repository.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items).Id;
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, clock, shared);
            var created = await repository.ImportExistingBatchAsync(
                "library-import-proof",
                [new ExistingSeriesImportRequest(
                    "Import proof series",
                    2026,
                    "covered",
                    "Imported from disk.",
                    "WEB-1080p",
                    "WEB-1080p",
                    QualityCutoffMet: true,
                    UnmonitorWhenCutoffMet: false,
                    snapshot.FilePath,
                    snapshot.FileSizeBytes,
                    [new ImportedEpisodeItem(1, 1, true, snapshot.FilePath, snapshot.FileSizeBytes)],
                    snapshot)],
                CancellationToken.None);
            Assert.Equal(1, created);
            mediaId = Assert.Single(await repository.ListAsync(CancellationToken.None)).Id;
        }

        var persisted = await shared.GetLatestPreferenceEvaluationSnapshotAsync(
            kind,
            mediaId,
            "library-import-proof",
            snapshot.FileIdentity,
            CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(mediaId, persisted!.MediaId);
        Assert.Equal(snapshot.PlanHash, persisted.PlanHash);
        Assert.Equal(snapshot.FilePath, persisted.FilePath);
        Assert.Equal(snapshot.Source, persisted.Source);
        var wanted = Assert.Single((await shared.GetWantedSummaryAsync(kind, CancellationToken.None)).RecentItems);
        Assert.Equal(snapshot.FilePath, wanted.FilePath);
        Assert.Equal(snapshot.FileSizeBytes, wanted.FileSizeBytes);

        // A file already read by the media-facts pass is normally settled.
        // Removing only its typed baseline must put it back into the probe
        // queue, so upgrades cannot stay held forever after the snapshot
        // feature (or a restored database) arrives later than the file facts.
        await shared.UpdateProbedFileFactsAsync(
            kind,
            mediaId,
            snapshot.FilePath!,
            new ProbedFileFacts("HEVC", "TrueHD", "5.1"),
            CancellationToken.None,
            "library-import-proof");
        MediaPreferencePlanExpectation[] expectedPlan =
        [
            new(
                "library-import-proof",
                snapshot.PlanId,
                snapshot.PlanVersion,
                snapshot.PlanHash)
        ];
        Assert.Empty(await shared.ListFileProbeCandidatesAsync(
            kind,
            10,
            CancellationToken.None,
            expectedPlan));
        MediaPreferencePlanExpectation[] nextPlanExpected =
        [
            new MediaPreferencePlanExpectation(
                "library-import-proof",
                snapshot.PlanId,
                snapshot.PlanVersion + ".next",
                snapshot.PlanHash + "next")
        ];
        Assert.Single(await shared.ListFileProbeCandidatesAsync(
            kind,
            10,
            CancellationToken.None,
            nextPlanExpected));

        await using (var connection = await storage.Factory.OpenConnectionAsync(
                         kind == MediaKind.Movie ? DelunoDatabaseNames.Movies : DelunoDatabaseNames.Series))
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM media_preference_evaluations WHERE media_id = @mediaId;";
            var parameter = delete.CreateParameter();
            parameter.ParameterName = "@mediaId";
            parameter.Value = mediaId;
            delete.Parameters.Add(parameter);
            await delete.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var baselineRepair = Assert.Single(
            await shared.ListFileProbeCandidatesAsync(
                kind,
                10,
                CancellationToken.None,
                expectedPlan));
        Assert.Equal(mediaId, baselineRepair.MediaId);
        Assert.Equal(snapshot.FilePath, baselineRepair.FilePath);
        Assert.Equal(snapshot.FileSizeBytes, baselineRepair.FileSizeBytes);
    }

    [Fact]
    public async Task Series_file_probe_re_evaluates_every_installed_episode_file_when_the_plan_changes()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, clock, MediaKind.Series);
        var shared = new SqliteMediaStateRepository(storage.Factory, clock);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, clock, shared);
        const string libraryId = "library-season-probe";
        const string firstPath = @"D:\Media\Show.S01E01.1080p.WEB-DL.mkv";
        const string secondPath = @"D:\Media\Show.S01E02.1080p.WEB-DL.mkv";

        var created = await repository.ImportExistingBatchAsync(
            libraryId,
            [new ExistingSeriesImportRequest(
                "Probe proof show",
                2026,
                "covered",
                "Imported from disk.",
                "WEB 1080p",
                "WEB 1080p",
                QualityCutoffMet: true,
                UnmonitorWhenCutoffMet: false,
                FilePath: firstPath,
                FileSizeBytes: 1_001,
                Episodes:
                [
                    new ImportedEpisodeItem(1, 1, true, firstPath, 1_001),
                    new ImportedEpisodeItem(1, 2, true, secondPath, 1_002)
                ])],
            CancellationToken.None);
        Assert.Equal(1, created);
        var series = Assert.Single(await repository.ListAsync(CancellationToken.None));
        const string planId = "quality-profile/season-probe";
        const string planVersion = "1";
        const string planHash = "season-probe-plan-v1";

        foreach (var file in new[] { (firstPath, 1_001L), (secondPath, 1_002L) })
        {
            await shared.UpdateProbedFileFactsAsync(
                MediaKind.Series,
                series.Id,
                file.Item1,
                new ProbedFileFacts("HEVC", "AAC", "2.0"),
                CancellationToken.None,
                libraryId);
            await shared.SavePreferenceEvaluationSnapshotAsync(
                MediaKind.Series,
                new PreferenceEvaluationSnapshot(
                    series.Id,
                    libraryId,
                    PreferenceFileIdentity.Compute(file.Item1, file.Item2),
                    file.Item1,
                    file.Item2,
                    planId,
                    planVersion,
                    planHash,
                    [],
                    new PreferenceEvaluation(
                        planId,
                        planVersion,
                        planHash,
                        PreferenceEvaluationStatus.MeetsPlan,
                        hardGatesPassed: true,
                        targetsMet: true,
                        families: [],
                        reasons: []),
                    [],
                    clock.GetUtcNow(),
                    "test"),
                CancellationToken.None);
        }

        MediaPreferencePlanExpectation[] currentPlan =
        [
            new(libraryId, planId, planVersion, planHash)
        ];
        Assert.Empty(await shared.ListFileProbeCandidatesAsync(
            MediaKind.Series,
            10,
            CancellationToken.None,
            currentPlan));

        MediaPreferencePlanExpectation[] changedPlan =
        [
            new(libraryId, planId, "2", "season-probe-plan-v2")
        ];
        var candidates = await shared.ListFileProbeCandidatesAsync(
            MediaKind.Series,
            10,
            CancellationToken.None,
            changedPlan);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(series.Id, candidate.MediaId);
            Assert.Equal(libraryId, candidate.LibraryId);
        });
        Assert.Equal(
            [firstPath, secondPath],
            candidates.Select(candidate => candidate.FilePath).ToArray());
    }

    private static async Task InitializeSchemaAsync(
        TestStorage storage,
        TimeProvider timeProvider,
        MediaKind kind)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        if (kind == MediaKind.Movie)
        {
            await new MoviesSchemaInitializer(
                storage.Factory,
                migrator,
                NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        }
        else
        {
            await new SeriesSchemaInitializer(
                storage.Factory,
                migrator,
                NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        }
    }

    private static async Task<string> AddMediaAsync(
        TestStorage storage,
        TimeProvider timeProvider,
        MediaKind kind,
        int? index = null)
    {
        var suffix = index is null ? string.Empty : $" {index.Value}";
        var externalId = index is null ? "0000001" : $"{index.Value + 1:0000000}";
        if (kind == MediaKind.Movie)
        {
            var movie = await new SqliteMovieCatalogRepository(storage.Factory, timeProvider).AddAsync(
                new CreateMovieRequest($"Shared movie{suffix}", 2026, $"tt{externalId}"),
                CancellationToken.None);
            return movie.Id;
        }

        var series = await new SqliteSeriesCatalogRepository(storage.Factory, timeProvider).AddAsync(
            new CreateSeriesRequest($"Shared series{suffix}", 2026, $"tt{externalId}"),
            CancellationToken.None);
        return series.Id;
    }
}
