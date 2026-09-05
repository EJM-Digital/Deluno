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

    /// <summary>
    /// The store's half of #459. A blank scope reaching this layer used to be
    /// written as <c>all</c>, so a caller who said nothing about what a key may
    /// do was handed a key that could do everything.
    ///
    /// <para>The endpoint refuses a blank scope now, so nothing should arrive
    /// here blank. This is the floor under that: if one ever does, the narrowest
    /// scope is the safe guess, not the widest.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_scope_is_stored_as_the_narrowest_one_not_the_widest(string? scopes)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-20T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSecurityRepository(storage.Factory, time);

        var created = await repository.CreateApiKeyAsync(
            new CreateApiKeyRequest("blank scope", scopes),
            CancellationToken.None);

        Assert.Equal("read", created.Item.Scopes);
        Assert.DoesNotContain("all", created.Item.Scopes);
    }
}
