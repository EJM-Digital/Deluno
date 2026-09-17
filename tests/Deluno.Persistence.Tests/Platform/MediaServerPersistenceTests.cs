using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>#493: media servers are stored like the other connections.</summary>
public sealed class MediaServerPersistenceTests
{
    [Fact]
    public async Task A_token_is_stored_protected_and_a_blank_one_keeps_it()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);

        var created = await repository.SaveAsync(null, new SaveMediaServerRequest("jellyfin", "Rig Jellyfin", "http://192.168.1.51:8096/", "api-key-1", true), CancellationToken.None);
        Assert.NotNull(created);
        Assert.Equal("http://192.168.1.51:8096", created!.BaseUrl);
        Assert.Equal("api-key-1", created.Token);
        Assert.True(created.HasToken);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT token FROM media_servers;";
            Assert.NotEqual("api-key-1", (string)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        }

        var renamed = await repository.SaveAsync(created.Id, new SaveMediaServerRequest("jellyfin", "Jellyfin on the rig", "http://192.168.1.51:8096", "", true), CancellationToken.None);
        Assert.Equal("Jellyfin on the rig", renamed!.Name);
        Assert.Equal("api-key-1", renamed.Token);

        // Leaving soon on home (#521): off by default, and a save that does not say keeps it.
        Assert.False(renamed.LeavingSoonOnHome);
        var onHome = await repository.SaveAsync(created.Id, new SaveMediaServerRequest("jellyfin", "Jellyfin on the rig", "http://192.168.1.51:8096", "", true, LeavingSoonOnHome: true), CancellationToken.None);
        Assert.True(onHome!.LeavingSoonOnHome);
        Assert.True((await repository.SaveAsync(created.Id, new SaveMediaServerRequest("jellyfin", "Jellyfin on the rig", "http://192.168.1.51:8096", "", true), CancellationToken.None))!.LeavingSoonOnHome);
    }

    /// <summary>
    /// What a server called itself belongs to the address it was said from. A
    /// row pointed somewhere else must not keep claiming to be the old machine.
    /// </summary>
    [Fact]
    public async Task A_new_address_forgets_who_the_old_one_was()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);
        var created = await repository.SaveAsync(null, new SaveMediaServerRequest("emby", "Emby", "http://192.168.1.51:8097", "key", true), CancellationToken.None);
        await repository.RecordHealthAsync(created!.Id, new MediaServerTestResult(true, "healthy", "Connected to RIG-EMBY", 12, "RIG-EMBY", "abc"), CancellationToken.None);
        Assert.Equal("RIG-EMBY", (await repository.GetAsync(created.Id, CancellationToken.None))!.ServerName);

        var moved = await repository.SaveAsync(created.Id, new SaveMediaServerRequest("emby", "Emby", "http://192.168.1.60:8096", "", true), CancellationToken.None);

        Assert.Null(moved!.ServerName);
        Assert.Null(moved.ServerId);
        Assert.Equal("untested", moved.HealthStatus);
    }

    private static async Task<SqliteMediaServerRepository> RepositoryAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-16T12:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteMediaServerRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
    }
}
