using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Series;

public sealed class SeriesNumberingPersistenceTests
{
    [Fact]
    public async Task Owner_mapping_survives_provider_refresh_and_resolves_absolute_files_safely()
    {
        var storage = TestStorage.Create();
        using var _ = storage;
        var now = DateTimeOffset.Parse("2026-08-31T02:00:00Z");
        var timeProvider = new FixedTimeProvider(now);

        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Anime Example", 2024, "tt1234567", SeriesType: SeriesTypes.Anime, NumberingScheme: SeriesNumberingSchemes.Absolute),
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, now.AddDays(-2), AbsoluteNumber: 1),
                new CatalogueEpisodeItem(1, 2, "Two", null, now.AddDays(-1), AbsoluteNumber: 2)
            ],
            "provider",
            CancellationToken.None);

        var before = await repository.GetNumberingAsync(series.Id, CancellationToken.None);
        Assert.NotNull(before);
        Assert.Equal(SeriesTypes.Anime, before.SeriesType);
        Assert.Equal(SeriesNumberingSchemes.Absolute, before.NumberingScheme);
        Assert.Equal(1, before.Episodes.Single(item => item.EpisodeNumber == 1).AbsoluteNumber);

        var firstEpisodeId = before.Episodes.Single(item => item.EpisodeNumber == 1).EpisodeId;
        var updated = await repository.UpdateNumberingAsync(
            series.Id,
            new UpdateSeriesNumberingRequest(
                NumberingSource: SeriesNumberingSources.Owner,
                Mappings: [new SeriesNumberingMapping(firstEpisodeId, AbsoluteNumber: 101)]),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(101, updated.Episodes.Single(item => item.EpisodeId == firstEpisodeId).AbsoluteNumber);
        Assert.Equal(SeriesNumberingSources.Owner, updated.Episodes.Single(item => item.EpisodeId == firstEpisodeId).NumberingSource);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One updated", null, now.AddDays(-2), AbsoluteNumber: 1),
                new CatalogueEpisodeItem(1, 2, "Two updated", null, now.AddDays(-1), AbsoluteNumber: 2)
            ],
            "provider",
            CancellationToken.None);

        var after = await repository.GetNumberingAsync(series.Id, CancellationToken.None);
        Assert.NotNull(after);
        Assert.Equal(101, after.Episodes.Single(item => item.EpisodeId == firstEpisodeId).AbsoluteNumber);
        Assert.Equal(SeriesNumberingSources.Owner, after.Episodes.Single(item => item.EpisodeId == firstEpisodeId).NumberingSource);

        var parsed = SeriesNumberingResolver.ParseFileName("Anime Example - 101.mkv", SeriesNumberingSchemes.Absolute);
        Assert.Single(parsed.Matches);
        Assert.True(SeriesNumberingResolver.TryResolve(parsed.Matches[0], after.Episodes, out var resolved, out var reason), reason);
        Assert.Equal(firstEpisodeId, resolved?.EpisodeId);
    }

    [Fact]
    public void Resolver_supports_standard_multi_episode_airdate_and_scene_without_guessing()
    {
        var standard = SeriesNumberingResolver.ParseFileName("Show.S02E03E04.1080p.mkv");
        Assert.Equal(2, standard.Matches.Count);
        Assert.Equal(2, standard.Matches[0].SeasonNumber);
        Assert.Equal(3, standard.Matches[0].EpisodeNumber);
        Assert.Equal(4, standard.Matches[1].EpisodeNumber);

        var airDate = SeriesNumberingResolver.ParseFileName("Show.2026-08-31.mkv", SeriesNumberingSchemes.AirDate);
        Assert.Equal(new DateOnly(2026, 8, 31), Assert.Single(airDate.Matches).AirDate);

        var scene = SeriesNumberingResolver.ParseFileName("Show.S03E07.1080p.mkv", SeriesNumberingSchemes.Scene);
        Assert.Equal(3, Assert.Single(scene.Matches).SceneSeasonNumber);
        Assert.Equal(7, scene.Matches[0].SceneEpisodeNumber);

        var range = SeriesNumberingResolver.ParseFileName("Show.S00E01-E03.mkv");
        Assert.Equal([1, 2, 3], range.Matches.Select(item => item.EpisodeNumber).ToArray());
        Assert.Equal([0], SeriesNumberingResolver.ParseSeasonPackNumbers("Show Season 0 Complete.mkv"));
        Assert.Equal([1], SeriesNumberingResolver.ParseSeasonPackNumbers("Show.S01.1080p.mkv"));
        Assert.Equal([1], SeriesNumberingResolver.ParseSeasonPackNumbers(Path.Combine("downloads", "Show.S01")));
        Assert.Empty(SeriesNumberingResolver.ParseSeasonPackNumbers("Show.S01E01.mkv"));

        var noGuess = SeriesNumberingResolver.ParseFileName("Show.1080p.mkv", SeriesNumberingSchemes.Absolute);
        Assert.Empty(noGuess.Matches);
        Assert.NotNull(noGuess.Warning);
    }

    [Fact]
    public void Series_type_supplies_a_safe_numbering_default_when_scheme_is_omitted()
    {
        Assert.Equal(SeriesNumberingSchemes.Standard, SeriesNumberingSchemes.Resolve(SeriesTypes.Standard, null));
        Assert.Equal(SeriesNumberingSchemes.AirDate, SeriesNumberingSchemes.Resolve(SeriesTypes.Daily, null));
        Assert.Equal(SeriesNumberingSchemes.Absolute, SeriesNumberingSchemes.Resolve(SeriesTypes.Anime, null));
        Assert.Equal(SeriesNumberingSchemes.Scene, SeriesNumberingSchemes.Resolve(SeriesTypes.Daily, SeriesNumberingSchemes.Scene));
    }

    [Fact]
    public async Task Alternate_number_import_resolves_one_catalogued_episode_and_skips_ambiguous_keys()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-08-31T02:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Absolute Import", 2024, "tt7654321", SeriesType: SeriesTypes.Anime, NumberingScheme: SeriesNumberingSchemes.Absolute),
            CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, now.AddDays(-2), AbsoluteNumber: 101),
                new CatalogueEpisodeItem(1, 2, "Two", null, now.AddDays(-1), AbsoluteNumber: 102)
            ],
            "provider",
            CancellationToken.None);

        await repository.ImportExistingAsync(
            "library-1",
            "Absolute Import",
            2024,
            "covered",
            "Imported alternate number",
            "WEB 1080p",
            "WEB 1080p",
            true,
            false,
            @"C:\media\absolute-import\Absolute Import - 101.mkv",
            1024,
            null,
            CancellationToken.None,
            alternateEpisodes:
            [
                new ImportedEpisodeNumberingItem(
                    SeriesNumberingSchemes.Absolute,
                    AbsoluteNumber: 101,
                    FilePath: @"C:\media\absolute-import\Absolute Import - 101.mkv",
                    FileSizeBytes: 1024)
            ]);

        var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var imported = Assert.Single(detail!.Episodes, episode => episode.EpisodeNumber == 1);
        Assert.True(imported.HasFile);
        Assert.Equal(101, imported.AbsoluteNumber);

        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 3, "Duplicate", null, now, AbsoluteNumber: 101)
            ],
            "provider",
            CancellationToken.None);
        await repository.ImportExistingAsync(
            "library-1",
            "Absolute Import",
            2024,
            "covered",
            "Ambiguous alternate number",
            null,
            null,
            true,
            false,
            @"C:\media\absolute-import\Absolute Import - 101-retry.mkv",
            2048,
            null,
            CancellationToken.None,
            alternateEpisodes:
            [
                new ImportedEpisodeNumberingItem(
                    SeriesNumberingSchemes.Absolute,
                    AbsoluteNumber: 101,
                    FilePath: @"C:\media\absolute-import\Absolute Import - 101-retry.mkv",
                    FileSizeBytes: 2048)
            ]);

        var afterAmbiguous = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.DoesNotContain(afterAmbiguous!.Episodes, episode => episode.EpisodeNumber == 3 && episode.HasFile);
    }

    [Fact]
    public async Task Season_pack_import_requires_an_explicit_catalogued_episode_manifest()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-08-31T02:00:00Z");
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Season Pack Example", 2024, "tt9988776"),
            CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(0, 1, "Special", null, now.AddDays(-5)),
                new CatalogueEpisodeItem(1, 1, "One", null, now.AddDays(-4)),
                new CatalogueEpisodeItem(1, 2, "Two", null, now.AddDays(-3)),
                new CatalogueEpisodeItem(1, 3, "Three", null, now.AddDays(-2))
            ],
            "provider",
            CancellationToken.None);

        await repository.ImportExistingAsync(
            "library-1",
            "Season Pack Example",
            2024,
            "covered",
            "Season pack imported",
            "WEB 1080p",
            "WEB 1080p",
            true,
            false,
            @"C:\media\season-pack\Season Pack Example S01.mkv",
            4096,
            null,
            CancellationToken.None,
            seasonPacks:
            [
                new ImportedSeasonPackItem(
                    1,
                    @"C:\media\season-pack\Season Pack Example S01.mkv",
                    4096)
            ]);

        var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.DoesNotContain(detail!.Episodes, item => item.HasFile);

        await repository.ImportExistingAsync(
            "library-1",
            "Season Pack Example",
            2024,
            "covered",
            "Verified season pack manifest imported",
            "WEB 1080p",
            "WEB 1080p",
            true,
            false,
            @"C:\media\season-pack\Season Pack Example S01",
            8192,
            null,
            CancellationToken.None,
            seasonPacks:
            [
                new ImportedSeasonPackItem(
                    1,
                    @"C:\media\season-pack\Season Pack Example S01",
                    8192,
                    [
                        new ImportedEpisodeItem(1, 1, true, @"C:\media\season-pack\S01E01.mkv", 4096),
                        new ImportedEpisodeItem(1, 2, true, @"C:\media\season-pack\S01E02.mkv", 4096),
                        new ImportedEpisodeItem(1, 99, true, @"C:\media\season-pack\S01E99.mkv", 4096)
                    ])
            ]);

        detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.True(detail!.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 1).HasFile);
        Assert.True(detail.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 2).HasFile);
        Assert.False(detail.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 3).HasFile);
        Assert.False(detail.Episodes.Single(item => item.SeasonNumber == 0).HasFile);
        Assert.Equal(
            2,
            detail.Episodes.Count(item => item.SeasonNumber == 1 && item.HasFile));
    }
}
