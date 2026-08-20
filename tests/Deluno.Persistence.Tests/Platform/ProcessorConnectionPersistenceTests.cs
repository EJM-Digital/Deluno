using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class ProcessorConnectionPersistenceTests
{
    [Fact]
    public async Task Processor_connections_encrypt_secrets_and_preserve_health_without_exposing_the_secret()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T02:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));

        var created = await repository.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest(
                "FileFlows",
                "fileflows-webhook",
                "https://fileflows.example.test/webhook/deluno",
                "Authorization",
                "secret-token",
                IsEnabled: true),
            CancellationToken.None);

        Assert.True(created.SecretConfigured);
        Assert.Equal("fileflows-webhook", created.Provider);
        Assert.Equal("https://fileflows.example.test/webhook/deluno", created.SubmissionUrl);

        var persisted = Assert.Single(await repository.ListProcessorConnectionsAsync(CancellationToken.None));
        Assert.True(persisted.SecretConfigured);
        Assert.Equal("secret-token", persisted.Secret);

        var health = await repository.RecordProcessorConnectionHealthAsync(
            created.Id,
            "healthy",
            "Processor endpoint responded successfully.",
            CancellationToken.None);
        Assert.NotNull(health);
        Assert.Equal("healthy", health!.HealthStatus);
        Assert.Equal(clock.GetUtcNow(), health.LastHealthTestUtc);

        var updated = await repository.UpdateProcessorConnectionAsync(
            created.Id,
            new UpdateProcessorConnectionRequest(
                "FileFlows primary",
                "generic-webhook",
                "https://fileflows.example.test/webhook/primary",
                "X-Api-Key",
                null,
                IsEnabled: false),
            CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal("FileFlows primary", updated!.Name);
        Assert.Equal("generic-webhook", updated.Provider);
        Assert.Equal("X-Api-Key", updated.AuthHeaderName);
        Assert.Equal("secret-token", updated.Secret);
        Assert.False(updated.IsEnabled);

        Assert.Equal(updated.Id, (await repository.FindProcessorConnectionByNameAsync("fileflows primary", CancellationToken.None))!.Id);
        Assert.True(await repository.DeleteProcessorConnectionAsync(created.Id, CancellationToken.None));
        Assert.Empty(await repository.ListProcessorConnectionsAsync(CancellationToken.None));
    }
}
