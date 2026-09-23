using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #606: a native tracker indexer keeps which tracker it is, its settings, and
/// its secrets; the secrets are encrypted at rest and never serialized.
/// </summary>
public sealed class TrackerIndexerPersistenceTests
{
    private static async Task<SqliteConnectionsRepository> CreateRepositoryAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-24T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
    }

    private static CreateIndexerRequest Tracker() =>
        new(Name: "Test Tracker",
            Protocol: "tracker",
            Privacy: "private",
            BaseUrl: "https://tracker.example/",
            ApiKey: null,
            Priority: 5,
            Categories: "2000,5000",
            Tags: null,
            MediaScope: "both",
            IsEnabled: true,
            DefinitionId: "TestTracker",
            DefinitionSettings: new Dictionary<string, string> { ["sort"] = "seeders", ["freeleech"] = "false" },
            DefinitionSecrets: new Dictionary<string, string> { ["username"] = "james", ["password"] = "hunter2" });

    [Fact]
    public async Task Keeps_the_tracker_its_settings_and_its_secrets()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);

        var created = await repo.CreateIndexerAsync(Tracker(), CancellationToken.None);
        var read = (await repo.ListIndexersAsync(CancellationToken.None)).Single(item => item.Id == created.Id);

        Assert.Equal("tracker", read.Protocol);
        Assert.Equal("testtracker", read.DefinitionId);
        Assert.Equal("seeders", read.DefinitionSettings!["sort"]);
        Assert.Equal("hunter2", read.DefinitionSecrets!["password"]);
        Assert.Equal(["password", "username"], read.DefinitionSecretsSet);
    }

    [Fact]
    public async Task The_secrets_are_encrypted_on_disk_and_never_serialized()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(Tracker(), CancellationToken.None);

        var json = JsonSerializer.Serialize((await repo.ListIndexersAsync(CancellationToken.None)).Single());
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.Contains("definitionSecretsSet", JsonSerializer.Serialize(created, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);

        await using var connection = await storage.Factory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT definition_secrets FROM indexer_sources WHERE id = @id;";
        command.Parameters.Add(new SqliteParameter("@id", created.Id));
        var stored = (string)(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain("hunter2", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_replaces_settings_and_merges_secrets()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(Tracker(), CancellationToken.None);

        // The screen never has the saved password to send back: a secret it
        // does not name stays; one it names changes; an empty one is removed.
        var updated = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null,
                DefinitionSettings: new Dictionary<string, string> { ["sort"] = "time" },
                DefinitionSecrets: new Dictionary<string, string> { ["username"] = "james2", ["cookie"] = "", ["pin"] = "1234" }),
            CancellationToken.None);
        var read = (await repo.ListIndexersAsync(CancellationToken.None)).Single();

        Assert.Equal(new Dictionary<string, string> { ["sort"] = "time" }, read.DefinitionSettings);
        Assert.Equal("james2", read.DefinitionSecrets!["username"]);
        Assert.Equal("hunter2", read.DefinitionSecrets["password"]);
        Assert.Equal("1234", read.DefinitionSecrets["pin"]);
        Assert.Equal(updated!.DefinitionSecretsSet, read.DefinitionSecretsSet);
    }

    [Fact]
    public async Task An_update_that_says_nothing_about_the_tracker_leaves_it_alone()
    {
        using var storage = TestStorage.Create();
        var repo = await CreateRepositoryAsync(storage);
        var created = await repo.CreateIndexerAsync(Tracker(), CancellationToken.None);

        await repo.UpdateIndexerAsync(created.Id, new UpdateIndexerRequest("Renamed", null, null, null, null, null, null, null, null, null), CancellationToken.None);
        var read = (await repo.ListIndexersAsync(CancellationToken.None)).Single();

        Assert.Equal("Renamed", read.Name);
        Assert.Equal("seeders", read.DefinitionSettings!["sort"]);
        Assert.Equal("hunter2", read.DefinitionSecrets!["password"]);
    }
}
