using Deluno.Filesystem;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>Low-severity gap rows (#1329) for the import pipeline, each on real files in a temp folder.</summary>
public sealed partial class ImportPipelineServiceTests
{
    [Fact]
    public async Task A_preview_names_the_file_from_the_first_tag_and_the_first_genre_and_leaves_the_token_empty_when_there_is_none()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloads = Path.Combine(storage.DataRoot, "downloads");
        var movieRoot = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(movieRoot);
        var source = Path.Combine(downloads, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(source, new byte[2048]);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRoot, downloads, movieFileFormat: "{Movie Title} ({Release Year}) [{Tag}] [{Genre}] [{Network}] [{Quality Profile}]");
        await CreateMovieLibraryAsync(libraries, movieRoot, downloads);
        var service = CreateService(storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));

        var withValues = await service.PreviewAsync(
            new ImportPreviewRequest(source, null, "movies", "Arrival", 2016, ["Drama", "Mystery"], ["kids", "extra"], null, "en", Network: "HBO", QualityProfile: "HD"),
            CancellationToken.None);
        Assert.Equal("Arrival (2016) [kids] [Drama] [HBO] [HD].mkv", Path.GetFileName(withValues.DestinationPath));

        var withNone = await service.PreviewAsync(
            new ImportPreviewRequest(source, null, "movies", "Arrival", 2016, [], [], null, "en"),
            CancellationToken.None);
        Assert.Equal("Arrival (2016).mkv", Path.GetFileName(withNone.DestinationPath));
    }

    private async Task<ImportPipelineResult> ImportArrivalWithFreeSpaceAsync(int minimumFreeSpaceMb, long freeBytes)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloads = Path.Combine(storage.DataRoot, "downloads");
        var movieRoot = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(movieRoot);
        var source = Path.Combine(downloads, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(source, new byte[4096]);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRoot, downloads, minimumFreeSpaceMb: minimumFreeSpaceMb);
        await CreateMovieLibraryAsync(libraries, movieRoot, downloads);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies, freeSpaceBytesProbe: _ => freeBytes);

        return await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(source, null, "movies", "Arrival", 2016, ["Drama"], [], "Paramount", "en", ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);
    }

    [Fact]
    public async Task Free_space_exactly_equal_to_the_minimum_imports_a_shortfall_of_one_byte_does_not_and_a_minimum_of_zero_never_checks()
    {
        const long mb = 1024L * 1024;

        Assert.True((await ImportArrivalWithFreeSpaceAsync(500, 500 * mb)).Succeeded);

        var oneByteShort = await ImportArrivalWithFreeSpaceAsync(500, 500 * mb - 1);
        Assert.False(oneByteShort.Succeeded);
        Assert.Equal(StatusCodes.Status507InsufficientStorage, oneByteShort.StatusCode);

        Assert.True((await ImportArrivalWithFreeSpaceAsync(0, 0)).Succeeded);
    }
}
