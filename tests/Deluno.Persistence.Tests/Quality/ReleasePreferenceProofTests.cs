using System.Text.Json;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// Small executable proof fixtures for the normative release-preference
/// contract. The tests deliberately generate permutations rather than relying
/// only on one hand-ordered input, which catches accidental dependence on
/// database/indexer ordering.
/// </summary>
public sealed class ReleasePreferenceProofTests
{
    [Fact]
    public void Fact_order_and_restart_serialization_do_not_change_the_evaluation()
    {
        var plan = AudioPlan();
        var facts = Facts("audio.format.truehd-atmos");

        var first = ReleasePreferenceEvaluator.Evaluate(plan, facts);
        var reordered = ReleasePreferenceEvaluator.Evaluate(plan, facts.Reverse());
        var restoredPlan = ReleasePreferencePlanCodec.Deserialize(ReleasePreferencePlanCodec.Serialize(plan));
        var afterRestart = ReleasePreferenceEvaluator.Evaluate(restoredPlan, facts);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(reordered));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(afterRestart));
        Assert.Equal(first.PlanHash, restoredPlan.PlanHash);
        Assert.Equal("truehd-atmos", Assert.Single(first.Families).SelectedLevelId);
    }

    [Fact]
    public void Plan_provenance_order_does_not_change_hash_or_canonical_json()
    {
        var firstSource = new PreferencePlanProvenance(
            "guide",
            "same-rule",
            "v1",
            MappingId: "map",
            MatcherDefinition: "z-pattern",
            MappedTraitIds: ["audio.format.truehd"]);
        var secondSource = new PreferencePlanProvenance(
            "guide",
            "same-rule",
            "v1",
            MappingId: "map",
            MatcherDefinition: "a-pattern",
            MappedTraitIds: ["audio.format.dts"]);
        var first = AudioPlan() with { Sources = [firstSource, secondSource] };
        var reversed = AudioPlan() with { Sources = [secondSource, firstSource] };

        Assert.Equal(first.PlanHash, reversed.PlanHash);
        Assert.Equal(
            ReleasePreferencePlanCodec.Serialize(first),
            ReleasePreferencePlanCodec.Serialize(reversed));
    }

    [Fact]
    public void Plan_validation_rejects_impossible_required_and_compatibility_intent()
    {
        var requiredSpecificButForbiddenBroad = AudioPlan() with
        {
            RequiredTraitIds = ["audio.format.truehd-atmos"],
            ForbiddenTraitIds = ["audio.format.truehd"]
        };
        var requiredPathWith_incompatible_traits = AudioPlan() with
        {
            CompatibilityGroups = [new PreferenceCompatibilityGroup(
                "device/main",
                [["audio.format.truehd", "audio.format.dts"]])],
            Relationships = [new PreferenceRelationship(
                "audio.format.truehd",
                "audio.format.dts",
                PreferenceRelationshipKind.Incompatible)]
        };

        var requiredErrors = ReleasePreferencePlanValidator.Validate(requiredSpecificButForbiddenBroad);
        var pathErrors = ReleasePreferencePlanValidator.Validate(requiredPathWith_incompatible_traits);

        Assert.Contains(requiredErrors, error => error.Contains("forbidden", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pathErrors, error => error.Contains("incompatible", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comparison_is_identity_antisymmetric_and_transitive_for_ordered_facts()
    {
        var plan = AudioPlan();
        var atmos = Facts("audio.format.truehd-atmos");
        var truehd = Facts("audio.format.truehd");
        var dts = Facts("audio.format.dts");

        Assert.Equal(PreferenceCandidateStatus.Equivalent, ReleasePreferenceEvaluator.Compare(plan, atmos, atmos).Status);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, ReleasePreferenceEvaluator.Compare(plan, dts, truehd).Status);
        // Worse than the installed file, but nothing rejected it.
        Assert.Equal(PreferenceCandidateStatus.CurrentBetter, ReleasePreferenceEvaluator.Compare(plan, truehd, dts).Status);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, ReleasePreferenceEvaluator.Compare(plan, dts, atmos).Status);
    }

    [Fact]
    public void Selection_comparison_is_antisymmetric_and_transitive_for_the_generated_matrix()
    {
        var plan = AudioPlan();
        var observations = new[]
        {
            ReleasePreferenceEvaluator.Evaluate(plan, Facts("audio.format.truehd-atmos")),
            ReleasePreferenceEvaluator.Evaluate(plan, Facts("audio.format.truehd")),
            ReleasePreferenceEvaluator.Evaluate(plan, Facts("audio.format.dts"))
        };

        for (var left = 0; left < observations.Length; left++)
        {
            Assert.Equal(0, ReleasePreferenceEvaluator.CompareForSelection(
                plan,
                observations[left],
                observations[left]));

            for (var right = 0; right < observations.Length; right++)
            {
                var forward = Math.Sign(ReleasePreferenceEvaluator.CompareForSelection(
                    plan, observations[left], observations[right]));
                var reverse = Math.Sign(ReleasePreferenceEvaluator.CompareForSelection(
                    plan, observations[right], observations[left]));
                Assert.Equal(-forward, reverse);
            }
        }

        for (var first = 0; first < observations.Length; first++)
        {
            for (var second = 0; second < observations.Length; second++)
            {
                for (var third = 0; third < observations.Length; third++)
                {
                    var firstToSecond = ReleasePreferenceEvaluator.CompareForSelection(
                        plan, observations[first], observations[second]);
                    var secondToThird = ReleasePreferenceEvaluator.CompareForSelection(
                        plan, observations[second], observations[third]);
                    if (firstToSecond < 0 && secondToThird < 0)
                    {
                        Assert.True(
                            ReleasePreferenceEvaluator.CompareForSelection(
                                plan, observations[first], observations[third]) < 0,
                            $"Expected transitivity for {first}, {second}, {third}.");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public void Canonical_truehd_dts_and_hdr_matrix_is_stable_for_movie_and_tv_plans(string mediaType)
    {
        var plan = AudioAndHdrPlan(mediaType);
        var current = MatrixFacts("audio.format.dts-hd-ma", "video.dynamic-range.hdr10");

        var same = ReleasePreferenceEvaluator.Compare(plan, current, current);
        var trueHd = ReleasePreferenceEvaluator.Compare(
            plan,
            current,
            MatrixFacts("audio.format.truehd", "video.dynamic-range.hdr10"));
        var dts = ReleasePreferenceEvaluator.Compare(
            plan,
            current,
            MatrixFacts("audio.format.dts", "video.dynamic-range.hdr10"));
        var aboveTarget = ReleasePreferenceEvaluator.Compare(
            plan,
            MatrixFacts("audio.format.truehd", "video.dynamic-range.hdr10"),
            MatrixFacts("audio.format.truehd-atmos", "video.dynamic-range.hdr10"));
        var dtsX = ReleasePreferenceEvaluator.Compare(
            plan,
            current,
            MatrixFacts("audio.format.dtsx", "video.dynamic-range.hdr10"));
        var trueHdAtmos = ReleasePreferenceEvaluator.Compare(
            plan,
            current,
            MatrixFacts("audio.format.truehd-atmos", "video.dynamic-range.hdr10"));
        var unknownAudio = ReleasePreferenceEvaluator.Compare(
            plan,
            current,
            MatrixFacts(null, "video.dynamic-range.hdr10"));
        var dolbyVisionWithoutFallback = ReleasePreferenceEvaluator.Compare(
            plan,
            current,
            MatrixFacts("audio.format.dts-hd-ma", "video.dynamic-range.dolby-vision"));

        Assert.Equal(PreferenceCandidateStatus.Equivalent, same.Status);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, trueHd.Status);
        Assert.Equal("audio", trueHd.PersistentImprovementFamilyId);

        // Section 11 requires "current is better", which is not the same
        // outcome as "rejected". DTS passes every hard gate in this plan;
        // only the installed file being better stops it. Reporting a gate
        // failure here would name a rule that never fired.
        Assert.Equal(PreferenceCandidateStatus.CurrentBetter, dts.Status);
        Assert.True(dts.Regressed);
        Assert.Equal("audio", dts.DecisiveFamilyId);
        // Names the family that decided it, without claiming a gate failed
        // and without repeating the headline the summary already carries.
        Assert.Contains(dts.Reasons, reason =>
            reason.Contains("Audio format", StringComparison.OrdinalIgnoreCase)
            && reason.Contains("ranks lower", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dts.Reasons, reason =>
            reason.Contains("hard safety", StringComparison.OrdinalIgnoreCase));

        // Both rows that improve audio past the explicit target are upgrades
        // from a below-target installed file, and both name the audio family.
        Assert.Equal(PreferenceCandidateStatus.Upgrade, dtsX.Status);
        Assert.Equal("audio", dtsX.PersistentImprovementFamilyId);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, trueHdAtmos.Status);
        Assert.Equal("audio", trueHdAtmos.PersistentImprovementFamilyId);

        Assert.Equal(PreferenceCandidateStatus.Equivalent, aboveTarget.Status);
        Assert.False(aboveTarget.PersistentImprovement);
        Assert.Equal(PreferenceCandidateStatus.NeedsReview, unknownAudio.Status);
        Assert.Equal(PreferenceCandidateStatus.Rejected, dolbyVisionWithoutFallback.Status);
    }

    /// <summary>
    /// Section 6: the first differing dimension decides. A lower-priority
    /// family whose evidence is missing therefore cannot reopen a question a
    /// higher-priority family has already answered, in either direction.
    ///
    /// This is the ordinary case on a real indexer, not an edge case:
    /// release.revision is open-world, so almost every ordinary release name
    /// leaves it unknown. Without this rule, every worse-quality release comes
    /// back as "needs review" and sends the owner to look at a repack token
    /// that could not have changed the answer.
    /// </summary>
    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public void An_unknown_lower_priority_family_cannot_reopen_a_decided_comparison(string mediaType)
    {
        var plan = AudioAndHdrPlan(mediaType);

        // Audio is the higher-priority family and already decides against the
        // candidate; HDR is unknown for the candidate and cannot rescue it.
        var worseWithUnknownTail = ReleasePreferenceEvaluator.Compare(
            plan,
            MatrixFacts("audio.format.truehd", "video.dynamic-range.hdr10"),
            [
                new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
                new PreferenceFact("audio.format.truehd-atmos", PreferenceFactState.Absent),
                new PreferenceFact("audio.format.dtsx", PreferenceFactState.Absent),
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
                new PreferenceFact("audio.format.dts-hd-ma", PreferenceFactState.Absent),
                new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Present),
                new PreferenceFact("video.dynamic-range.dolby-vision-fallback", PreferenceFactState.Unknown),
                new PreferenceFact("video.dynamic-range.sdr", PreferenceFactState.Absent)
            ]);

        Assert.Equal(PreferenceCandidateStatus.CurrentBetter, worseWithUnknownTail.Status);
        Assert.Equal("audio", worseWithUnknownTail.DecisiveFamilyId);

        // The boundary is unchanged where the unknown really could decide:
        // with no higher-priority difference, incomplete evidence is a review.
        var undecidedWithUnknownTail = ReleasePreferenceEvaluator.Compare(
            plan,
            MatrixFacts("audio.format.truehd", "video.dynamic-range.hdr10"),
            [
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
                new PreferenceFact("audio.format.truehd-atmos", PreferenceFactState.Absent),
                new PreferenceFact("audio.format.dtsx", PreferenceFactState.Absent),
                new PreferenceFact("audio.format.dts-hd-ma", PreferenceFactState.Absent),
                new PreferenceFact("audio.format.dts", PreferenceFactState.Absent),
                new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Unknown),
                new PreferenceFact("video.dynamic-range.dolby-vision-fallback", PreferenceFactState.Unknown),
                new PreferenceFact("video.dynamic-range.sdr", PreferenceFactState.Unknown)
            ]);

        Assert.Equal(PreferenceCandidateStatus.NeedsReview, undecidedWithUnknownTail.Status);
    }

    /// <summary>
    /// Section 11 closing rule: when the owner says either TrueHD or DTS:X is
    /// fine, those alternatives share one level. Moving between them is
    /// lateral, so it must never become replacement work in either direction.
    /// </summary>
    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public void Equal_target_alternatives_are_lateral_and_never_replace_each_other(string mediaType)
    {
        var plan = EqualTopLevelAudioPlan(mediaType);
        var trueHd = MatrixFacts("audio.format.truehd", "video.dynamic-range.hdr10");
        var dtsX = MatrixFacts("audio.format.dtsx", "video.dynamic-range.hdr10");

        var forward = ReleasePreferenceEvaluator.Compare(plan, trueHd, dtsX);
        var reverse = ReleasePreferenceEvaluator.Compare(plan, dtsX, trueHd);

        Assert.Equal(PreferenceCandidateStatus.Equivalent, forward.Status);
        Assert.Equal(PreferenceCandidateStatus.Equivalent, reverse.Status);
        Assert.False(forward.PersistentImprovement);
        Assert.False(reverse.PersistentImprovement);
        Assert.Equal(
            0,
            ReleasePreferenceEvaluator.CompareForSelection(
                plan,
                ReleasePreferenceEvaluator.Evaluate(plan, trueHd),
                ReleasePreferenceEvaluator.Evaluate(plan, dtsX)));
    }

    /// <summary>
    /// Section 12 canonical "best compatible everywhere" device group. The two
    /// rows that were never fixtures are the ones that decide whether Dolby
    /// Vision is safe: a proven HDR10 fallback plays on both televisions and
    /// is preferred, and an unproven fallback is never automatic.
    /// </summary>
    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public void Compatible_everywhere_prefers_proven_fallback_and_reviews_an_unproven_one(string mediaType)
    {
        var plan = AudioAndHdrPlan(mediaType);
        var hdr10 = MatrixFacts("audio.format.truehd", "video.dynamic-range.hdr10");
        var dolbyVisionWithFallback = MatrixFacts(
            "audio.format.truehd",
            "video.dynamic-range.dolby-vision-fallback");

        var hdr10Evaluation = ReleasePreferenceEvaluator.Evaluate(plan, hdr10);
        var fallbackEvaluation = ReleasePreferenceEvaluator.Evaluate(plan, dolbyVisionWithFallback);

        Assert.True(hdr10Evaluation.HardGatesPassed);
        Assert.True(fallbackEvaluation.HardGatesPassed);

        // Compatible on both televisions, and the HDR family order puts a
        // proven Dolby Vision fallback above plain HDR10.
        Assert.True(ReleasePreferenceEvaluator.CompareForSelection(
            plan,
            fallbackEvaluation,
            hdr10Evaluation) < 0);

        PreferenceFact[] unprovenFallbackFacts = [
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
            new PreferenceFact("video.dynamic-range.dolby-vision-fallback", PreferenceFactState.Unknown),
            new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Unknown)
        ];
        var unprovenFallback = ReleasePreferenceEvaluator.Evaluate(plan, unprovenFallbackFacts);

        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, unprovenFallback.Status);
        Assert.False(unprovenFallback.HardGatesPassed);
        Assert.Equal(
            PreferenceCandidateStatus.NeedsReview,
            ReleasePreferenceEvaluator.Compare(plan, hdr10, unprovenFallbackFacts).Status);
    }

    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public void Movie_and_tv_comparisons_are_independent_of_fact_enumeration_order(string mediaType)
    {
        var plan = AudioAndHdrPlan(mediaType);
        var current = MatrixFacts("audio.format.dts-hd-ma", "video.dynamic-range.hdr10").ToArray();
        var candidate = MatrixFacts("audio.format.truehd", "video.dynamic-range.hdr10").ToArray();
        var expected = JsonSerializer.Serialize(ReleasePreferenceEvaluator.Compare(plan, current, candidate));

        // Different query providers and SQLite plans can return the same fact
        // set in any row order. Exercise enough deterministic shuffles to cover
        // every fact in every position without making the proof probabilistic.
        for (var seed = 0; seed < 128; seed++)
        {
            var shuffledCurrent = Shuffle(current, seed);
            var shuffledCandidate = Shuffle(candidate, seed * 397 + 17);
            var actual = ReleasePreferenceEvaluator.Compare(plan, shuffledCurrent, shuffledCandidate);

            Assert.Equal(expected, JsonSerializer.Serialize(actual));
        }
    }

    [Fact]
    public void Selection_comparison_rejects_evaluations_from_a_different_plan_version()
    {
        var plan = AudioPlan();
        var changedPlan = plan with { Version = "2" };
        var first = ReleasePreferenceEvaluator.Evaluate(plan, Facts("audio.format.truehd"));
        var second = ReleasePreferenceEvaluator.Evaluate(changedPlan, Facts("audio.format.dts"));

        var exception = Assert.Throws<ArgumentException>(() =>
            ReleasePreferenceEvaluator.CompareForSelection(plan, first, second));

        Assert.Contains("different release-preference plan", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_open_world_evidence_never_becomes_an_automatic_acceptance()
    {
        var plan = AudioPlan() with { RequiredTraitIds = ["audio.format.truehd"] };
        var evaluation = ReleasePreferenceEvaluator.Evaluate(
            plan,
            [new PreferenceFact("audio.format.truehd", PreferenceFactState.Unknown)]);

        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, evaluation.Status);
        Assert.False(evaluation.HardGatesPassed);
    }

    [Fact]
    public void Required_any_gate_accepts_one_capability_but_rejects_none_and_reviews_unknown()
    {
        var plan = AudioPlan() with
        {
            RequiredAnyTraitGroups = [["video.dynamic-range.hdr10", "video.dynamic-range.dolby-vision-fallback"]]
        };

        var accepted = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Present)
        ]);
        var missing = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("video.dynamic-range.sdr", PreferenceFactState.Present),
            new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Absent),
            new PreferenceFact("video.dynamic-range.dolby-vision-fallback", PreferenceFactState.Absent)
        ]);
        var review = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Unknown),
            new PreferenceFact("video.dynamic-range.dolby-vision-fallback", PreferenceFactState.Absent)
        ]);

        Assert.True(accepted.HardGatesPassed);
        Assert.Equal(PreferenceEvaluationStatus.Missing, missing.Status);
        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, review.Status);
    }

    [Fact]
    public void Compatibility_groups_require_one_complete_path_without_cross_device_mixing()
    {
        var plan = AudioPlan() with
        {
            CompatibilityGroups = [new PreferenceCompatibilityGroup(
                "device/main",
                [
                    ["audio.format.truehd", "audio.channels.5-1"],
                    ["audio.format.dts", "audio.channels.2-0"]
                ])]
        };

        var accepted = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
            new PreferenceFact("audio.channels.5-1", PreferenceFactState.Present)
        ]);
        Assert.True(accepted.HardGatesPassed);

        var crossDeviceMix = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Absent),
            new PreferenceFact("audio.channels.5-1", PreferenceFactState.Absent),
            new PreferenceFact("audio.channels.2-0", PreferenceFactState.Present)
        ]);
        Assert.Equal(PreferenceEvaluationStatus.Missing, crossDeviceMix.Status);

        var unknown = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Absent),
            new PreferenceFact("audio.channels.2-0", PreferenceFactState.Absent)
        ]);
        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, unknown.Status);
        Assert.False(unknown.HardGatesPassed);
    }

    [Fact]
    public void Compatibility_groups_are_canonical_and_hash_stable_after_restart()
    {
        var plan = AudioPlan() with
        {
            CompatibilityGroups = [new PreferenceCompatibilityGroup(
                "device/main",
                [
                    ["audio.format.truehd", "audio.channels.5-1"],
                    ["audio.format.dts", "audio.channels.2-0"]
                ])]
        };
        var equivalent = plan with
        {
            CompatibilityGroups = [new PreferenceCompatibilityGroup(
                " Device/Main ",
                [
                    ["audio.channels.2-0", "audio.format.dts"],
                    ["audio.channels.5-1", "audio.format.truehd"]
                ])]
        };

        Assert.Equal(plan.PlanHash, equivalent.PlanHash);
        var serialized = ReleasePreferencePlanCodec.Serialize(plan);
        Assert.Equal(serialized, ReleasePreferencePlanCodec.Serialize(equivalent));

        var restored = ReleasePreferencePlanCodec.Deserialize(serialized);
        Assert.Equal(plan.PlanHash, restored.PlanHash);
        Assert.Equal(serialized, ReleasePreferencePlanCodec.Serialize(restored));
    }

    [Fact]
    public void Overlap_implication_resolves_one_effective_level_and_tie_break_cannot_upgrade()
    {
        var plan = AudioPlan() with
        {
            Families = [
                AudioPlan().Families[0] with { TargetLevelId = "truehd-atmos" },
                new PreferenceFamily(
                    "group",
                    "Release group",
                    2,
                    PreferenceIntent.TieBreak,
                    [
                        new PreferenceFamilyLevel("trusted", 0, ["release-group.trusted"]),
                        new PreferenceFamilyLevel("other", 1, ["release-group.unclassified"])
                    ],
                    UpgradeDriving: false)
            ],
            Relationships = [new PreferenceRelationship(
                "audio.format.truehd-atmos",
                "audio.format.truehd",
                PreferenceRelationshipKind.Implies)]
        };

        var current = Facts("audio.format.truehd")
            .Concat([new PreferenceFact("release-group.unclassified", PreferenceFactState.Present)])
            .ToArray();
        var candidate = Facts("audio.format.truehd-atmos")
            .Concat([new PreferenceFact("release-group.trusted", PreferenceFactState.Present)])
            .ToArray();
        var comparison = ReleasePreferenceEvaluator.Compare(plan, current, candidate);

        Assert.Equal(PreferenceCandidateStatus.Upgrade, comparison.Status);
        Assert.Equal("audio", comparison.DecisiveFamilyId);
        Assert.Equal(0, comparison.Candidate.Families.Single(item => item.FamilyId == "audio").SelectedRank);
        Assert.False(comparison.Candidate.Families.Single(item => item.FamilyId == "group").UpgradeDriving);
    }

    [Fact]
    public void Repeated_identical_cycles_reach_a_fixed_point_once_targets_are_met()
    {
        var plan = AudioPlan();
        var held = Facts("audio.format.dts");
        var preferred = Facts("audio.format.truehd");

        var first = ReleasePreferenceEvaluator.Compare(plan, held, preferred);
        var second = ReleasePreferenceEvaluator.Compare(plan, preferred, preferred);
        var third = ReleasePreferenceEvaluator.Compare(plan, preferred, preferred);

        Assert.Equal(PreferenceCandidateStatus.Upgrade, first.Status);
        Assert.True(first.Candidate.TargetsMet);
        Assert.Equal(PreferenceCandidateStatus.Equivalent, second.Status);
        Assert.Equal(JsonSerializer.Serialize(second), JsonSerializer.Serialize(third));
    }

    [Fact]
    public void A_file_at_the_visible_target_is_not_reopened_by_a_higher_above_target_candidate()
    {
        var plan = AudioPlan() with
        {
            Families = [AudioPlan().Families[0] with
            {
                Levels = [
                    new PreferenceFamilyLevel("truehd-atmos", 0, ["audio.format.truehd-atmos"]),
                    new PreferenceFamilyLevel("truehd", 1, ["audio.format.truehd"]),
                    new PreferenceFamilyLevel("dts", 2, ["audio.format.dts"])
                ],
                TargetLevelId = "truehd"
            }]
        };
        var current = Facts("audio.format.truehd");
        var candidate = Facts("audio.format.truehd-atmos");

        var comparison = ReleasePreferenceEvaluator.Compare(plan, current, candidate);

        Assert.Equal(PreferenceCandidateStatus.Equivalent, comparison.Status);
        Assert.True(comparison.Current.TargetsMet);
        Assert.False(comparison.PersistentImprovement);
    }

    [Fact]
    public void A_candidate_reaching_a_target_from_an_empty_installed_family_names_the_improvement()
    {
        var plan = AudioPlan();
        var current = new[]
        {
            new PreferenceFact("audio.format.truehd-atmos", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Absent)
        };
        var candidate = Facts("audio.format.truehd");

        var comparison = ReleasePreferenceEvaluator.Compare(plan, current, candidate);

        Assert.Equal(PreferenceCandidateStatus.Upgrade, comparison.Status);
        Assert.True(comparison.PersistentImprovement);
        Assert.Equal("audio", comparison.DecisiveFamilyId);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("Audio format", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Above_target_improvement_does_not_replace_a_file_without_an_unmet_target_improvement()
    {
        var plan = new ReleasePreferencePlan(
            "proof/multi-family",
            "1",
            "movies",
            [
                new PreferenceFamily(
                    "audio",
                    "Audio format",
                    1,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("truehd-atmos", 0, ["audio.format.truehd-atmos"]),
                        new PreferenceFamilyLevel("truehd", 1, ["audio.format.truehd"]),
                        new PreferenceFamilyLevel("dts", 2, ["audio.format.dts"])
                    ],
                    TargetLevelId: "truehd"),
                new PreferenceFamily(
                    "source",
                    "Source",
                    2,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("web", 0, ["source.webdl"]),
                        new PreferenceFamilyLevel("dvd", 1, ["source.dvd"])
                    ],
                    TargetLevelId: "web")
            ]);
        PreferenceFact[] current = [
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd-atmos", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Absent),
            new PreferenceFact("source.dvd", PreferenceFactState.Present),
            new PreferenceFact("source.webdl", PreferenceFactState.Absent)
        ];
        PreferenceFact[] candidate = [
            new PreferenceFact("audio.format.truehd-atmos", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Absent),
            new PreferenceFact("source.dvd", PreferenceFactState.Present),
            new PreferenceFact("source.webdl", PreferenceFactState.Absent)
        ];

        var comparison = ReleasePreferenceEvaluator.Compare(plan, current, candidate);

        Assert.Equal(PreferenceCandidateStatus.Acceptable, comparison.Status);
        Assert.False(comparison.PersistentImprovement);
        Assert.Equal("audio", comparison.DecisiveFamilyId);
        Assert.Null(comparison.PersistentImprovementFamilyId);
    }

    [Fact]
    public void Lexicographic_above_target_gain_can_coexist_with_a_lower_unmet_target_upgrade()
    {
        var plan = new ReleasePreferencePlan(
            "proof/multi-family-upgrade",
            "1",
            "movies",
            [
                new PreferenceFamily(
                    "audio",
                    "Audio format",
                    1,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("truehd-atmos", 0, ["audio.format.truehd-atmos"]),
                        new PreferenceFamilyLevel("truehd", 1, ["audio.format.truehd"]),
                        new PreferenceFamilyLevel("dts", 2, ["audio.format.dts"])
                    ],
                    TargetLevelId: "truehd"),
                new PreferenceFamily(
                    "source",
                    "Source",
                    2,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("web", 0, ["source.webdl"]),
                        new PreferenceFamilyLevel("dvd", 1, ["source.dvd"])
                    ],
                    TargetLevelId: "web")
            ]);
        PreferenceFact[] current = [
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd-atmos", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Absent),
            new PreferenceFact("source.dvd", PreferenceFactState.Present),
            new PreferenceFact("source.webdl", PreferenceFactState.Absent)
        ];
        PreferenceFact[] candidate = [
            new PreferenceFact("audio.format.truehd-atmos", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Absent),
            new PreferenceFact("source.webdl", PreferenceFactState.Present),
            new PreferenceFact("source.dvd", PreferenceFactState.Absent)
        ];

        var comparison = ReleasePreferenceEvaluator.Compare(plan, current, candidate);

        Assert.Equal(PreferenceCandidateStatus.Upgrade, comparison.Status);
        Assert.True(comparison.PersistentImprovement);
        Assert.Equal("audio", comparison.DecisiveFamilyId);
        Assert.Equal("source", comparison.PersistentImprovementFamilyId);
    }

    private static ReleasePreferencePlan AudioPlan()
        => new(
            "proof/audio",
            "1",
            "movies",
            [new PreferenceFamily(
                "audio",
                "Audio format",
                1,
                PreferenceIntent.Ranked,
                [
                    new PreferenceFamilyLevel("truehd-atmos", 0, ["audio.format.truehd-atmos"]),
                    new PreferenceFamilyLevel("truehd", 1, ["audio.format.truehd"]),
                    new PreferenceFamilyLevel("dts", 2, ["audio.format.dts"])
                ],
                "truehd")],
            Relationships: [
                new PreferenceRelationship("audio.format.truehd-atmos", "audio.format.truehd", PreferenceRelationshipKind.Implies)]);

    private static ReleasePreferencePlan AudioAndHdrPlan(string mediaType)
        => new(
            $"proof/{mediaType}/audio-hdr",
            "1",
            mediaType,
            [
                new PreferenceFamily(
                    "audio",
                    "Audio format",
                    1,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("truehd-atmos", 0, ["audio.format.truehd-atmos"]),
                        new PreferenceFamilyLevel("dtsx", 1, ["audio.format.dtsx"]),
                        new PreferenceFamilyLevel("truehd", 2, ["audio.format.truehd"]),
                        new PreferenceFamilyLevel("dts-hd-ma", 3, ["audio.format.dts-hd-ma"]),
                        new PreferenceFamilyLevel("dts", 4, ["audio.format.dts"])
                    ],
                    TargetLevelId: "truehd"),
                new PreferenceFamily(
                    "hdr",
                    "HDR",
                    2,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("dolby-vision-fallback", 0, ["video.dynamic-range.dolby-vision-fallback"]),
                        new PreferenceFamilyLevel("hdr10", 1, ["video.dynamic-range.hdr10"]),
                        new PreferenceFamilyLevel("sdr", 2, ["video.dynamic-range.sdr"])
                    ],
                    TargetLevelId: "hdr10")
            ],
            CompatibilityGroups: [new PreferenceCompatibilityGroup(
                "device/everywhere",
                [["video.dynamic-range.hdr10"]])],
            Relationships: [
                new PreferenceRelationship("audio.format.truehd-atmos", "audio.format.truehd", PreferenceRelationshipKind.Implies),
                new PreferenceRelationship("audio.format.dtsx", "audio.format.dts-hd-ma", PreferenceRelationshipKind.CoreOf),
                new PreferenceRelationship("video.dynamic-range.dolby-vision-fallback", "video.dynamic-range.hdr10", PreferenceRelationshipKind.CarriedBy)]);

    /// <summary>
    /// The same canonical plan, but with TrueHD and DTS:X occupying one equal
    /// top level, which is how "either is fine" is represented.
    /// </summary>
    private static ReleasePreferencePlan EqualTopLevelAudioPlan(string mediaType)
    {
        var plan = AudioAndHdrPlan(mediaType);
        return plan with
        {
            Id = $"proof/{mediaType}/audio-hdr-equal",
            Families = [
                plan.Families[0] with
                {
                    Levels = [
                        new PreferenceFamilyLevel("truehd-atmos", 0, ["audio.format.truehd-atmos"]),
                        new PreferenceFamilyLevel(
                            "truehd-or-dtsx",
                            1,
                            ["audio.format.truehd", "audio.format.dtsx"]),
                        new PreferenceFamilyLevel("dts-hd-ma", 2, ["audio.format.dts-hd-ma"]),
                        new PreferenceFamilyLevel("dts", 3, ["audio.format.dts"])
                    ],
                    TargetLevelId = "truehd-or-dtsx"
                },
                plan.Families[1]
            ]
        };
    }

    private static IReadOnlyList<PreferenceFact> MatrixFacts(string? audio, string? hdr)
    {
        var facts = new List<PreferenceFact>();
        foreach (var trait in new[]
        {
            "audio.format.truehd-atmos",
            "audio.format.dtsx",
            "audio.format.truehd",
            "audio.format.dts-hd-ma",
            "audio.format.dts"
        })
        {
            if (string.Equals(audio, trait, StringComparison.OrdinalIgnoreCase))
            {
                facts.Add(new PreferenceFact(trait, PreferenceFactState.Present));
            }
            else if (audio is not null
                && !(string.Equals(audio, "audio.format.truehd-atmos", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(trait, "audio.format.truehd", StringComparison.OrdinalIgnoreCase))
                && !(string.Equals(audio, "audio.format.dtsx", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(trait, "audio.format.dts-hd-ma", StringComparison.OrdinalIgnoreCase)))
            {
                facts.Add(new PreferenceFact(trait, PreferenceFactState.Absent));
            }
        }

        foreach (var trait in new[]
        {
            "video.dynamic-range.dolby-vision-fallback",
            "video.dynamic-range.hdr10",
            "video.dynamic-range.sdr"
        })
        {
            if (string.Equals(hdr, trait, StringComparison.OrdinalIgnoreCase))
            {
                facts.Add(new PreferenceFact(trait, PreferenceFactState.Present));
            }
            else if (hdr is not null
                && !(string.Equals(hdr, "video.dynamic-range.dolby-vision-fallback", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(trait, "video.dynamic-range.hdr10", StringComparison.OrdinalIgnoreCase)))
            {
                // A proven Dolby Vision fallback carries HDR10. Asserting
                // HDR10 absent alongside it is contradictory evidence, not a
                // stricter fixture; let the relationship normalizer establish
                // the carried trait exactly as it does for audio.
                facts.Add(new PreferenceFact(trait, PreferenceFactState.Absent));
            }
        }

        if (string.Equals(hdr, "video.dynamic-range.dolby-vision", StringComparison.OrdinalIgnoreCase))
        {
            facts.Add(new PreferenceFact("video.dynamic-range.dolby-vision", PreferenceFactState.Present));
            facts.Add(new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Absent));
        }

        return facts;
    }

    private static IReadOnlyList<PreferenceFact> Facts(string selected)
    {
        var facts = new List<PreferenceFact>
        {
            new(selected, PreferenceFactState.Present)
        };
        foreach (var trait in new[] { "audio.format.truehd-atmos", "audio.format.truehd", "audio.format.dts" })
        {
            if (trait.Equals(selected, StringComparison.OrdinalIgnoreCase)) continue;
            // Do not assert the implied TrueHD capability as absent when the
            // specific Atmos trait is present; the relationship normalizer
            // must be allowed to establish it.
            if (selected.Equals("audio.format.truehd-atmos", StringComparison.OrdinalIgnoreCase)
                && trait.Equals("audio.format.truehd", StringComparison.OrdinalIgnoreCase)) continue;
            facts.Add(new PreferenceFact(trait, PreferenceFactState.Absent));
        }
        return facts;
    }

    private static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> source, int seed)
    {
        var values = source.ToArray();
        var random = new Random(seed);
        for (var index = values.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (values[index], values[other]) = (values[other], values[index]);
        }

        return values;
    }
}
