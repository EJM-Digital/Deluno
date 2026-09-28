using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// Optional path pairs on a processor connection (#962), stored and listed
/// the same shape as download client remote path mappings (#809).
/// </summary>
public sealed class ProcessorPathMappingPersistenceTests
{
    private static async Task<(SqliteProcessorRepository Repository, ProcessorConnectionItem Connection)> SceneAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T02:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteProcessorRepository(storage.Factory, clock, TestSecretProtection.Create(storage));
        var connection = await repository.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "generic-webhook", "https://weir.example.test/webhook", "Authorization", null, true),
            CancellationToken.None);
        return (repository, connection);
    }

    [Fact]
    public async Task A_created_mapping_is_listed_back_with_its_pair()
    {
        using var storage = TestStorage.Create();
        var (repository, connection) = await SceneAsync(storage);

        var created = await repository.CreateProcessorPathMappingAsync(
            connection.Id,
            new CreateProcessorPathMappingRequest("D:\\Refined", "/data/refined", true, 5),
            CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal("D:\\Refined", created!.DelunoPath);
        Assert.Equal("/data/refined", created.ProcessorPath);
        Assert.Equal(5, created.Priority);

        var listed = Assert.Single(await repository.ListProcessorPathMappingsAsync(connection.Id, CancellationToken.None));
        Assert.Equal(created.Id, listed.Id);
    }

    [Fact]
    public async Task A_mapping_for_an_unknown_connection_is_refused()
    {
        using var storage = TestStorage.Create();
        var (repository, _) = await SceneAsync(storage);

        var created = await repository.CreateProcessorPathMappingAsync(
            "no-such-connection",
            new CreateProcessorPathMappingRequest("D:\\Refined", "/data/refined", true, null),
            CancellationToken.None);

        Assert.Null(created);
    }

    [Fact]
    public async Task A_mapping_missing_either_path_is_refused()
    {
        using var storage = TestStorage.Create();
        var (repository, connection) = await SceneAsync(storage);

        var created = await repository.CreateProcessorPathMappingAsync(
            connection.Id,
            new CreateProcessorPathMappingRequest("D:\\Refined", "  ", true, null),
            CancellationToken.None);

        Assert.Null(created);
    }

    [Fact]
    public async Task Deleting_a_mapping_removes_it_and_a_second_delete_finds_nothing()
    {
        using var storage = TestStorage.Create();
        var (repository, connection) = await SceneAsync(storage);
        var created = (await repository.CreateProcessorPathMappingAsync(
            connection.Id,
            new CreateProcessorPathMappingRequest("D:\\Refined", "/data/refined", true, null),
            CancellationToken.None))!;

        Assert.True(await repository.DeleteProcessorPathMappingAsync(connection.Id, created.Id, CancellationToken.None));
        Assert.Empty(await repository.ListProcessorPathMappingsAsync(connection.Id, CancellationToken.None));
        Assert.False(await repository.DeleteProcessorPathMappingAsync(connection.Id, created.Id, CancellationToken.None));
    }
}
