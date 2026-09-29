using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>#1046: the tags a Seerr connection, a collection and the "no longer on any list" setting give to titles.</summary>
public sealed class SourceTagsPersistenceTests
{
    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-29T12:00:00Z"));

    private static async Task InitialiseAsync(TestStorage storage)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, Clock);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_seerr_connection_stores_its_tags_and_a_save_that_sends_none_keeps_them()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);
        var repository = new SqliteSeerrRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));

        var untagged = await repository.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", true), CancellationToken.None);
        Assert.Equal(("", ""), (untagged!.Tags, untagged.Tags4k));

        var tagged = await repository.SaveAsync(untagged.Id, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "", true, Tags: " requested-by-seerr , kids ", Tags4k: "requested-4k"), CancellationToken.None);
        Assert.Equal(("requested-by-seerr, kids", "requested-4k"), (tagged!.Tags, tagged.Tags4k));

        var kept = await repository.SaveAsync(untagged.Id, new SaveSeerrRequest("Renamed", "http://127.0.0.1:5055", "", true), CancellationToken.None);
        Assert.Equal(("requested-by-seerr, kids", "requested-4k"), (kept!.Tags, kept.Tags4k));

        var cleared = await repository.SaveAsync(untagged.Id, new SaveSeerrRequest("Renamed", "http://127.0.0.1:5055", "", true, Tags: "", Tags4k: ""), CancellationToken.None);
        Assert.Equal(("", ""), (cleared!.Tags, cleared.Tags4k));
    }

    [Fact]
    public async Task Giving_tags_defines_a_tag_nobody_has_and_never_duplicates_one_a_title_carries()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);
        var settings = new SqlitePlatformSettingsRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));
        var mediaTags = new SqliteMediaTagStore(storage.Factory, Clock);
        var service = new TitleTagService(settings, mediaTags);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, Clock);
        var movie = await movies.AddAsync(new CreateMovieRequest("Frozen", 2013, null, MetadataProvider: "tmdb", MetadataProviderId: "109445"), CancellationToken.None);
        var existing = await settings.CreateTagAsync(new CreateTagRequest("kids", null, null), CancellationToken.None);

        var first = await service.GiveAsync(MediaKind.Movie, movie.Id, ["Kids", "requested-by-seerr"], CancellationToken.None);
        Assert.Equal(["kids", "requested-by-seerr"], first);

        var second = await service.GiveAsync(MediaKind.Movie, movie.Id, ["kids", "requested-by-seerr", "extra"], CancellationToken.None);
        Assert.Equal(["extra"], second);

        Assert.Equal(["extra", "kids", "requested-by-seerr"], (await service.ListNamesAsync(MediaKind.Movie, movie.Id, CancellationToken.None)).Order().ToArray());
        var definitions = await settings.ListTagsAsync(CancellationToken.None);
        Assert.Equal(3, definitions.Count);
        Assert.Contains(definitions, item => item.Id == existing.Id);
    }

    [Fact]
    public async Task A_collection_stores_its_tags_and_they_follow_a_tag_rename()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);
        var collections = new SqliteMovieCollectionsRepository(storage.Factory, Clock);
        var metadata = new MetadataCollection("tmdb", "645", "The Nolan Collection", null, null, null, []);

        var created = await collections.UpsertAsync("movies-main", "Movies", "D:\\Media\\Movies", null, null,
            new CreateMovieCollectionRequest("645", "movies-main", Monitored: true, Tags: "franchise, nolan"), metadata, CancellationToken.None);
        Assert.Equal("franchise, nolan", created.Tags);

        var untouched = await collections.UpdateAsync(created.Id, new UpdateMovieCollectionRequest(Monitored: true), CancellationToken.None);
        Assert.Equal("franchise, nolan", untouched!.Tags);
        var updated = await collections.UpdateAsync(created.Id, new UpdateMovieCollectionRequest(Tags: "franchise"), CancellationToken.None);
        Assert.Equal("franchise", updated!.Tags);

        var settings = new SqlitePlatformSettingsRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));
        var tag = await settings.CreateTagAsync(new CreateTagRequest("franchise", null, null), CancellationToken.None);
        var references = new SqliteTagReferenceStore(storage.Factory);
        var users = await references.ListAllUsersAsync(CancellationToken.None);
        Assert.Contains(users["franchise"], user => user.Kind == "collection" && user.Id == created.Id);

        await references.RenameAsync(tag.Id, "franchise", "saga", CancellationToken.None);
        Assert.Equal("saga", (await collections.GetAsync(created.Id, CancellationToken.None))!.Tags);
    }

    [Fact]
    public async Task Removing_a_collection_releases_its_tag_and_leaves_every_movie_exactly_as_it_was()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);
        var collections = new SqliteMovieCollectionsRepository(storage.Factory, Clock);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, Clock);
        var movie = await movies.AddAsync(new CreateMovieRequest("Inception", 2010, null, MetadataProvider: "tmdb", MetadataProviderId: "27205"), CancellationToken.None);
        var metadata = new MetadataCollection("tmdb", "645", "The Nolan Collection", null, null, null,
            [new MetadataCollectionMovie("27205", "Inception", 2010, null, null, null, null, null)]);
        var created = await collections.UpsertAsync("movies-main", "Movies", "D:\\Media\\Movies", null, null,
            new CreateMovieCollectionRequest("645", "movies-main", Tags: "franchise"), metadata, CancellationToken.None);
        await collections.SaveSnapshotAsync(created.Id, metadata, Clock.GetUtcNow(), CancellationToken.None);
        Assert.True(await collections.LinkMovieAsync(created.Id, "27205", movie.Id, CancellationToken.None));
        var before = await movies.GetByIdAsync(movie.Id, CancellationToken.None);
        var references = new SqliteTagReferenceStore(storage.Factory);
        Assert.Contains((await references.ListAllUsersAsync(CancellationToken.None))["franchise"], user => user.Id == created.Id);

        Assert.True(await collections.DeleteAsync(created.Id, CancellationToken.None));

        Assert.Null(await collections.GetAsync(created.Id, CancellationToken.None));
        Assert.Empty(await collections.ListMembersAsync(created.Id, CancellationToken.None));
        Assert.False((await references.ListAllUsersAsync(CancellationToken.None)).ContainsKey("franchise"));
        Assert.Equal(before, await movies.GetByIdAsync(movie.Id, CancellationToken.None));
        Assert.False(await collections.DeleteAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_seerr_connection_and_the_off_list_setting_count_as_users_of_a_tag_and_follow_a_rename()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);
        var seerr = new SqliteSeerrRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));
        var settings = new SqlitePlatformSettingsRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));
        var connection = await seerr.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", true, Tags: "from-seerr", Tags4k: "from-seerr,four-k"), CancellationToken.None);
        var current = await settings.GetAsync(CancellationToken.None);
        await settings.SaveAsync(PlatformSettingsPatchMerger.Apply(current, new PatchPlatformSettingsRequest(TagTitlesOffEveryList: true, OffListTag: "gone")), CancellationToken.None);
        var tag = await settings.CreateTagAsync(new CreateTagRequest("from-seerr", null, null), CancellationToken.None);
        await settings.CreateTagAsync(new CreateTagRequest("gone", null, null), CancellationToken.None);
        var references = new SqliteTagReferenceStore(storage.Factory);

        var users = await references.ListAllUsersAsync(CancellationToken.None);
        Assert.Single(users["from-seerr"], user => user.Kind == "seerrConnection" && user.Id == connection!.Id);
        Assert.Contains(users["gone"], user => user.Kind == "listSetting");

        await references.RenameAsync(tag.Id, "from-seerr", "requested", CancellationToken.None);
        var renamed = await seerr.GetAsync(connection!.Id, CancellationToken.None);
        Assert.Equal(("requested", "requested,four-k"), (renamed!.Tags, renamed.Tags4k));
    }

    [Fact]
    public async Task The_off_list_tag_setting_is_off_and_blank_until_someone_turns_it_on()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);
        var settings = new SqlitePlatformSettingsRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));

        var fresh = await settings.GetAsync(CancellationToken.None);
        Assert.False(fresh.TagTitlesOffEveryList);
        Assert.Equal("", fresh.OffListTag);

        await settings.SaveAsync(PlatformSettingsPatchMerger.Apply(fresh, new PatchPlatformSettingsRequest(TagTitlesOffEveryList: true, OffListTag: " off-list ")), CancellationToken.None);
        var saved = await settings.GetAsync(CancellationToken.None);
        Assert.True(saved.TagTitlesOffEveryList);
        Assert.Equal("off-list", saved.OffListTag);

        await settings.SaveAsync(PlatformSettingsPatchMerger.Apply(saved, new PatchPlatformSettingsRequest(Timezone: "Australia/Sydney")), CancellationToken.None);
        var untouched = await settings.GetAsync(CancellationToken.None);
        Assert.True(untouched.TagTitlesOffEveryList);
        Assert.Equal("off-list", untouched.OffListTag);
    }

    [Fact]
    public async Task A_title_is_only_off_every_list_once_every_list_it_came_from_has_dropped_it()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-29T12:00:00Z"));
        var intake = new SqliteIntakeRepository(storage.Factory, clock);
        var listA = await CreateSourceAsync(intake, "List A");
        var listB = await CreateSourceAsync(intake, "List B");

        async Task Seen(IntakeSourceItem source, string entityId, string title)
            => await intake.RecordIntakeTitleOriginAsync(new CreateIntakeTitleOriginRequest(source.Id, source.Name, "url-list", "movies", entityId, title.ToLowerInvariant(), title, 2020, null), CancellationToken.None);

        // Both lists carry Shared; only list A carries Solo.
        await Seen(listA, "movie-shared", "Shared");
        await Seen(listB, "movie-shared", "Shared");
        await Seen(listA, "movie-solo", "Solo");

        // A later sync of list A finds only Shared, so Solo has left its only list; Shared is still on list B.
        clock.Advance(TimeSpan.FromHours(1));
        var syncStarted = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        await Seen(listA, "movie-shared", "Shared");
        var offEveryList = await intake.MarkDroppedTitlesAsync(listA.Id, syncStarted, CancellationToken.None);
        Assert.Equal(["movie-solo"], offEveryList.Select(item => item.EntityId));

        // Marking is once: the same title is not handed back on the next sync.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await intake.MarkDroppedTitlesAsync(listA.Id, clock.GetUtcNow(), CancellationToken.None));

        // List B drops Shared too, and now it is on no list at all.
        clock.Advance(TimeSpan.FromHours(1));
        var offAfterB = await intake.MarkDroppedTitlesAsync(listB.Id, clock.GetUtcNow(), CancellationToken.None);
        Assert.Equal(["movie-shared"], offAfterB.Select(item => item.EntityId));

        // Seen again by a later sync, Solo is back on a list and can be dropped once more.
        clock.Advance(TimeSpan.FromHours(1));
        await Seen(listA, "movie-solo", "Solo");
        clock.Advance(TimeSpan.FromHours(1));
        var again = await intake.MarkDroppedTitlesAsync(listA.Id, clock.GetUtcNow(), CancellationToken.None);
        Assert.Equal(["movie-solo"], again.Select(item => item.EntityId));
    }

    [Fact]
    public async Task A_list_stores_its_tags_keeps_them_when_a_save_sends_none_and_they_follow_a_tag_rename()
    {
        using var storage = TestStorage.Create();
        await InitialiseAsync(storage);
        var intake = new SqliteIntakeRepository(storage.Factory, Clock);
        var plain = await CreateSourceAsync(intake, "Plain list");
        Assert.Equal("", plain.Tags);

        var created = await intake.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest("Tagged list", "url-list", "https://example.test/t", "movies", null, null,
                null, null, null, null, null, "any", 24, false, true, Tags: " from-list , kids "),
            CancellationToken.None);
        Assert.Equal("from-list, kids", created.Tags);

        UpdateIntakeSourceRequest Update(string? tags) => new(
            "Tagged list", "url-list", "https://example.test/t", "movies", null, null, null, null, null, null, null, "any", 24, false, true, Tags: tags);
        Assert.Equal("from-list, kids", (await intake.UpdateIntakeSourceAsync(created.Id, Update(null), CancellationToken.None))!.Tags);
        Assert.Equal("", (await intake.UpdateIntakeSourceAsync(created.Id, Update(""), CancellationToken.None))!.Tags);
        await intake.UpdateIntakeSourceAsync(created.Id, Update("from-list"), CancellationToken.None);

        var settings = new SqlitePlatformSettingsRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));
        var tag = await settings.CreateTagAsync(new CreateTagRequest("from-list", null, null), CancellationToken.None);
        var references = new SqliteTagReferenceStore(storage.Factory);
        Assert.Contains((await references.ListAllUsersAsync(CancellationToken.None))["from-list"], user => user.Kind == "list" && user.Id == created.Id);

        await references.RenameAsync(tag.Id, "from-list", "imported", CancellationToken.None);
        Assert.Equal("imported", (await intake.GetIntakeSourceAsync(created.Id, CancellationToken.None))!.Tags);
    }

    private static Task<IntakeSourceItem> CreateSourceAsync(SqliteIntakeRepository intake, string name)
        => intake.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest(name, "url-list", "https://example.test/" + name.Replace(' ', '-'), "movies", null, null,
                null, null, null, null, null, "any", 24, false, true),
            CancellationToken.None);
}
