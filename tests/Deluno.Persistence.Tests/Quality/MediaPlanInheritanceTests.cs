using Deluno.Quality.Contracts;

namespace Deluno.Persistence.Tests.Quality;

public sealed class MediaPlanInheritanceTests
{
    [Fact]
    public void A_library_override_changes_only_the_fields_it_names()
    {
        var plan = BasePlan();

        var result = MediaPlanInheritanceResolver.Resolve(
            plan,
            new MediaPlanLayerOverride(
                QualityProfileId: "quality-library",
                SearchIntervalOverrideHours: 24),
            libraryId: "library-1");

        Assert.Equal("quality-library", result.EffectivePlan.QualityProfileId);
        Assert.Equal(24, result.EffectivePlan.SearchIntervalOverrideHours);
        Assert.Equal("destination-plan", result.EffectivePlan.DestinationRuleId);
        Assert.Equal("cf-b,cf-a", result.EffectivePlan.CustomFormatIds);
        Assert.Equal(MediaPlanLayerKinds.Library, Field(result, "qualityProfileId").SourceKind);
        Assert.Equal("library-1", Field(result, "qualityProfileId").SourceId);
        Assert.Equal(MediaPlanLayerKinds.Library, Field(result, "searchIntervalOverrideHours").SourceKind);
        Assert.Equal(MediaPlanLayerKinds.MediaPlan, Field(result, "destinationRuleId").SourceKind);
    }

    [Fact]
    public void Title_override_wins_for_one_field_without_replacing_the_library_layer()
    {
        var result = MediaPlanInheritanceResolver.Resolve(
            BasePlan(),
            new MediaPlanLayerOverride(
                UpgradeUntilCutoff: false,
                Notes: "Library note"),
            new MediaPlanLayerOverride(
                UpgradeUntilCutoff: true),
            libraryId: "library-1",
            titleId: "movie-1");

        Assert.True(result.EffectivePlan.UpgradeUntilCutoff);
        Assert.Equal("Library note", result.EffectivePlan.Notes);
        Assert.Equal(MediaPlanLayerKinds.Title, Field(result, "upgradeUntilCutoff").SourceKind);
        Assert.Equal("movie-1", Field(result, "upgradeUntilCutoff").SourceId);
        Assert.Equal(MediaPlanLayerKinds.Library, Field(result, "notes").SourceKind);
        Assert.Equal("library-1", Field(result, "notes").SourceId);
    }

    [Fact]
    public void Global_automation_gate_is_a_non_overridable_safety_layer()
    {
        var result = MediaPlanInheritanceResolver.Resolve(
            BasePlan(),
            new MediaPlanLayerOverride(IsEnabled: true),
            new MediaPlanLayerOverride(IsEnabled: true),
            new MediaPlanGlobalSafety(AutomationEnabled: false),
            libraryId: "library-1",
            titleId: "movie-1");

        Assert.False(result.EffectivePlan.IsEnabled);
        var field = Field(result, "isEnabled");
        Assert.Equal(MediaPlanLayerKinds.GlobalSafety, field.SourceKind);
        Assert.True(field.IsSafetyLocked);
        Assert.Contains("Global automation is disabled", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Empty_layers_preserve_the_plan_and_mark_every_field_as_plan_owned()
    {
        var plan = BasePlan();

        var result = MediaPlanInheritanceResolver.Resolve(plan);

        Assert.Equal(plan, result.EffectivePlan);
        Assert.Empty(result.Warnings);
        Assert.All(result.Fields, field =>
        {
            Assert.Equal(MediaPlanLayerKinds.MediaPlan, field.SourceKind);
            Assert.Null(field.SourceId);
            Assert.False(field.IsSafetyLocked);
        });
    }

    private static MediaPlanSnapshot BasePlan()
        => new(
            "Family",
            "movies",
            "quality-plan",
            "destination-plan",
            "cf-b,cf-a",
            12,
            6,
            true,
            true,
            "Plan note",
            new MediaPlanAutomationIntent("family-1080p", 1, "balanced"),
            new ReleasePreferencePlanReference("plan-1", "v1", "hash-1"));

    private static MediaPlanFieldResolution Field(MediaPlanEffectiveResolution result, string name)
        => Assert.Single(result.Fields, field => field.Field == name);
}
