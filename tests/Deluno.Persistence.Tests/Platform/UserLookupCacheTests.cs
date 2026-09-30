using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Data;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// Every authenticated request looks its user up; a short cache keeps that off
/// the database, and every user change drops it (#1081).
/// </summary>
public sealed class UserLookupCacheTests
{
    [Fact]
    public async Task A_repeat_lookup_is_served_from_memory_until_it_expires()
    {
        using var storage = TestStorage.Create();
        var clock = new MovableClock(DateTimeOffset.Parse("2026-09-30T08:00:00Z"));
        var repository = await CreateAsync(storage, clock);
        var user = await Bootstrap(repository);
        Assert.NotNull(await repository.GetUserByIdAsync(user.Id, CancellationToken.None));

        await DeleteUsersDirectlyAsync(storage);

        // Within 30 seconds the cached copy answers without touching the database.
        Assert.NotNull(await repository.GetUserByIdAsync(user.Id, CancellationToken.None));

        clock.Now = clock.Now.AddSeconds(31);
        Assert.Null(await repository.GetUserByIdAsync(user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Changing_the_password_or_signing_out_everywhere_drops_the_cached_user()
    {
        using var storage = TestStorage.Create();
        var clock = new MovableClock(DateTimeOffset.Parse("2026-09-30T08:00:00Z"));
        var repository = await CreateAsync(storage, clock);
        var user = await Bootstrap(repository);
        var before = await repository.GetUserByIdAsync(user.Id, CancellationToken.None);

        Assert.True(await repository.ChangeUserPasswordAsync(user.Id, "old-password", "new-password", CancellationToken.None));
        var afterPassword = await repository.GetUserByIdAsync(user.Id, CancellationToken.None);
        Assert.NotEqual(before!.SecurityStamp, afterPassword!.SecurityStamp);

        Assert.True(await repository.RevokeUserAccessTokensAsync(user.Id, CancellationToken.None));
        var afterRevoke = await repository.GetUserByIdAsync(user.Id, CancellationToken.None);
        Assert.NotEqual(afterPassword.SecurityStamp, afterRevoke!.SecurityStamp);
    }

    private static async Task<SqliteSecurityRepository> CreateAsync(TestStorage storage, TimeProvider clock)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteSecurityRepository(storage.Factory, clock);
    }

    private static Task<UserItem> Bootstrap(SqliteSecurityRepository repository)
        => repository.BootstrapUserAsync(
            new BootstrapUserRequest("admin", "Admin User", "old-password"),
            CancellationToken.None);

    private static async Task DeleteUsersDirectlyAsync(TestStorage storage)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM users;";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
