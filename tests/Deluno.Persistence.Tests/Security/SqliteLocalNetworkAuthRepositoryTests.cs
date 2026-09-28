using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Security;

public sealed class SqliteLocalNetworkAuthRepositoryTests
{
    private static async Task<SqliteLocalNetworkAuthRepository> CreateAsync(TestStorage storage, FixedTimeProvider time)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteLocalNetworkAuthRepository(storage.Factory, time);
    }

    [Fact]
    public async Task A_fresh_database_reads_off_with_nothing_saved()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateAsync(storage, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z")));

        var settings = await repository.GetAsync(CancellationToken.None);

        Assert.False(settings.Enabled);
        Assert.Empty(settings.AdditionalLocalNetworks);
        Assert.Empty(settings.AllowList);
        Assert.Empty(settings.TrustedProxies);
    }

    [Fact]
    public async Task Saved_settings_round_trip()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateAsync(storage, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z")));

        await repository.SaveAsync(
            new LocalNetworkAuthSettings(true, ["203.0.113.0/24"], ["192.168.1.20"], ["10.0.0.5"]),
            CancellationToken.None);
        var saved = await repository.GetAsync(CancellationToken.None);

        Assert.True(saved.Enabled);
        Assert.Equal(["203.0.113.0/24"], saved.AdditionalLocalNetworks);
        Assert.Equal(["192.168.1.20"], saved.AllowList);
        Assert.Equal(["10.0.0.5"], saved.TrustedProxies);
    }
}
