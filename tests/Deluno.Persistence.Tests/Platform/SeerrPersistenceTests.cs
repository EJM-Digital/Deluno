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
        Assert.Equal(($"Seerr on {Deluno.Contracts.DelunoHostName.Current}", "key-1", "3.4.1", "healthy"), (renamed!.Name, renamed.ApiKey, renamed.Version, renamed.HealthStatus));

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

        var self = $"Seerr on {Deluno.Contracts.DelunoHostName.Current}";
        Assert.Equal($"{self} could not be cleared yet (Deluno could not reach {self}); Deluno tries again at the next check.", note);
        var owed = Assert.Single(await service.ListCleanupsAsync(CancellationToken.None));
        Assert.Equal(("m1", "Nosferatu", "653", 1), (owed.EntityId, owed.Title, owed.TmdbId, owed.Attempts));

        await service.RetryAsync(CancellationToken.None);
        Assert.Equal(2, Assert.Single(await service.ListCleanupsAsync(CancellationToken.None)).Attempts);

        client.Down = false;
        await service.RetryAsync(CancellationToken.None);

        Assert.Empty(await service.ListCleanupsAsync(CancellationToken.None));
        Assert.Equal(["movies:653", "movies:653", "movies:653"], client.Asked);
    }

    /// <summary>#851: the library/quality-profile mapping a server's approvals should land in round-trips, and stays null (today's "first library of that type" fallback) until set.</summary>
    [Fact]
    public async Task A_servers_library_mapping_round_trips_and_defaults_to_unset()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);

        var created = await repository.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", true), CancellationToken.None);
        Assert.Null(created!.MovieLibraryId);
        Assert.Null(created.SeriesLibraryId);

        var mapped = await repository.SaveAsync(
            created.Id,
            new SaveSeerrRequest(
                "Lab Seerr", "http://127.0.0.1:5055", "",
                IsEnabled: true,
                MovieLibraryId: "library-movies",
                MovieQualityProfileId: "profile-movies",
                SeriesLibraryId: "library-tv",
                SeriesQualityProfileId: "profile-tv"),
            CancellationToken.None);

        Assert.Equal(("library-movies", "profile-movies", "library-tv", "profile-tv"),
            (mapped!.MovieLibraryId, mapped.MovieQualityProfileId, mapped.SeriesLibraryId, mapped.SeriesQualityProfileId));

        var reloaded = await repository.GetAsync(created.Id, CancellationToken.None);
        Assert.Equal("library-movies", reloaded!.MovieLibraryId);
    }

    /// <summary>#851/#852: the acquisitions log records an add, is listed newest-first per server, and #852's own lookup finds the latest "added" row by Deluno's entity id.</summary>
    [Fact]
    public async Task Acquisitions_are_recorded_listed_and_found_by_entity()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeerrAcquisitionsRepository(storage.Factory, clock);

        await repository.RecordAsync("conn-1", "Lab Seerr", "movies", "movie-1", "Arrival", 501, "added", null, "James", CancellationToken.None, ruleId: "rule-1", ruleName: "Anime");
        clock.Advance(TimeSpan.FromMinutes(1));
        await repository.RecordAsync("conn-1", "Lab Seerr", "movies", "movie-2", "Sicario", 502, "failed", "No provider id", null, CancellationToken.None);

        var recent = await repository.ListRecentAsync("conn-1", 10, CancellationToken.None);
        Assert.Equal(2, recent.Count);
        Assert.Equal("Sicario", recent[0].Title); // newest first

        var found = await repository.FindLatestAddedAsync("movies", "movie-1", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(501, found!.SeerrMediaId);
        Assert.Equal("added", found.Status);
        Assert.Equal("James", found.RequestedBy); // #985: who asked is recorded on the acquisition
        Assert.Equal(("rule-1", "Anime"), (found.RuleId, found.RuleName)); // #1033: which rule placed it

        Assert.Null(await repository.FindLatestAddedAsync("movies", "movie-2", CancellationToken.None)); // only "failed", never "added"

        await repository.UpdateStatusAsync(found.Id, "available", null, CancellationToken.None);
        var updated = await repository.FindLatestAddedAsync("movies", "movie-1", CancellationToken.None);
        Assert.Null(updated); // no longer "added" once marked available
    }

    /// <summary>#851: a generated webhook secret is stored protected (not the plaintext), round-trips through GetAsync, and regenerating replaces it.</summary>
    [Fact]
    public async Task A_webhook_secret_is_generated_stored_protected_and_replaced_on_regenerate()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);
        var created = await repository.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", true), CancellationToken.None);
        Assert.False(created!.HasWebhookSecret);

        var firstSecret = await repository.RegenerateWebhookSecretAsync(created.Id, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(firstSecret));

        var reloaded = await repository.GetAsync(created.Id, CancellationToken.None);
        Assert.True(reloaded!.HasWebhookSecret);
        Assert.Equal(firstSecret, reloaded.WebhookSecret);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT webhook_secret FROM seerr_servers;";
            Assert.NotEqual(firstSecret, (string)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        }

        var secondSecret = await repository.RegenerateWebhookSecretAsync(created.Id, CancellationToken.None);
        Assert.NotEqual(firstSecret, secondSecret);

        Assert.Null(await repository.RegenerateWebhookSecretAsync("not-a-real-id", CancellationToken.None));
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

        public Task<MediaServerAnswer<bool>> MarkAvailableAsync(SeerrConnection connection, int seerrMediaId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int?> FindMediaIdAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
