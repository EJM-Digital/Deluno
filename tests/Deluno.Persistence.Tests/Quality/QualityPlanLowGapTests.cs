using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// The low-severity plan rows of #1329: how a profile compiles into a plan (which rules it considers, which need review),
/// the plan reference's all-or-nothing rule and the cheap identity of an installed file (a real file in a temp folder).
/// </summary>
public sealed class QualityPlanLowGapTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("deluno-plan-low-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CustomFormatItem Format(string id, string name, string conditions)
        => new(id, name, "movies", 10, null, conditions, true, Now, Now);

    private static QualityProfileItem Profile(string customFormatIds, string cutoff = "WEB 1080p")
        => new("profile-1", "Profile", "movies", cutoff, "WEB 1080p,Bluray 1080p", customFormatIds, true, false, false, null, null, false, Now, Now);

    private const string Good = "[{\"type\":\"source\",\"value\":\"web\",\"required\":true}]";

    [Fact]
    public void A_plan_reference_with_only_some_of_plan_id_version_and_hash_is_refused_and_an_empty_one_is_none()
    {
        Assert.Null(ReleasePreferencePlanReference.Normalize(null));
        Assert.Null(ReleasePreferencePlanReference.Normalize(new ReleasePreferencePlanReference(" ", "", "  ")));
        Assert.Null(ReleasePreferencePlanReferenceCodec.Serialize(new ReleasePreferencePlanReference("", "", "")));

        foreach (var partial in new[]
                 {
                     new ReleasePreferencePlanReference("plan", "", ""),
                     new ReleasePreferencePlanReference("", "v1", ""),
                     new ReleasePreferencePlanReference("", "", "abc"),
                     new ReleasePreferencePlanReference("plan", "v1", ""),
                     new ReleasePreferencePlanReference("plan", "", "abc"),
                     new ReleasePreferencePlanReference("", "v1", "abc")
                 })
        {
            var error = Assert.Throws<ArgumentException>(() => ReleasePreferencePlanReference.Normalize(partial));
            Assert.Contains("must include a plan id, version and hash", error.Message);
        }

        var whole = ReleasePreferencePlanReference.Normalize(new ReleasePreferencePlanReference(" plan ", " v1 ", " ABC "));
        Assert.Equal(new ReleasePreferencePlanReference("plan", "v1", "abc"), whole);
    }

    [Fact]
    public void The_identity_of_an_installed_file_is_stable_when_unchanged_and_changes_with_its_path_size_or_write_time()
    {
        var path = Path.Combine(_folder, "Film (2020).mkv");
        File.WriteAllText(path, "film");
        var other = Path.Combine(_folder, "Film (2020) copy.mkv");
        File.WriteAllText(other, "film");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(other, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var first = PreferenceFileIdentity.Compute(path, 4);

        Assert.Equal(first, PreferenceFileIdentity.Compute(path, 4));
        Assert.StartsWith("preference-file/v1:", first);
        Assert.NotEqual(first, PreferenceFileIdentity.Compute(path, 5));
        Assert.NotEqual(first, PreferenceFileIdentity.Compute(other, 4));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(first, PreferenceFileIdentity.Compute(path, 4));
    }

    [Fact]
    public void An_unrecognised_cutoff_compiles_to_the_default_web_1080p_cutoff()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("movies", "Gold plated", ["WEB 720p", "WEB 1080p"]);

        var family = plan.Families.Single(item => item.Id == "quality");
        Assert.Equal("web-1080p", family.TargetLevelId);
    }

    [Fact]
    public void Only_the_custom_formats_the_profile_selected_are_considered_and_an_inventory_wide_rule_never_changes_the_plan()
    {
        var selected = Format("selected", "Selected", Good);
        var bystander = Format("bystander", "Bystander", "[{\"type\":\"codec\",\"value\":\"x265\",\"required\":true}]");
        var profile = Profile("selected");

        var alone = ReleasePreferencePlanFactory.CreateQualityPlan(profile, [selected]);
        var withInventory = ReleasePreferencePlanFactory.CreateQualityPlan(profile, [selected, bystander]);
        var none = ReleasePreferencePlanFactory.CreateQualityPlan(Profile(""), [selected, bystander]);

        Assert.Equal(alone.PlanHash, withInventory.PlanHash);
        Assert.NotEqual(alone.PlanHash, none.PlanHash);
        Assert.Equal(
            ReleasePreferencePlanFactory.CreateQualityPlan(Profile(""), []).Families.Count,
            none.Families.Count);
    }

    [Fact]
    public void CompileProfile_lists_the_rules_that_need_review_with_warnings_and_sets_the_review_flag()
    {
        var formats = new[]
        {
            Format("good", "Good rule", Good),
            Format("blank", "Blank rule", ""),
            Format("odd", "Odd rule", "[{\"type\":\"telepathy\",\"value\":\"web\",\"required\":true}]")
        };

        var clean = ReleasePreferencePlanFactory.CompileProfile(Profile("good"), formats);
        var review = ReleasePreferencePlanFactory.CompileProfile(Profile("good,blank,odd,gone"), formats);

        Assert.False(clean.RequiresReview);
        Assert.Empty(clean.AdvancedRules);
        Assert.Empty(clean.Warnings);
        Assert.True(review.RequiresReview);
        Assert.Equal(["blank", "odd", "gone"], review.AdvancedRules.Select(rule => rule.RuleId));
        Assert.Contains("Custom format 'Blank rule' cannot be evaluated completely and needs review.", review.Warnings);
        Assert.Contains("Custom format 'Odd rule' cannot be evaluated completely and needs review.", review.Warnings);
        Assert.Contains("Custom format 'gone' is referenced but missing from the inventory.", review.Warnings);
        Assert.Contains(review.AdvancedRules, rule =>
            rule.RuleId == "blank" && rule.Explanation!.Contains("has no matcher conditions"));
        Assert.Contains(review.AdvancedRules, rule =>
            rule.RuleId == "gone" && rule.Explanation!.Contains("not present in the current inventory"));
        Assert.All(review.AdvancedRules, rule => Assert.True(rule.RequiresReview));
    }
}
