using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class ReleaseProfilePersistenceTests
{
    [Fact]
    public async Task Release_profiles_round_trip_and_filter_by_global_or_matching_tag()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteReleaseProfileRepository(storage.Factory, timeProvider);
        var global = await repository.CreateAsync(
            new CreateReleaseProfileRequest(
                Name: "Global timing",
                TagName: "",
                PreferredProtocol: "usenet",
                UsenetDelayMinutes: 30,
                TorrentDelayMinutes: 60,
                MustContain: "WEB",
                MustNotContain: "CAM",
                PreferredTerms: [new ReleaseTermScore("Remux", 100)]),
            CancellationToken.None);
        var kids = await repository.CreateAsync(
            new CreateReleaseProfileRequest(
                Name: "Kids rule",
                TagName: "Kids",
                PreferredProtocol: "torrent",
                UsenetDelayMinutes: 0,
                TorrentDelayMinutes: 15,
                MustContain: "",
                MustNotContain: "uncut",
                PreferredTerms: [new ReleaseTermScore("family", 50)]),
            CancellationToken.None);

        var applicable = await repository.ListApplicableAsync(["kids", "4K"], CancellationToken.None);
        Assert.Equal(2, applicable.Count);
        Assert.Contains(applicable, item => item.Id == global.Id);
        var storedKids = Assert.Single(applicable, item => item.Id == kids.Id);
        Assert.Equal("Kids", storedKids.TagName);
        Assert.Equal("torrent", storedKids.PreferredProtocol);
        Assert.Equal(15, storedKids.TorrentDelayMinutes);
        Assert.Equal("uncut", storedKids.MustNotContain);
        Assert.Equal(50, Assert.Single(storedKids.PreferredTerms).Score);

        var unrelated = await repository.ListApplicableAsync(["Sports"], CancellationToken.None);
        Assert.Single(unrelated);
        Assert.Equal(global.Id, unrelated[0].Id);
    }

    [Fact]
    public async Task Release_profiles_support_update_and_delete()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteReleaseProfileRepository(storage.Factory, timeProvider);
        var created = await repository.CreateAsync(
            new CreateReleaseProfileRequest("Rule", "Archive", "any", 0, 0, "", "", []),
            CancellationToken.None);

        var updated = await repository.UpdateAsync(
            created.Id,
            new UpdateReleaseProfileRequest("Updated rule", "Archive", "usenet", 90, 0, "Remux", "", [new ReleaseTermScore("HDR", 25)]),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Updated rule", updated.Name);
        Assert.Equal("usenet", updated.PreferredProtocol);
        Assert.Equal(90, updated.UsenetDelayMinutes);
        Assert.Equal("Remux", updated.MustContain);
        Assert.Equal("HDR", Assert.Single(updated.PreferredTerms).Term);

        Assert.True(await repository.DeleteAsync(created.Id, CancellationToken.None));
        Assert.Null(await repository.GetAsync(created.Id, CancellationToken.None));
        Assert.False(await repository.DeleteAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Release_profiles_reject_duplicate_tag_names_at_the_database_boundary()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteReleaseProfileRepository(storage.Factory, timeProvider);
        await repository.CreateAsync(new CreateReleaseProfileRequest("One", "Kids", "any", 0, 0, "", "", []), CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => repository.CreateAsync(
            new CreateReleaseProfileRequest("Two", " kids ", "any", 0, 0, "", "", []),
            CancellationToken.None));
    }
}
