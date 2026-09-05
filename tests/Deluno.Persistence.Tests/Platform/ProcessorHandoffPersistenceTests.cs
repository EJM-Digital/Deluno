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

    /// <summary>
    /// Downloading the same release again starts a new hand-off cycle.
    ///
    /// <para><b>Found on the lab, walking acquisition end to end.</b> A film was
    /// removed and re-acquired. It was dispatched, qBittorrent downloaded it,
    /// Weir refined it and wrote the output — and Deluno never imported it.
    /// The refined file sat in the folder while the queue went on saying Deluno
    /// was waiting for a cleaned output.</para>
    ///
    /// <para>The hand-off is keyed on the source path and inserted
    /// <c>ON CONFLICT DO NOTHING</c>, so the second download found the first
    /// download's row — still <c>completed</c>, still carrying the previous
    /// output path and import job. WorkPlanner only acts on a hand-off whose
    /// status is <c>waiting</c>, so nothing was submitted and nothing was
    /// imported.</para>
    ///
    /// <para>Doing nothing is right while the row is still working — that is
    /// what stops one download being handed to the processor twice, and the
    /// test above pins it. It is wrong once the row has finished.</para>
    /// </summary>
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task A_finished_handoff_restarts_when_the_same_release_arrives_again(string finishedStatus)
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-05T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var request = new CreateProcessorHandoffRequest(
            "library-1", "movies", "client-1", "queue-1", "Big Buck Bunny", "/downloads/bbb", "Weir");

        var first = await repository.EnsureProcessorHandoffAsync(request, CancellationToken.None);
        await repository.UpdateProcessorHandoffAsync(
            first.Id, finishedStatus, "/refined/bbb.mkv", "import-job-1", null, CancellationToken.None);

        // The same release, downloaded to the same place, a second time.
        var second = await repository.EnsureProcessorHandoffAsync(
            request with { QueueItemId = "queue-2" },
            CancellationToken.None);

        Assert.Equal("waiting", second.Status);
        Assert.Equal("queue-2", second.QueueItemId);
        // The previous cycle's results must not be inherited by this one.
        Assert.Null(second.OutputPath);
        Assert.Null(second.ImportJobId);
        Assert.Null(second.FailureMessage);
        // Still one row: this is the same source path, restarted, not a second
        // hand-off competing with the first.
        Assert.Single(await repository.ListProcessorHandoffsAsync("library-1", 10, CancellationToken.None));
    }

    /// <summary>
    /// And a hand-off that is still working is left exactly alone, which is the
    /// idempotency the conflict clause exists for.
    ///
    /// <para>Every status the processor stages go through, because the restart
    /// must key on "this cycle has ended" rather than on the one state anybody
    /// happened to think of.</para>
    /// </summary>
    [Theory]
    [InlineData("waiting")]
    [InlineData("submitted")]
    [InlineData("accepted")]
    [InlineData("started")]
    public async Task A_handoff_still_working_is_not_disturbed(string workingStatus)
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-05T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var request = new CreateProcessorHandoffRequest(
            "library-1", "movies", "client-1", "queue-1", "Big Buck Bunny", "/downloads/bbb", "Weir");

        var first = await repository.EnsureProcessorHandoffAsync(request, CancellationToken.None);
        await repository.UpdateProcessorHandoffAsync(
            first.Id, workingStatus, null, null, null, CancellationToken.None);

        var again = await repository.EnsureProcessorHandoffAsync(
            request with { QueueItemId = "queue-2" },
            CancellationToken.None);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(workingStatus, again.Status);
        // Not re-pointed at a different queue item while it is mid-flight.
        Assert.Equal("queue-1", again.QueueItemId);
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
