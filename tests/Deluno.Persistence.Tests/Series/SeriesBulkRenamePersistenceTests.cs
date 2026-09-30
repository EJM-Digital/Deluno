using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Series;

/// <summary>
/// #788: the series rename-apply endpoint reads every episode's file path
/// through <see cref="SqliteSeriesCatalogRepository.ListEpisodeFilePathsAsync"/>
/// and, after moving the folder on disk itself, rewrites each one through
/// <see cref="SqliteSeriesCatalogRepository.UpdateEpisodeFileLocationAsync"/>.
/// These pin that persistence contract.
/// </summary>
public sealed class SeriesBulkRenamePersistenceTests
{
    [Fact]
    public async Task ListEpisodeFilePathsAsync_returns_only_this_series_files_with_a_file()
    {
        var now = DateTimeOffset.Parse("2026-09-26T00:00:00Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);

        var target = await repository.AddAsync(new CreateSeriesRequest("The Expanse", 2015, "tt3230854"), CancellationToken.None);
        var other = await repository.AddAsync(new CreateSeriesRequest("Severance", 2022, "tt11280740"), CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(target.Id, [new CatalogueEpisodeItem(1, 1, "Dulcinea", null, now.AddDays(-40)), new CatalogueEpisodeItem(1, 2, "The Big Empty", null, now.AddDays(-39))], source: "tmdb", CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(other.Id, [new CatalogueEpisodeItem(1, 1, "Good News About Hell", null, now.AddDays(-40))], source: "tmdb", CancellationToken.None);

        await repository.ImportExistingAsync(
            "tv-main", "The Expanse", 2015, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: @"C:\tv-main\The Expanse (2015)\Season 01\S01E01.mkv", fileSizeBytes: 1,
            episodes:
            [
                new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\tv-main\The Expanse (2015)\Season 01\S01E01.mkv", FileSizeBytes: 1),
                new ImportedEpisodeItem(1, 2, HasFile: true, FilePath: @"C:\tv-main\The Expanse (2015)\Season 01\S01E02.mkv", FileSizeBytes: 1)
            ],
            CancellationToken.None);
        await repository.ImportExistingAsync(
            "tv-main", "Severance", 2022, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: @"C:\tv-main\Severance (2022)\Season 01\S01E01.mkv", fileSizeBytes: 1,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\tv-main\Severance (2022)\Season 01\S01E01.mkv", FileSizeBytes: 1)],
            CancellationToken.None);

        var paths = await repository.ListEpisodeFilePathsAsync(target.Id, CancellationToken.None);

        Assert.Equal(2, paths.Count);
        Assert.All(paths, entry => Assert.Contains("The Expanse", entry.FilePath));
    }

    [Fact]
    public async Task UpdateEpisodeFileLocationAsync_only_rewrites_the_row_matching_the_previous_path()
    {
        var now = DateTimeOffset.Parse("2026-09-26T00:00:00Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);

        var series = await repository.AddAsync(new CreateSeriesRequest("The Expanse", 2015, "tt3230854"), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(series.Id, [new CatalogueEpisodeItem(1, 1, "Dulcinea", null, now.AddDays(-40))], source: "tmdb", CancellationToken.None);

        var oldPath = @"C:\tv-main\The Expanse (2015)\Season 01\S01E01.mkv";
        await repository.ImportExistingAsync(
            "tv-main", "The Expanse", 2015, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: oldPath, fileSizeBytes: 1,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: oldPath, FileSizeBytes: 1)],
            CancellationToken.None);

        var episodeId = (await repository.ListEpisodeFilePathsAsync(series.Id, CancellationToken.None)).Single().EpisodeId;
        var newPath = @"C:\tv-main\The Expanse (2015) [renamed]\Season 01\S01E01.mkv";

        // A stale previous path (already moved, or never matched) must refuse
        // rather than overwrite a row it cannot prove is the one that moved.
        Assert.False(await repository.UpdateEpisodeFileLocationAsync(episodeId, "C:\\wrong\\path.mkv", newPath, CancellationToken.None));

        Assert.True(await repository.UpdateEpisodeFileLocationAsync(episodeId, oldPath, newPath, CancellationToken.None));

        var after = (await repository.ListEpisodeFilePathsAsync(series.Id, CancellationToken.None)).Single();
        Assert.Equal(newPath, after.FilePath);
    }

    [Fact]
    public async Task UpdateEpisodeFileLocationsAsync_moves_the_shows_own_remembered_path_with_the_episodes()
    {
        var now = DateTimeOffset.Parse("2026-09-26T00:00:00Z");
        var storage = TestStorage.Create();
        using var _ = storage;
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);

        var series = await repository.AddAsync(new CreateSeriesRequest("The Expanse", 2015, "tt3230854"), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(series.Id, [new CatalogueEpisodeItem(1, 1, "Dulcinea", null, now.AddDays(-40))], source: "tmdb", CancellationToken.None);
        var oldPath = @"C:\tv-main\The Expanse (2015)\Season 01\S01E01.mkv";
        await repository.ImportExistingAsync(
            "tv-main", "The Expanse", 2015, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: oldPath, fileSizeBytes: 1,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: oldPath, FileSizeBytes: 1)],
            CancellationToken.None);
        var episodeId = (await repository.ListEpisodeFilePathsAsync(series.Id, CancellationToken.None)).Single().EpisodeId;
        var newPath = @"C:\tv-main\The Expanse (2015) [renamed]\Season 01\S01E01.mkv";

        await repository.UpdateEpisodeFileLocationsAsync([(episodeId, oldPath, newPath)], CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM series_wanted_state WHERE series_id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = series.Id;
        command.Parameters.Add(parameter);
        Assert.Equal(newPath, (string?)await command.ExecuteScalarAsync(CancellationToken.None));
    }
}
