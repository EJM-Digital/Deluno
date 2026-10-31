using Deluno.Contracts;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>The subtitle rules in Deluno.Contracts that need no database: names, languages, tallies. #1268.</summary>
public sealed class SubtitleContractsTests
{
    [Fact]
    public void A_name_policy_accepts_by_any_required_word_and_refuses_any_forbidden_one()
    {
        var empty = new SubtitleNamePolicy();
        Assert.False(empty.IsEnabled);
        Assert.True(empty.Accepts("anything"));

        var policy = new SubtitleNamePolicy(["ntb", "flux"], ["hdr"]);
        Assert.True(policy.IsEnabled);
        Assert.True(policy.Accepts(null));
        Assert.True(policy.Accepts("   "));
        Assert.True(policy.Accepts("Show.S01E01.FLUX"));
        Assert.False(policy.Accepts("Show.S01E01.FLUX.HDR"));
        Assert.False(policy.Accepts("Show.S01E01.OTHER"));

        var onlyForbidden = new SubtitleNamePolicy(null, ["hdr"]);
        Assert.True(onlyForbidden.IsEnabled);
        Assert.True(onlyForbidden.Accepts("Show.SDR"));
        Assert.False(onlyForbidden.Accepts("Show.HDR"));

        Assert.True(new SubtitleNamePolicy([], []).Accepts("x") && !new SubtitleNamePolicy([], []).IsEnabled);
    }

    [Fact]
    public void Two_name_policies_are_equal_when_their_words_are_the_same_in_the_same_order()
    {
        var a = new SubtitleNamePolicy(["a", "b"], ["c"]);
        var b = new SubtitleNamePolicy(["a", "b"], ["c"]);

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals(new SubtitleNamePolicy(["a", "b"], ["d"])));
        Assert.False(a.Equals(new SubtitleNamePolicy(["a"], ["c"])));
        Assert.False(a.Equals(null));
        Assert.True(new SubtitleNamePolicy().Equals(new SubtitleNamePolicy([], null)));
        Assert.Equal(new SubtitleNamePolicy().GetHashCode(), new SubtitleNamePolicy([], []).GetHashCode());
    }

    [Fact]
    public void A_name_policy_is_cleaned_stored_and_read_back_and_a_bad_one_reads_as_none()
    {
        Assert.Null(SubtitleNamePolicyCodec.Normalize(null));
        Assert.Null(SubtitleNamePolicyCodec.Normalize(new SubtitleNamePolicy([" ", ""], null)));

        var cleaned = SubtitleNamePolicyCodec.Normalize(new SubtitleNamePolicy([" NTb ", "flux", "ntb"], ["", "HDR"]))!;
        Assert.Equal(["flux", "ntb"], cleaned.MustContain);
        Assert.Equal(["hdr"], cleaned.MustNotContain);

        Assert.Null(SubtitleNamePolicyCodec.Serialize(null));
        var json = SubtitleNamePolicyCodec.Serialize(new SubtitleNamePolicy(["NTb"], null))!;
        Assert.Equal(["ntb"], SubtitleNamePolicyCodec.Deserialize(json)!.MustContain);
        Assert.Null(SubtitleNamePolicyCodec.Deserialize(null));
        Assert.Null(SubtitleNamePolicyCodec.Deserialize("  "));
        Assert.Null(SubtitleNamePolicyCodec.Deserialize("{not json"));
    }

    [Fact]
    public void Decisions_are_tallied_named_and_described_in_plain_words()
    {
        var tally = new SubtitleDecisionTally();
        foreach (var decision in Enum.GetValues<SubtitleDecision>())
        {
            tally = tally.With(decision);
        }

        Assert.Equal(new SubtitleDecisionTally(1, 1, 1, 1, 1), tally);
        Assert.Equal(5, tally.Total);
        Assert.Equal(3, tally.Held);
        Assert.Equal(
            ["rejected", "needs-review", "needs-sync", "usable-fallback", "meets-subtitle-plan"],
            Enum.GetValues<SubtitleDecision>().OrderBy(d => (int)d).Select(d => d.ToStoredName()));
    }

    [Fact]
    public void A_tally_reads_as_one_sentence_whatever_the_mix()
    {
        Assert.Equal("Nothing in Films was short of a subtitle.", SubtitleDecisions.Describe(new SubtitleDecisionTally(), "Films"));
        Assert.Equal(
            "Looked for 2 subtitle(s) in Films: 2 nobody had.",
            SubtitleDecisions.Describe(new SubtitleDecisionTally(Rejected: 2), "Films"));
        Assert.Equal(
            "Looked for 3 subtitle(s) in Films: 1 that need a look and 2 nobody had.",
            SubtitleDecisions.Describe(new SubtitleDecisionTally(Rejected: 2, NeedsReview: 1), "Films"));
        Assert.Equal(
            "Fetched 6 of 7 subtitle(s) for Films: 3 made for the exact file, 2 queued to be timed against the audio, 1 watchable but still worth upgrading and 1 nobody had.",
            SubtitleDecisions.Describe(new SubtitleDecisionTally(Rejected: 1, NeedsSync: 2, UsableFallback: 1, MeetsSubtitlePlan: 3), "Films"));
    }

    [Fact]
    public void Language_modes_and_sources_normalise_what_they_are_given()
    {
        Assert.Equal(SubtitleLanguageModes.First, SubtitleLanguageModes.Normalize(" FIRST "));
        Assert.Equal(SubtitleLanguageModes.All, SubtitleLanguageModes.Normalize(null));
        Assert.Equal(SubtitleLanguageModes.All, SubtitleLanguageModes.Normalize("whatever"));
        Assert.Equal(0, SubtitleLanguageModes.WantedPerFile([], "first"));
        Assert.Equal(1, SubtitleLanguageModes.WantedPerFile(["en", "fr"], "first"));
        Assert.Equal(2, SubtitleLanguageModes.WantedPerFile(["en", "fr"], "all"));

        Assert.True(SubtitleSources.IsKnown(" Fetched "));
        Assert.False(SubtitleSources.IsKnown("other"));
        Assert.False(SubtitleSources.IsKnown(null));
    }

    [Fact]
    public void Languages_are_found_by_code_name_alias_or_locale_and_unreadable_ones_are_dropped()
    {
        Assert.Null(SubtitleLanguages.Normalize(null));
        Assert.Null(SubtitleLanguages.Normalize("  "));
        Assert.Equal("en", SubtitleLanguages.Normalize(" ENG "));
        Assert.Equal("fr", SubtitleLanguages.Normalize("French"));
        Assert.Equal("pt", SubtitleLanguages.Normalize("pt-BR"));
        Assert.Equal("zh", SubtitleLanguages.Normalize("zh_Hans"));
        Assert.Equal(SubtitleLanguages.Unknown, SubtitleLanguages.Normalize("zxx"));
        Assert.Null(SubtitleLanguages.Normalize("-"));
        Assert.Null(SubtitleLanguages.Normalize("xx-yy"));

        Assert.Empty(SubtitleLanguages.ParseList(null));
        Assert.Equal(["en", "fr"], SubtitleLanguages.ParseList("eng, French,en,,und,nonsense"));

        Assert.Equal("Unknown", SubtitleLanguages.DisplayName(null));
        Assert.Equal("Unknown", SubtitleLanguages.DisplayName("und"));
        Assert.Equal("French", SubtitleLanguages.DisplayName("fre"));
    }

    [Fact]
    public void Type_preferences_are_cleaned_keep_one_per_language_and_fall_back_to_the_default()
    {
        Assert.Empty(SubtitleLanguageTypePreferenceCodec.Normalize(null));
        Assert.Empty(SubtitleLanguageTypePreferenceCodec.Normalize([]));

        var cleaned = SubtitleLanguageTypePreferenceCodec.Normalize(
        [
            new("fre", "PLAIN-ONLY"),
            new("nonsense"),
            new("und"),
            new("en", "bogus"),
            new("french", SubtitleHearingImpairedPreferences.HearingImpairedPreferred)
        ]);

        Assert.Equal(["en", "fr"], cleaned.Select(p => p.Language));
        Assert.Equal(SubtitleHearingImpairedPreferences.PlainPreferred, cleaned[0].HearingImpaired);
        Assert.Equal(SubtitleHearingImpairedPreferences.HearingImpairedPreferred, cleaned[1].HearingImpaired);

        Assert.Null(SubtitleLanguageTypePreferenceCodec.Serialize(null));
        var json = SubtitleLanguageTypePreferenceCodec.Serialize([new("fre", SubtitleHearingImpairedPreferences.PlainOnly, true)])!;
        var read = Assert.Single(SubtitleLanguageTypePreferenceCodec.Deserialize(json));
        Assert.Equal("fr", read.Language);
        Assert.True(read.IncludeForced);
        Assert.Empty(SubtitleLanguageTypePreferenceCodec.Deserialize(" "));
        Assert.Empty(SubtitleLanguageTypePreferenceCodec.Deserialize("{bad"));

        Assert.Same(read, SubtitleLanguageTypePreferenceCodec.For([read], "French"));
        Assert.Equal("de", SubtitleLanguageTypePreferenceCodec.For([read], "German").Language);
        Assert.Equal("zzz", SubtitleLanguageTypePreferenceCodec.For([], "zzz").Language);
        Assert.Equal(SubtitleHearingImpairedPreferences.PlainOnly, SubtitleHearingImpairedPreferences.Normalize(" Plain-Only "));
        Assert.Equal(SubtitleHearingImpairedPreferences.PlainPreferred, SubtitleHearingImpairedPreferences.Normalize(null));
    }

    [Fact]
    public void A_timing_policy_that_is_off_never_repairs_and_an_on_one_repairs_below_its_rung()
    {
        Assert.False(new SubtitleTimingPolicy(Enabled: false).ShouldSync(default));
        Assert.True(new SubtitleTimingPolicy().ShouldSync(default));
    }

    [Fact]
    public void A_name_policy_with_only_required_words_has_nothing_forbidden()
    {
        var policy = new SubtitleNamePolicy(["ntb"], null);

        Assert.True(policy.Accepts("Show.NTb"));
        Assert.False(policy.Accepts("Show.Other"));
    }

    [Fact]
    public void A_content_policy_is_cleaned_stored_and_read_back_and_an_empty_one_is_none()
    {
        Assert.False(new SubtitleContentModificationPolicy(CueColour: " ").IsEnabled);
        Assert.Null(SubtitleContentModificationPolicyCodec.Normalize(null));
        Assert.Null(SubtitleContentModificationPolicyCodec.Normalize(new SubtitleContentModificationPolicy(CueColour: " ")));
        Assert.Equal("#ffff00", SubtitleContentModificationPolicyCodec.Normalize(new SubtitleContentModificationPolicy(CueColour: " #FFFF00 "))!.CueColour);

        Assert.Null(SubtitleContentModificationPolicyCodec.Serialize(null));
        var json = SubtitleContentModificationPolicyCodec.Serialize(new SubtitleContentModificationPolicy(RemoveEmoji: true))!;
        Assert.True(SubtitleContentModificationPolicyCodec.Deserialize(json)!.RemoveEmoji);
        Assert.Null(SubtitleContentModificationPolicyCodec.Deserialize(null));
        Assert.Null(SubtitleContentModificationPolicyCodec.Deserialize("{}"));
    }

    [Fact]
    public void A_timing_policy_is_clamped_stored_and_read_back()
    {
        Assert.Null(SubtitleTimingPolicyCodec.Normalize(null));
        var wild = SubtitleTimingPolicyCodec.Normalize(new SubtitleTimingPolicy(
            SyncOnlyBelow: "same-source", MaxOffsetSeconds: 9999, RequiredPeakSigma: 0.1,
            ExcludedProviders: [" B ", "a", "", "b"]))!;
        Assert.Equal(300, wild.MaxOffsetSeconds);
        Assert.Equal(1.0, wild.RequiredPeakSigma);
        Assert.Equal(["a", "b"], wild.ExcludedProviders);
        Assert.Null(SubtitleTimingPolicyCodec.Normalize(new SubtitleTimingPolicy(ExcludedProviders: [" "]))!.ExcludedProviders);
        Assert.Equal(0, SubtitleTimingPolicyCodec.Normalize(new SubtitleTimingPolicy(MaxOffsetSeconds: 0, RequiredPeakSigma: 99))!.MaxOffsetSeconds - 1);
        Assert.Equal(10.0, SubtitleTimingPolicyCodec.Normalize(new SubtitleTimingPolicy(RequiredPeakSigma: 99))!.RequiredPeakSigma);

        Assert.Null(SubtitleTimingPolicyCodec.Serialize(null));
        var json = SubtitleTimingPolicyCodec.Serialize(new SubtitleTimingPolicy(MaxOffsetSeconds: 30))!;
        Assert.Equal(30, SubtitleTimingPolicyCodec.Deserialize(json)!.MaxOffsetSeconds);
        Assert.Null(SubtitleTimingPolicyCodec.Deserialize(" "));
    }

    [Fact]
    public void Picture_based_codecs_are_told_apart_from_text_ones()
    {
        Assert.True(SubtitlePictureCodecs.IsPictureBased(" HDMV_PGS_SUBTITLE "));
        Assert.True(SubtitlePictureCodecs.IsPictureBased("dvd_subtitle"));
        Assert.False(SubtitlePictureCodecs.IsPictureBased("subrip"));
        Assert.False(SubtitlePictureCodecs.IsPictureBased(null));
        Assert.Equal(5, SubtitlePictureCodecs.All.Count);
    }

    [Fact]
    public void A_timing_policy_repairs_only_below_the_rung_it_names()
    {
        var best = Enum.GetValues<Deluno.Contracts.SubtitleMatch>().Max();
        var worst = Enum.GetValues<Deluno.Contracts.SubtitleMatch>().Min();

        Assert.Equal(SubtitleSyncThreshold.MadeForThisFile, SubtitleSyncThreshold.Normalize(null));
        Assert.False(new SubtitleTimingPolicy().ShouldSync(best));
        Assert.False(new SubtitleTimingPolicy(SyncOnlyBelow: "same-source").ShouldSync(best));
        Assert.True(new SubtitleTimingPolicy(SyncOnlyBelow: "same-source").ShouldSync(worst));
    }
}
