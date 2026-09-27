using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Platform.Migration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// The shared table every health surface reads from (#909): System &#8594;
/// Health, the Dashboard tile, the bell and outbound notifications all read
/// <see cref="IHealthCheckRepository"/>, so what it tracks -- pass/fail,
/// when a problem started, whether it is snoozed -- is the whole contract.
/// </summary>
public sealed class HealthCheckPersistenceTests
{
    [Fact]
    public async Task A_new_problem_is_reported_as_started()
    {
        var repository = await RepositoryAsync();

        var transitions = await repository.UpsertResultsAsync(
            [HealthCheckOutcome.Problem("download-client:1", "download-client", "Client", "Not reachable.", "cause", "fix")],
            CancellationToken.None);

        var transition = Assert.Single(transitions);
        Assert.Equal(HealthCheckTransition.Started, transition.Transition);
        Assert.Equal(HealthCheckStatuses.Problem, transition.Result.Status);
        Assert.NotNull(transition.Result.ProblemSinceUtc);
    }

    [Fact]
    public async Task A_problem_reported_again_is_unchanged_and_keeps_its_start_time()
    {
        var repository = await RepositoryAsync();
        var first = await repository.UpsertResultsAsync(
            [HealthCheckOutcome.Problem("download-client:1", "download-client", "Client", "Not reachable.", "cause", "fix")],
            CancellationToken.None);
        var startedAt = first.Single().Result.ProblemSinceUtc;

        var second = await repository.UpsertResultsAsync(
            [HealthCheckOutcome.Problem("download-client:1", "download-client", "Client", "Still not reachable.", "cause", "fix")],
            CancellationToken.None);

        var transition = Assert.Single(second);
        Assert.Equal(HealthCheckTransition.Unchanged, transition.Transition);
        Assert.Equal(startedAt, transition.Result.ProblemSinceUtc);
    }

    [Fact]
    public async Task A_problem_that_passes_is_reported_as_cleared_and_loses_its_snooze()
    {
        var repository = await RepositoryAsync();
        await repository.UpsertResultsAsync(
            [HealthCheckOutcome.Problem("download-client:1", "download-client", "Client", "Not reachable.", "cause", "fix")],
            CancellationToken.None);
        await repository.SnoozeAsync("download-client:1", DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);

        var transitions = await repository.UpsertResultsAsync(
            [HealthCheckOutcome.Ok("download-client:1", "download-client", "Client", "Reachable again.")],
            CancellationToken.None);

        var transition = Assert.Single(transitions);
        Assert.Equal(HealthCheckTransition.Cleared, transition.Transition);
        Assert.Null(transition.Result.ProblemSinceUtc);
        Assert.Null(transition.Result.SnoozedUntilUtc);
    }

    [Fact]
    public async Task Snoozing_a_problem_still_counts_it_but_not_as_unsnoozed()
    {
        var repository = await RepositoryAsync();
        await repository.UpsertResultsAsync(
            [HealthCheckOutcome.Problem("indexer:1", "indexer", "Indexer", "Not reachable.", "cause", "fix")],
            CancellationToken.None);

        await repository.SnoozeAsync("indexer:1", DateTimeOffset.UtcNow.AddHours(24), CancellationToken.None);
        var summary = await repository.GetSummaryAsync(CancellationToken.None);

        Assert.Equal(1, summary.ProblemCount);
        Assert.Equal(0, summary.UnsnoozedProblemCount);
    }

    [Fact]
    public async Task Immediate_run_requests_are_read_once_and_cleared()
    {
        var repository = await RepositoryAsync();

        await repository.RequestImmediateRunAsync(HealthCheckCategories.Library, CancellationToken.None);
        await repository.RequestImmediateRunAsync(HealthCheckCategories.Library, CancellationToken.None);
        await repository.RequestImmediateRunAsync(HealthCheckCategories.Indexer, CancellationToken.None);

        var pending = await repository.TakePendingImmediateRunsAsync(CancellationToken.None);
        Assert.Equal(
            new[] { HealthCheckCategories.Indexer, HealthCheckCategories.Library },
            pending.OrderBy(category => category, StringComparer.Ordinal).ToArray());

        var second = await repository.TakePendingImmediateRunsAsync(CancellationToken.None);
        Assert.Empty(second);
    }

    private static async Task<IHealthCheckRepository> RepositoryAsync()
    {
        // Not disposed: TestStorage's backing files live for the process and
        // xunit runs each test in its own instance, matching the pattern the
        // other persistence tests in this file's neighbourhood already use
        // for a repository that outlives a single `using` block.
        var storage = TestStorage.Create();
        var timeProvider = TimeProvider.System;
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteHealthCheckRepository(storage.Factory, timeProvider);
    }
}
