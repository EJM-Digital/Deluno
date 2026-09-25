using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The bug proven live on the rig (#606): The Pirate Bay's circuit opened,
/// survived a restart because it is persisted in integration_circuit_states,
/// and the "Reset circuit" button only touched the indexer_sources row --
/// never the resilience policy's own state -- so every search kept silently
/// skipping the indexer even after a person reset it.
/// </summary>
public sealed class IntegrationCircuitStatePersistenceTests
{
    [Fact]
    public async Task A_circuit_opened_by_one_policy_instance_is_still_open_after_a_restart()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await InitializeSchemaAsync(storage, timeProvider);

        var beforeRestart = new IntegrationResiliencePolicy(
            timeProvider,
            storage.Factory,
            NullLogger<IntegrationResiliencePolicy>.Instance);

        var request = new IntegrationResilienceRequest(
            "indexer:tpb-id:torznab:https://thepiratebay.example:443",
            "search",
            MaxAttempts: 1,
            FailureThreshold: 1,
            ServiceType: "indexer",
            ServiceId: "tpb-id",
            ServiceName: "The Pirate Bay");

        var opened = await beforeRestart.ExecuteAsync(
            request,
            _ => Task.FromResult("down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);
        Assert.True(opened.CircuitOpened);

        // A fresh process: a brand new policy instance, backed by the same
        // jobs database, standing in for a redeploy/restart.
        var afterRestart = new IntegrationResiliencePolicy(
            timeProvider,
            storage.Factory,
            NullLogger<IntegrationResiliencePolicy>.Instance);

        var skipped = await afterRestart.ExecuteAsync(
            request,
            _ => Task.FromResult("should-not-run"),
            _ => IntegrationResilienceOutcome.Success,
            CancellationToken.None);

        Assert.True(skipped.CircuitOpen);
    }

    /// <summary>
    /// #732: a fixed one-minute break never escalated, so a dead indexer or
    /// client got retried every minute forever. The escalation level -- how
    /// many consecutive openings this circuit has had with no success in
    /// between -- rides along with the rest of the persisted row: a restart
    /// mid-streak must resume climbing the ladder instead of dropping back to
    /// the first (one-minute) rung.
    /// </summary>
    [Fact]
    public async Task Escalation_level_survives_a_restart_and_keeps_climbing_the_ladder()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await InitializeSchemaAsync(storage, timeProvider);

        var request = new IntegrationResilienceRequest(
            "indexer:tpb-id:torznab:https://thepiratebay.example:443",
            "search",
            MaxAttempts: 1,
            FailureThreshold: 1,
            ServiceType: "indexer",
            ServiceId: "tpb-id",
            ServiceName: "The Pirate Bay");

        var beforeRestart = new IntegrationResiliencePolicy(
            timeProvider,
            storage.Factory,
            NullLogger<IntegrationResiliencePolicy>.Instance);

        // First opening: one minute.
        var first = await beforeRestart.ExecuteAsync(
            request,
            _ => Task.FromResult("down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(1), first.RetryAfterUtc!.Value - timeProvider.GetUtcNow());
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        // Second opening in the same streak, still the pre-restart process:
        // five minutes.
        var second = await beforeRestart.ExecuteAsync(
            request,
            _ => Task.FromResult("still-down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(5), second.RetryAfterUtc!.Value - timeProvider.GetUtcNow());

        // A fresh process, backed by the same jobs database, standing in for
        // a restart while the second break is still running. Its first call
        // loads persisted state -- including the escalation level -- while
        // the row is still in the "circuit_open_until_utc > now" window that
        // load query filters on; that has to happen before the break ends,
        // or the freshly-expired row is skipped and the level is lost.
        var afterRestart = new IntegrationResiliencePolicy(
            timeProvider,
            storage.Factory,
            NullLogger<IntegrationResiliencePolicy>.Instance);

        var stillOpenAfterRestart = await afterRestart.ExecuteAsync(
            request,
            _ => Task.FromResult("should-not-run"),
            _ => IntegrationResilienceOutcome.Success,
            CancellationToken.None);
        Assert.True(stillOpenAfterRestart.CircuitOpen);

        timeProvider.Advance(TimeSpan.FromMinutes(5));

        var third = await afterRestart.ExecuteAsync(
            request,
            _ => Task.FromResult("still-down-after-restart"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);

        // Fifteen minutes -- the third rung -- not back at one minute: the
        // escalation level came from the database, not a fresh in-memory
        // circuit.
        Assert.Equal(TimeSpan.FromMinutes(15), third.RetryAfterUtc!.Value - timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task CloseCircuitsAsync_clears_the_persisted_row_so_it_does_not_reappear_after_a_restart()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await InitializeSchemaAsync(storage, timeProvider);

        var policy = new IntegrationResiliencePolicy(
            timeProvider,
            storage.Factory,
            NullLogger<IntegrationResiliencePolicy>.Instance);

        var request = new IntegrationResilienceRequest(
            "indexer:tpb-id:torznab:https://thepiratebay.example:443",
            "search",
            MaxAttempts: 1,
            FailureThreshold: 1,
            ServiceType: "indexer",
            ServiceId: "tpb-id",
            ServiceName: "The Pirate Bay");

        var opened = await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);
        Assert.True(opened.CircuitOpened);

        // This is what the reset-circuit endpoint now calls. Note the caller
        // only knows "indexer"/"tpb-id" -- not the exact suffix
        // ("torznab:https://...") the search path happened to build the key
        // with -- and closing still has to reach it.
        await policy.CloseCircuitsAsync("indexer", "tpb-id", CancellationToken.None);

        Assert.False(policy.IsCircuitOpen(request.Key, out _));

        // And a brand new process reading the same database must not see it
        // either -- the whole point is that the persisted row is gone, not
        // just masked in this process's memory.
        var afterRestart = new IntegrationResiliencePolicy(
            timeProvider,
            storage.Factory,
            NullLogger<IntegrationResiliencePolicy>.Instance);

        var resumed = await afterRestart.ExecuteAsync(
            request,
            _ => Task.FromResult("ok-now"),
            _ => IntegrationResilienceOutcome.Success,
            CancellationToken.None);

        Assert.False(resumed.CircuitOpen);
        Assert.Equal("ok-now", resumed.Value);
    }

    [Fact]
    public async Task CloseCircuitsAsync_closes_every_key_suffix_recorded_under_the_same_service_id()
    {
        // The manual Test endpoint and the background search path build
        // different key suffixes for the same indexer (Test includes the
        // protocol, search does not). A close keyed only on "indexer"/id has
        // to reach both, or a successful Test can report healthy while the
        // circuit a search actually consults stays open.
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await InitializeSchemaAsync(storage, timeProvider);

        var policy = new IntegrationResiliencePolicy(
            timeProvider,
            storage.Factory,
            NullLogger<IntegrationResiliencePolicy>.Instance);

        var searchRequest = new IntegrationResilienceRequest(
            "indexer:tpb-id:https://thepiratebay.example",
            "search",
            MaxAttempts: 1,
            FailureThreshold: 1,
            ServiceType: "indexer",
            ServiceId: "tpb-id",
            ServiceName: "The Pirate Bay");
        var testRequest = searchRequest with { Key = "indexer:tpb-id:torznab:https://thepiratebay.example:443" };

        await policy.ExecuteAsync(searchRequest, _ => Task.FromResult("down"), _ => IntegrationResilienceOutcome.RetryableFailure, CancellationToken.None);
        await policy.ExecuteAsync(testRequest, _ => Task.FromResult("down"), _ => IntegrationResilienceOutcome.RetryableFailure, CancellationToken.None);

        Assert.True(policy.IsCircuitOpen(searchRequest.Key, out _));
        Assert.True(policy.IsCircuitOpen(testRequest.Key, out _));

        await policy.CloseCircuitsAsync("indexer", "tpb-id", CancellationToken.None);

        Assert.False(policy.IsCircuitOpen(searchRequest.Key, out _));
        Assert.False(policy.IsCircuitOpen(testRequest.Key, out _));
    }

    private static async Task InitializeSchemaAsync(TestStorage storage, TimeProvider timeProvider)
        => await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
}
