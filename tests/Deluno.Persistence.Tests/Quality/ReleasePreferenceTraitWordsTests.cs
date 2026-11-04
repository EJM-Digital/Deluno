using System.Text.RegularExpressions;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #1488: a refusal names the custom format or built-in trait the person set,
/// in one plain sentence, and never an internal trait id.
/// </summary>
public sealed class ReleasePreferenceTraitWordsTests
{
    private const string FormatName = "x265 (no HDR)";
    private const string FormatId = "format-one";
    private static readonly string Matched = CustomFormatPreferenceTraits.Matched(FormatId);
    private static readonly string NotMatched = CustomFormatPreferenceTraits.NotMatched(FormatId);

    private static ReleasePreferencePlan Plan(
        IReadOnlyList<string>? required = null,
        IReadOnlyList<string>? forbidden = null,
        IReadOnlyList<IReadOnlyList<string>>? requiredAny = null,
        string? ruleName = FormatName)
        => new(
            Id: "movies/default",
            Version: "1",
            MediaType: "movies",
            Families:
            [
                new PreferenceFamily("quality", "Quality", 1, PreferenceIntent.Ranked,
                    [new PreferenceFamilyLevel("bluray-1080", 0, ["quality.bluray-1080p"])],
                    TargetLevelId: "bluray-1080")
            ],
            RequiredTraitIds: required,
            ForbiddenTraitIds: forbidden,
            Sources:
            [
                new PreferencePlanProvenance(
                    SourceKind: CustomFormatPreferenceTraits.SourceKind,
                    SourceId: FormatId,
                    SourceVersion: "1",
                    MappingVersion: CustomFormatPreferenceTraits.MappingVersion,
                    MatcherDefinition: "x265",
                    MappedTraitIds: [Matched, NotMatched],
                    RuleId: FormatId,
                    RuleName: ruleName)
            ],
            RequiredAnyTraitGroups: requiredAny);

    private static IReadOnlyList<string> Reasons(ReleasePreferencePlan plan, params PreferenceFact[] facts)
        => ReleasePreferenceEvaluator.Evaluate(plan, facts).Reasons;

    private static PreferenceFact Fact(string id, PreferenceFactState state) => new(id, state);

    [Fact]
    public void A_forbidden_custom_format_that_is_present_is_named()
    {
        var reasons = Reasons(Plan(forbidden: [Matched]), Fact(Matched, PreferenceFactState.Present));

        Assert.Contains("It matches your custom format \"x265 (no HDR)\", which this profile never takes.", reasons);
    }

    [Fact]
    public void A_forbidden_custom_format_that_is_unknown_is_named()
    {
        var reasons = Reasons(Plan(forbidden: [Matched]), Fact(Matched, PreferenceFactState.Unknown));

        Assert.Contains("Deluno cannot tell whether it matches your custom format \"x265 (no HDR)\", which this profile never takes.", reasons);
    }

    [Fact]
    public void A_required_custom_format_that_is_absent_is_named()
    {
        var reasons = Reasons(Plan(required: [Matched]), Fact(Matched, PreferenceFactState.Absent));

        Assert.Contains("It does not match your custom format \"x265 (no HDR)\", which this profile requires.", reasons);
    }

    [Fact]
    public void A_required_custom_format_that_is_unknown_is_named()
    {
        var reasons = Reasons(Plan(required: [Matched]), Fact(Matched, PreferenceFactState.Unknown));

        Assert.Contains("Deluno cannot tell whether it matches your custom format \"x265 (no HDR)\", which this profile requires.", reasons);
    }

    [Fact]
    public void A_required_not_matched_half_reads_the_other_way_round()
    {
        var reasons = Reasons(Plan(required: [NotMatched]), Fact(NotMatched, PreferenceFactState.Absent));

        Assert.Contains("It matches your custom format \"x265 (no HDR)\", which this profile never takes.", reasons);
    }

    [Fact]
    public void A_required_built_in_trait_uses_its_plain_label()
    {
        var reasons = Reasons(Plan(required: ["video.codec.hevc"]), Fact("video.codec.hevc", PreferenceFactState.Absent));

        Assert.Contains("It does not match HEVC, which this profile requires.", reasons);
    }

    [Fact]
    public void A_required_language_says_it_is_a_language()
    {
        var reasons = Reasons(Plan(required: ["audio.language.en"]), Fact("audio.language.en", PreferenceFactState.Absent));

        Assert.Contains(reasons, reason => reason.Contains("the language ", StringComparison.Ordinal) && !reason.Contains("audio.language", StringComparison.Ordinal));
    }

    [Fact]
    public void A_required_any_group_with_nothing_present_names_each_member()
    {
        var reasons = Reasons(
            Plan(requiredAny: [["video.codec.hevc", Matched]]),
            Fact("video.codec.hevc", PreferenceFactState.Absent),
            Fact(Matched, PreferenceFactState.Absent));

        Assert.Contains("It matches none of HEVC, your custom format \"x265 (no HDR)\", and this profile needs at least one.", reasons);
    }

    [Fact]
    public void A_required_any_group_with_conflicting_evidence_names_each_member()
    {
        var reasons = Reasons(
            Plan(requiredAny: [["video.codec.hevc", "video.codec.av1"]]),
            Fact("video.codec.hevc", PreferenceFactState.Conflicting),
            Fact("video.codec.av1", PreferenceFactState.Absent));

        Assert.Contains("The evidence conflicts on whether it matches any of HEVC, AV1, and this profile needs at least one.", reasons);
    }

    [Fact]
    public void A_required_any_group_with_incomplete_evidence_names_each_member()
    {
        var reasons = Reasons(
            Plan(requiredAny: [["video.codec.hevc", "video.codec.av1"]]),
            Fact("video.codec.hevc", PreferenceFactState.Unknown),
            Fact("video.codec.av1", PreferenceFactState.Absent));

        Assert.Contains("Deluno cannot yet tell whether it matches any of HEVC, AV1, and this profile needs at least one.", reasons);
    }

    [Fact]
    public void A_custom_format_whose_name_is_not_in_the_plan_reads_in_plain_words()
    {
        var reasons = Reasons(Plan(forbidden: [Matched], ruleName: null), Fact(Matched, PreferenceFactState.Present));

        Assert.Contains("It matches a custom format that no longer exists, which this profile never takes.", reasons);
    }

    [Fact]
    public void A_trait_reached_by_an_alias_uses_its_plain_label()
    {
        var reasons = Reasons(Plan(forbidden: ["unwanted.cam"]), Fact("unwanted.cam", PreferenceFactState.Present));

        Assert.Contains("It matches CAM, which this profile never takes.", reasons);
    }

    [Fact]
    public void No_reason_the_evaluator_can_write_contains_a_trait_id()
    {
        var idPattern = new Regex(@"custom-format\.|\.matched|\.not-matched|\b(?:video|audio|source|quality|release-group|transient|unwanted)\.[a-z0-9-]+", RegexOptions.IgnoreCase);
        string[] ids = [Matched, NotMatched, "video.codec.hevc", "audio.language.en", "unwanted.cam", "source.webrip"];
        var swept = 0;

        foreach (var ruleName in new string?[] { FormatName, null })
        foreach (var id in ids)
        foreach (var state in Enum.GetValues<PreferenceFactState>())
        {
            var facts = new[] { Fact(id, state), Fact("video.codec.av1", state) };
            var plans = new[]
            {
                Plan(required: [id], ruleName: ruleName),
                Plan(forbidden: [id], ruleName: ruleName),
                Plan(requiredAny: [[id, "video.codec.av1"]], ruleName: ruleName)
            };
            foreach (var plan in plans)
            {
                PreferenceEvaluation evaluation;
                try
                {
                    evaluation = ReleasePreferenceEvaluator.Evaluate(plan, facts);
                }
                catch (ArgumentException)
                {
                    // A plan the validator refuses never reaches a person.
                    continue;
                }

                foreach (var reason in evaluation.Reasons)
                {
                    swept++;
                    Assert.DoesNotMatch(idPattern, reason);
                }
            }
        }

        Assert.True(swept > 50);
    }
}
