using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1024: auto-tagging rules, checked against Sonarr's and Radarr's own
/// AutoTaggingService. Same-type conditions are OR-ed, different types are
/// AND-ed, each can be negated, and a rule set to remove automatically takes
/// its tags back when a title stops matching.
/// </summary>
public sealed class AutoTaggingTests
{
    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-29T12:00:00Z"));

    private static AutoTagFacts Facts(
        MediaKind kind = MediaKind.Movie,
        string[]? genres = null,
        string[]? tags = null,
        bool monitored = true,
        string? language = "en",
        int? year = 2020,
        int? runtime = 100,
        string? seriesType = null,
        AutoTagAddFacts? add = null,
        string? status = null,
        string[]? keywords = null,
        string? studio = null) =>
        new(kind, "A Title", genres ?? [], monitored, language, null, [], [], seriesType, status, tags ?? [], year, keywords ?? [], runtime, studio, null, false, null, false, add);

    private static AutoTagRule Rule(string name, string[] tags, bool remove, params AutoTagSpecification[] specs) =>
        new("rule-" + name, name, tags, remove, AutoTagScopes.Both, true, specs, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static AutoTagSpecification Spec(string type, string value, bool negate = false) => new(type, negate, value);

    /* ------------------------------------------------------------ matching */

    [Fact]
    public void Conditions_of_the_same_kind_are_alternatives_and_different_kinds_must_all_hold()
    {
        var rule = Rule("Anime", ["anime"], false,
            Spec(AutoTagSpecificationTypes.Genre, "Animation"),
            Spec(AutoTagSpecificationTypes.Genre, "Anime"),
            Spec(AutoTagSpecificationTypes.OriginalLanguage, "ja"));

        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(rule, Facts(genres: ["Anime"], language: "ja")));
        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(rule, Facts(genres: ["Animation"], language: "Japanese")));
        Assert.Equal(AutoTagVerdict.NoMatch, AutoTagMatcher.Evaluate(rule, Facts(genres: ["Animation"], language: "en")));
        Assert.Equal(AutoTagVerdict.NoMatch, AutoTagMatcher.Evaluate(rule, Facts(genres: ["Drama"], language: "ja")));
    }

    [Fact]
    public void A_negated_condition_matches_when_its_test_fails()
    {
        var rule = Rule("Not horror", ["family-ok"], false, Spec(AutoTagSpecificationTypes.Genre, "Horror", negate: true));

        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(rule, Facts(genres: ["Comedy"])));
        Assert.Equal(AutoTagVerdict.NoMatch, AutoTagMatcher.Evaluate(rule, Facts(genres: ["Horror", "Comedy"])));
    }

    [Theory]
    [InlineData("2000-2010", 2005, true)]
    [InlineData("2000-2010", 2011, false)]
    [InlineData("-1999", 1980, true)]
    [InlineData("-1999", 2000, false)]
    [InlineData("2015-", 2024, true)]
    [InlineData("2015-", 2014, false)]
    [InlineData("2020", 2020, true)]
    public void A_year_range_can_leave_either_end_open(string range, int year, bool expected)
    {
        var rule = Rule("Years", ["era"], false, Spec(AutoTagSpecificationTypes.Year, range));
        Assert.Equal(expected ? AutoTagVerdict.Match : AutoTagVerdict.NoMatch, AutoTagMatcher.Evaluate(rule, Facts(year: year)));
    }

    [Fact]
    public void The_radarr_and_sonarr_conditions_read_the_title()
    {
        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(Rule("m", ["t"], false, Spec(AutoTagSpecificationTypes.Monitored, "false")), Facts(monitored: false)));
        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(Rule("s", ["t"], false, Spec(AutoTagSpecificationTypes.Status, "Ended")), Facts(kind: MediaKind.Series, status: "ended")));
        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(Rule("k", ["t"], false, Spec(AutoTagSpecificationTypes.Keyword, "time loop")), Facts(keywords: ["Time Loop"])));
        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(Rule("r", ["t"], false, Spec(AutoTagSpecificationTypes.Runtime, "-90")), Facts(runtime: 80)));
        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(Rule("st", ["t"], false, Spec(AutoTagSpecificationTypes.Studio, "Ghibli")), Facts(studio: "ghibli")));
        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(Rule("tag", ["t"], false, Spec(AutoTagSpecificationTypes.Tag, "kids")), Facts(tags: ["Kids"])));
        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(Rule("type", ["t"], false, Spec(AutoTagSpecificationTypes.SeriesType, "anime")), Facts(kind: MediaKind.Series, seriesType: "anime")));
    }

    [Fact]
    public void A_condition_only_an_add_can_answer_leaves_the_rule_alone_on_a_refresh()
    {
        var rule = Rule("Seerr 4K", ["4k-request"], true, Spec(AutoTagSpecificationTypes.Requested4k, "true"), Spec(AutoTagSpecificationTypes.ViaSeerr, "true"));

        Assert.Equal(AutoTagVerdict.Match, AutoTagMatcher.Evaluate(rule, Facts(add: new AutoTagAddFacts(true, true, null))));
        Assert.Equal(AutoTagVerdict.NoMatch, AutoTagMatcher.Evaluate(rule, Facts(add: new AutoTagAddFacts(false, true, null))));
        Assert.Equal(AutoTagVerdict.NotDecidable, AutoTagMatcher.Evaluate(rule, Facts(add: null)));

        // A title tagged when it was added must not lose the tag on its first refresh.
        var changes = AutoTagMatcher.Changes([rule], Facts(tags: ["4k-request"], add: null));
        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void A_matching_rule_adds_its_tags_and_a_title_that_stops_matching_loses_them_only_when_the_rule_says_so()
    {
        var keep = Rule("Keep", ["old"], false, Spec(AutoTagSpecificationTypes.Year, "-1999"));
        var lose = Rule("Lose", ["classic"], true, Spec(AutoTagSpecificationTypes.Year, "-1999"));

        var old = AutoTagMatcher.Changes([keep, lose], Facts(year: 1985));
        Assert.Equal(["old", "classic"], old.Add.Select(change => change.Tag));

        var recent = AutoTagMatcher.Changes([keep, lose], Facts(year: 2021, tags: ["old", "classic"]));
        Assert.Empty(recent.Add);
        Assert.Equal(["classic"], recent.Remove.Select(change => change.Tag));
    }

    [Fact]
    public void A_tag_another_matching_rule_still_wants_is_not_removed()
    {
        var first = Rule("First", ["shared"], true, Spec(AutoTagSpecificationTypes.Genre, "Drama"));
        var second = Rule("Second", ["shared"], true, Spec(AutoTagSpecificationTypes.Genre, "Comedy"));

        var changes = AutoTagMatcher.Changes([first, second], Facts(genres: ["Comedy"], tags: ["shared"]));

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void A_paused_rule_and_a_rule_for_the_other_kind_do_nothing()
    {
        var paused = Rule("Paused", ["x"], false, Spec(AutoTagSpecificationTypes.Monitored, "true")) with { IsEnabled = false };
        var seriesOnly = Rule("Series only", ["y"], false, Spec(AutoTagSpecificationTypes.Monitored, "true")) with { AppliesTo = AutoTagScopes.Series };

        Assert.True(AutoTagMatcher.Changes([paused, seriesOnly], Facts(kind: MediaKind.Movie)).IsEmpty);
        Assert.Equal(["y"], AutoTagMatcher.Changes([seriesOnly], Facts(kind: MediaKind.Series)).Add.Select(change => change.Tag));
    }

    [Fact]
    public void A_rule_needs_a_name_a_tag_and_a_real_condition()
    {
        Assert.Empty(AutoTagRuleValidator.Validate(new SaveAutoTagRuleRequest("Anime", ["anime"], false, null, null, [Spec(AutoTagSpecificationTypes.Genre, "Anime")])));

        var errors = AutoTagRuleValidator.Validate(new SaveAutoTagRuleRequest(" ", [], null, null, null, []));
        Assert.Contains("name", errors.Keys);
        Assert.Contains("tags", errors.Keys);
        Assert.Contains("specifications", errors.Keys);

        Assert.Contains("specifications", AutoTagRuleValidator.Validate(new SaveAutoTagRuleRequest("R", ["t"], null, null, null, [Spec(AutoTagSpecificationTypes.Year, "recently")])).Keys);
        Assert.Contains("specifications", AutoTagRuleValidator.Validate(new SaveAutoTagRuleRequest("R", ["t"], null, null, null, [Spec("nonsense", "x")])).Keys);
    }

    /* ---------------------------------------------------------- persistence */

    private static async Task<TestStorage> NewStorageAsync()
    {
        var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, Clock);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return storage;
    }

    [Fact]
    public async Task A_new_install_has_no_rules_and_a_saved_rule_reads_back_in_order()
    {
        using var storage = await NewStorageAsync();
        var store = new SqliteAutoTagRuleStore(storage.Factory, Clock);
        Assert.Empty(await store.ListAsync(CancellationToken.None));

        var created = await store.CreateAsync(
            new SaveAutoTagRuleRequest("Anime", ["anime", "japan"], true, "series", true,
                [Spec(AutoTagSpecificationTypes.Genre, "Animation"), Spec(AutoTagSpecificationTypes.OriginalLanguage, "ja", negate: false)]),
            CancellationToken.None);

        var read = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(created.Id, read.Id);
        Assert.Equal(["anime", "japan"], read.Tags);
        Assert.True(read.RemoveAutomatically);
        Assert.Equal(AutoTagScopes.Series, read.AppliesTo);
        Assert.Equal(["genre", "originalLanguage"], read.Specifications.Select(spec => spec.Type));

        var updated = await store.UpdateAsync(created.Id, new SaveAutoTagRuleRequest("Anime", ["anime"], false, "both", true, [Spec(AutoTagSpecificationTypes.Anime, "true")]), CancellationToken.None);
        Assert.Equal(["anime"], updated!.Tags);
        Assert.Single(updated.Specifications);

        Assert.True(await store.DeleteAsync(created.Id, CancellationToken.None));
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_rule_counts_as_a_user_of_the_tags_it_gives_and_the_tag_it_looks_for_and_follows_a_rename()
    {
        using var storage = await NewStorageAsync();
        var store = new SqliteAutoTagRuleStore(storage.Factory, Clock);
        var settings = new SqlitePlatformSettingsRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));
        var gives = await settings.CreateTagAsync(new CreateTagRequest("anime", null, null), CancellationToken.None);
        var looksFor = await settings.CreateTagAsync(new CreateTagRequest("kids", null, null), CancellationToken.None);
        var rule = await store.CreateAsync(
            new SaveAutoTagRuleRequest("Kids anime", ["anime"], false, null, null, [Spec(AutoTagSpecificationTypes.Tag, "kids"), Spec(AutoTagSpecificationTypes.Genre, "Animation")]),
            CancellationToken.None);
        var references = new SqliteTagReferenceStore(storage.Factory);

        var users = await references.ListAllUsersAsync(CancellationToken.None);
        Assert.Contains(users["anime"], user => user.Kind == "autoTagRule" && user.Name == "Kids anime");
        Assert.Contains(users["kids"], user => user.Kind == "autoTagRule" && user.Name.StartsWith("Kids anime", StringComparison.Ordinal));

        await references.RenameAsync(gives.Id, "anime", "cartoons", CancellationToken.None);
        await references.RenameAsync(looksFor.Id, "kids", "family", CancellationToken.None);

        var renamed = (await store.GetAsync(rule.Id, CancellationToken.None))!;
        Assert.Equal(["cartoons"], renamed.Tags);
        Assert.Equal("family", renamed.Specifications.Single(spec => spec.Type == AutoTagSpecificationTypes.Tag).Value);
        Assert.Equal("Animation", renamed.Specifications.Single(spec => spec.Type == AutoTagSpecificationTypes.Genre).Value);

        // Still in use, so the endpoint that deletes a tag refuses it (it asks this same list).
        var after = await references.ListAllUsersAsync(CancellationToken.None);
        Assert.True(after.ContainsKey("cartoons"));
        Assert.True(after.ContainsKey("family"));

        await store.DeleteAsync(rule.Id, CancellationToken.None);
        var freed = await references.ListAllUsersAsync(CancellationToken.None);
        Assert.False(freed.ContainsKey("cartoons"));
        Assert.False(freed.ContainsKey("family"));
    }

    [Fact]
    public async Task Absolute_episode_numbers_for_anime_are_off_until_chosen_and_stay_chosen()
    {
        using var storage = await NewStorageAsync();
        var settings = new SqlitePlatformSettingsRepository(storage.Factory, Clock, TestSecretProtection.Create(storage));

        var fresh = await settings.GetAsync(CancellationToken.None);
        Assert.False(fresh.ShowAbsoluteEpisodeNumbers);

        await settings.SaveAsync(PlatformSettingsPatchMerger.Apply(fresh, new PatchPlatformSettingsRequest(ShowAbsoluteEpisodeNumbers: true)), CancellationToken.None);
        var on = await settings.GetAsync(CancellationToken.None);
        Assert.True(on.ShowAbsoluteEpisodeNumbers);

        // A save that says nothing about it keeps what was chosen.
        await settings.SaveAsync(PlatformSettingsPatchMerger.Apply(on, new PatchPlatformSettingsRequest(ShowRelativeDates: false)), CancellationToken.None);
        Assert.True((await settings.GetAsync(CancellationToken.None)).ShowAbsoluteEpisodeNumbers);

        await settings.SaveAsync(PlatformSettingsPatchMerger.Apply(on, new PatchPlatformSettingsRequest(ShowAbsoluteEpisodeNumbers: false)), CancellationToken.None);
        Assert.False((await settings.GetAsync(CancellationToken.None)).ShowAbsoluteEpisodeNumbers);
    }

    /* ------------------------------------------------------- the running app */

    private static async Task<string> CreateRuleAsync(ApplicationTestHost app, object body)
    {
        var response = await app.Client.PostAsJsonAsync("/api/auto-tag-rules", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> AddMovieAsync(ApplicationTestHost app, object body)
    {
        var response = await app.Client.PostAsJsonAsync("/api/movies", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string[]> TagsOfAsync(ApplicationTestHost app, MediaKind kind, string id) =>
        (await app.Services.GetRequiredService<IMediaTagStore>().ListAsync(kind, id, CancellationToken.None)).Select(tag => tag.Name).Order().ToArray();

    [Fact]
    public async Task Rules_tag_a_title_when_it_is_added_before_the_added_notification_reads_its_tags()
    {
        var outbound = new RecordingOutbound();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        var notifications = app.Services.GetRequiredService<INotificationService>();
        var preferences = await notifications.GetPreferencesAsync(CancellationToken.None);
        preferences.AddedEnabled = true;
        await notifications.UpdatePreferencesAsync(preferences, CancellationToken.None);
        await CreateRuleAsync(app, new
        {
            name = "Cartoons",
            tags = new[] { "cartoons" },
            specifications = new[] { new { type = "genre", negate = false, value = "Animation" } }
        });

        var cartoon = await AddMovieAsync(app, new { title = "A Cartoon", releaseYear = 2024, monitored = true, genres = "Animation, Family" });
        var drama = await AddMovieAsync(app, new { title = "A Drama", releaseYear = 2024, monitored = true, genres = "Drama" });

        Assert.Equal(["cartoons"], await TagsOfAsync(app, MediaKind.Movie, cartoon));
        Assert.Empty(await TagsOfAsync(app, MediaKind.Movie, drama));
        Assert.Equal(2, outbound.Dispatched.Count);
        Assert.True(TagRoutingMatch.IsEligible("cartoons", outbound.Dispatched[0]));
        Assert.False(TagRoutingMatch.IsEligible("cartoons", outbound.Dispatched[1]));

        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "movie", cartoon, CancellationToken.None);
        var line = Assert.Single(activity, item => item.Category == "tags.auto");
        Assert.Contains("“cartoons”", line.Message);
        Assert.Contains("“Cartoons”", line.Message);
        Assert.Contains("when it was added", line.Message);
    }

    [Fact]
    public async Task With_no_rules_nothing_is_tagged_and_nothing_is_written_to_activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var movie = await AddMovieAsync(app, new { title = "A Cartoon", releaseYear = 2024, monitored = true, genres = "Animation" });

        Assert.Empty(await TagsOfAsync(app, MediaKind.Movie, movie));
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "movie", movie, CancellationToken.None);
        Assert.DoesNotContain(activity, item => item.Category == "tags.auto");
    }

    [Fact]
    public async Task An_edit_applies_the_rules_and_a_title_that_stops_matching_loses_the_tags_of_a_remove_automatically_rule()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateRuleAsync(app, new
        {
            name = "Paused",
            tags = new[] { "on-hold" },
            removeAutomatically = true,
            specifications = new[] { new { type = "monitored", negate = false, value = "false" } }
        });
        var movie = await AddMovieAsync(app, new { title = "Some Film", releaseYear = 2024, monitored = true });
        Assert.Empty(await TagsOfAsync(app, MediaKind.Movie, movie));

        var unmonitor = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { movie }, monitored = false });
        Assert.Equal(HttpStatusCode.OK, unmonitor.StatusCode);
        Assert.Equal(["on-hold"], await TagsOfAsync(app, MediaKind.Movie, movie));

        var monitor = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { movie }, monitored = true });
        Assert.Equal(HttpStatusCode.OK, monitor.StatusCode);
        Assert.Empty(await TagsOfAsync(app, MediaKind.Movie, movie));

        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "movie", movie, CancellationToken.None);
        Assert.Contains(activity, item => item.Category == "tags.auto" && item.Message.Contains("took the tag “on-hold”", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Running_the_rules_over_the_library_tags_titles_already_in_it_and_says_so_in_activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movie = await AddMovieAsync(app, new { title = "Old Film", releaseYear = 1985, monitored = true });
        await CreateRuleAsync(app, new
        {
            name = "Old",
            tags = new[] { "old" },
            specifications = new[] { new { type = "year", negate = false, value = "-1999" } }
        });
        Assert.Empty(await TagsOfAsync(app, MediaKind.Movie, movie));

        var run = await app.Client.PostAsync("/api/auto-tag-rules/run", null);

        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.Equal(["old"], await TagsOfAsync(app, MediaKind.Movie, movie));
        var summary = (await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, null, null, CancellationToken.None))
            .First(item => item.Category == "tags.auto" && item.Message.Contains("checked every title", StringComparison.Ordinal));
        Assert.Contains("1 title", summary.Message);
    }

    [Fact]
    public async Task A_tag_used_by_a_rule_cannot_be_deleted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tag = await app.Services.GetRequiredService<IPlatformSettingsRepository>().CreateTagAsync(new CreateTagRequest("cartoons", null, null), CancellationToken.None);
        await CreateRuleAsync(app, new
        {
            name = "Cartoons",
            tags = new[] { "cartoons" },
            specifications = new[] { new { type = "genre", negate = false, value = "Animation" } }
        });

        var refused = await app.Client.DeleteAsync($"/api/tags/{tag.Id}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("still in use", await refused.Content.ReadAsStringAsync());
    }

    private sealed class RecordingOutbound : IOutboundNotificationService
    {
        public List<IReadOnlyList<string>?> Dispatched { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            if (eventCategory == "title.added")
            {
                Dispatched.Add(titleTags);
            }

            return Task.CompletedTask;
        }

        public Task<NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
