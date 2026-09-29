using Deluno.Infrastructure.Storage;
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
    public async Task Release_profiles_default_to_enabled_and_every_indexer_then_round_trip_a_narrower_scope()
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

        Assert.True(created.Enabled);
        Assert.Empty(created.IndexerIds);

        var scoped = await repository.UpdateAsync(
            created.Id,
            new UpdateReleaseProfileRequest(
                Name: null,
                TagName: null,
                PreferredProtocol: null,
                UsenetDelayMinutes: null,
                TorrentDelayMinutes: null,
                MustContain: null,
                MustNotContain: null,
                PreferredTerms: null,
                Enabled: false,
                IndexerIds: ["indexer-b", "indexer-a"]),
            CancellationToken.None);

        Assert.NotNull(scoped);
        Assert.False(scoped.Enabled);
        Assert.Equal(["indexer-a", "indexer-b"], scoped.IndexerIds);

        var reloaded = await repository.GetAsync(created.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.Enabled);
        Assert.Equal(["indexer-a", "indexer-b"], reloaded.IndexerIds);
    }

    private static async Task<SqliteReleaseProfileRepository> NewRepositoryAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteReleaseProfileRepository(storage.Factory, timeProvider);
    }

    [Fact]
    public async Task One_release_rule_can_apply_to_several_tags_and_a_title_with_any_of_them_matches()
    {
        using var storage = TestStorage.Create();
        var repository = await NewRepositoryAsync(storage);
        var rule = await repository.CreateAsync(
            new CreateReleaseProfileRequest("Family rule", null, "any", 0, 30, "", "", [], TagNames: ["Kids", " family ", "kids"]),
            CancellationToken.None);

        Assert.Equal(["Kids", "family"], rule.AppliesToTags);
        Assert.Equal("Kids", rule.TagName);

        Assert.Single(await repository.ListApplicableAsync(["FAMILY"], CancellationToken.None));
        Assert.Single(await repository.ListApplicableAsync(["kids", "4K"], CancellationToken.None));
        Assert.Empty(await repository.ListApplicableAsync(["Sports"], CancellationToken.None));

        var reloaded = await repository.GetAsync(rule.Id, CancellationToken.None);
        Assert.Equal(["Kids", "family"], reloaded!.AppliesToTags);

        var narrowed = await repository.UpdateAsync(
            rule.Id,
            new UpdateReleaseProfileRequest(null, null, null, null, null, null, null, null, TagNames: ["Anime"]),
            CancellationToken.None);
        Assert.Equal(["Anime"], narrowed!.AppliesToTags);
        Assert.Equal("Anime", narrowed.TagName);
    }

    [Fact]
    public async Task By_default_every_matching_rule_applies_and_first_match_uses_only_the_top_one()
    {
        using var storage = TestStorage.Create();
        var repository = await NewRepositoryAsync(storage);
        var fallback = await repository.CreateAsync(new CreateReleaseProfileRequest("Default", "", "any", 0, 0, "", "", []), CancellationToken.None);
        var kids = await repository.CreateAsync(new CreateReleaseProfileRequest("Kids", null, "any", 0, 0, "", "", [], TagNames: ["kids", "family"]), CancellationToken.None);
        var anime = await repository.CreateAsync(new CreateReleaseProfileRequest("Anime", "anime", "any", 0, 0, "", "", []), CancellationToken.None);

        // Nothing changes on an install that has never chosen: every matching rule combines.
        Assert.Equal(ReleaseRuleModes.Combine, await repository.GetRuleModeAsync(CancellationToken.None));
        Assert.Equal(3, (await repository.ListApplicableAsync(["kids", "anime"], CancellationToken.None)).Count);

        await repository.SetRuleModeAsync("first-match", CancellationToken.None);

        // Kids is above Anime, so a title with both tags uses Kids only.
        var both = Assert.Single(await repository.ListApplicableAsync(["kids", "anime"], CancellationToken.None));
        Assert.Equal(kids.Id, both.Id);

        // Drag Anime to the top: now it wins.
        var reordered = await repository.ReorderAsync([anime.Id, kids.Id, fallback.Id], CancellationToken.None);
        Assert.Equal([anime.Id, kids.Id, fallback.Id], reordered.Select(item => item.Id).ToArray());
        Assert.Equal(anime.Id, Assert.Single(await repository.ListApplicableAsync(["kids", "anime"], CancellationToken.None)).Id);

        // A title matching no tagged rule falls to the rule with no tag.
        Assert.Equal(fallback.Id, Assert.Single(await repository.ListApplicableAsync(["sports"], CancellationToken.None)).Id);
        Assert.Equal(fallback.Id, Assert.Single(await repository.ListApplicableAsync([], CancellationToken.None)).Id);

        // A switched-off top rule is skipped, not chosen and ignored.
        await repository.UpdateAsync(anime.Id, new UpdateReleaseProfileRequest(null, null, null, null, null, null, null, null, Enabled: false), CancellationToken.None);
        Assert.Equal(kids.Id, Assert.Single(await repository.ListApplicableAsync(["kids", "anime"], CancellationToken.None)).Id);

        await repository.SetRuleModeAsync("combine", CancellationToken.None);
        Assert.Equal(ReleaseRuleModes.Combine, await repository.GetRuleModeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Ids_left_out_of_a_reorder_keep_their_place_after_the_ones_named()
    {
        using var storage = TestStorage.Create();
        var repository = await NewRepositoryAsync(storage);
        var a = await repository.CreateAsync(new CreateReleaseProfileRequest("A", "a", "any", 0, 0, "", "", []), CancellationToken.None);
        var b = await repository.CreateAsync(new CreateReleaseProfileRequest("B", "b", "any", 0, 0, "", "", []), CancellationToken.None);
        var c = await repository.CreateAsync(new CreateReleaseProfileRequest("C", "c", "any", 0, 0, "", "", []), CancellationToken.None);

        var result = await repository.ReorderAsync([c.Id, "no-such-rule"], CancellationToken.None);

        Assert.Equal([c.Id, a.Id, b.Id], result.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Existing_single_tag_rules_become_one_tag_sets_in_their_old_order_when_the_schema_upgrades()
    {
        using var storage = TestStorage.Create();
        var repository = await NewRepositoryAsync(storage);
        // A row as an older install stored it: only tag_name, no tag set, no order.
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO release_profiles (id, name, tag_name, preferred_protocol, usenet_delay_minutes, torrent_delay_minutes, must_contain, must_not_contain, preferred_terms_json, created_utc, updated_utc) "
                + "VALUES ('old1', 'Old', 'Kids', 'any', 0, 0, '', '', '[]', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var old = await repository.GetAsync("old1", CancellationToken.None);
        Assert.Equal(["Kids"], old!.AppliesToTags);
        Assert.Single(await repository.ListApplicableAsync(["kids"], CancellationToken.None));
        Assert.Equal(ReleaseRuleModes.Combine, await repository.GetRuleModeAsync(CancellationToken.None));
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
