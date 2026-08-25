using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class LibraryWorkflowCleanupPersistenceTests
{
    [Fact]
    public async Task Workflow_cleanup_policy_round_trips_and_is_updated_with_the_workflow()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-24T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteLibrariesRepository(storage.Factory, clock);
        var created = await repository.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Anime",
                MediaType: "tv",
                Purpose: "Anime",
                RootPath: @"C:\Media\Anime",
                DownloadsPath: @"C:\Downloads\Anime",
                QualityProfileId: null,
                ImportWorkflow: "standard",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null,
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: true,
                SearchIntervalHours: null,
                RetryDelayHours: null,
                MaxItemsPerRun: null,
                CleanupMode: "remove-source-after-import",
                RemoveEmptySourceFolders: true),
            CancellationToken.None);

        Assert.Equal("remove-source-after-import", created.CleanupMode);
        Assert.True(created.RemoveEmptySourceFolders);

        var updated = await repository.UpdateLibraryWorkflowAsync(
            created.Id,
            new UpdateLibraryWorkflowRequest(
                ImportWorkflow: "refine-before-import",
                ProcessorName: "Weir",
                ProcessorOutputPath: @"C:\Processed",
                ProcessorTimeoutMinutes: 120,
                ProcessorFailureMode: "manual-review",
                CleanupMode: "keep-source",
                RemoveEmptySourceFolders: false),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("keep-source", updated!.CleanupMode);
        Assert.False(updated.RemoveEmptySourceFolders);
        Assert.Equal("refine-before-import", updated.ImportWorkflow);
    }
}
