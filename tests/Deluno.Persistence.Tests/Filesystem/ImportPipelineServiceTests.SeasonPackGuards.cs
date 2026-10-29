using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// What a season pack is allowed to do before and after it places a file: one season per folder, one free-space
/// check for the whole folder, and a follow-up that fails after the files and catalogue committed. Real files in a
/// temp folder, the real series store, the real import service.
/// </summary>
public sealed partial class ImportPipelineServiceTests
{
    private sealed class PackScene : IDisposable
    {
        public required TestStorage Storage { get; init; }
        public required ImportPipelineService Service { get; init; }
        public required SqliteSeriesCatalogRepository Repository { get; init; }
        public required string SeriesId { get; init; }
        public required string SeriesTitle { get; init; }
        public required string Downloads { get; init; }
        public required string SeriesRoot { get; init; }
        public void Dispose() => Storage.Dispose();

        public string PlacedFolder => Path.Combine(SeriesRoot, SeriesTitle + " (2026)");

        public int PlacedFileCount => Directory.Exists(PlacedFolder) ? Directory.GetFiles(PlacedFolder, "*.mkv", SearchOption.AllDirectories).Length : 0;

        public ImportExecuteRequest Request(string folder)
            => new(
                new ImportPreviewRequest(
                    folder, null, "tv", SeriesTitle, 2026, [], [], null, null,
                    SeriesId: SeriesId, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
                "copy", false, true);
    }

    private async Task<PackScene> PackSceneAsync(
        string title,
        int[] seasons,
        int episodesPerSeason,
        int? minimumFreeSpaceMb = null,
        Func<string, long?>? freeSpaceProbe = null,
        IJobScheduler? mediaServerScheduler = null)
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloads = Path.Combine(storage.DataRoot, "downloads");
        var seriesRoot = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(seriesRoot);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, Path.Combine(storage.DataRoot, "movies"), downloads, seriesRoot, minimumFreeSpaceMb: minimumFreeSpaceMb);
        await CreateTvLibraryAsync(libraries, seriesRoot, downloads);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest(title, 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [.. seasons.SelectMany(season => Enumerable.Range(1, episodesPerSeason)
                .Select(episode => new CatalogueEpisodeItem(season, episode, $"Episode {episode}", null, timeProvider.GetUtcNow().AddDays(-2))))],
            "provider",
            CancellationToken.None);
        var service = CreateService(
            storage, timeProvider, platform, libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: repository,
            mediaServerJobScheduler: mediaServerScheduler,
            freeSpaceBytesProbe: freeSpaceProbe);
        return new PackScene
        {
            Storage = storage,
            Service = service,
            Repository = repository,
            SeriesId = series.Id,
            SeriesTitle = title,
            Downloads = downloads,
            SeriesRoot = seriesRoot
        };
    }

    private static async Task<string> WritePackAsync(string downloads, string folderName, params (int Season, int Episode)[] files)
    {
        var folder = Path.Combine(downloads, folderName);
        Directory.CreateDirectory(folder);
        foreach (var (season, episode) in files)
        {
            var path = Path.Combine(folder, $"{folderName.Split('.')[0]}.S{season:D2}E{episode:D2}.WEB-DL.mkv");
            await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)(0x30 + season * 8 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        }

        return folder;
    }

    private sealed class ThrowingJobScheduler : IJobScheduler
    {
        public Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The media server queue is unavailable.");
    }

    [Fact]
    public async Task A_season_pack_folder_that_does_not_name_exactly_one_season_blocks_the_whole_pack_and_places_nothing()
    {
        using var scene = await PackSceneAsync("Mixed", [1, 2], 2);

        // The folder names two seasons.
        var twoSeasons = await WritePackAsync(scene.Downloads, "Mixed.S01-S02.WEB-DL", (1, 1), (1, 2));
        var refused = await scene.Service.ExecuteAsync(scene.Request(twoSeasons), CancellationToken.None);
        Assert.False(refused.Succeeded);
        Assert.Equal(409, refused.StatusCode);
        Assert.Contains("exactly one season", refused.Message);

        // The folder names no season at all.
        var noSeason = await WritePackAsync(scene.Downloads, "Mixed.WEB-DL", (1, 1), (1, 2));
        var noneRefused = await scene.Service.ExecuteAsync(scene.Request(noSeason), CancellationToken.None);
        Assert.False(noneRefused.Succeeded);
        Assert.Equal(409, noneRefused.StatusCode);

        // The folder names season 1; one file inside resolves to season 2, so the good file is held back too.
        var oneOutside = await WritePackAsync(scene.Downloads, "Mixed.S01.WEB-DL", (1, 1), (2, 1));
        var blocked = await scene.Service.ExecuteAsync(scene.Request(oneOutside), CancellationToken.None);
        Assert.False(blocked.Succeeded);
        Assert.Equal(409, blocked.StatusCode);
        Assert.Contains("S02E01", blocked.Message);
        Assert.Contains("outside season 01", blocked.Message);

        Assert.Equal(0, scene.PlacedFileCount);
        Assert.Equal(2, Directory.GetFiles(oneOutside, "*.mkv").Length);
        Assert.DoesNotContain((await scene.Repository.GetInventoryDetailAsync(scene.SeriesId, CancellationToken.None))!.Episodes, e => e.HasFile);
    }

    [Fact]
    public async Task A_season_pack_checks_free_space_once_for_its_folder_and_places_nothing_when_it_is_short()
    {
        var asked = new List<string>();
        long free = 100L * 1024 * 1024;
        using var scene = await PackSceneAsync("Roomy", [1], 3, minimumFreeSpaceMb: 500, freeSpaceProbe: path =>
        {
            lock (asked) { asked.Add(path); }
            return free;
        });
        var folder = await WritePackAsync(scene.Downloads, "Roomy.S01.WEB-DL", (1, 1), (1, 2), (1, 3));

        var short_ = await scene.Service.ExecuteAsync(scene.Request(folder), CancellationToken.None);
        Assert.False(short_.Succeeded);
        Assert.Equal(507, short_.StatusCode);
        Assert.Contains("below the configured minimum", short_.Message);
        Assert.Equal(0, scene.PlacedFileCount);
        Assert.Equal(3, Directory.GetFiles(folder, "*.mkv").Length);
        // Three files, one question: the destination folder the whole pack lands in.
        Assert.Single(asked);
        Assert.Equal(scene.PlacedFolder, asked[0].TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);

        asked.Clear();
        free = long.MaxValue;
        var roomy = await scene.Service.ExecuteAsync(scene.Request(folder), CancellationToken.None);
        Assert.True(roomy.Succeeded, roomy.Message);
        Assert.Single(asked);
        Assert.Equal(3, scene.PlacedFileCount);
    }

    [Fact]
    public async Task A_follow_up_that_fails_after_the_pack_and_catalogue_committed_still_reports_the_pack_imported()
    {
        using var scene = await PackSceneAsync("Follow", [1], 2, mediaServerScheduler: new ThrowingJobScheduler());
        var folder = await WritePackAsync(scene.Downloads, "Follow.S01.WEB-DL", (1, 1), (1, 2));

        var result = await scene.Service.ExecuteAsync(scene.Request(folder), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("The season pack was imported, but a follow-up record failed: The media server queue is unavailable.", result.Message);
        Assert.Equal(2, scene.PlacedFileCount);
        Assert.Equal(2, (await scene.Repository.GetInventoryDetailAsync(scene.SeriesId, CancellationToken.None))!.Episodes.Count(e => e.HasFile));
        // Nothing was rolled back: the sources are still there as copies, and no failed-import case was opened.
        Assert.Equal(2, Directory.GetFiles(folder, "*.mkv").Length);
        var recovery = await scene.Repository.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.DoesNotContain(recovery.RecentCases, c => c.Summary.Contains("rolled back", StringComparison.OrdinalIgnoreCase));
    }
}
