using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Security;

public sealed class SqliteAllowedHostsRepositoryTests
{
    private static async Task<SqliteAllowedHostsRepository> CreateAsync(TestStorage storage, FixedTimeProvider time)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteAllowedHostsRepository(storage.Factory, time);
    }

    [Fact]
    public async Task A_fresh_database_reads_off_with_no_hosts()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateAsync(storage, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z")));

        var settings = await repository.GetAsync(CancellationToken.None);

        Assert.False(settings.Enabled);
        Assert.Empty(settings.AllowedHosts);
    }

    [Fact]
    public async Task Saved_settings_round_trip()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateAsync(storage, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z")));

        await repository.SaveAsync(new AllowedHostsSettings(true, ["localhost", "my-pc", "*.example.com"]), CancellationToken.None);
        var saved = await repository.GetAsync(CancellationToken.None);

        Assert.True(saved.Enabled);
        Assert.Equal(["localhost", "my-pc", "*.example.com"], saved.AllowedHosts);
    }

    [Fact]
    public async Task Saving_de_duplicates_and_drops_blank_entries()
    {
        using var storage = TestStorage.Create();
        var repository = await CreateAsync(storage, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z")));

        await repository.SaveAsync(new AllowedHostsSettings(true, ["localhost", " ", "LOCALHOST", "", "my-pc"]), CancellationToken.None);
        var saved = await repository.GetAsync(CancellationToken.None);

        Assert.Single(saved.AllowedHosts, host => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("my-pc", saved.AllowedHosts);
        Assert.Equal(2, saved.AllowedHosts.Count);
    }
}
