using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Series;

/// <summary>
/// Covers <see cref="SqliteSeriesCatalogRepository.SyncEpisodeCatalogueAsync"/> and the
/// wanted/eligible query surface. This is the precondition ADR-001/#118 need before the
/// movie and series engines can be merged: without it, a behavioural regression in the
/// series catalogue sync would ship silently while the movie tests stayed green.
/// </summary>
public sealed class SeriesCatalogueSyncPersistenceTests
{
    private static Task<(TestStorage Storage, SqliteSeriesCatalogRepository Repository, FixedTimeProvider TimeProvider, string SeriesId)> CreateSeriesAsync(
        DateTimeOffset now)
        => CreateSeriesAsync(now, monitored: true);

    private static async Task<(TestStorage Storage, SqliteSeriesCatalogRepository Repository, FixedTimeProvider TimeProvider, string SeriesId)> CreateSeriesAsync(
        DateTimeOffset now,
        bool monitored)
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(now);

        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: monitored),
            CancellationToken.None);

        return (storage, repository, timeProvider, series.Id);
    }

    /// <summary>
    /// TheTVDB's absolute number is stored (#989), and a later refresh from a
    /// list with no absolute numbers (TMDb's) does not blank it.
    /// </summary>
    [Fact]
    public async Task An_absolute_number_from_TheTVDB_survives_a_refresh_that_has_none()
    {
        var now = DateTimeOffset.Parse("2026-09-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(2, 1, "Return", null, now.AddDays(-40), AbsoluteNumber: 26)],
            source: "tvdb",
            CancellationToken.None);
        var first = await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None);
        Assert.Equal(26, Assert.Single(first!.Episodes).AbsoluteNumber);

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(2, 1, "Return", null, now.AddDays(-40))],
            source: "tmdb",
            CancellationToken.None);
        var second = await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None);
        Assert.Equal(26, Assert.Single(second!.Episodes).AbsoluteNumber);
    }

    /// <summary>
    /// Specials a TheTVDB refresh adds arrive unmonitored, whatever the show's
    /// monitor preset, so Deluno never hunts episodes nobody asked for; the
    /// regular episodes that refresh adds follow the preset (#989).
    /// </summary>
    [Fact]
    public async Task Specials_a_TheTVDB_refresh_adds_are_unmonitored_and_new_episodes_follow_the_preset()
    {
        var now = DateTimeOffset.Parse("2026-09-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Pilot", null, now.AddDays(-40)),
                new CatalogueEpisodeItem(0, 1, "Behind the Story #1", null, now.AddDays(-40)),
                new CatalogueEpisodeItem(0, 2, "Inside the Series", null, now.AddDays(-30))
            ],
            source: "tvdb",
            CancellationToken.None);

        var detail = await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None);
        Assert.All(detail!.Episodes.Where(e => e.SeasonNumber == 0), e => Assert.False(e.Monitored));
        Assert.True(detail.Episodes.Single(e => e.SeasonNumber == 1).Monitored);

        await repository.UpdateMonitorNewEpisodesAsync(seriesId, false, CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 2, "Next", null, now.AddDays(-20)), new CatalogueEpisodeItem(0, 3, "Extra", null, now.AddDays(-10))],
            source: "tvdb",
            CancellationToken.None);
        var after = await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None);
        Assert.False(after!.Episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 2).Monitored);
        Assert.False(after.Episodes.Single(e => e.SeasonNumber == 0 && e.EpisodeNumber == 3).Monitored);
    }

    /// <summary>
    /// An imported episode records the quality it holds.
    ///
    /// <para>On the rig, Breaking Bad S01E03 imported a WEB 1080p file and its
    /// own row reported <c>hasFile=true, currentQuality='', cutoffMet=false</c>.
    /// The series row carried the quality and the episode row never did, so the
    /// episode showed none on its page and the upgrade check had nothing to
    /// compare against - a covered episode that could never settle.</para>
    /// </summary>
    [Fact]
    public async Task An_imported_episode_records_the_quality_it_holds()
    {
        var now = DateTimeOffset.Parse("2026-09-16T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 3, "...And the Bag's in the River", null, now.AddDays(-40))],
            source: "tmdb",
            CancellationToken.None);

        await repository.ImportExistingAsync(
            libraryId: "series-main",
            title: "Severance",
            startYear: 2022,
            wantedStatus: "covered",
            wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            filePath: @"C:\media\s01e03.mkv",
            fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 3, HasFile: true, FilePath: @"C:\media\s01e03.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        var inventory = await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None);
        var episode = Assert.Single(inventory!.Episodes, item => item.SeasonNumber == 1 && item.EpisodeNumber == 3);

        Assert.True(episode.HasFile);
        Assert.Equal("WEB 1080p", episode.CurrentQuality);
        Assert.Equal("WEB 1080p", episode.TargetQuality);
        Assert.True(episode.QualityCutoffMet);
    }

    /// <summary>
    /// A rescan fills in what is missing without undoing what is settled.
    ///
    /// <para>Proved on the rig, in both directions. Reviewing existing files
    /// healed an episode that held a file and recorded no quality - which is
    /// how an install upgrading past that defect recovers. The same pass then
    /// cleared <c>quality_cutoff_met</c> on an episode that had legitimately
    /// met it, because the flag was written unconditionally: a rescan reports
    /// "there is a file here" without deciding a cutoff, so every settled
    /// episode came back looking upgradable.</para>
    /// </summary>
    [Fact]
    public async Task A_rescan_fills_a_missing_quality_without_unsettling_a_met_cutoff()
    {
        var now = DateTimeOffset.Parse("2026-09-16T05:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 1, "Pilot", null, now.AddDays(-40))],
            source: "tmdb",
            CancellationToken.None);

        // An import that met the cutoff: settled.
        await repository.ImportExistingAsync(
            libraryId: "series-main", title: "Severance", startYear: 2022,
            wantedStatus: "covered", wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            filePath: @"C:\media\s01e01.mkv", fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        var settled = Assert.Single(
            (await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes,
            item => item.EpisodeNumber == 1);
        Assert.True(settled.QualityCutoffMet);

        // A rescan: it sees the file, and decides no cutoff.
        await repository.ImportExistingAsync(
            libraryId: "series-main", title: "Severance", startYear: 2022,
            wantedStatus: "covered", wantedReason: "Found during a library review.",
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            filePath: @"C:\media\s01e01.mkv", fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        var afterRescan = Assert.Single(
            (await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes,
            item => item.EpisodeNumber == 1);

        Assert.Equal("WEB 1080p", afterRescan.CurrentQuality);
        Assert.True(afterRescan.QualityCutoffMet);
    }

    /// <summary>
    /// An episode's cutoff is a fact about its own file, not about the show.
    ///
    /// <para>Proved on the rig with Breaking Bad: seventy-one episodes, two on
    /// disk. The series is correctly <c>missing</c> and has correctly not met
    /// its cutoff — but both episodes held a WEB 1080p file at the library's
    /// cutoff quality, and both were written not-met because the series verdict
    /// was applied to every episode. They could never settle, and offered
    /// themselves for upgrade forever.</para>
    ///
    /// <para>An episode whose own quality is unknown still takes the series
    /// answer, which is what every caller did before.</para>
    /// </summary>
    [Fact]
    public async Task An_episode_meets_its_own_cutoff_even_when_the_show_has_not()
    {
        var now = DateTimeOffset.Parse("2026-09-16T07:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Pilot", null, now.AddDays(-40)),
                new CatalogueEpisodeItem(1, 2, "Half Loop", null, now.AddDays(-33)),
                new CatalogueEpisodeItem(1, 3, "Cat's in the Bag", null, now.AddDays(-26))
            ],
            source: "tmdb",
            CancellationToken.None);

        // The show is missing most of its episodes, so its own verdict is
        // "not met" - exactly the state that used to be copied onto every row.
        await repository.ImportExistingAsync(
            libraryId: "series-main", title: "Severance", startYear: 2022,
            wantedStatus: "missing", wantedReason: "Episodes are missing from the library.",
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            filePath: @"C:\media\s01e01.mkv", fileSizeBytes: 123456,
            episodes:
            [
                // On disk at the cutoff quality: the caller decided this one met it.
                new ImportedEpisodeItem(
                    1, 1, HasFile: true, FilePath: @"C:\media\s01e01.mkv", FileSizeBytes: 123456,
                    CurrentQuality: "WEB 1080p", QualityCutoffMet: true),
                // On disk, but below the cutoff: decided, and decided false.
                new ImportedEpisodeItem(
                    1, 2, HasFile: true, FilePath: @"C:\media\s01e02.mkv", FileSizeBytes: 45678,
                    CurrentQuality: "HDTV 720p", QualityCutoffMet: false),
                // Not on disk at all.
                new ImportedEpisodeItem(1, 3, HasFile: false)
            ],
            CancellationToken.None);

        var episodes = (await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes;

        var met = Assert.Single(episodes, item => item.EpisodeNumber == 1);
        Assert.Equal("WEB 1080p", met.CurrentQuality);
        Assert.True(met.QualityCutoffMet);

        var below = Assert.Single(episodes, item => item.EpisodeNumber == 2);
        Assert.Equal("HDTV 720p", below.CurrentQuality);
        Assert.False(below.QualityCutoffMet);

        var absent = Assert.Single(episodes, item => item.EpisodeNumber == 3);
        Assert.False(absent.HasFile);
        Assert.False(absent.QualityCutoffMet);
    }

    /// <summary>
    /// A caller that does not know an episode's quality still gets the old
    /// behaviour: the series answer, applied as before.
    /// </summary>
    [Fact]
    public async Task An_episode_with_no_quality_of_its_own_still_takes_the_series_answer()
    {
        var now = DateTimeOffset.Parse("2026-09-16T08:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 1, "Pilot", null, now.AddDays(-40))],
            source: "tmdb",
            CancellationToken.None);

        await repository.ImportExistingAsync(
            libraryId: "series-main", title: "Severance", startYear: 2022,
            wantedStatus: "covered", wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            filePath: @"C:\media\s01e01.mkv", fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        var episode = Assert.Single(
            (await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes,
            item => item.EpisodeNumber == 1);

        Assert.Equal("WEB 1080p", episode.CurrentQuality);
        Assert.True(episode.QualityCutoffMet);
    }

    [Fact]
    public async Task A_show_whose_aired_episodes_were_all_removed_is_not_missing_and_an_unmonitored_gap_is_not_counted()
    {
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;
        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "Two", null, now.AddDays(-23)),
                new CatalogueEpisodeItem(1, 3, "Three", null, now.AddDays(-16)),
            ],
            source: "tmdb",
            CancellationToken.None);
        await repository.ImportExistingAsync(
            libraryId: "series-main", title: "Severance", startYear: 2022,
            wantedStatus: "covered", wantedReason: "Seeded.", currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: true, filePath: @"C:\media\s01e01.mkv", fileSizeBytes: 1,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\s01e01.mkv", FileSizeBytes: 1)],
            CancellationToken.None);
        var ids = await GetEpisodeIdsAsync(storage, seriesId);

        // Episodes 2 and 3 have no file. With episode 2 wanted it is Missing; unmonitor 3 and it is still one short.
        await repository.UpdateEpisodeMonitoredAsync([ids[(1, 3)]], monitored: false, CancellationToken.None);
        var missing = Assert.Single((await repository.ListPageAsync(new CatalogueQuery(PageSize: 50), CancellationToken.None)).Items);
        Assert.Equal(WantedStatuses.Missing, missing.WantedStatus);
        Assert.Equal(2, missing.AiredEpisodeCount);
        Assert.Equal(1, missing.AiredWithFileCount);

        // Retention trims the show: nothing is wanted now, so nothing is missing, on the shelf and in the stored chip.
        await repository.UpdateEpisodeMonitoredAsync([ids[(1, 1)], ids[(1, 2)]], monitored: false, CancellationToken.None);
        var trimmed = Assert.Single((await repository.ListPageAsync(new CatalogueQuery(PageSize: 50), CancellationToken.None)).Items);
        Assert.Equal(WantedStatuses.Covered, trimmed.WantedStatus);
        Assert.Empty((await repository.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Missing, PageSize: 50), CancellationToken.None)).Items);
        // Nothing in it is wanted, so it lives under Unmonitored rather than on a status pill (#1072).
        Assert.Empty((await repository.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Covered, PageSize: 50), CancellationToken.None)).Items);
        Assert.Single((await repository.ListPageAsync(new CatalogueQuery(Monitored: false, PageSize: 50), CancellationToken.None)).Items);
        Assert.Single((await repository.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Covered, IncludeUnmonitoredInStatus: true, PageSize: 50), CancellationToken.None)).Items);
    }

    private static async Task<string?> ReadStoredRungAsync(TestStorage storage, string seriesId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT wanted_status FROM series_wanted_state WHERE series_id = @seriesId;";
        AddParam(command, "@seriesId", seriesId);
        return (string?)await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async Task<(TestStorage Storage, SqliteSeriesCatalogRepository Repository, string SeriesId, Dictionary<(int Season, int Episode), string> Ids)> SeedRungShowAsync(
        DateTimeOffset now,
        params CatalogueEpisodeItem[] episodes)
    {
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        await repository.SyncEpisodeCatalogueAsync(seriesId, episodes, source: "tmdb", CancellationToken.None);
        await repository.ImportExistingAsync(
            libraryId: "series-main", title: "Severance", startYear: 2022,
            wantedStatus: "covered", wantedReason: "Seeded.", currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: true, filePath: @"C:\media\s01e01.mkv", fileSizeBytes: 1,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\s01e01.mkv", FileSizeBytes: 1)],
            CancellationToken.None);
        return (storage, repository, seriesId, await GetEpisodeIdsAsync(storage, seriesId));
    }

    [Fact]
    public async Task Season_pass_moves_the_stored_rung_so_a_re_monitored_season_is_searched_again()
    {
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var (storage, repository, seriesId, _) = await SeedRungShowAsync(
            now,
            new CatalogueEpisodeItem(1, 1, "One", null, now.AddDays(-30)),
            new CatalogueEpisodeItem(2, 1, "Two", null, now.AddDays(-10)));
        using var _s = storage;
        Assert.Equal(WantedStatuses.Missing, await ReadStoredRungAsync(storage, seriesId));

        await repository.SetSeasonPassMonitoringAsync([new SeasonPassScope(seriesId, null)], monitored: false, CancellationToken.None);
        Assert.Equal(WantedStatuses.Covered, await ReadStoredRungAsync(storage, seriesId));

        await repository.SetSeasonPassMonitoringAsync([new SeasonPassScope(seriesId, 2)], monitored: true, CancellationToken.None);
        Assert.Equal(WantedStatuses.Missing, await ReadStoredRungAsync(storage, seriesId));
        var card = Assert.Single((await repository.ListPageAsync(new CatalogueQuery(PageSize: 50), CancellationToken.None)).Items);
        Assert.Equal(WantedStatuses.Missing, card.WantedStatus);
    }

    [Fact]
    public async Task A_tracked_episode_file_gone_moves_the_stored_rung_with_it()
    {
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var (storage, repository, seriesId, _) = await SeedRungShowAsync(
            now,
            new CatalogueEpisodeItem(1, 1, "One", null, now.AddDays(-30)),
            new CatalogueEpisodeItem(1, 2, "Two", null, now.AddDays(7)));
        using var _s = storage;
        Assert.Equal(WantedStatuses.Upgrade, await ReadStoredRungAsync(storage, seriesId));
        var episodeId = (await GetEpisodeIdsAsync(storage, seriesId))[(1, 1)];

        Assert.True(await repository.MarkTrackedFileGoneAsync(seriesId, episodeId, "series-main", @"C:\media\s01e01.mkv", CancellationToken.None));

        // Nothing aired is wanted now; only the coming episode is.
        Assert.Equal(WantedStatuses.Upcoming, await ReadStoredRungAsync(storage, seriesId));
    }

    [Fact]
    public async Task A_tracked_episode_file_found_missing_moves_the_stored_rung_to_missing()
    {
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var (storage, repository, seriesId, ids) = await SeedRungShowAsync(
            now,
            new CatalogueEpisodeItem(1, 1, "One", null, now.AddDays(-30)));
        using var _s = storage;
        Assert.Equal(WantedStatuses.Upgrade, await ReadStoredRungAsync(storage, seriesId));

        Assert.True(await repository.MarkTrackedFileMissingAsync(seriesId, ids[(1, 1)], "series-main", @"C:\media\s01e01.mkv", CancellationToken.None));

        Assert.Equal(WantedStatuses.Missing, await ReadStoredRungAsync(storage, seriesId));
    }

    [Fact]
    public async Task A_show_with_no_monitored_episodes_reads_covered_and_unmonitored_on_the_card_the_pills_and_the_filter()
    {
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var (storage, repository, seriesId, ids) = await SeedRungShowAsync(
            now,
            new CatalogueEpisodeItem(1, 1, "One", null, now.AddDays(-30)),
            new CatalogueEpisodeItem(1, 2, "Two", null, now.AddDays(7)));
        using var _s = storage;

        // Nothing is wanted, though the future-dated episode is still on the calendar and the show itself is switched on.
        await repository.UpdateEpisodeMonitoredAsync([ids[(1, 1)], ids[(1, 2)]], monitored: false, CancellationToken.None);

        Assert.Equal(WantedStatuses.Covered, await ReadStoredRungAsync(storage, seriesId));
        var page = await repository.ListPageAsync(new CatalogueQuery(PageSize: 50), CancellationToken.None);
        var card = Assert.Single(page.Items);
        Assert.Equal(WantedStatuses.Covered, card.WantedStatus);
        Assert.False(card.Monitored);
        Assert.Equal(1, page.Facets!.Unmonitored);
        Assert.Equal(0, page.Facets.Monitored);
        Assert.Equal(0, page.Facets.Upcoming);
        Assert.Equal(0, page.Facets.Covered);
        Assert.Single((await repository.ListPageAsync(new CatalogueQuery(Monitored: false, PageSize: 50), CancellationToken.None)).Items);
        Assert.Empty((await repository.ListPageAsync(new CatalogueQuery(Monitored: true, PageSize: 50), CancellationToken.None)).Items);

        // Wanting the coming episode again makes it Upcoming and Monitored everywhere at once.
        await repository.UpdateEpisodeMonitoredAsync([ids[(1, 2)]], monitored: true, CancellationToken.None);
        Assert.Equal(WantedStatuses.Upcoming, await ReadStoredRungAsync(storage, seriesId));
        var again = await repository.ListPageAsync(new CatalogueQuery(PageSize: 50), CancellationToken.None);
        Assert.True(Assert.Single(again.Items).Monitored);
        Assert.Equal(1, again.Facets!.Monitored);
        Assert.Equal(1, again.Facets.Upcoming);
    }

    [Fact]
    public async Task SyncEpisodeCatalogueAsync_ReSync_updates_metadata_but_never_clobbers_disk_state()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        // First sync — two episodes for one season.
        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Good News About Hell", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "Half Loop", null, now.AddDays(-23)),
            ],
            source: "tmdb",
            CancellationToken.None);

        // Simulate disk state: episode 1 has a file and is imported; episode 2 is unmonitored
        // by the user. Both are recorded through ImportExistingAsync / UpdateEpisodeMonitoredAsync,
        // the same paths real disk-state changes go through.
        var episodeIds = await GetEpisodeIdsAsync(storage, seriesId);

        await repository.ImportExistingAsync(
            libraryId: "series-main",
            title: "Severance",
            startYear: 2022,
            wantedStatus: "covered",
            wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            filePath: @"C:\media\severance\s01e01.mkv",
            fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\severance\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        await repository.UpdateEpisodeMonitoredAsync([episodeIds[(1, 2)]], monitored: false, CancellationToken.None);

        var beforeResync = await ReadEpisodeStateAsync(storage, episodeIds[(1, 1)]);
        Assert.True(beforeResync.HasFile);
        Assert.Equal(@"C:\media\severance\s01e01.mkv", beforeResync.FilePath);
        Assert.NotNull(beforeResync.ImportedUtc);

        // Re-sync with changed titles, as a metadata refresh would produce.
        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Good News About Hell (Renamed)", "An overview.", now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "Half Loop (Renamed)", null, now.AddDays(-23)),
            ],
            source: "tmdb",
            CancellationToken.None);

        var episode1 = await ReadEpisodeStateAsync(storage, episodeIds[(1, 1)]);
        var episode2 = await ReadEpisodeStateAsync(storage, episodeIds[(1, 2)]);

        // Titles/overview updated by the sync...
        Assert.Equal("Good News About Hell (Renamed)", episode1.Title);
        Assert.Equal("An overview.", episode1.Overview);
        Assert.Equal("Half Loop (Renamed)", episode2.Title);

        // ...but disk state from before the re-sync is untouched, per the doc comment at
        // SqliteSeriesCatalogRepository.cs:1988-1996.
        Assert.True(episode1.HasFile);
        Assert.Equal(@"C:\media\severance\s01e01.mkv", episode1.FilePath);
        Assert.NotNull(episode1.ImportedUtc);
        Assert.False(episode1.QualityCutoffMet); // sync never sets this true; only workflow evaluation does.
        Assert.True(episode1.Monitored);

        Assert.False(episode2.HasFile);
        Assert.False(episode2.Monitored); // the user's unmonitor decision must survive the re-sync.
    }

    [Fact]
    public async Task ListParentSeriesIdsAsync_returns_only_the_distinct_parents_of_selected_episodes()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 1, "Good News About Hell", null, now.AddDays(-30))],
            source: "tmdb",
            CancellationToken.None);

        var episodeId = (await GetEpisodeIdsAsync(storage, seriesId))[(1, 1)];
        var parentSeriesIds = await repository.ListParentSeriesIdsAsync(
            [episodeId, "missing-episode", episodeId],
            CancellationToken.None);

        Assert.Equal([seriesId], parentSeriesIds);
    }

    [Fact]
    public async Task SyncEpisodeCatalogueAsync_groups_out_of_order_episodes_into_one_row_per_distinct_season()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        var result = await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(3, 1, "S3E1", null, now.AddDays(-3)),
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(2, 1, "S2E1", null, now.AddDays(-15)),
                new CatalogueEpisodeItem(1, 2, "S1E2", null, now.AddDays(-28)),
                new CatalogueEpisodeItem(2, 2, "S2E2", null, now.AddDays(-14)),
                new CatalogueEpisodeItem(3, 2, "S3E2", null, now.AddDays(-2)),
            ],
            source: "tmdb",
            CancellationToken.None);

        Assert.Equal(3, result.SeasonCount);
        Assert.Equal(6, result.EpisodeCount);
        Assert.Equal(6, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);

        using (var seasons = connection.CreateCommand())
        {
            seasons.CommandText = "SELECT COUNT(*) FROM season_entries WHERE series_id = @seriesId;";
            AddParam(seasons, "@seriesId", seriesId);
            Assert.Equal(3L, (long)(await seasons.ExecuteScalarAsync())!);
        }

        using var episodes = connection.CreateCommand();
        episodes.CommandText =
            """
            SELECT e.season_number, COUNT(*), COUNT(DISTINCT e.season_id)
            FROM episode_entries e
            WHERE e.series_id = @seriesId
            GROUP BY e.season_number;
            """;
        AddParam(episodes, "@seriesId", seriesId);
        using var reader = await episodes.ExecuteReaderAsync();
        var seasonsSeen = 0;
        while (await reader.ReadAsync())
        {
            seasonsSeen++;
            Assert.Equal(2L, reader.GetInt64(1));
            // Every episode in a season must attach to exactly one season row.
            Assert.Equal(1L, reader.GetInt64(2));
        }

        Assert.Equal(3, seasonsSeen);
    }

    [Fact]
    public async Task SyncEpisodeCatalogueAsync_leaves_specials_unmonitored_but_monitors_real_seasons()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                // Season 0 is specials: extras and recaps most people do not
                // want hunted, so they arrive unmonitored (#243).
                new CatalogueEpisodeItem(0, 1, "Behind the scenes", null, now.AddDays(-40)),
                new CatalogueEpisodeItem(0, 2, "Recap", null, now.AddDays(-39)),
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "S1E2", null, now.AddDays(-28)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT season_number, SUM(monitored)
            FROM episode_entries
            WHERE series_id = @seriesId
            GROUP BY season_number
            ORDER BY season_number;
            """;
        AddParam(command, "@seriesId", seriesId);

        var monitoredBySeason = new Dictionary<long, long>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            monitoredBySeason[reader.GetInt64(0)] = reader.GetInt64(1);
        }

        Assert.Equal(0L, monitoredBySeason[0]);
        Assert.Equal(2L, monitoredBySeason[1]);
    }

    /// <summary>
    /// An unmonitored series' newly-learned episodes arrive unmonitored too
    /// (#653). Abbott Elementary was added with <c>monitored: false</c> on the
    /// rig and every one of its 93 episodes still came in monitored, which the
    /// scheduled cycle then searched and grabbed within the minute — the
    /// series' own flag was never consulted when episodes were created. Sonarr
    /// leaves a new episode unwanted the same way when its series is
    /// unmonitored; this is that same rule, one level up from season 0.
    /// </summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_leaves_episodes_unmonitored_when_the_series_is()
    {
        var now = DateTimeOffset.Parse("2026-09-25T04:54:16Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now, monitored: false);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "S1E2", null, now.AddDays(-28)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(monitored) FROM episode_entries WHERE series_id = @seriesId;";
        AddParam(command, "@seriesId", seriesId);

        var monitoredCount = await command.ExecuteScalarAsync();
        Assert.Equal(0L, Convert.ToInt64(monitoredCount));
    }

    /// <summary>
    /// The per-series "monitor new episodes" preset (#801): a monitored show
    /// that has opted out of auto-monitoring newly discovered episodes still
    /// gets those episodes added to its catalogue - just unmonitored - the
    /// same way an unmonitored series' episodes are (#653), one level down.
    /// </summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_leaves_new_episodes_unmonitored_when_the_preset_is_off()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:54:16Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        var updated = await repository.UpdateMonitorNewEpisodesAsync(seriesId, monitorNewEpisodes: false, CancellationToken.None);
        Assert.True(updated);

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "S1E2", null, now.AddDays(-28)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(monitored) FROM episode_entries WHERE series_id = @seriesId;";
        AddParam(command, "@seriesId", seriesId);

        var monitoredCount = await command.ExecuteScalarAsync();
        Assert.Equal(0L, Convert.ToInt64(monitoredCount));

        var series = await repository.GetByIdAsync(seriesId, CancellationToken.None);
        Assert.False(series!.MonitorNewEpisodes);
    }

    /// <summary>
    /// #1063: episodes arriving from a fallback source (TMDb) follow the preset
    /// like any others, specials stay unmonitored, and the inventory says which
    /// source each episode's numbering came from.
    /// </summary>
    [Fact]
    public async Task Fallback_source_episodes_follow_the_preset_and_the_inventory_names_the_source()
    {
        var now = DateTimeOffset.Parse("2026-09-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.UpdateMonitorNewEpisodesAsync(seriesId, monitorNewEpisodes: false, CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(0, 1, "Special", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 1, "One", null, now.AddDays(-28))
            ],
            source: "tmdb",
            CancellationToken.None);

        var detail = await repository.GetInventoryDetailAsync(seriesId, CancellationToken.None);
        Assert.All(detail!.Episodes, episode =>
        {
            Assert.False(episode.Monitored);
            Assert.Equal("tmdb", episode.CatalogueSource);
        });
    }

    /// <summary>
    /// #848: a series added from a list with monitor mode "Future" only
    /// monitors episodes that have not aired yet at the time its episode
    /// catalogue is first populated — the show it was added over is not
    /// chased backwards.
    /// </summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_only_monitors_future_episodes_for_the_future_list_monitor_mode()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:54:16Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true, ListMonitorMode: "future"),
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "Already aired", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "Not aired yet", null, now.AddDays(30)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT episode_number, monitored FROM episode_entries WHERE series_id = @seriesId ORDER BY episode_number;";
        AddParam(command, "@seriesId", series.Id);
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(1)); // already-aired episode 1 stays unmonitored
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(1)); // future episode 2 is monitored
    }

    /// <summary>
    /// #848: "Existing" is for a list added over a title Deluno already holds
    /// files for — a newly discovered episode never already has a file, so
    /// nothing new gets chased under this mode.
    /// </summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_monitors_nothing_new_for_the_existing_list_monitor_mode()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:54:16Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true, ListMonitorMode: "existing"),
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30))],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(monitored) FROM episode_entries WHERE series_id = @seriesId;";
        AddParam(command, "@seriesId", series.Id);
        var monitoredCount = await command.ExecuteScalarAsync();
        Assert.Equal(0L, Convert.ToInt64(monitoredCount));
    }

    /// <summary>
    /// #848: "Missing" is for episodes without a file. Every episode this
    /// sync discovers is freshly inserted with no file yet, so it agrees with
    /// "All" here -- the same fact <c>ListMonitorModeAllows</c>'s own comment
    /// makes about this mode.
    /// </summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_monitors_freshly_discovered_episodes_for_the_missing_list_monitor_mode()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:54:16Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true, ListMonitorMode: "missing"),
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30))],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(monitored) FROM episode_entries WHERE series_id = @seriesId;";
        AddParam(command, "@seriesId", series.Id);
        var monitoredCount = await command.ExecuteScalarAsync();
        Assert.Equal(1L, Convert.ToInt64(monitoredCount));
    }

    /// <summary>
    /// #848: "Recent" only monitors the most recent season a sync discovers --
    /// an older season the list also carries stays unmonitored.
    /// </summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_only_monitors_the_most_recent_season_for_the_recent_list_monitor_mode()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:54:16Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true, ListMonitorMode: "recent"),
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-400)),
                new CatalogueEpisodeItem(2, 1, "S2E1", null, now.AddDays(-30)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT season_number, monitored FROM episode_entries WHERE series_id = @seriesId ORDER BY season_number;";
        AddParam(command, "@seriesId", series.Id);
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(1)); // season 1 (older) stays unmonitored
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(1)); // season 2 (most recent) is monitored
    }

    /// <summary>
    /// #991: "First season" only monitors the earliest non-Specials season a
    /// sync discovers — later seasons stay unmonitored, and so does Specials.
    /// </summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_only_monitors_the_first_season_for_the_firstSeason_list_monitor_mode()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:54:16Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true, ListMonitorMode: "firstseason"),
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(0, 1, "Special", null, now.AddDays(-400)),
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-400)),
                new CatalogueEpisodeItem(2, 1, "S2E1", null, now.AddDays(-30)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT season_number, monitored FROM episode_entries WHERE series_id = @seriesId ORDER BY season_number;";
        AddParam(command, "@seriesId", series.Id);
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(1)); // Specials stays unmonitored
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(1)); // season 1 (first) is monitored
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(1)); // season 2 stays unmonitored
    }

    /// <summary>#991: "Pilot" only monitors the very first episode of the first season.</summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_only_monitors_the_pilot_for_the_pilot_list_monitor_mode()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:54:16Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true, ListMonitorMode: "pilot"),
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-400)),
                new CatalogueEpisodeItem(1, 2, "S1E2", null, now.AddDays(-390)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT episode_number, monitored FROM episode_entries WHERE series_id = @seriesId ORDER BY episode_number;";
        AddParam(command, "@seriesId", series.Id);
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(1)); // episode 1 (the pilot) is monitored
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(1)); // episode 2 stays unmonitored
    }

    /// <summary>#991: "None" adds the show but monitors nothing it discovers.</summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_monitors_nothing_for_the_none_list_monitor_mode()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:54:16Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740", Monitored: true, ListMonitorMode: "none"),
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-400))],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(monitored) FROM episode_entries WHERE series_id = @seriesId;";
        AddParam(command, "@seriesId", series.Id);
        var monitoredCount = await command.ExecuteScalarAsync();
        Assert.Equal(0L, Convert.ToInt64(monitoredCount ?? 0L));
    }

    /// <summary>The preset defaults to on, matching every series' behaviour before it existed.</summary>
    [Fact]
    public async Task SyncEpisodeCatalogueAsync_monitors_new_episodes_by_default()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:54:16Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        var series = await repository.GetByIdAsync(seriesId, CancellationToken.None);
        Assert.True(series!.MonitorNewEpisodes);

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30))],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(monitored) FROM episode_entries WHERE series_id = @seriesId;";
        AddParam(command, "@seriesId", seriesId);

        var monitoredCount = await command.ExecuteScalarAsync();
        Assert.Equal(1L, Convert.ToInt64(monitoredCount));
    }

    /// <summary>
    /// The cross-series Season Pass listing (#791): one row per series/season,
    /// with episode counts and how many of them are monitored.
    /// </summary>
    [Fact]
    public async Task ListSeasonPassAsync_reports_episode_and_monitored_counts_per_season()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(0, 1, "Special", null, now.AddDays(-50)),
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "S1E2", null, now.AddDays(-28)),
            ],
            source: "tmdb",
            CancellationToken.None);

        var seasonPass = await repository.ListSeasonPassAsync(CancellationToken.None);

        var special = Assert.Single(seasonPass, item => item.SeriesId == seriesId && item.SeasonNumber == 0);
        Assert.Equal(1, special.EpisodeCount);
        Assert.Equal(0, special.MonitoredCount); // specials arrive unmonitored (#243)

        var season1 = Assert.Single(seasonPass, item => item.SeriesId == seriesId && item.SeasonNumber == 1);
        Assert.Equal("Severance", season1.SeriesTitle);
        Assert.Equal(2, season1.EpisodeCount);
        Assert.Equal(2, season1.MonitoredCount);
    }

    /// <summary>
    /// Bulk-toggling from the Season Pass screen (#791): a season-scoped
    /// toggle touches only that season, and a series-scoped one (null season)
    /// touches every episode the series has.
    /// </summary>
    [Fact]
    public async Task SetSeasonPassMonitoringAsync_toggles_at_the_season_and_series_level()
    {
        var now = DateTimeOffset.Parse("2026-09-27T04:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "S1E2", null, now.AddDays(-28)),
                new CatalogueEpisodeItem(2, 1, "S2E1", null, now.AddDays(-10)),
            ],
            source: "tmdb",
            CancellationToken.None);

        // Unmonitor season 1 only.
        var seasonUpdated = await repository.SetSeasonPassMonitoringAsync(
            [new SeasonPassScope(seriesId, SeasonNumber: 1)],
            monitored: false,
            CancellationToken.None);
        Assert.Equal(2, seasonUpdated);

        var afterSeasonToggle = await repository.ListSeasonPassAsync(CancellationToken.None);
        Assert.Equal(0, Assert.Single(afterSeasonToggle, item => item.SeasonNumber == 1).MonitoredCount);
        Assert.Equal(1, Assert.Single(afterSeasonToggle, item => item.SeasonNumber == 2).MonitoredCount);

        // Now unmonitor the whole series.
        var seriesUpdated = await repository.SetSeasonPassMonitoringAsync(
            [new SeasonPassScope(seriesId, SeasonNumber: null)],
            monitored: false,
            CancellationToken.None);
        Assert.Equal(3, seriesUpdated);

        var afterSeriesToggle = await repository.ListSeasonPassAsync(CancellationToken.None);
        Assert.All(afterSeriesToggle, item => Assert.Equal(0, item.MonitoredCount));
    }

    [Fact]
    public async Task SyncEpisodeCatalogueAsync_backfills_wanted_state_idempotently()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.EnsureWantedStateAsync(
            seriesId,
            libraryId: "series-main",
            wantedStatus: "missing",
            wantedReason: "No accepted episodes exist.",
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 1, "Good News About Hell", null, now.AddDays(-30))],
            source: "tmdb",
            CancellationToken.None);

        var updated = await repository.ReevaluateLibraryWantedStateAsync(
            "series-main", "WEB 1080p", upgradeUntilCutoff: true, upgradeUnknownItems: false, CancellationToken.None);
        Assert.Equal(1, updated);

        await using (var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None))
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM episode_wanted_state WHERE series_id = @seriesId;";
            AddParam(count, "@seriesId", seriesId);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);

            using var seriesStateCount = connection.CreateCommand();
            seriesStateCount.CommandText =
                "SELECT COUNT(*) FROM series_wanted_state WHERE series_id = @seriesId AND library_id = @libraryId;";
            AddParam(seriesStateCount, "@seriesId", seriesId);
            AddParam(seriesStateCount, "@libraryId", "series-main");
            Assert.Equal(1L, (long)(await seriesStateCount.ExecuteScalarAsync())!);
        }

        // Re-running the reevaluation must not create a second row per series/library.
        var updatedAgain = await repository.ReevaluateLibraryWantedStateAsync(
            "series-main", "WEB 1080p", upgradeUntilCutoff: true, upgradeUnknownItems: false, CancellationToken.None);
        Assert.Equal(1, updatedAgain);

        await using var verifyConnection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM series_wanted_state WHERE series_id = @seriesId;";
        AddParam(verify, "@seriesId", seriesId);
        Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// The scheduled cycle's episode pass (<c>LibrarySearchJobHandler</c>) and
    /// every other automatic path read episode eligibility through
    /// <c>ListEligibleWantedEpisodesAsync</c>. An episode row that is itself
    /// monitored is not enough on its own if its series is not — that gap is
    /// exactly how Abbott Elementary's episodes were searched and grabbed five
    /// minutes after being added unmonitored (#653). A person's own retry
    /// (<c>requireMonitored: false</c>) still reaches the episode.
    /// </summary>
    [Fact]
    public async Task ListEligibleWantedEpisodesAsync_excludes_episodes_of_an_unmonitored_series()
    {
        var now = DateTimeOffset.Parse("2026-09-25T04:59:15Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.EnsureWantedStateAsync(
            seriesId, "series-main", "missing", "Needs episodes.", false, null, "WEB 1080p", false, CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30))],
            source: "tmdb",
            CancellationToken.None);

        // Confirms the episode itself is monitored: e.monitored = 1 was
        // always true here, and used to be the whole gate.
        Assert.Single(await repository.ListEligibleWantedEpisodesAsync(
            "series-main", 10, now, ignoreRetryWindow: false, CancellationToken.None));

        await repository.UpdateMonitoredAsync([seriesId], monitored: false, CancellationToken.None);

        Assert.Empty(await repository.ListEligibleWantedEpisodesAsync(
            "series-main", 10, now, ignoreRetryWindow: false, CancellationToken.None));
        // Targeted but still automatic (add-triggered, health-replacement)
        // must not reach it either.
        Assert.Empty(await repository.ListEligibleWantedEpisodesAsync(
            "series-main", 10, now, ignoreRetryWindow: true, CancellationToken.None));
        // A person's own retry still can.
        Assert.Single(await repository.ListEligibleWantedEpisodesAsync(
            "series-main", 10, now, ignoreRetryWindow: true, CancellationToken.None, requireMonitored: false));
    }

    [Fact]
    public async Task ListEligibleWantedAsync_excludes_files_present_unmonitored_and_deferred_series()
    {
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var (storage, repository, _, wantedSeriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.EnsureWantedStateAsync(
            wantedSeriesId, "series-main", "missing", "Needs episodes.", false, null, "WEB 1080p", false, CancellationToken.None);

        // A second series that already has a file — must not appear.
        var coveredSeries = await repository.AddAsync(new CreateSeriesRequest("Silo", 2023, "tt14688458"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            coveredSeries.Id, "series-main", "covered", "Current file is accepted.", true, "WEB 1080p", "WEB 1080p", true, CancellationToken.None);

        // A third, unmonitored series — excluded unless the retry window is ignored.
        var unmonitoredSeries = await repository.AddAsync(new CreateSeriesRequest("Andor", 2022, "tt9253284"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            unmonitoredSeries.Id, "series-main", "missing", "Needs episodes.", false, null, "WEB 1080p", false, CancellationToken.None);
        await repository.UpdateMonitoredAsync([unmonitoredSeries.Id], monitored: false, CancellationToken.None);

        // A fourth series, deferred into the future — excluded unless retry window is ignored.
        var deferredSeries = await repository.AddAsync(new CreateSeriesRequest("Shogun", 2024, "tt2788316"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            deferredSeries.Id, "series-main", "missing", "Needs episodes.", false, null, "WEB 1080p", false, CancellationToken.None);
        await repository.DeferWantedSearchAsync(deferredSeries.Id, "series-main", now.AddHours(24), CancellationToken.None);

        var eligible = await repository.ListEligibleWantedAsync("series-main", 10, now, ignoreRetryWindow: false, CancellationToken.None);
        var eligibleIgnoringRetry = await repository.ListEligibleWantedAsync("series-main", 10, now, ignoreRetryWindow: true, CancellationToken.None);
        // Only a person's own retry (requireMonitored: false) still reaches the unmonitored one (#653).
        var eligibleIgnoringRetryAndMonitored = await repository.ListEligibleWantedAsync(
            "series-main", 10, now, ignoreRetryWindow: true, CancellationToken.None, requireMonitored: false);

        Assert.Single(eligible);
        Assert.Equal(wantedSeriesId, eligible[0].SeriesId);

        // Ignoring the retry window still excludes the series with a file and
        // the unmonitored one — a targeted automatic search (add-triggered,
        // health-replacement) ignores the retry window but never bypasses
        // monitoring — but includes the merely-deferred one, since that is a
        // retry-window skip, not a monitoring skip.
        Assert.DoesNotContain(eligibleIgnoringRetry, item => item.SeriesId == coveredSeries.Id);
        Assert.Contains(eligibleIgnoringRetry, item => item.SeriesId == wantedSeriesId);
        Assert.DoesNotContain(eligibleIgnoringRetry, item => item.SeriesId == unmonitoredSeries.Id);
        Assert.Contains(eligibleIgnoringRetry, item => item.SeriesId == deferredSeries.Id);

        // A person's own retry still finds the unmonitored one.
        Assert.Contains(eligibleIgnoringRetryAndMonitored, item => item.SeriesId == unmonitoredSeries.Id);
    }

    [Fact]
    public async Task ListEligibleWantedEpisodesAsync_excludes_unmonitored_and_not_yet_eligible_episodes()
    {
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        // The catalogue-sync backfill only creates an episode_wanted_state row when a
        // series_wanted_state row already exists for the library, so seed that first.
        await repository.EnsureWantedStateAsync(
            seriesId, "series-main", "missing", "Needs episodes.", false, null, "WEB 1080p", false, CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Ep 1", null, now.AddDays(-10)),
                new CatalogueEpisodeItem(1, 2, "Ep 2", null, now.AddDays(-9)),
                new CatalogueEpisodeItem(1, 3, "Ep 3", null, now.AddDays(-8)),
            ],
            source: "tmdb",
            CancellationToken.None);

        var episodeIds = await GetEpisodeIdsAsync(storage, seriesId);

        // The query considers wanted_status IN ('missing','upgrade'). Episode 1 is
        // missing and eligible now, episode 2 missing but not eligible until later,
        // and episode 3 unmonitored entirely.
        await using (var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None))
        {
            await SetEpisodeWantedStatusAsync(connection, episodeIds[(1, 1)], "series-main", "missing", nextEligibleSearchUtc: null);
            await SetEpisodeWantedStatusAsync(connection, episodeIds[(1, 2)], "series-main", "missing", nextEligibleSearchUtc: now.AddHours(6));
            await SetEpisodeWantedStatusAsync(connection, episodeIds[(1, 3)], "series-main", "missing", nextEligibleSearchUtc: null);
        }

        await repository.UpdateEpisodeMonitoredAsync([episodeIds[(1, 3)]], monitored: false, CancellationToken.None);

        var eligible = await repository.ListEligibleWantedEpisodesAsync(
            "series-main", 10, now, ignoreRetryWindow: false, CancellationToken.None);

        Assert.Single(eligible);
        Assert.Equal(episodeIds[(1, 1)], eligible[0].EpisodeId);

        // A manual "search now" ignores the retry window, the way the series-level
        // pass already does — episode 2 is inside its window and comes back too.
        // Episode 3 stays out: unmonitored is a decision, not a delay.
        var manual = await repository.ListEligibleWantedEpisodesAsync(
            "series-main", 10, now, ignoreRetryWindow: true, CancellationToken.None);

        Assert.Equal(2, manual.Count);
        Assert.DoesNotContain(manual, item => item.EpisodeId == episodeIds[(1, 3)]);

        // An upgrade-only cycle must not pick up missing episodes, or a library
        // with upgrades on and missing off would search for missing ones anyway.
        var upgradesOnly = await repository.ListEligibleWantedEpisodesAsync(
            "series-main", 10, now, ignoreRetryWindow: true, CancellationToken.None, wantedStatus: "upgrade");

        Assert.Empty(upgradesOnly);
    }

    [Fact]
    public async Task ListWantedEpisodesAsync_excludes_episodes_that_already_have_a_file()
    {
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Ep 1", null, now.AddDays(-10)),
                new CatalogueEpisodeItem(1, 2, "Ep 2", null, now.AddDays(-9)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await repository.ImportExistingAsync(
            libraryId: "series-main",
            title: "Severance",
            startYear: 2022,
            wantedStatus: "covered",
            wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            filePath: @"C:\media\severance\s01e01.mkv",
            fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\severance\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        var wanted = await repository.ListWantedEpisodesAsync(10, CancellationToken.None);

        Assert.Single(wanted);
        var episodeIds = await GetEpisodeIdsAsync(storage, seriesId);
        Assert.Equal(episodeIds[(1, 2)], wanted[0].EpisodeId);
        Assert.Equal(
            123456L,
            await repository.GetEpisodeFileSizeBytesAsync(
                episodeIds[(1, 1)],
                CancellationToken.None));
    }

    [Fact]
    public async Task ImportExistingAsync_writes_series_and_episode_rows_for_an_existing_disk_file()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);

        var created = await repository.ImportExistingAsync(
            libraryId: "series-main",
            title: "Severance",
            startYear: 2022,
            wantedStatus: "covered",
            wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            filePath: @"C:\media\severance\s01e01.mkv",
            fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\severance\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        Assert.True(created);

        var series = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.Equal("Severance", series.Title);
        Assert.True(series.Monitored);

        var episodeIds = await GetEpisodeIdsAsync(storage, series.Id);
        Assert.True(episodeIds.ContainsKey((1, 1)));

        var episode = await ReadEpisodeStateAsync(storage, episodeIds[(1, 1)]);
        Assert.True(episode.HasFile);
        Assert.Equal(@"C:\media\severance\s01e01.mkv", episode.FilePath);
        Assert.NotNull(episode.ImportedUtc);

        // Importing the same series again must not create a duplicate series row.
        var createdAgain = await repository.ImportExistingAsync(
            libraryId: "series-main",
            title: "Severance",
            startYear: 2022,
            wantedStatus: "covered",
            wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            filePath: @"C:\media\severance\s01e01.mkv",
            fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\severance\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        Assert.False(createdAgain);
        Assert.Single(await repository.ListAsync(CancellationToken.None));
    }

    private static async Task SetEpisodeWantedStatusAsync(
        System.Data.Common.DbConnection connection,
        string episodeId,
        string libraryId,
        string wantedStatus,
        DateTimeOffset? nextEligibleSearchUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE episode_wanted_state
            SET wanted_status = @wantedStatus, next_eligible_search_utc = @next
            WHERE episode_id = @episodeId AND library_id = @libraryId;
            """;
        AddParam(command, "@wantedStatus", wantedStatus);
        AddParam(command, "@next", nextEligibleSearchUtc?.ToString("O"));
        AddParam(command, "@episodeId", episodeId);
        AddParam(command, "@libraryId", libraryId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<(int Season, int Episode), string>> GetEpisodeIdsAsync(TestStorage storage, string seriesId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, season_number, episode_number FROM episode_entries WHERE series_id = @seriesId;";
        AddParam(command, "@seriesId", seriesId);

        var result = new Dictionary<(int, int), string>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[(reader.GetInt32(1), reader.GetInt32(2))] = reader.GetString(0);
        }

        return result;
    }

    private sealed record EpisodeState(
        string? Title,
        string? Overview,
        bool HasFile,
        bool QualityCutoffMet,
        string? FilePath,
        string? ImportedUtc,
        bool Monitored);

    private static async Task<EpisodeState> ReadEpisodeStateAsync(TestStorage storage, string episodeId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT title, overview, has_file, quality_cutoff_met, file_path, imported_utc, monitored
            FROM episode_entries WHERE id = @id;
            """;
        AddParam(command, "@id", episodeId);
        using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new EpisodeState(
            Title: reader.IsDBNull(0) ? null : reader.GetString(0),
            Overview: reader.IsDBNull(1) ? null : reader.GetString(1),
            HasFile: reader.GetInt64(2) == 1,
            QualityCutoffMet: reader.GetInt64(3) == 1,
            FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
            ImportedUtc: reader.IsDBNull(5) ? null : reader.GetString(5),
            Monitored: reader.GetInt64(6) == 1);
    }

    private static void AddParam(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
