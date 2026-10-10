using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1240: which category a struck torrent goes to, what counts as private, where the choices are kept, and where a
/// site's privacy comes from.
/// </summary>
public sealed class KeepSeedingSettingsAndPrivacyTests
{
    private static KeepSeedingSettings Settings(
        string? client = null, string? siteCategory = null, string[]? tags = null, bool? @override = null, Dictionary<string, string>? tagCategories = null)
        => new(
            client is null ? null : new Dictionary<string, string> { ["client-1"] = client },
            new Dictionary<string, KeepSeedingIndexerSettings> { ["site-1"] = new(@override, tags, siteCategory) },
            tagCategories);

    // ------------------------------------------------------------- category order

    [Theory]
    [InlineData("c", "s", "t", "t", KeepSeedingCategorySources.Tag)]
    [InlineData("c", "s", null, "s", KeepSeedingCategorySources.Site)]
    [InlineData("c", null, null, "c", KeepSeedingCategorySources.Client)]
    [InlineData(null, "s", null, "s", KeepSeedingCategorySources.Site)]
    [InlineData(null, null, "t", "t", KeepSeedingCategorySources.Tag)]
    [InlineData(null, null, null, null, null)]
    public void The_most_specific_category_wins(string? client, string? site, string? tag, string? expected, string? source)
    {
        var settings = Settings(client, site, tag is null ? ["ratio"] : ["ratio"], tagCategories: tag is null ? null : new Dictionary<string, string> { ["ratio"] = tag });

        var decision = KeepSeedingResolver.Decide(settings, "client-1", "site-1", clientSaysPrivate: true, siteExpectsSharing: false);

        Assert.Equal(expected, decision.Category);
        Assert.Equal(source, decision.CategorySource);
        Assert.Equal(expected is not null, decision.HasCategory);
    }

    [Fact]
    public void With_several_tags_the_first_one_listed_that_has_a_category_wins()
    {
        var settings = Settings(siteCategory: "s", tags: ["quiet", "ratio", "other"], tagCategories: new Dictionary<string, string> { ["other"] = "from-other", ["ratio"] = "from-ratio" });

        var decision = KeepSeedingResolver.Decide(settings, "client-1", "site-1", true, false);

        Assert.Equal("from-ratio", decision.Category);
        Assert.Equal("ratio", decision.Tag);
    }

    [Fact]
    public void Tag_names_and_ids_match_whatever_their_case()
    {
        var settings = new KeepSeedingSettings(
            new Dictionary<string, string> { ["CLIENT-1"] = "keep" },
            new Dictionary<string, KeepSeedingIndexerSettings> { ["SITE-1"] = new(Tags: ["Ratio"]) },
            new Dictionary<string, string> { ["rATIO"] = "tagged" });

        Assert.Equal("tagged", KeepSeedingResolver.Decide(settings, "client-1", "site-1", null, false).Category);
        Assert.Equal("keep", KeepSeedingResolver.Decide(settings, "client-1", "other-site", true, false).Category);
    }

    // ------------------------------------------------------------- what counts as private

    [Theory]
    // client flag, site expects sharing, override, expected private
    [InlineData(true, false, null, true)]
    [InlineData(false, true, null, true)]
    [InlineData(null, true, null, true)]
    [InlineData(false, false, null, false)]
    [InlineData(null, false, null, false)]
    [InlineData(null, true, false, false)]
    [InlineData(null, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, true)]
    public void A_torrent_is_private_when_the_client_or_its_site_says_so_and_the_person_can_override_the_site(bool? client, bool site, bool? @override, bool expected)
    {
        var decision = KeepSeedingResolver.Decide(Settings(client: "c", @override: @override), "client-1", "site-1", client, site);

        Assert.Equal(expected, decision.IsPrivate);
        Assert.Equal(expected, decision.KeepsSeeding);
    }

    [Fact]
    public void A_site_with_a_seeding_tag_keeps_seeding_even_when_nothing_says_it_is_private()
    {
        var decision = KeepSeedingResolver.Decide(Settings(client: "c", tags: ["ratio"]), "client-1", "site-1", null, false);

        Assert.False(decision.IsPrivate);
        Assert.True(decision.IsTagged);
        Assert.True(decision.KeepsSeeding);
    }

    [Fact]
    public void A_torrent_with_no_known_site_is_judged_on_the_client_flag_alone()
    {
        var settings = Settings(client: "c", tags: ["ratio"]);

        Assert.True(KeepSeedingResolver.Decide(settings, "client-1", null, true, false).KeepsSeeding);
        Assert.False(KeepSeedingResolver.Decide(settings, "client-1", null, null, false).KeepsSeeding);
        Assert.False(KeepSeedingResolver.Decide(settings, "client-1", "unknown-site", false, false).KeepsSeeding);
    }

    // ------------------------------------------------------------- where it is kept

    [Fact]
    public async Task The_settings_and_the_problems_are_kept_and_read_back_clean()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-11T09:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var store = new SqliteKeepSeedingStore(storage.Factory, time);
        Assert.Empty((await store.GetAsync(CancellationToken.None)).ClientCategoryMap);

        var saved = await store.SaveAsync(new KeepSeedingSettings(
            new Dictionary<string, string> { ["client-1"] = "  keep-seeding  ", ["client-2"] = "   " },
            new Dictionary<string, KeepSeedingIndexerSettings>
            {
                ["site-1"] = new(true, [" ratio ", "Ratio", ""], " site-keep "),
                ["empty"] = new(null, [], null)
            },
            new Dictionary<string, string> { [" ratio "] = " tag-keep ", ["blank"] = "" }), CancellationToken.None);

        Assert.Equal("keep-seeding", saved.ClientCategory("client-1"));
        Assert.Null(saved.ClientCategory("client-2"));
        Assert.Equal(["ratio"], saved.Indexer("site-1")!.TagList);
        Assert.True(saved.Indexer("site-1")!.PrivateOverride);
        Assert.Equal("site-keep", saved.Indexer("site-1")!.Category);
        Assert.Null(saved.Indexer("empty"));
        Assert.Equal("tag-keep", saved.TagCategory("ratio"));
        Assert.Null(saved.TagCategory("blank"));
        var again = await store.GetAsync(CancellationToken.None);
        Assert.Equal("tag-keep", again.TagCategory("ratio"));

        var problem = new KeepSeedingProblem(
            KeepSeedingProblem.KeyFor("Client-1", "ABC"), "client-1", "qBittorrent", "ABC", "Example", KeepSeedingProblemKinds.Refused,
            "Deluno could not move it.", "keep", time.GetUtcNow(), time.GetUtcNow(), 1);
        await store.SaveProblemAsync(problem, CancellationToken.None);
        await store.SaveProblemAsync(problem with { Attempts = 2 }, CancellationToken.None);
        Assert.Equal(2, Assert.Single(await store.ListProblemsAsync(CancellationToken.None)).Attempts);
        Assert.False(await store.ClearProblemAsync("nothing", CancellationToken.None));
        Assert.True(await store.ClearProblemAsync("client-1:abc", CancellationToken.None));
        Assert.Empty(await store.ListProblemsAsync(CancellationToken.None));
    }

    // ------------------------------------------------------------- where a site's privacy comes from

    [Theory]
    [InlineData("private", "private")]
    [InlineData("Private", "private")]
    [InlineData("public", "public")]
    [InlineData("semi-private", "semi-private")]
    [InlineData("semiprivate", "semi-private")]
    [InlineData("anything else", "semi-private")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void A_definitions_type_is_read_the_way_Prowlarr_reads_a_Cardigann_one(string? type, string expected)
        => Assert.Equal(expected, IndexerPrivacy.FromDefinitionType(type));

    [Theory]
    [InlineData("private", null, "private")]
    [InlineData("public", null, "public")]
    [InlineData("semi-private", null, "semi-private")]
    [InlineData(null, null, "unknown")]
    // What the person said wins over the definition.
    [InlineData("private", "public", "public")]
    [InlineData("public", "private", "private")]
    public async Task A_site_added_from_a_tracker_definition_keeps_what_the_definition_says_about_itself(string? definition, string? said, string expected)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-11T09:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage), new Names(definition));

        var created = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Example", "torznab", said, "http://tracker.test/", "key", 10, "2000", "", "movies", true,
                DefinitionId: definition is null ? null : "example"),
            CancellationToken.None);

        Assert.Equal(expected, created.Privacy);
        Assert.Equal(expected, (await connections.ListIndexersAsync(CancellationToken.None)).Single().Privacy);
    }

    [Fact]
    public async Task A_hand_added_source_stays_not_set_because_nothing_in_it_says()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-11T09:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage), new Names("private"));

        var created = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Hand added", "torznab", null, "http://tracker.test/", "key", 10, "2000", "", "movies", true),
            CancellationToken.None);

        Assert.Equal(IndexerPrivacy.Unknown, created.Privacy);
    }

    private sealed class Names(string? privacy) : ITrackerDefinitionNames
    {
        public string? DisplayName(string definitionId) => "Example";

        public string? Privacy(string definitionId) => privacy;
    }
}
