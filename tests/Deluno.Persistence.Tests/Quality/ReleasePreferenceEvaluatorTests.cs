using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

public sealed class ReleasePreferenceEvaluatorTests
{
    private static ReleasePreferencePlan Plan(
        bool includeTransient = false)
    {
        var families = new List<PreferenceFamily>
        {
                new PreferenceFamily(
                    "quality",
                    "Quality",
                    1,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("bluray-1080", 0, ["quality.bluray-1080p"]),
                        new PreferenceFamilyLevel("web-1080", 1, ["quality.web-1080p"])
                    ],
                    TargetLevelId: "bluray-1080"),
                new PreferenceFamily(
                    "audio",
                    "Audio",
                    2,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("truehd", 0, ["audio.format.truehd"]),
                        new PreferenceFamilyLevel("dts", 1, ["audio.format.dts"])
                    ],
                    TargetLevelId: "truehd")
        };

        if (includeTransient)
        {
            families.Add(new PreferenceFamily(
                "seeders",
                "Seeders",
                99,
                PreferenceIntent.TieBreak,
                [new PreferenceFamilyLevel("many", 1, ["transient.seeders"])],
                TargetLevelId: "many",
                UpgradeDriving: false,
                Transient: true));
        }

        return new ReleasePreferencePlan(
            Id: "movies/default",
            Version: "1",
            MediaType: "movies",
            Families: families);
    }

    [Fact]
    public void Plan_hash_is_deterministic_when_families_are_reordered()
    {
        var first = Plan();
        var second = first with
        {
            Families = [first.Families[1], first.Families[0]]
        };

        Assert.Equal(first.PlanHash, second.PlanHash);
    }

    [Fact]
    public void Truehd_is_better_than_dts_without_a_numeric_total()
    {
        var plan = Plan();
        PreferenceFact[] current =
        [
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent)
        ];
        PreferenceFact[] candidate =
        [
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present)
        ];

        var comparison = ReleasePreferenceEvaluator.Compare(plan, current, candidate);

        Assert.Equal(PreferenceCandidateStatus.Upgrade, comparison.Status);
        Assert.Equal("audio", comparison.DecisiveFamilyId);
        Assert.True(comparison.PersistentImprovement);
    }

    [Fact]
    public void A_hard_gate_dominates_a_better_preference()
    {
        var plan = Plan() with { RequiredTraitIds = ["compatibility.direct-play"] };
        var comparison = ReleasePreferenceEvaluator.Compare(
            plan,
            [new PreferenceFact("compatibility.direct-play", PreferenceFactState.Present)],
            new[]
            {
                new PreferenceFact("compatibility.direct-play", PreferenceFactState.Absent),
                new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Present)
            });

        Assert.Equal(PreferenceCandidateStatus.Rejected, comparison.Status);
    }

    [Fact]
    public void An_installed_file_that_meets_all_targets_stops_equivalent_upgrades()
    {
        var plan = Plan();
        PreferenceFact[] facts =
        [
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present)
        ];

        var comparison = ReleasePreferenceEvaluator.Compare(plan, facts, facts);

        Assert.Equal(PreferenceCandidateStatus.Equivalent, comparison.Status);
        Assert.True(comparison.Equivalent);
        Assert.False(comparison.PersistentImprovement);
    }

    [Fact]
    public void A_file_that_fails_a_hard_gate_is_not_treated_as_settled_when_targets_are_met()
    {
        var plan = Plan() with { ForbiddenTraitIds = ["unwanted.cam"] };
        var current = new[]
        {
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
            new PreferenceFact("unwanted.cam", PreferenceFactState.Present)
        };
        var candidate = new[]
        {
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
            new PreferenceFact("unwanted.cam", PreferenceFactState.Absent)
        };

        var comparison = ReleasePreferenceEvaluator.Compare(plan, current, candidate);

        Assert.Equal(PreferenceEvaluationStatus.Missing, comparison.Current.Status);
        Assert.Equal(PreferenceEvaluationStatus.MeetsPlan, comparison.Candidate.Status);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, comparison.Status);
        Assert.False(comparison.PersistentImprovement);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("hard gates", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_facts_need_review_instead_of_becoming_absent()
    {
        var evaluation = ReleasePreferenceEvaluator.Evaluate(
            Plan(),
            [new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present)]);

        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, evaluation.Status);
    }

    [Fact]
    public void Proven_higher_priority_target_improvement_is_not_blocked_by_unknown_lower_priority_facts()
    {
        var plan = Plan();
        var comparison = ReleasePreferenceEvaluator.Compare(
            plan,
            [
                new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
                new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent)
            ],
            [
                new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
                new PreferenceFact("quality.web-1080p", PreferenceFactState.Absent)
            ]);

        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, comparison.Current.Status);
        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, comparison.Candidate.Status);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, comparison.Status);
        Assert.True(comparison.PersistentImprovement);
        Assert.Equal("quality", comparison.PersistentImprovementFamilyId);
        Assert.Equal("quality", comparison.DecisiveFamilyId);
    }

    [Fact]
    public void Unknown_higher_priority_facts_still_hold_automatic_replacement()
    {
        var plan = Plan();
        var comparison = ReleasePreferenceEvaluator.Compare(
            plan,
            [
                new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent)
            ],
            [
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
                new PreferenceFact("audio.format.dts", PreferenceFactState.Absent)
            ]);

        Assert.Equal(PreferenceCandidateStatus.NeedsReview, comparison.Status);
        Assert.Equal("quality", comparison.DecisiveFamilyId);
        Assert.False(comparison.PersistentImprovement);
    }

    [Fact]
    public void Identical_unknown_vectors_are_equivalent_and_do_not_create_replacement_work()
    {
        var plan = Plan();
        PreferenceFact[] facts =
        [
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Absent)
        ];

        var comparison = ReleasePreferenceEvaluator.Compare(plan, facts, facts);

        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, comparison.Current.Status);
        Assert.Equal(PreferenceCandidateStatus.Equivalent, comparison.Status);
        Assert.True(comparison.Equivalent);
        Assert.False(comparison.PersistentImprovement);
    }

    [Fact]
    public void Persistent_regressions_are_rejected_even_when_audio_improves()
    {
        var plan = Plan();
        var comparison = ReleasePreferenceEvaluator.Compare(
            plan,
            new[]
            {
                new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
                new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent)
            },
            new[]
            {
                new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
                new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Present)
            });

        Assert.Equal(PreferenceCandidateStatus.Rejected, comparison.Status);
        Assert.True(comparison.Regressed);
    }

    [Fact]
    public void Lexicographic_comparison_is_transitive_across_multiple_releases()
    {
        var plan = Plan();
        PreferenceFact[] webDts =
        [
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent)
        ];
        PreferenceFact[] blurayDts =
        [
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent)
        ];
        PreferenceFact[] blurayTruehd =
        [
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Present),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Absent)
        ];

        Assert.Equal(PreferenceCandidateStatus.Upgrade, ReleasePreferenceEvaluator.Compare(plan, webDts, blurayDts).Status);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, ReleasePreferenceEvaluator.Compare(plan, blurayDts, blurayTruehd).Status);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, ReleasePreferenceEvaluator.Compare(plan, webDts, blurayTruehd).Status);
        Assert.Equal(PreferenceCandidateStatus.Rejected, ReleasePreferenceEvaluator.Compare(plan, blurayTruehd, webDts).Status);
    }

    [Fact]
    public void Tie_break_only_changes_do_not_create_a_persistent_upgrade()
    {
        var plan = Plan() with
        {
            Families =
            [
                Plan().Families[0],
                Plan().Families[1],
                new PreferenceFamily(
                    "release-group",
                    "Release group",
                    3,
                    PreferenceIntent.TieBreak,
                    [
                        new PreferenceFamilyLevel("trusted", 0, ["release-group.trusted"]),
                        new PreferenceFamilyLevel("unclassified", 1, ["release-group.unclassified"])
                    ],
                    UpgradeDriving: false)
            ]
        };

        var current =
            new[]
            {
                new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
                new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
                new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
                new PreferenceFact("release-group.unclassified", PreferenceFactState.Present),
                new PreferenceFact("release-group.trusted", PreferenceFactState.Absent)
            };
        var candidate =
            new[]
            {
                new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
                new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
                new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
                new PreferenceFact("release-group.trusted", PreferenceFactState.Present),
                new PreferenceFact("release-group.unclassified", PreferenceFactState.Absent)
            };

        var comparison = ReleasePreferenceEvaluator.Compare(plan, current, candidate);

        Assert.Equal(PreferenceCandidateStatus.Acceptable, comparison.Status);
        Assert.False(comparison.PersistentImprovement);
        Assert.Null(comparison.DecisiveFamilyId);
    }

    [Fact]
    public void Transient_facts_do_not_create_a_persistent_upgrade()
    {
        var plan = Plan(includeTransient: true);
        PreferenceFact[] current =
        [
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent)
        ];
        PreferenceFact[] candidate =
        [
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("transient.seeders", PreferenceFactState.Present)
        ];

        var comparison = ReleasePreferenceEvaluator.Compare(plan, current, candidate);

        Assert.Equal(PreferenceCandidateStatus.Acceptable, comparison.Status);
        Assert.False(comparison.PersistentImprovement);
    }

    [Fact]
    public void Transient_families_choose_between_equivalent_candidates_after_persistent_families()
    {
        var plan = Plan(includeTransient: true);
        var current = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("transient.seeders", PreferenceFactState.Absent)
        ]);
        var candidate = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("transient.seeders", PreferenceFactState.Present)
        ]);

        var candidateFirst = ReleasePreferenceEvaluator.CompareForSelection(plan, candidate, current);
        var currentFirst = ReleasePreferenceEvaluator.CompareForSelection(plan, current, candidate);
        var candidateTransient = candidate.Families.Single(item => item.FamilyId == "seeders");
        var currentTransient = current.Families.Single(item => item.FamilyId == "seeders");
        Assert.Equal(PreferenceFactState.Present, candidateTransient.State);
        Assert.Equal(PreferenceFactState.Absent, currentTransient.State);
        Assert.True(candidateFirst < 0);
        Assert.True(currentFirst > 0, $"currentFirst={currentFirst}");
    }

    [Fact]
    public void Neutral_families_are_explanatory_and_cannot_choose_a_search_winner()
    {
        var plan = Plan() with
        {
            Families =
            [
                Plan().Families[0],
                Plan().Families[1],
                new PreferenceFamily(
                    "edition",
                    "Edition",
                    3,
                    PreferenceIntent.Neutral,
                    [
                        new PreferenceFamilyLevel("preferred", 0, ["edition.imax"]),
                        new PreferenceFamilyLevel("other", 1, ["edition.extended"])
                    ],
                    UpgradeDriving: false)
            ]
        };
        var preferredObservation = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("edition.imax", PreferenceFactState.Present),
            new PreferenceFact("edition.extended", PreferenceFactState.Absent)
        ]);
        var otherObservation = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("edition.imax", PreferenceFactState.Absent),
            new PreferenceFact("edition.extended", PreferenceFactState.Present)
        ]);

        Assert.Equal(0, ReleasePreferenceEvaluator.CompareForSelection(plan, preferredObservation, otherObservation));
    }

    [Fact]
    public void A_transient_advantage_cannot_displace_a_persistent_preference()
    {
        var plan = Plan(includeTransient: true);
        var worseQuality = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("transient.seeders", PreferenceFactState.Present)
        ]);
        var betterQuality = ReleasePreferenceEvaluator.Evaluate(plan, [
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Absent),
            new PreferenceFact("audio.format.dts", PreferenceFactState.Present),
            new PreferenceFact("audio.format.truehd", PreferenceFactState.Absent),
            new PreferenceFact("transient.seeders", PreferenceFactState.Absent)
        ]);

        Assert.True(ReleasePreferenceEvaluator.CompareForSelection(plan, betterQuality, worseQuality) < 0);
    }

    [Fact]
    public void Implication_normalizes_a_specific_trait_without_double_counting()
    {
        var plan = Plan() with
        {
            Families =
            [
                new PreferenceFamily(
                    "quality",
                    "Quality",
                    1,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("bluray-1080", 0, ["quality.bluray-1080p"]),
                        new PreferenceFamilyLevel("web-1080", 1, ["quality.web-1080p"])
                    ],
                    TargetLevelId: "bluray-1080"),
                new PreferenceFamily(
                    "audio",
                    "Audio",
                    2,
                    PreferenceIntent.Ranked,
                    [
                        new PreferenceFamilyLevel("truehd-atmos", 0, ["audio.format.truehd-atmos"]),
                        new PreferenceFamilyLevel("truehd", 1, ["audio.format.truehd"]),
                        new PreferenceFamilyLevel("dts", 2, ["audio.format.dts"])
                    ],
                    TargetLevelId: "truehd")
            ],
            Relationships = [new PreferenceRelationship("audio.format.truehd-atmos", "audio.format.truehd", PreferenceRelationshipKind.Implies)]
        };

        var evaluation = ReleasePreferenceEvaluator.Evaluate(
            plan,
            [
                new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Present),
                new PreferenceFact("audio.format.truehd-atmos", PreferenceFactState.Present)
            ]);

        var audio = Assert.Single(evaluation.Families, family => family.FamilyId == "audio");
        Assert.Equal("truehd-atmos", audio.SelectedLevelId);
        Assert.Equal(0, audio.SelectedRank);
        Assert.True(audio.TargetMet);
        Assert.Equal(PreferenceEvaluationStatus.MeetsPlan, evaluation.Status);
    }

    [Fact]
    public void Open_world_unknown_for_a_better_level_requires_review()
    {
        var plan = Plan();
        var evaluation = ReleasePreferenceEvaluator.Evaluate(
            plan,
            [
                new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Unknown),
                new PreferenceFact("quality.web-1080p", PreferenceFactState.Present),
                new PreferenceFact("audio.format.truehd", PreferenceFactState.Present)
            ]);

        Assert.Equal(PreferenceEvaluationStatus.NeedsReview, evaluation.Status);
        Assert.Equal(PreferenceFactState.Unknown, evaluation.Families.Single(family => family.FamilyId == "quality").State);
    }

    [Fact]
    public void Validator_rejects_a_ranked_upgrade_family_without_stop_when_target()
    {
        var invalid = Plan() with
        {
            Families =
            [
                Plan().Families[0] with { TargetLevelId = null },
                Plan().Families[1]
            ]
        };

        var errors = ReleasePreferencePlanValidator.Validate(invalid);

        Assert.Contains(errors, error => error.Contains("stop-when target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_reports_malformed_collections_without_throwing()
    {
        var nullFamilies = Plan() with { Families = null! };
        var nullLevels = Plan() with { Families = [Plan().Families[0] with { Levels = null! }, Plan().Families[1]] };

        var familyErrors = ReleasePreferencePlanValidator.Validate(nullFamilies);
        var levelErrors = ReleasePreferencePlanValidator.Validate(nullLevels);

        Assert.Contains(familyErrors, error => error.Contains("families are required", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(levelErrors, error => error.Contains("at least one level", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_duplicate_and_self_relationships()
    {
        var relationship = new PreferenceRelationship(
            "audio.format.truehd-atmos",
            "audio.format.truehd",
            PreferenceRelationshipKind.Implies);
        var invalid = Plan() with
        {
            Relationships =
            [
                relationship,
                relationship,
                new PreferenceRelationship("audio.format.truehd", "audio.format.truehd", PreferenceRelationshipKind.Implies)
            ]
        };

        var errors = ReleasePreferencePlanValidator.Validate(invalid);

        Assert.Contains(errors, error => error.Contains("more than once", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("same trait", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_hash_ignores_duplicate_gate_values_because_they_are_set_semantics()
    {
        var first = Plan() with { RequiredTraitIds = ["compatibility.direct-play"] };
        var second = Plan() with { RequiredTraitIds = ["compatibility.direct-play", "compatibility.direct-play"] };

        Assert.Equal(first.PlanHash, second.PlanHash);
    }

    [Fact]
    public void Hash_includes_scope_and_provenance_but_not_input_order()
    {
        var first = Plan() with
        {
            CompatibilityScope = "all-devices",
            Scenario = "family-1080p",
            Provenance = "deluno-defaults-v1",
            DimensionOrder = ["quality", "audio"]
        };
        var second = first with { DimensionOrder = ["quality", "audio"] };

        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.NotEqual(first.PlanHash, (first with { Scenario = "premium-4k" }).PlanHash);
    }
}
