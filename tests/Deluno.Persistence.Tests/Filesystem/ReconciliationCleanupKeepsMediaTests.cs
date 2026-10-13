using Deluno.Filesystem;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Series.Data;
using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #1259: "cleanup-artifact" used to delete a staging or backup file for good. A stage file can be the only copy of a
/// move import and a backup file is the previous library file, so both leave through the recycle bin, or stay.
/// </summary>
public sealed class ReconciliationCleanupKeepsMediaTests
{
    [Theory]
    [InlineData(".deluno-stage-test.tmp")]
    [InlineData(".deluno-backup-test.tmp")]
    public async Task Cleaning_up_an_artifact_sends_it_to_the_recycle_bin_and_says_so(string suffix)
    {
        using var storage = TestStorage.Create();
        var (service, jobs, bin, artifactPath) = await SetUpAsync(storage, suffix, withBin: true);
        var artifact = Assert.Single((await service.ScanAsync(CancellationToken.None)).Issues, item => item.Kind == "partialImportArtifact");

        var result = await service.RepairAsync(new FilesystemReconciliationRepairRequest(artifact.Id, "cleanup-artifact"), CancellationToken.None);

        Assert.True(result.Repaired);
        Assert.Contains("recycle bin", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(artifactPath));
        Assert.Single(await bin!.ListAsync(CancellationToken.None));
        var activity = await jobs.ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Message.Contains("recycle bin", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(".deluno-stage-test.tmp")]
    [InlineData(".deluno-backup-test.tmp")]
    public async Task Without_a_recycle_bin_the_artifact_is_kept_never_deleted(string suffix)
    {
        using var storage = TestStorage.Create();
        var (service, jobs, _, artifactPath) = await SetUpAsync(storage, suffix, withBin: false);
        var artifact = Assert.Single((await service.ScanAsync(CancellationToken.None)).Issues, item => item.Kind == "partialImportArtifact");

        var result = await service.RepairAsync(new FilesystemReconciliationRepairRequest(artifact.Id, "cleanup-artifact"), CancellationToken.None);

        Assert.False(result.Repaired);
        Assert.True(File.Exists(artifactPath));
        Assert.Contains("left", result.Message, StringComparison.OrdinalIgnoreCase);
        var activity = await jobs.ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Message.Contains("left", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(FilesystemReconciliationService Service, SqliteJobStore Jobs, RecycleBinService? Bin, string ArtifactPath)> SetUpAsync(
        TestStorage storage, string suffix, bool withBin)
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-14T08:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var root = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(root);
        var libraries = new SqliteLibrariesRepository(storage.Factory, time);
        await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies", MediaType: "movies", Purpose: "Main", RootPath: root, DownloadsPath: null,
                QualityProfileId: null, ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null, ProcessorFailureMode: null, AutoSearchEnabled: false,
                MissingSearchEnabled: false, UpgradeSearchEnabled: false, SearchIntervalMinutes: 6, RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);
        var artifactPath = Path.Combine(root, "Film.2024.mkv" + suffix);
        await File.WriteAllBytesAsync(artifactPath, Enumerable.Range(0, 2048).Select(v => (byte)(v % 200)).ToArray());

        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        RecycleBinService? bin = withBin
            ? new RecycleBinService(Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }), time, NullLogger<RecycleBinService>.Instance)
            : null;
        var service = new FilesystemReconciliationService(
            libraries,
            new SqliteMovieCatalogRepository(storage.Factory, time),
            new SqliteSeriesCatalogRepository(storage.Factory, time),
            jobs,
            time,
            null,
            bin);
        return (service, jobs, bin, artifactPath);
    }
}
