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
