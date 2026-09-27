using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Persistence.Tests.Series;

/// <summary>
/// #654: an episode with a file used to be written <c>covered</c> whatever
/// its own quality, so a 720p episode under a 1080p cutoff with
/// upgrade-until-cutoff on was never searched for an upgrade again. Every
/// write path that gives an episode a file — a season pack, a single
/// episode, and an existing-library rescan — goes through the same
/// <c>ImportExistingAsync</c>/<c>ImportExistingBatchAsync</c> upsert, so one
/// fix there settles all three.
/// </summary>
public sealed class EpisodeQualityDecidesWantedStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
    private const string LibraryId = "lib-tv-654";

    [Fact]
    public async Task Season_pack_import_of_a_below_cutoff_episode_is_marked_upgrade()
    {
        var (repository, storage, series) = await ShowWithCataloguedEpisodeAsync();
        using var _ = storage;

        await repository.ImportExistingAsync(
            LibraryId,
            series.Title,
            series.StartYear,
            wantedStatus: "upgrade",
            wantedReason: "This TV show is currently WEB 720p.",
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            filePath: @"C:\media\tlou\The Last of Us S01.mkv",
            fileSizeBytes: 4096,
            episodes: null,
            CancellationToken.None,
            seasonPacks:
            [
                new ImportedSeasonPackItem(
                    1,
                    @"C:\media\tlou\The Last of Us S01.mkv",
                    4096,
                    [new ImportedEpisodeItem(1, 1, true, @"C:\media\tlou\S01E01.mkv", 4096)])
            ],
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var episode = detail!.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 1);

        Assert.True(episode.HasFile);
        Assert.Equal("upgrade", episode.WantedStatus);
        Assert.Contains("season pack", episode.WantedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WEB 1080p", episode.WantedReason);
    }

    [Fact]
    public async Task Season_pack_import_of_an_at_cutoff_episode_is_marked_covered()
    {
        var (repository, storage, series) = await ShowWithCataloguedEpisodeAsync();
        using var _ = storage;

        await repository.ImportExistingAsync(
            LibraryId,
            series.Title,
            series.StartYear,
            wantedStatus: "covered",
            wantedReason: "This TV show already meets your target quality with WEB 1080p.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            filePath: @"C:\media\tlou\The Last of Us S01.mkv",
            fileSizeBytes: 4096,
            episodes: null,
            CancellationToken.None,
            seasonPacks:
            [
                new ImportedSeasonPackItem(
                    1,
                    @"C:\media\tlou\The Last of Us S01.mkv",
                    4096,
                    [new ImportedEpisodeItem(1, 1, true, @"C:\media\tlou\S01E01.mkv", 4096)])
            ],
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var episode = detail!.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 1);

        Assert.True(episode.HasFile);
        Assert.Equal("covered", episode.WantedStatus);
    }

    [Fact]
    public async Task A_rescan_corrects_an_episode_that_was_wrongly_written_covered()
    {
        var (repository, storage, series) = await ShowWithCataloguedEpisodeAsync();
        using var _ = storage;

        // A row shaped exactly like the defect: has a file, is below its
        // cutoff, and was nonetheless written `covered` -- what the old,
        // unconditional `episode.HasFile ? "covered" : "missing"` produced.
        await SeedWronglyCoveredEpisodeAsync(storage, series.Id);

        var beforeRescan = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var beforeEpisode = beforeRescan!.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 1);
        Assert.Equal("covered", beforeEpisode.WantedStatus);
        Assert.False(beforeEpisode.QualityCutoffMet);

        // The existing-library rescan path: the same file, re-declared with
        // the library's real upgrade policy this time.
        await repository.ImportExistingAsync(
            LibraryId,
            series.Title,
            series.StartYear,
            wantedStatus: "upgrade",
            wantedReason: "This TV show is currently WEB 720p.",
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            filePath: @"C:\media\tlou\The Last of Us S01E01.mkv",
            fileSizeBytes: 4096,
            episodes:
            [
                new ImportedEpisodeItem(1, 1, true, @"C:\media\tlou\The Last of Us S01E01.mkv", 4096, CurrentQuality: "WEB 720p", QualityCutoffMet: false)
            ],
            CancellationToken.None,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false,
            episodeImportSource: "existing-library");

        var afterRescan = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var afterEpisode = afterRescan!.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 1);

        Assert.Equal("upgrade", afterEpisode.WantedStatus);
        Assert.Contains("existing library", afterEpisode.WantedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_upgradable_episode_is_picked_up_by_the_automatic_search()
    {
        var (repository, storage, series) = await ShowWithCataloguedEpisodeAsync();
        using var _ = storage;

        await repository.ImportExistingAsync(
            LibraryId,
            series.Title,
            series.StartYear,
            wantedStatus: "upgrade",
            wantedReason: "This TV show is currently WEB 720p.",
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            filePath: @"C:\media\tlou\The Last of Us S01.mkv",
            fileSizeBytes: 4096,
            episodes: null,
            CancellationToken.None,
            seasonPacks:
            [
                new ImportedSeasonPackItem(
                    1,
                    @"C:\media\tlou\The Last of Us S01.mkv",
                    4096,
                    [new ImportedEpisodeItem(1, 1, true, @"C:\media\tlou\S01E01.mkv", 4096)])
            ],
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        var eligible = await repository.ListEligibleWantedEpisodesAsync(
            LibraryId,
            take: 10,
            Now,
            ignoreRetryWindow: true,
            CancellationToken.None);

        Assert.Contains(eligible, item => item.SeriesId == series.Id && item.SeasonNumber == 1 && item.EpisodeNumber == 1);
    }

    private static async Task<(SqliteSeriesCatalogRepository Repository, TestStorage Storage, SeriesListItem Series)> ShowWithCataloguedEpisodeAsync()
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("The Last of Us", 2023, "tt3581920"),
            CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 1, "When You're Lost in the Darkness", null, Now.AddDays(-30))],
            "provider",
            CancellationToken.None);

        return (repository, storage, series);
    }

    private static async Task SeedWronglyCoveredEpisodeAsync(TestStorage storage, string seriesId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE episode_entries
            SET has_file = 1, file_path = @filePath, file_size_bytes = 4096, quality_cutoff_met = 0
            WHERE series_id = @seriesId AND season_number = 1 AND episode_number = 1;

            INSERT INTO series_wanted_state (
                series_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                current_quality, target_quality, updated_utc
            )
            VALUES (@seriesId, @libraryId, 'covered', 'Episode imported from your existing library.', 1, 0,
                'WEB 720p', 'WEB 1080p', @now)
            ON CONFLICT(series_id, library_id) DO UPDATE SET
                wanted_status = excluded.wanted_status,
                has_file = 1,
                current_quality = excluded.current_quality,
                target_quality = excluded.target_quality,
                quality_cutoff_met = excluded.quality_cutoff_met;

            INSERT INTO episode_wanted_state (
                episode_id, series_id, library_id, wanted_status, wanted_reason,
                current_quality, target_quality, quality_cutoff_met, updated_utc
            )
            SELECT id, @seriesId, @libraryId, 'covered', 'Episode imported from your existing library.',
                'WEB 720p', 'WEB 1080p', 0, @now
            FROM episode_entries
            WHERE series_id = @seriesId AND season_number = 1 AND episode_number = 1
            ON CONFLICT(episode_id) DO UPDATE SET
                wanted_status = excluded.wanted_status,
                current_quality = excluded.current_quality,
                target_quality = excluded.target_quality,
                quality_cutoff_met = excluded.quality_cutoff_met;
            """;
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@libraryId", LibraryId);
        AddParameter(command, "@filePath", @"C:\media\tlou\The Last of Us S01E01.mkv");
        AddParameter(command, "@now", Now.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
