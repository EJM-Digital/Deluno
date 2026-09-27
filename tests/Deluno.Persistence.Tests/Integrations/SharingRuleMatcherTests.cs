using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// The seeding-rule matrix's own matching (#820): category/tracker/tag,
/// evaluated in priority order, with the first enabled match winning.
/// </summary>
public sealed class SharingRuleMatcherTests
{
    private static SharingRuleItem Rule(
        string id,
        int priority,
        string? category = null,
        string? tracker = null,
        string? tagsCsv = null,
        string tagMatchMode = SharingRuleItem.TagMatchAny,
        bool isEnabled = true,
        int? minSeeders = null,
        int? maxInactiveDays = null)
        => new(
            id,
            priority,
            category,
            tracker,
            tagsCsv,
            tagMatchMode,
            Mode: null,
            ForHours: null,
            UntilRatio: null,
            StuckAction: null,
            StuckAfterDays: null,
            CompletionAction: null,
            MinSeeders: minSeeders,
            MaxInactiveDays: maxInactiveDays,
            IsEnabled: isEnabled,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);

    [Fact]
    public void No_rules_match_nothing()
    {
        var match = SharingRuleMatcher.FindMatch([], new SharingRuleMatchContext("movies", "tracker.example.com", []));

        Assert.Null(match);
    }

    [Fact]
    public void Category_match_wins_over_no_match()
    {
        var rules = new[] { Rule("a", priority: 1, category: "tv-anime") };

        var match = SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext("tv-anime", null, []));

        Assert.Equal("a", match?.Id);
    }

    [Fact]
    public void Tracker_domain_pattern_matches_the_domain_and_its_subdomains()
    {
        var rules = new[] { Rule("a", priority: 1, tracker: "*.example.com") };

        Assert.NotNull(SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext(null, "tracker.example.com", [])));
        Assert.NotNull(SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext(null, "example.com", [])));
        Assert.Null(SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext(null, "example.org", [])));
    }

    [Fact]
    public void Tag_any_mode_matches_when_at_least_one_tag_is_held()
    {
        var rules = new[] { Rule("a", priority: 1, tagsCsv: "anime,4k", tagMatchMode: SharingRuleItem.TagMatchAny) };

        var match = SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext(null, null, ["4k"]));

        Assert.Equal("a", match?.Id);
    }

    [Fact]
    public void Tag_all_mode_requires_every_tag_to_be_held()
    {
        var rules = new[] { Rule("a", priority: 1, tagsCsv: "anime,4k", tagMatchMode: SharingRuleItem.TagMatchAll) };

        Assert.Null(SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext(null, null, ["4k"])));
        Assert.NotNull(SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext(null, null, ["4k", "anime"])));
    }

    [Fact]
    public void A_rule_that_names_no_criterion_never_matches()
    {
        var rules = new[] { Rule("a", priority: 1) };

        var match = SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext("movies", "tracker.example.com", ["anime"]));

        Assert.Null(match);
    }

    [Fact]
    public void First_matching_rule_in_priority_order_wins()
    {
        var rules = new[]
        {
            Rule("low-priority-but-also-matches", priority: 5, category: "movies"),
            Rule("highest-priority", priority: 1, category: "movies")
        };

        var match = SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext("movies", null, []));

        Assert.Equal("highest-priority", match?.Id);
    }

    [Fact]
    public void A_disabled_rule_is_skipped_even_if_it_would_otherwise_match_first()
    {
        var rules = new[]
        {
            Rule("disabled", priority: 1, category: "movies", isEnabled: false),
            Rule("enabled", priority: 2, category: "movies")
        };

        var match = SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext("movies", null, []));

        Assert.Equal("enabled", match?.Id);
    }

    [Fact]
    public void A_rule_scoped_to_one_category_does_not_match_a_different_one()
    {
        var rules = new[] { Rule("a", priority: 1, category: "tv-anime") };

        var match = SharingRuleMatcher.FindMatch(rules, new SharingRuleMatchContext("movies", null, []));

        Assert.Null(match);
    }
}
