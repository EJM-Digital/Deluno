using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Series.Data;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #1498. Deluno restarted in the middle of importing Gladiator II from the processor's cleaned output. Restart recovery
/// imported the file again with no title (an "Unknown Year" copy, a title named after the path) while the queue's own import
/// wrote the right copy. One import per file, whatever step the restart came at; the result tied to its title; and a file
/// with no title is never written.
/// </summary>
public sealed partial class ImportPipelineServiceTests
{
    private const string GladiatorRelease = "Gladiator.II.2024.1080p.BluRay.x264-HiP_EniaHD.mkv";

    public static TheoryData<string, bool> RestartSteps => new()
    {
        { "killed-after-staging", true },
        { "killed-after-staging", false },
        { "killed-mid-move", true },
        { "killed-mid-move", false },
        { "killed-after-placing-before-the-title-was-told", true },
        { "killed-after-placing-before-the-title-was-told", false },
        { "killed-after-everything", true },
        { "killed-after-everything", false }
    };

    [Theory]
    [MemberData(nameof(RestartSteps))]
    public async Task A_restart_at_any_step_of_an_import_leaves_one_copy_and_one_title(string step, bool recoveryFirst)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-11-05T05:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var film = await movies.AddAsync(new CreateMovieRequest("Gladiator II", 2024, "tt9218128"), CancellationToken.None);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var bytes = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        var sourcePath = Path.Combine(downloadsPath, GladiatorRelease);
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                sourcePath, GladiatorRelease, "movies", "Gladiator II", 2024, [], [], null, null, ImdbId: "tt9218128"),
            TransferMode: "copy",
            Overwrite: false,
            AllowCopyFallback: true);
        await File.WriteAllBytesAsync(sourcePath, bytes);
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));
        var destinationPath = (await service.PreviewAsync(request.Preview, CancellationToken.None)).DestinationPath;
        var stagingPath = destinationPath + ".deluno-stage-restarttest.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var marker = new ImportRestartMarker(sourcePath, destinationPath, stagingPath, "movies", "Gladiator II", JsonSerializer.Serialize(request));

        switch (step)
        {
            case "killed-after-staging":
                await File.WriteAllBytesAsync(stagingPath, bytes);
                await File.WriteAllTextAsync(ImportRestartMarkerFile.PathFor(stagingPath), JsonSerializer.Serialize(marker));
                break;
            case "killed-mid-move":
                // A move consumed the download into the staging file.
                File.Delete(sourcePath);
                await File.WriteAllBytesAsync(stagingPath, bytes);
                File.SetLastWriteTimeUtc(stagingPath, DateTime.UtcNow.AddMinutes(-1));
                await File.WriteAllTextAsync(ImportRestartMarkerFile.PathFor(stagingPath), JsonSerializer.Serialize(marker));
                break;
            case "killed-after-placing-before-the-title-was-told":
                await File.WriteAllBytesAsync(destinationPath, bytes);
                await File.WriteAllTextAsync(ImportRestartMarkerFile.PathFor(stagingPath), JsonSerializer.Serialize(marker));
                break;
            case "killed-after-everything":
                Assert.True((await service.ExecuteAsync(request, CancellationToken.None)).Succeeded);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }

        // After the restart, the queue puts the import back (the same request) and recovery looks at what was left.
        var recovery = CreateRestartRecoveryService(libraries, service, jobStore);
        if (recoveryFirst)
        {
            await recovery.RecoverAsync(CancellationToken.None);
            await service.ExecuteAsync(request, CancellationToken.None);
        }
        else
        {
            await service.ExecuteAsync(request, CancellationToken.None);
            await recovery.RecoverAsync(CancellationToken.None);
        }

        var media = Directory.EnumerateFiles(movieRootPath, "*", SearchOption.AllDirectories).ToArray();
        var copy = Assert.Single(media);
        Assert.EndsWith(".mkv", copy);
        Assert.Contains("Gladiator II (2024)", copy);
        Assert.DoesNotContain("Unknown Year", string.Join('\n', media), StringComparison.OrdinalIgnoreCase);
        var titles = await movies.ListAsync(CancellationToken.None);
        var only = Assert.Single(titles);
        Assert.Equal(film.Id, only.Id);
        Assert.True((await movies.GetByIdAsync(film.Id, CancellationToken.None))!.HasFile, "the one copy is tied to the title");
    }

    [Fact]
    public async Task Restart_recovery_runs_the_import_with_the_title_and_grab_it_was_for()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null);
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(scene.SourcePath, null, "movies", "Gladiator II", 2024, [], [], null, null, ImdbId: "tt9218128", LibraryId: rig.Library.Id),
            TransferMode: "copy",
            Overwrite: false,
            AllowCopyFallback: true,
            DispatchId: "dispatch-1");
        File.WriteAllText(
            scene.MarkerPath,
            JsonSerializer.Serialize(new ImportRestartMarker(scene.SourcePath, scene.DestinationPath, scene.StagingPath, "movies", "Gladiator II", JsonSerializer.Serialize(request))));

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        var rerun = Assert.Single(rig.Pipeline.Imports);
        Assert.Equal("Gladiator II", rerun.Preview.Title);
        Assert.Equal(2024, rerun.Preview.Year);
        Assert.Equal("tt9218128", rerun.Preview.ImdbId);
        Assert.Equal(rig.Library.Id, rerun.Preview.LibraryId);
        Assert.Equal("dispatch-1", rerun.DispatchId);
        Assert.Equal(scene.SourcePath, rerun.Preview.SourcePath);
    }

    [Fact]
    public async Task Recovery_leaves_a_file_to_the_import_that_is_running_it_and_finishes_it_afterwards()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null);

        using (ImportSourceClaims.TryClaim(scene.SourcePath))
        {
            var held = await rig.CreateService().RecoverAsync(CancellationToken.None);
            Assert.Equal(new ImportRestartRecoveryReport(0, 0, 1), held);
            Assert.Empty(rig.Pipeline.Imports);
            Assert.True(File.Exists(scene.MarkerPath), "the note stays until the running import is done");
            Assert.True(File.Exists(scene.StagingPath));
        }

        var after = await rig.CreateService().RecoverAsync(CancellationToken.None);
        Assert.Equal(1, after.Recovered);
        Assert.Single(rig.Pipeline.Imports);
    }

    [Fact]
    public async Task A_second_import_of_a_file_that_is_being_imported_waits_and_writes_nothing()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-11-05T05:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var sourcePath = Path.Combine(downloadsPath, GladiatorRelease);
        await File.WriteAllBytesAsync(sourcePath, new byte[4096]);

        using var claim = ImportSourceClaims.TryClaim(sourcePath);
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Gladiator II", 2024, [], [], null, null, ImdbId: "tt9218128"),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ImportFileReadiness.RetryableStatusCode, result.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(movieRootPath, "*", SearchOption.AllDirectories));
        Assert.Empty(await movies.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_film_with_no_year_is_never_filed_as_Unknown_Year()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-11-05T05:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var sourcePath = Path.Combine(downloadsPath, GladiatorRelease);
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        // A title, an id, and no year: the year token would read "Unknown Year".
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Gladiator II", null, [], [], null, null, ImdbId: "tt9218128"),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(movieRootPath, "*", SearchOption.AllDirectories));
        Assert.Empty(await movies.ListAsync(CancellationToken.None));
        var recovery = Assert.Single((await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Equal("unmatched", recovery.FailureKind);
        Assert.Contains("Unknown Year", recovery.Summary);
    }

    [Fact]
    public void Unknown_Year_is_only_ever_produced_by_the_naming_renderer_and_no_import_writes_it()
    {
        // The one producer is the renderer's year token; every other place that builds a name uses the renderer.
        var root = FindRepoRoot();
        var separator = Path.DirectorySeparatorChar;
        var producers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}") && !path.Contains($"{separator}bin{separator}"))
            .Where(path => File.ReadAllText(path).Contains("Unknown Year\"", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .ToArray();
        Assert.Equal(["NamingTemplateRenderer.cs"], producers);

        // And the import refuses a destination that carries it, before it writes anything.
        Assert.Contains(
            "HasUnknownYear(preview.DestinationPath)",
            File.ReadAllText(Path.Combine(root, "src", "Deluno.Filesystem", "ImportPipelineService.cs")));
        Assert.True(NamingTemplateRenderer.HasUnknownYear("E:/Movies/Gladiator.II (Unknown Year)/x.mkv"));
        Assert.False(NamingTemplateRenderer.HasUnknownYear("E:/Movies/Gladiator II (2024)/x.mkv"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("a-series-id-that-is-not-in-the-library")]
    public async Task An_episode_with_no_title_and_no_show_Deluno_has_is_never_written_and_no_show_is_made_for_it(string? seriesId)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-11-05T05:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var sourcePath = Path.Combine(downloadsPath, "Say.Yes.to.the.Dress.S08E12.1080p.WEB-DL.x264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "tv", null, null, [], [], null, null, SeriesId: seriesId),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(File.Exists(sourcePath), "the download is untouched");
        Assert.Empty(Directory.EnumerateFiles(seriesRootPath, "*", SearchOption.AllDirectories));
        Assert.Empty(await seriesRepository.ListAsync(CancellationToken.None));
        var recovery = Assert.Single((await seriesRepository.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Equal("unmatched", recovery.FailureKind);
        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
    }

    [Fact]
    public async Task An_episode_with_no_title_whose_show_id_no_longer_exists_is_never_written_even_when_the_folder_name_has_no_year()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-11-05T05:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath, seriesFolderFormat: "{Series Title}");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var sourcePath = Path.Combine(downloadsPath, "Say.Yes.to.the.Dress.S08E12.1080p.WEB-DL.x264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "tv", null, null, [], [], null, null, SeriesId: "a-series-id-that-is-not-in-the-library"),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(seriesRootPath, "*", SearchOption.AllDirectories));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task An_episode_with_no_title_and_no_show_Deluno_has_is_refused_with_the_reason_before_anything_is_written()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-11-05T05:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var sourcePath = Path.Combine(downloadsPath, "Say.Yes.to.the.Dress.S08E12.1080p.WEB-DL.x264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "tv", null, null, [], [], null, null),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("could not tell which show", result.Message);
        Assert.Empty(Directory.EnumerateFiles(seriesRootPath, "*", SearchOption.AllDirectories));
        Assert.Empty(await seriesRepository.ListAsync(CancellationToken.None));
        var recovery = Assert.Single((await seriesRepository.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Equal("unmatched", recovery.FailureKind);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}

public sealed class ShelfNeverShowsAPathTests
{
    [Theory]
    [InlineData("E:\\Weir\\Ready\\Movies\\Gladiator.II.2024.1080p.BluRay.x264-HiP_EniaHD.mkv", "Gladiator.II.2024.1080p.BluRay.x264-HiP_EniaHD.mkv")]
    [InlineData("/data/ready/Movies/Sintel.2010.mkv", "Sintel.2010.mkv")]
    [InlineData("Sintel.2010.1080p", "Sintel.2010.1080p")]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("E:\\Weir\\Ready\\Movies\\", "Movies")]
    [InlineData("/", null)]
    [InlineData("\\", null)]
    public void An_arrivals_release_is_a_name_never_a_path(string? evidence, string? expected)
        => Assert.Equal(expected, Deluno.Worker.Dashboard.DashboardLiveStore.NameOnly(evidence));
}
