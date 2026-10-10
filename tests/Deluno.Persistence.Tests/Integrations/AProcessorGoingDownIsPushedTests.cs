using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1202: Weir quit and Deluno's Dashboard and System said so, while the tray stayed green for 12 minutes because the
/// stored health changed without a push. A processor whose health changes is now announced like any other health
/// change, and a repeat of the same health says nothing.
/// </summary>
public sealed class AProcessorGoingDownIsPushedTests
{
    [Fact]
    public async Task A_processor_that_stops_answering_is_pushed_once()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-10T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var publisher = new RecordingPublisher();
        var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage), publisher);
        var weir = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "weir", "http://127.0.0.1:9347/submit", null, null, IsEnabled: true),
            CancellationToken.None);

        await processors.RecordProcessorConnectionHealthAsync(weir.Id, "unreachable", "Weir did not answer.", CancellationToken.None);
        await processors.RecordProcessorConnectionHealthAsync(weir.Id, "unreachable", "Weir did not answer.", CancellationToken.None);

        var pushed = Assert.Single(publisher.Health);
        Assert.Equal($"processor:{weir.Id}", pushed.Source);
        Assert.Equal("unreachable", pushed.Status);
    }

    [Fact]
    public async Task A_processor_that_answers_again_asks_for_its_checks_at_once()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-10T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var healthChecks = new SqliteHealthCheckRepository(storage.Factory, time);
        var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage), null, null, healthChecks);
        var weir = await processors.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "weir", "http://127.0.0.1:9347/submit", null, null, IsEnabled: true),
            CancellationToken.None);

        await processors.RecordProcessorConnectionHealthAsync(weir.Id, "unreachable", "Weir did not answer.", CancellationToken.None);
        Assert.Empty(await healthChecks.TakePendingImmediateRunsAsync(CancellationToken.None));

        // Weir answers again: the processor checks run now, not on the fifteen-minute pass.
        await processors.RecordProcessorConnectionHealthAsync(weir.Id, "healthy", null, CancellationToken.None);
        Assert.Equal([HealthCheckCategories.Processor], await healthChecks.TakePendingImmediateRunsAsync(CancellationToken.None));

        // A Test while all is well asks for nothing; a Test while a processor row is still unknown asks again.
        await processors.RecordProcessorConnectionHealthAsync(weir.Id, "healthy", null, CancellationToken.None);
        Assert.Empty(await healthChecks.TakePendingImmediateRunsAsync(CancellationToken.None));

        await healthChecks.UpsertResultsAsync(
            [HealthCheckOutcome.Unknown($"processor:{weir.Id}:watching:lib", HealthCheckCategories.Processor, "Weir watches", "did not answer Deluno's capabilities check")],
            CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await processors.RecordProcessorConnectionHealthAsync(weir.Id, "healthy", null, CancellationToken.None);
        Assert.Equal([HealthCheckCategories.Processor], await healthChecks.TakePendingImmediateRunsAsync(CancellationToken.None));
    }

    private sealed class RecordingPublisher : NullRealtimeEventPublisher
    {
        public List<(string Source, string Status)> Health { get; } = [];

        public override Task PublishHealthChangedAsync(string source, string status, string message, CancellationToken cancellationToken)
        {
            Health.Add((source, status));
            return Task.CompletedTask;
        }
    }
}
