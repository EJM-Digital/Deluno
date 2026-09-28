using Deluno.Libraries.Contracts;
using Deluno.Libraries.Routing;

namespace Deluno.Integrations.Tests.Libraries;

/// <summary>
/// #1033: the resolver Seerr (and, in time, any other caller that used to
/// guess "the first library of this type") reads to place a title by
/// Deluno's own destination rules, evaluated on the title's metadata rather
/// than a release's file name.
/// </summary>
public sealed class LibraryRoutingResolverTests
{
    private static readonly LibraryRoutingResolver Resolver = new();

    private static LibraryItem Library(string id, string mediaType = "movies")
        => new(
            Id: id,
            Name: id,
            MediaType: mediaType,
            Purpose: "General",
            RootPath: $@"D:\Media\{id}",
            DownloadsPath: null,
            QualityProfileId: null,
            QualityProfileName: null,
            CutoffQuality: null,
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true,
            ImportWorkflow: "direct",
            ProcessorName: null,
            ProcessorOutputPath: null,
            ProcessorTimeoutMinutes: 60,
            ProcessorFailureMode: "hold",
            AutoSearchEnabled: true,
            MissingSearchEnabled: true,
            UpgradeSearchEnabled: true,
            SearchIntervalMinutes: 6,
            RetryDelayHours: 12,
            MaxItemsPerRun: 50,
            SearchWindowStartHour: null,
            SearchWindowEndHour: null,
            AutomationStatus: "active",
            SearchRequested: false,
            LastSearchedUtc: null,
            NextSearchUtc: null,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);

    private static DestinationRuleItem Rule(string matchKind, string matchValue, string libraryId, string mediaType = "movies", int priority = 10, bool isEnabled = true)
        => new(
            Id: $"rule-{matchKind}-{matchValue}",
            Name: $"{matchKind}:{matchValue}",
            MediaType: mediaType,
            MatchKind: matchKind,
            MatchValue: matchValue,
            RootPath: string.Empty,
            FolderTemplate: null,
            Priority: priority,
            IsEnabled: isEnabled,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch,
            LibraryId: libraryId);

    [Fact]
    public void A_genre_rule_wins_when_it_matches()
    {
        var libraries = new[] { Library("movies"), Library("docs") };
        var rules = new[] { Rule("genre", "Documentary", "docs") };
        var metadata = new LibraryRoutingMetadata(["Documentary", "History"], "en", null, null);

        var result = Resolver.Resolve("movies", metadata, libraries, rules, fallbackLibraryId: "movies");

        Assert.True(result.Matched);
        Assert.Equal("docs", result.Library!.Id);
        Assert.Equal("rule-genre-Documentary", result.Rule!.Id);
    }

    [Fact]
    public void An_anime_rule_matches_animation_with_a_japanese_original_language()
    {
        var libraries = new[] { Library("movies"), Library("anime") };
        var rules = new[] { Rule("anime", "true", "anime") };
        var metadata = new LibraryRoutingMetadata(["Animation", "Adventure"], "ja", null, null);

        var result = Resolver.Resolve("movies", metadata, libraries, rules, fallbackLibraryId: "movies");

        Assert.Equal("anime", result.Library!.Id);
    }

    [Fact]
    public void An_anime_rule_does_not_match_western_animation()
    {
        var libraries = new[] { Library("movies"), Library("anime") };
        var rules = new[] { Rule("anime", "true", "anime") };
        var metadata = new LibraryRoutingMetadata(["Animation", "Family"], "en", null, null);

        var result = Resolver.Resolve("movies", metadata, libraries, rules, fallbackLibraryId: "movies");

        Assert.Equal("movies", result.Library!.Id);
        Assert.Null(result.Rule);
    }

    /// <summary>#985's per-connection 4K mapping is now a "requested in 4K" rule condition.</summary>
    [Fact]
    public void A_requested_4k_rule_matches_only_a_4k_request()
    {
        var libraries = new[] { Library("movies"), Library("movies-4k") };
        var rules = new[] { Rule("requested4k", "true", "movies-4k") };

        var fourK = Resolver.Resolve("movies", new LibraryRoutingMetadata([], "en", null, null, RequestedIn4k: true), libraries, rules, fallbackLibraryId: "movies");
        Assert.Equal("movies-4k", fourK.Library!.Id);

        var notFourK = Resolver.Resolve("movies", new LibraryRoutingMetadata([], "en", null, null, RequestedIn4k: false), libraries, rules, fallbackLibraryId: "movies");
        Assert.Equal("movies", notFourK.Library!.Id);
        Assert.Null(notFourK.Rule);
    }

    [Fact]
    public void No_rule_matches_so_the_fallback_library_is_used()
    {
        var libraries = new[] { Library("movies") };
        var rules = new[] { Rule("genre", "Documentary", "docs") }; // "docs" does not exist among libraries
        var metadata = new LibraryRoutingMetadata(["Action"], "en", null, null);

        var result = Resolver.Resolve("movies", metadata, libraries, rules, fallbackLibraryId: "movies");

        Assert.Equal("movies", result.Library!.Id);
        Assert.Null(result.Rule);
    }

    [Fact]
    public void No_rule_matches_and_no_fallback_is_set_so_nothing_resolves()
    {
        var libraries = new[] { Library("movies") };
        var metadata = new LibraryRoutingMetadata(["Action"], "en", null, null);

        var result = Resolver.Resolve("movies", metadata, libraries, rules: [], fallbackLibraryId: null);

        Assert.False(result.Matched);
        Assert.Null(result.Library);
        Assert.Null(result.Rule);
    }

    [Fact]
    public void A_disabled_rule_is_skipped()
    {
        var libraries = new[] { Library("movies"), Library("docs") };
        var rules = new[] { Rule("genre", "Documentary", "docs", isEnabled: false) };
        var metadata = new LibraryRoutingMetadata(["Documentary"], "en", null, null);

        var result = Resolver.Resolve("movies", metadata, libraries, rules, fallbackLibraryId: "movies");

        Assert.Equal("movies", result.Library!.Id);
        Assert.Null(result.Rule);
    }

    [Fact]
    public void A_tag_rule_places_a_tagged_title_and_names_the_tag()
    {
        var libraries = new[] { Library("movies"), Library("kids") };
        var rules = new[] { Rule("tag", "kids", "kids") };
        var metadata = new LibraryRoutingMetadata(["Family"], "en", null, null, Tags: ["Kids", "watched"]);

        var result = Resolver.Resolve("movies", metadata, libraries, rules, fallbackLibraryId: "movies");

        Assert.Equal("kids", result.Library!.Id);
        Assert.Equal("kids", result.MatchedTag);
        Assert.Contains("tag \"kids\"", result.Explanation);
    }

    [Fact]
    public void A_tag_rule_never_places_an_untagged_title()
    {
        var libraries = new[] { Library("movies"), Library("kids") };
        var rules = new[] { Rule("tag", "kids", "kids") };

        foreach (var tags in new IReadOnlyList<string>?[] { null, [], ["other"] })
        {
            var metadata = new LibraryRoutingMetadata(["Family"], "en", null, null, Tags: tags);
            var result = Resolver.Resolve("movies", metadata, libraries, rules, fallbackLibraryId: "movies");

            Assert.Equal("movies", result.Library!.Id);
            Assert.Null(result.Rule);
            Assert.Null(result.MatchedTag);
        }
    }

    [Fact]
    public void A_tag_rule_follows_the_usual_priority_order()
    {
        var libraries = new[] { Library("movies"), Library("kids"), Library("docs") };
        var rules = new[]
        {
            Rule("genre", "Documentary", "docs", priority: 5),
            Rule("tag", "kids", "kids", priority: 10)
        };
        var metadata = new LibraryRoutingMetadata(["Documentary"], "en", null, null, Tags: ["kids"]);

        var result = Resolver.Resolve("movies", metadata, libraries, rules, fallbackLibraryId: "movies");

        Assert.Equal("docs", result.Library!.Id);
        Assert.Null(result.MatchedTag);
    }

    [Fact]
    public void No_library_of_the_requested_media_type_never_matches()
    {
        var libraries = new[] { Library("movies", "movies") };
        var metadata = new LibraryRoutingMetadata([], "en", null, null);

        var result = Resolver.Resolve("tv", metadata, libraries, rules: [], fallbackLibraryId: "movies");

        Assert.False(result.Matched);
    }
}
