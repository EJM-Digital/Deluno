using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// How much a profile cares about each preference it selected.
///
/// <para>#394: a custom format carried one score globally, so a profile could
/// choose whether to care about HDR10 and never how much. Two shelves that both
/// want HDR could not disagree about whether it is a nice-to-have or the whole
/// point.</para>
/// </summary>
public sealed class ProfileFormatIntentsTests
{
    [Fact]
    public void The_five_answers_are_the_same_words_the_rules_list_uses()
    {
        // Reusing #382's vocabulary is the point: somebody who has read one of
        // these screens has read both. A per-profile score would have been
        // easier to store and would have put an unbounded number back on the
        // surface #353 removed it from.
        Assert.Equal(PreferenceIntent.Forbidden, ProfileFormatIntents.ToPreferenceIntent(ProfileFormatIntents.MustNotHave));
        Assert.Equal(PreferenceIntent.Neutral, ProfileFormatIntents.ToPreferenceIntent(ProfileFormatIntents.DoNotCare));
        Assert.Equal(PreferenceIntent.Ranked, ProfileFormatIntents.ToPreferenceIntent(ProfileFormatIntents.Avoid));
        Assert.Equal(PreferenceIntent.Ranked, ProfileFormatIntents.ToPreferenceIntent(ProfileFormatIntents.Prefer));
        Assert.Equal(PreferenceIntent.Ranked, ProfileFormatIntents.ToPreferenceIntent(ProfileFormatIntents.StronglyPrefer));
    }

    [Fact]
    public void Only_strongly_prefer_can_justify_replacing_a_file_you_already_have()
    {
        Assert.True(ProfileFormatIntents.DrivesUpgrade(ProfileFormatIntents.StronglyPrefer));
        Assert.False(ProfileFormatIntents.DrivesUpgrade(ProfileFormatIntents.Prefer));
        Assert.False(ProfileFormatIntents.DrivesUpgrade(ProfileFormatIntents.Avoid));
        Assert.False(ProfileFormatIntents.DrivesUpgrade(ProfileFormatIntents.DoNotCare));
    }

    [Fact]
    public void A_profile_that_has_not_answered_starts_from_the_guides_recommendation()
    {
        // Not "do not care". A profile that has said nothing behaves exactly as
        // it did before it could answer at all.
        Assert.Equal(ProfileFormatIntents.MustNotHave, ProfileFormatIntents.FromGuideScore(-10000));
        Assert.Equal(ProfileFormatIntents.Avoid, ProfileFormatIntents.FromGuideScore(-50));
        Assert.Equal(ProfileFormatIntents.DoNotCare, ProfileFormatIntents.FromGuideScore(0));
        Assert.Equal(ProfileFormatIntents.Prefer, ProfileFormatIntents.FromGuideScore(100));
        Assert.Equal(ProfileFormatIntents.StronglyPrefer, ProfileFormatIntents.FromGuideScore(500));
    }

    [Fact]
    public void An_answer_nobody_recognises_is_read_as_do_not_care()
    {
        // Never as a refusal. Inventing "must not have" from a value Deluno
        // cannot read would silently empty somebody's search results.
        Assert.Equal(ProfileFormatIntents.DoNotCare, ProfileFormatIntents.Normalize("whatever"));
        Assert.Equal(ProfileFormatIntents.DoNotCare, ProfileFormatIntents.Normalize((string?)null));
        Assert.False(ProfileFormatIntents.Refuses("whatever"));
    }

    [Fact]
    public void Answers_round_trip_and_an_unreadable_one_is_not_fatal()
    {
        var intents = new Dictionary<string, string>
        {
            ["format-a"] = ProfileFormatIntents.StronglyPrefer,
            ["format-b"] = ProfileFormatIntents.MustNotHave
        };

        var round = ProfileFormatIntents.Deserialize(ProfileFormatIntents.Serialize(intents));
        Assert.Equal(ProfileFormatIntents.StronglyPrefer, round["format-a"]);
        Assert.Equal(ProfileFormatIntents.MustNotHave, round["format-b"]);

        Assert.Empty(ProfileFormatIntents.Deserialize("{not json"));
        Assert.Equal(string.Empty, ProfileFormatIntents.Serialize(new Dictionary<string, string>()));
    }

    [Fact]
    public void An_unknown_answer_is_dropped_rather_than_stored()
    {
        var normalized = ProfileFormatIntents.NormalizeAll(new Dictionary<string, string>
        {
            ["good"] = ProfileFormatIntents.Prefer,
            ["nonsense"] = "sideways",
            ["   "] = ProfileFormatIntents.Prefer
        });

        Assert.Single(normalized);
        Assert.True(normalized.ContainsKey("good"));
    }
}
