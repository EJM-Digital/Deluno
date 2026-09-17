using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Retention;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>#522: Seerr servers are stored like media servers, and a clearing Seerr refused is owed until it is done.</summary>
public sealed class SeerrPersistenceTests
{
    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-17T12:00:00Z"));

    [Fact]
    public async Task A_key_is_stored_protected_and_a_blank_one_keeps_it()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);

        var created = await repository.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055/", "key-1", true), CancellationToken.None);
        Assert.Equal(("http://127.0.0.1:5055", "key-1", true), (created!.BaseUrl, created.ApiKey, created.HasApiKey));

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT api_key FROM seerr_servers;";
            Assert.NotEqual("key-1", (string)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        }

        await repository.RecordHealthAsync(created.Id, new SeerrTestResult(true, "healthy", "Connected to Lab Seerr (version 3.4.1).", 9, "Lab Seerr", "3.4.1"), CancellationToken.None);
        var renamed = await repository.SaveAsync(created.Id, new SaveSeerrRequest("Jellyseerr", "http://127.0.0.1:5055", "", true), CancellationToken.None);
        Assert.Equal(("Jellyseerr", "key-1", "3.4.1", "healthy"), (renamed!.Name, renamed.ApiKey, renamed.Version, renamed.HealthStatus));

        var moved = await repository.SaveAsync(created.Id, new SaveSeerrRequest("Jellyseerr", "http://192.168.1.60:5055", "", true), CancellationToken.None);
        Assert.Equal(("untested", (string?)null), (moved!.HealthStatus, moved.Version));
    }

    [Fact]
    public async Task A_clearing_Seerr_refused_is_owed_and_goes_once_it_succeeds()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);
        await repository.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", true), CancellationToken.None);
        var client = new SwitchableClient { Down = true };
        var service = new RetentionSeerrService(repository, client, storage.Factory, Clock, NullLogger<RetentionSeerrService>.Instance);

        var note = await service.ClearAsync("m1", "movies", "Nosferatu", "653", null, CancellationToken.None);

        Assert.Equal("Lab Seerr could not be cleared yet (Deluno could not reach Lab Seerr); Deluno tries again at the next check.", note);
        var owed = Assert.Single(await service.ListCleanupsAsync(CancellationToken.None));
        Assert.Equal(("m1", "Nosferatu", "653", 1), (owed.EntityId, owed.Title, owed.TmdbId, owed.Attempts));

        await service.RetryAsync(CancellationToken.None);
        Assert.Equal(2, Assert.Single(await service.ListCleanupsAsync(CancellationToken.None)).Attempts);

        client.Down = false;
        await service.RetryAsync(CancellationToken.None);

        Assert.Empty(await service.ListCleanupsAsync(CancellationToken.None));
        Assert.Equal(["movies:653", "movies:653", "movies:653"], client.Asked);
    }

    private static async Task<SqliteSeerrRepository> RepositoryAsync(TestStorage storage)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, Clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteSeerrRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));
    }

    private sealed class SwitchableClient : ISeerrClient
    {
        public bool Down { get; set; }

        public List<string> Asked { get; } = [];

        public Task<SeerrTestResult> TestAsync(SeerrConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaServerAnswer<SeerrLibrary>> ListRequestsAsync(SeerrConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaServerAnswer<SeerrClearing>> ClearAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken)
        {
            Asked.Add($"{mediaType}:{tmdbId}");
            return Task.FromResult<MediaServerAnswer<SeerrClearing>>(Down
                ? new MediaServerAnswer<SeerrClearing>.Unreachable($"Deluno could not reach {connection.Name}.")
                : new MediaServerAnswer<SeerrClearing>.Reported(new SeerrClearing(true, "cleared")));
        }
    }
}
