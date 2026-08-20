using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Security;

public sealed class SqliteSecurityRepositoryTests
{
    [Fact]
    public async Task ValidateApiKey_throttles_last_used_writes_to_once_per_minute()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-20T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSecurityRepository(storage.Factory, time);
        var created = await repository.CreateApiKeyAsync(
            new CreateApiKeyRequest("throttle test", "queue"),
            CancellationToken.None);

        var first = await repository.ValidateApiKeyAsync(created.ApiKey, CancellationToken.None);
        Assert.Equal(time.GetUtcNow(), first!.LastUsedUtc);

        time.Advance(TimeSpan.FromSeconds(59));
        var repeated = await repository.ValidateApiKeyAsync(created.ApiKey, CancellationToken.None);
        Assert.Equal(first.LastUsedUtc, repeated!.LastUsedUtc);

        time.Advance(TimeSpan.FromSeconds(1));
        var afterMinute = await repository.ValidateApiKeyAsync(created.ApiKey, CancellationToken.None);
        Assert.Equal(time.GetUtcNow(), afterMinute!.LastUsedUtc);
    }
}
