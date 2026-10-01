using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1097: a recovery case opening or closing, and a hand-off to the processor moving on, are pushed
/// where they are saved, with the data a Live panel needs to draw them without fetching.
/// </summary>
public sealed class LiveEventsFromRecoveryAndProcessorsTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-10-01T09:00:00Z"));
    private readonly RecordingRealtimeEventPublisher _realtime = new();
    private readonly SqliteMovieCatalogRepository _movies;
    private readonly SqliteProcessorRepository _processors;

    public LiveEventsFromRecoveryAndProcessorsTests()
    {
        var migrator = new SqliteDatabaseMigrator(_storage.Factory, _time);
        new PlatformSchemaInitializer(_storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        new MoviesSchemaInitializer(_storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        new SeriesSchemaInitializer(_storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _movies = new SqliteMovieCatalogRepository(_storage.Factory, _time, realtimeEventPublisher: _realtime);
        _processors = new SqliteProcessorRepository(_storage.Factory, _time, TestSecretProtection.Create(_storage), _realtime);
    }

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task A_recovery_case_opening_is_pushed_once_even_when_the_same_failure_repeats()
    {
        var request = new CreateMovieImportRecoveryCaseRequest("Some Film (2019)", "unpack-failed", "The archive was password protected", "Grab a different release");
        var opened = await _movies.AddImportRecoveryCaseAsync(request, CancellationToken.None);
        // The same failure again is the same case: nothing new opened, so nothing new pushed.
        await _movies.AddImportRecoveryCaseAsync(request, CancellationToken.None);

        var change = Assert.Single(_realtime.LiveEventsOf<RecoveryCaseChangedEvent>(LiveEventNames.RecoveryCaseChanged));
        Assert.Equal("opened", change.State);
        Assert.Equal(opened.Id, change.CaseId);
        Assert.Equal("Some Film (2019)", change.Title);
        Assert.Equal(opened.FailureKind, change.Kind);
        Assert.Equal("movie", change.EntityType);
    }

    [Fact]
    public async Task A_recovery_case_settled_by_a_person_or_dismissed_is_pushed_as_closed()
    {
        var resolved = await _movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("One", "unpack-failed", "Broken", "Retry"), CancellationToken.None);
        var dismissed = await _movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Two", "corrupt", "Truncated", "Retry"), CancellationToken.None);

        await _movies.ResolveImportRecoveryCaseAsync(resolved.Id, "resolved", CancellationToken.None);
        await _movies.DeleteImportRecoveryCaseAsync(dismissed.Id, CancellationToken.None);
        // Closing what is already closed pushes nothing.
        await _movies.ResolveImportRecoveryCaseAsync(resolved.Id, "resolved", CancellationToken.None);
        await _movies.DeleteImportRecoveryCaseAsync(dismissed.Id, CancellationToken.None);

        var closed = _realtime.LiveEventsOf<RecoveryCaseChangedEvent>(LiveEventNames.RecoveryCaseChanged)
            .Where(change => change.State == "closed")
            .Select(change => change.CaseId)
            .ToArray();
        Assert.Equal([resolved.Id, dismissed.Id], closed);
    }

    [Fact]
    public async Task A_new_hand_off_a_status_change_and_a_new_processor_state_are_each_pushed_once()
    {
        var handoff = await _processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("lib-films", "movies", "client-1", "queue-1", "Dune.2024", "/downloads/Dune.2024", "Weir"),
            CancellationToken.None);
        // Asking for the same hand-off again creates nothing and says nothing.
        await _processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("lib-films", "movies", "client-1", "queue-1", "Dune.2024", "/downloads/Dune.2024", "Weir"),
            CancellationToken.None);

        await _processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);

        var observation = new ProcessorHandoffObservation("working", "Re-encoding", null, null, _time.GetUtcNow(), _time.GetUtcNow(), null, _time.GetUtcNow());
        await _processors.RecordProcessorObservationAsync(handoff.Id, observation, CancellationToken.None);
        // The same state again on the next check is not a change.
        await _processors.RecordProcessorObservationAsync(handoff.Id, observation, CancellationToken.None);

        await _processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Failed, null, null, "Weir could not read the file.", CancellationToken.None);

        var changes = _realtime.LiveEventsOf<ProcessorHandoffChangedEvent>(LiveEventNames.ProcessorHandoffChanged);
        Assert.Equal(
            [ProcessorHandoffStatuses.Waiting, ProcessorHandoffStatuses.Submitted, "working", ProcessorHandoffStatuses.Failed],
            changes.Select(change => change.State).ToArray());
        Assert.All(changes, change =>
        {
            Assert.Equal(handoff.Id, change.HandoffId);
            Assert.Equal("Dune.2024", change.ReleaseName);
            Assert.Equal("lib-films", change.LibraryId);
            Assert.Equal("queue-1", change.QueueItemId);
        });
        Assert.Equal("Re-encoding", changes[2].Message);
        Assert.Equal("Weir could not read the file.", changes[3].Message);
    }
}
