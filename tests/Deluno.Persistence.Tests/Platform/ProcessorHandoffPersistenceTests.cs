using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class ProcessorHandoffPersistenceTests
{
    [Fact]
    public async Task EnsureProcessorHandoffAsync_is_idempotent_and_persists_completion_correlation()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var request = new CreateProcessorHandoffRequest(
            "library-1", "movies", "client-1", "queue-1", "Dune Part Two", "/downloads/dune", "FileFlows");

        var first = await repository.EnsureProcessorHandoffAsync(request, CancellationToken.None);
        var repeated = await repository.EnsureProcessorHandoffAsync(request, CancellationToken.None);
        var completed = await repository.UpdateProcessorHandoffAsync(
            first.Id, "completed", "/processed/dune.mkv", "import-job-1", null, CancellationToken.None);

        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal("waiting", first.Status);
        Assert.NotNull(completed);
        Assert.Equal("completed", completed.Status);
        Assert.Equal("/processed/dune.mkv", completed.OutputPath);
        Assert.Equal("import-job-1", completed.ImportJobId);

        var matched = await repository.FindProcessorHandoffAsync("library-1", null, "/downloads/dune", CancellationToken.None);
        Assert.NotNull(matched);
        Assert.Equal(first.Id, matched.Id);
        Assert.Single(await repository.ListProcessorHandoffsAsync("library-1", 10, CancellationToken.None));
    }

    [Fact]
    public async Task FindProcessorHandoffAsync_does_not_match_a_handoff_from_another_library()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var handoff = await repository.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("library-a", "movies", "client-1", "queue-1", "Dune", "/downloads/dune", null),
            CancellationToken.None);

        var mismatched = await repository.FindProcessorHandoffAsync("library-b", handoff.Id, null, CancellationToken.None);

        Assert.Null(mismatched);
    }

    [Fact]
    public async Task Failed_handoff_can_be_retried_after_repository_reload_without_creating_a_duplicate()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var firstRepository = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var handoff = await firstRepository.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("library-1", "movies", "client-1", "queue-1", "Dune", "/downloads/dune", "FileFlows"),
            CancellationToken.None);
        await firstRepository.UpdateProcessorHandoffAsync(
            handoff.Id, "failed", null, null, "The processor endpoint was unavailable.", CancellationToken.None);

        var reloadedRepository = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var failed = await reloadedRepository.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.NotNull(failed);
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("The processor endpoint was unavailable.", failed.FailureMessage);

        var retried = await reloadedRepository.UpdateProcessorHandoffAsync(
            handoff.Id, "waiting", null, null, null, CancellationToken.None);
        var repeated = await reloadedRepository.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("library-1", "movies", "client-1", "queue-1", "Dune", "/downloads/dune", "FileFlows"),
            CancellationToken.None);

        Assert.NotNull(retried);
        Assert.Equal("waiting", retried!.Status);
        Assert.Null(retried.FailureMessage);
        Assert.Equal(handoff.Id, repeated.Id);
        Assert.Single(await reloadedRepository.ListProcessorHandoffsAsync("library-1", 10, CancellationToken.None));
    }
}
