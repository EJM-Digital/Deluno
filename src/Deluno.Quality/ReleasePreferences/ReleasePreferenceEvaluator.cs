namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Evaluates typed facts and compares two releases lexicographically. It never
/// adds dimensions together: a candidate that improves audio cannot buy its
/// way past a video compatibility regression.
/// </summary>
public static class ReleasePreferenceEvaluator
{
    public static PreferenceEvaluation Evaluate(
        ReleasePreferencePlan plan,
        IEnumerable<PreferenceFact>? facts)
    {
        ReleasePreferencePlanValidator.ThrowIfInvalid(plan);
        var factMap = NormalizeFacts(plan, (facts ?? [])
            .Where(fact => !string.IsNullOrWhiteSpace(fact.TraitId))
            .GroupBy(fact => fact.NormalizedTraitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, ResolveFact, StringComparer.OrdinalIgnoreCase));

        var reasons = new List<string>();
        var compatibilityEvaluations = new List<PreferenceCompatibilityEvaluation>();
        var hardGateStatus = EvaluateHardGates(plan, factMap, reasons, compatibilityEvaluations);
        // Keep one typed result for every family, including transient
        // tie-break families. They are intentionally excluded from target
        // status and installed-file upgrade comparison below, but missing
        // them here would make same-search selection unable to use the final
        // deterministic tie-break stage or explain why it won.
        var familyEvaluations = plan.OrderedFamilies
            .Select(family => EvaluateFamily(family, factMap, reasons))
            .ToArray();

        var targets = familyEvaluations
            .Where(item => item.UpgradeDriving && item.Intent == PreferenceIntent.Ranked)
            .ToArray();
        var targetsMet = targets.All(item => item.TargetMet && item.State == PreferenceFactState.Present);
        var decisionFamilies = familyEvaluations
            .Where(item => item.UpgradeDriving && item.Intent == PreferenceIntent.Ranked)
            .ToArray();
        var status = hardGateStatus switch
        {
            PreferenceEvaluationStatus.Missing => PreferenceEvaluationStatus.Missing,
            PreferenceEvaluationStatus.NeedsReview => PreferenceEvaluationStatus.NeedsReview,
            _ when decisionFamilies.Any(item => item.State == PreferenceFactState.Conflicting) => PreferenceEvaluationStatus.NeedsReview,
            _ when decisionFamilies.Any(item => item.State == PreferenceFactState.Unknown) => PreferenceEvaluationStatus.NeedsReview,
            _ when targetsMet => PreferenceEvaluationStatus.MeetsPlan,
            _ => PreferenceEvaluationStatus.BelowGoal
        };

        if (targets.Length == 0)
            reasons.Add("The plan has no upgrade-driving target families.");
        else if (targetsMet)
            reasons.Add("All upgrade-driving targets are met.");

        return new PreferenceEvaluation(
            plan.Id,
            plan.Version,
            plan.PlanHash,
            status,
            hardGateStatus is PreferenceEvaluationStatus.MeetsPlan or PreferenceEvaluationStatus.BelowGoal,
            targetsMet,
            familyEvaluations,
            reasons.Distinct(StringComparer.Ordinal).ToArray(),
            compatibilityEvaluations);
    }

    /// <summary>
    /// Orders candidates available in one search. This is separate from
    /// <see cref="Compare"/> because no installed file exists yet. It uses
    /// persistent families first and tie-break families second; no family
    /// values are added together.
    /// </summary>
    public static int CompareForSelection(
        ReleasePreferencePlan plan,
        PreferenceEvaluation left,
        PreferenceEvaluation right)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ReleasePreferencePlanValidator.ThrowIfInvalid(plan);
        EnsureEvaluationMatchesPlan(plan, left, nameof(left));
        EnsureEvaluationMatchesPlan(plan, right, nameof(right));

        var leftGate = left.HardGatesPassed ? 0 : 1;
        var rightGate = right.HardGatesPassed ? 0 : 1;
        if (leftGate != rightGate) return leftGate.CompareTo(rightGate);

        // A primary-device goal is a persistent owner choice. Once both
        // candidates pass the compatibility gate, prefer the candidate that
        // satisfies the primary path over one that only satisfies fallback.
        // Unordered compatibility groups do not participate here.
        if (CompareCompatibilityRanks(plan, left, right) is { } compatibilityComparison)
        {
            return compatibilityComparison;
        }

        var leftFamilies = left.Families.ToDictionary(item => item.FamilyId, StringComparer.OrdinalIgnoreCase);
        var rightFamilies = right.Families.ToDictionary(item => item.FamilyId, StringComparer.OrdinalIgnoreCase);
        // Persistent families decide the release class first. Transient
        // signals (seeders, age, current availability) are deterministic
        // tie-breakers only; they must never displace a better persistent
        // quality/format result, but they should still choose between otherwise
        // equivalent candidates in the same search.
        // Only ranked families are part of the persistent preference vector.
        // Tie-break families may choose between otherwise equivalent releases,
        // while neutral observations are explanatory only. Required and
        // forbidden traits have already been handled as gates above.
        foreach (var family in plan.OrderedFamilies.Where(item =>
                     !item.Transient
                     && item.Intent == PreferenceIntent.Ranked
                     && item.UpgradeDriving))
        {
            if (CompareFamilyRanks(leftFamilies, rightFamilies, family) is { } comparison)
            {
                return comparison;
            }
        }

        foreach (var family in plan.OrderedFamilies.Where(item =>
                     !item.Transient
                     && item.Intent == PreferenceIntent.TieBreak))
        {
            if (CompareFamilyRanks(leftFamilies, rightFamilies, family) is { } comparison)
            {
                return comparison;
            }
        }

        foreach (var family in plan.OrderedFamilies.Where(item =>
                     item.Transient
                     && item.Intent == PreferenceIntent.TieBreak))
        {
            if (CompareFamilyRanks(leftFamilies, rightFamilies, family) is { } comparison)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int? CompareFamilyRanks(
        IReadOnlyDictionary<string, PreferenceFamilyEvaluation> leftFamilies,
        IReadOnlyDictionary<string, PreferenceFamilyEvaluation> rightFamilies,
        PreferenceFamily family)
    {
        if (!leftFamilies.TryGetValue(family.Id, out var leftFamily)
            || !rightFamilies.TryGetValue(family.Id, out var rightFamily))
        {
            return null;
        }

        var leftRank = EffectiveRank(leftFamily);
        var rightRank = EffectiveRank(rightFamily);
        return leftRank == rightRank ? null : leftRank.CompareTo(rightRank);
    }

    private static int? CompareCompatibilityRanks(
        ReleasePreferencePlan plan,
        PreferenceEvaluation left,
        PreferenceEvaluation right)
    {
        foreach (var group in (plan.CompatibilityGroups ?? [])
                     .Where(group => group.AlternativeRanks is { Count: > 0 })
                     .OrderBy(group => group.Id, StringComparer.Ordinal))
        {
            var leftEvaluation = left.Compatibility
                .FirstOrDefault(item => string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase));
            var rightEvaluation = right.Compatibility
                .FirstOrDefault(item => string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase));
            if (leftEvaluation?.SelectedAlternativeRank is not { } leftRank
                || rightEvaluation?.SelectedAlternativeRank is not { } rightRank
                || leftRank == rightRank)
            {
                continue;
            }

            return leftRank.CompareTo(rightRank);
        }

        return null;
    }

    private static int EffectiveRank(PreferenceFamilyEvaluation family)
        => family.State switch
        {
            PreferenceFactState.Present => family.SelectedRank < 0 ? int.MaxValue - 1 : family.SelectedRank,
            PreferenceFactState.Absent => int.MaxValue - 1,
            PreferenceFactState.Unknown => int.MaxValue,
            PreferenceFactState.Conflicting => int.MaxValue,
            _ => int.MaxValue
        };

    private static void EnsureEvaluationMatchesPlan(
        ReleasePreferencePlan plan,
        PreferenceEvaluation evaluation,
        string parameterName)
    {
        if (string.Equals(evaluation.PlanId, plan.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(evaluation.PlanVersion, plan.Version, StringComparison.Ordinal)
            && string.Equals(evaluation.PlanHash, plan.PlanHash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ArgumentException(
            $"The {parameterName} evaluation was produced by a different release-preference plan. Re-evaluate it before comparing candidates.",
            parameterName);
    }

    public static PreferenceComparison Compare(
        ReleasePreferencePlan plan,
        IEnumerable<PreferenceFact>? currentFacts,
        IEnumerable<PreferenceFact>? candidateFacts)
    {
        var current = Evaluate(plan, currentFacts);
        var candidate = Evaluate(plan, candidateFacts);
        var reasons = new List<string>();

        if (!candidate.HardGatesPassed)
        {
            reasons.Add("Candidate failed a hard safety or compatibility gate.");
            return Result(PreferenceCandidateStatus.Rejected, false, false, false, null, reasons, current, candidate);
        }

        // Identity is an equivalence even when both files are still below a
        // target. Without this boundary, an identical candidate was labelled
        // merely Acceptable and could be mistaken for useful replacement work
        // by a caller that only inspected the candidate status. Compare the
        // effective typed outcome, not raw release names or legacy scores: two
        // different releases with the same proven plan state are also
        // equivalent for installed-file replacement.
        if (EquivalentSatisfaction(current, candidate))
        {
            reasons.Add("The installed file and candidate have equivalent proven satisfaction under this plan.");
            return Result(PreferenceCandidateStatus.Equivalent, false, false, true, null, reasons, current, candidate);
        }

        var currentByFamily = current.Families.ToDictionary(item => item.FamilyId, StringComparer.OrdinalIgnoreCase);
        var candidateByFamily = candidate.Families.ToDictionary(item => item.FamilyId, StringComparer.OrdinalIgnoreCase);
        string? decisive = null;
        var persistentImprovement = false;
        string? persistentImprovementFamilyId = null;
        var firstDifference = 0;

        foreach (var family in plan.OrderedFamilies.Where(item => !item.Transient && item.UpgradeDriving && item.Intent == PreferenceIntent.Ranked))
        {
            if (!currentByFamily.TryGetValue(family.Id, out var currentFamily) || !candidateByFamily.TryGetValue(family.Id, out var candidateFamily))
                continue;

            if (candidateFamily.State is PreferenceFactState.Unknown or PreferenceFactState.Conflicting
                || currentFamily.State is PreferenceFactState.Unknown or PreferenceFactState.Conflicting)
            {
                // Ranked families are lexicographic. Once a higher-priority
                // family has proven a persistent improvement below its target,
                // incomplete evidence in a lower-priority family cannot erase
                // that proof. This matters for ordinary quality upgrades: a
                // known 1080p -> 2160p improvement must not wait forever merely
                // because the release name does not prove a lower-priority HDR
                // or revision trait. Unknown evidence before any decisive
                // improvement remains a review boundary.
                if (firstDifference < 0 && persistentImprovement)
                {
                    continue;
                }

                var subject = candidateFamily.State is PreferenceFactState.Unknown or PreferenceFactState.Conflicting
                    ? "candidate"
                    : "installed file";
                return Result(PreferenceCandidateStatus.NeedsReview, false, false, false, family.Id,
                    [$"Family '{family.Dimension}' is unknown or conflicting for the {subject}."], current, candidate);
            }

            // An absent family is represented by -1 in the public
            // evaluation, but it is worse than every known level for
            // comparison purposes. Use the same effective ordering as search
            // selection so a candidate that reaches a target from an empty
            // installed family has a named persistent improvement.
            var currentRank = EffectiveRank(currentFamily);
            var candidateRank = EffectiveRank(candidateFamily);
            if (candidateRank < currentRank)
            {
                if (firstDifference == 0)
                {
                    firstDifference = -1;
                    decisive = family.Id;
                }

                // Improving a family that is already at its explicit target
                // is useful for same-search ordering, but it is not a reason
                // to replace an installed file. Keep walking after a better
                // above-target family so a lower-priority unmet target can
                // still provide the required persistent improvement evidence.
                if (!currentFamily.TargetMet && persistentImprovementFamilyId is null)
                {
                    persistentImprovement = true;
                    persistentImprovementFamilyId = family.Id;
                    reasons.Add($"Candidate improves the '{family.Dimension}' preference family below its target.");
                }
                continue;
            }
            if (candidateRank > currentRank)
            {
                if (firstDifference == 0)
                {
                    firstDifference = 1;
                    decisive = family.Id;
                }
            }
        }

        if (firstDifference > 0)
        {
            reasons.Add("Candidate is worse in the first differing preference family.");
            return Result(
                PreferenceCandidateStatus.Rejected,
                false,
                true,
                false,
                decisive,
                reasons,
                current,
                candidate,
                persistentImprovementFamilyId);
        }

        // Strict stopping: once every persistent target is met and the
        // installed file passes the plan's hard gates, a higher transient
        // signal or a merely different equivalent release cannot reopen the
        // title. A file that violates a required/forbidden gate still needs
        // remediation even when its quality/format families happen to be at
        // their targets.
        if (current.HardGatesPassed && current.TargetsMet)
        {
            reasons.Add("Installed file already meets every persistent target; automatic upgrades stop here.");
            return Result(PreferenceCandidateStatus.Equivalent, false, false, true, decisive, reasons, current, candidate, persistentImprovementFamilyId);
        }

        if (!current.HardGatesPassed)
        {
            reasons.Add("Installed file fails one or more hard gates; the candidate repairs the plan violation.");
            return Result(PreferenceCandidateStatus.Upgrade, false, false, false, decisive, reasons, current, candidate, persistentImprovementFamilyId);
        }

        if (persistentImprovement && !current.TargetsMet)
        {
            reasons.Add($"Candidate persistently improves '{persistentImprovementFamilyId}'.");
            return Result(PreferenceCandidateStatus.Upgrade, true, false, false, decisive, reasons, current, candidate, persistentImprovementFamilyId);
        }

        if (candidate.TargetsMet)
        {
            reasons.Add("Candidate reaches the remaining persistent targets.");
            return Result(PreferenceCandidateStatus.Upgrade, true, false, false, decisive, reasons, current, candidate, persistentImprovementFamilyId);
        }

        reasons.Add("Candidate is acceptable but does not persistently improve the installed file.");
        return Result(PreferenceCandidateStatus.Acceptable, false, false, false, decisive, reasons, current, candidate, persistentImprovementFamilyId);
    }

    private static PreferenceComparison Result(
        PreferenceCandidateStatus status,
        bool improvement,
        bool regressed,
        bool equivalent,
        string? decisive,
        IReadOnlyList<string> reasons,
        PreferenceEvaluation current,
        PreferenceEvaluation candidate,
        string? persistentImprovementFamilyId = null)
        => new(
            candidate.PlanId,
            candidate.PlanVersion,
            candidate.PlanHash,
            status,
            improvement,
            regressed,
            equivalent,
            decisive,
            reasons,
            current,
            candidate,
            persistentImprovementFamilyId);

    private static bool EquivalentSatisfaction(
        PreferenceEvaluation current,
        PreferenceEvaluation candidate)
    {
        if (current.PlanId != candidate.PlanId
            || !string.Equals(current.PlanVersion, candidate.PlanVersion, StringComparison.Ordinal)
            || !string.Equals(current.PlanHash, candidate.PlanHash, StringComparison.OrdinalIgnoreCase)
            || current.HardGatesPassed != candidate.HardGatesPassed
            || current.TargetsMet != candidate.TargetsMet)
        {
            return false;
        }

        var currentFamilies = current.Families.ToDictionary(item => item.FamilyId, StringComparer.OrdinalIgnoreCase);
        var candidateFamilies = candidate.Families.ToDictionary(item => item.FamilyId, StringComparer.OrdinalIgnoreCase);
        if (currentFamilies.Count != candidateFamilies.Count)
        {
            return false;
        }

        foreach (var (familyId, left) in currentFamilies)
        {
            if (!candidateFamilies.TryGetValue(familyId, out var right)
                || left.Intent != right.Intent
                || left.State != right.State
                || left.SelectedLevelId != right.SelectedLevelId
                || left.SelectedRank != right.SelectedRank
                || left.TargetLevelId != right.TargetLevelId
                || left.TargetMet != right.TargetMet
                || left.UpgradeDriving != right.UpgradeDriving
                || left.Transient != right.Transient)
            {
                return false;
            }
        }

        var currentCompatibility = current.Compatibility
            .OrderBy(item => item.GroupId, StringComparer.Ordinal)
            .ThenBy(item => item.State)
            .ThenBy(item => item.SelectedAlternativeRank)
            .ToArray();
        var candidateCompatibility = candidate.Compatibility
            .OrderBy(item => item.GroupId, StringComparer.Ordinal)
            .ThenBy(item => item.State)
            .ThenBy(item => item.SelectedAlternativeRank)
            .ToArray();
        return currentCompatibility.SequenceEqual(candidateCompatibility);
    }

    private static PreferenceEvaluationStatus EvaluateHardGates(
        ReleasePreferencePlan plan,
        IReadOnlyDictionary<string, PreferenceFact> facts,
        ICollection<string> reasons,
        ICollection<PreferenceCompatibilityEvaluation> compatibilityEvaluations)
    {
        var needsReview = false;
        foreach (var traitId in plan.RequiredTraitIds ?? [])
        {
            var state = StateOf(facts, traitId);
            if (state == PreferenceFactState.Present) continue;
            if (state is PreferenceFactState.Unknown or PreferenceFactState.Conflicting)
            {
                needsReview = true;
                reasons.Add($"Required trait '{traitId}' is not known to be present.");
            }
            else
            {
                reasons.Add($"Required trait '{traitId}' is absent.");
                return PreferenceEvaluationStatus.Missing;
            }
        }

        foreach (var group in plan.RequiredAnyTraitGroups ?? [])
        {
            var traits = (group ?? [])
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var states = traits.Select(trait => StateOf(facts, trait)).ToArray();
            if (states.Contains(PreferenceFactState.Present))
            {
                continue;
            }

            if (states.Contains(PreferenceFactState.Conflicting))
            {
                needsReview = true;
                reasons.Add($"At least one trait in required compatibility group '{string.Join(", ", traits)}' must be proven, but evidence conflicts.");
            }
            else if (states.Contains(PreferenceFactState.Unknown))
            {
                needsReview = true;
                reasons.Add($"At least one trait in required compatibility group '{string.Join(", ", traits)}' must be proven, but evidence is incomplete.");
            }
            else
            {
                reasons.Add($"No trait in required compatibility group '{string.Join(", ", traits)}' is present.");
                return PreferenceEvaluationStatus.Missing;
            }
        }

        var compatibilityStatus = EvaluateCompatibilityGroups(plan, facts, reasons, compatibilityEvaluations);
        if (compatibilityStatus == PreferenceEvaluationStatus.Missing)
        {
            return PreferenceEvaluationStatus.Missing;
        }

        if (compatibilityStatus == PreferenceEvaluationStatus.NeedsReview)
        {
            needsReview = true;
        }

        foreach (var traitId in plan.ForbiddenTraitIds ?? [])
        {
            var state = StateOf(facts, traitId);
            if (state == PreferenceFactState.Absent) continue;
            if (state == PreferenceFactState.Present)
            {
                reasons.Add($"Forbidden trait '{traitId}' is present.");
                return PreferenceEvaluationStatus.Missing;
            }

            needsReview = true;
            reasons.Add($"Forbidden trait '{traitId}' is unknown.");
        }

        return needsReview ? PreferenceEvaluationStatus.NeedsReview : PreferenceEvaluationStatus.MeetsPlan;
    }

    /// <summary>
    /// Evaluates the typed AND-of-OR-of-AND compatibility shape used by
    /// playback groups. It is intentionally separate from RequiredAnyTraitGroups:
    /// the latter can satisfy one independent gate at a time, while a
    /// compatibility alternative represents one complete device path.
    /// </summary>
    private static PreferenceEvaluationStatus EvaluateCompatibilityGroups(
        ReleasePreferencePlan plan,
        IReadOnlyDictionary<string, PreferenceFact> facts,
        ICollection<string> reasons,
        ICollection<PreferenceCompatibilityEvaluation> evaluations)
    {
        var needsReview = false;
        foreach (var group in plan.CompatibilityGroups ?? [])
        {
            var alternatives = (group.Alternatives ?? [])
                .Select((alternative, index) => new
                {
                    Alternative = alternative
                        .Where(trait => !string.IsNullOrWhiteSpace(trait))
                        .Select(Normalize)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    Index = index,
                    Rank = group.AlternativeRanks is { Count: > 0 } ranks && index < ranks.Count
                        ? (int?)ranks[index]
                        : null
                })
                .Where(item => item.Alternative.Length > 0)
                .ToArray();

            var selected = alternatives
                .Where(item => item.Alternative.All(trait =>
                    StateOf(facts, trait) == PreferenceFactState.Present))
                .OrderBy(item => item.Rank ?? int.MaxValue)
                .ThenBy(item => item.Index)
                .FirstOrDefault();
            if (selected is not null)
            {
                evaluations.Add(new PreferenceCompatibilityEvaluation(
                    group.Id,
                    PreferenceFactState.Present,
                    selected.Rank,
                    selected.Rank is { } rank
                        ? $"Matched compatibility path {rank.ToString(System.Globalization.CultureInfo.InvariantCulture)} in '{group.Id}'."
                        : $"Matched a compatibility path in '{group.Id}'."));
                continue;
            }

            // An alternative containing no absent facts could still pass once
            // a probe supplies the unknown/conflicting evidence. It must not be
            // treated as a rejection or as an automatic approval.
            var possible = alternatives.Any(item =>
            {
                var states = item.Alternative.Select(trait => StateOf(facts, trait)).ToArray();
                return !states.Contains(PreferenceFactState.Absent);
            });

            if (possible)
            {
                needsReview = true;
                evaluations.Add(new PreferenceCompatibilityEvaluation(
                    group.Id,
                    PreferenceFactState.Unknown,
                    null,
                    $"No proven compatibility path exists in '{group.Id}'; capability evidence needs review."));
                reasons.Add($"Compatibility group '{group.Id}' has no proven device path; one or more capability facts need review.");
            }
            else
            {
                evaluations.Add(new PreferenceCompatibilityEvaluation(
                    group.Id,
                    PreferenceFactState.Absent,
                    null,
                    $"No supported compatibility path exists in '{group.Id}'."));
                reasons.Add($"Compatibility group '{group.Id}' has no supported device path in this release.");
                return PreferenceEvaluationStatus.Missing;
            }
        }

        return needsReview ? PreferenceEvaluationStatus.NeedsReview : PreferenceEvaluationStatus.MeetsPlan;
    }

    private static PreferenceFamilyEvaluation EvaluateFamily(
        PreferenceFamily family,
        IReadOnlyDictionary<string, PreferenceFact> facts,
        ICollection<string> reasons)
    {
        var selected = family.OrderedLevels
            .Where(level => level.NormalizedTraitIds.Any(traitId => StateOf(facts, traitId) == PreferenceFactState.Present))
            .OrderBy(level => level.Rank)
            .ThenBy(level => level.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        var relevantStates = family.OrderedLevels
            .SelectMany(level => level.NormalizedTraitIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(traitId => StateOf(facts, traitId))
            .ToArray();
        var betterUnknown = selected is not null && family.OrderedLevels
            .Where(level => level.Rank < selected.Rank)
            .SelectMany(level => level.NormalizedTraitIds)
            .Any(traitId => StateOf(facts, traitId) == PreferenceFactState.Unknown);
        var state = selected is not null
            ? relevantStates.Contains(PreferenceFactState.Conflicting) ? PreferenceFactState.Conflicting
                : betterUnknown ? PreferenceFactState.Unknown
                : PreferenceFactState.Present
            : relevantStates.Contains(PreferenceFactState.Conflicting)
                ? PreferenceFactState.Conflicting
                : relevantStates.Contains(PreferenceFactState.Unknown)
                    ? PreferenceFactState.Unknown
                    : PreferenceFactState.Absent;

        var target = family.TargetLevel;
        var targetMet = !family.UpgradeDriving || family.Intent is PreferenceIntent.TieBreak or PreferenceIntent.Neutral
            ? true
            : target is not null && selected is not null && selected.Rank <= target.Rank;
        var explanation = selected is null
            ? $"No known trait matched the {family.Dimension} family."
            : $"Matched {family.Dimension} at {selected.Id}.";

        if (family.UpgradeDriving && !targetMet)
            reasons.Add($"{family.Dimension} is below its target.");

        return new PreferenceFamilyEvaluation(
            family.Id,
            family.Intent,
            state,
            selected?.Id,
            selected?.Rank ?? -1,
            target?.Id,
            targetMet,
            family.UpgradeDriving,
            family.Transient,
            explanation);
    }

    private static PreferenceFact ResolveFact(IEnumerable<PreferenceFact> facts)
    {
        var distinct = facts
            .GroupBy(fact => fact.State)
            .Select(group => group.First())
            .ToArray();
        if (distinct.Length == 1) return distinct[0];
        return new PreferenceFact(
            distinct[0].TraitId,
            PreferenceFactState.Conflicting,
            distinct.Select(fact => fact.Evidence?.Source).FirstOrDefault(source => source is not null) is { } source
                ? new PreferenceEvidence(source, Detail: "Multiple evidence sources disagree.")
                : null);
    }

    private static IReadOnlyDictionary<string, PreferenceFact> NormalizeFacts(
        ReleasePreferencePlan plan,
        IReadOnlyDictionary<string, PreferenceFact> source)
    {
        var facts = source.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var relationships = plan.Relationships ?? [];
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var relationship in relationships)
            {
                var from = Normalize(relationship.FromTraitId);
                var to = Normalize(relationship.ToTraitId);
                var fromState = StateOf(facts, from);
                var toState = StateOf(facts, to);

                if (relationship.Kind is PreferenceRelationshipKind.Implies
                    or PreferenceRelationshipKind.Subsumes
                    or PreferenceRelationshipKind.CoreOf
                    or PreferenceRelationshipKind.CarriedBy)
                {
                    if (fromState == PreferenceFactState.Present)
                    {
                        var next = toState == PreferenceFactState.Absent
                            ? PreferenceFactState.Conflicting
                            : PreferenceFactState.Present;
                        changed |= SetFact(facts, to, next, $"Relationship {relationship.Kind} from {from}.");
                    }
                }
                else if (relationship.Kind == PreferenceRelationshipKind.Requires && fromState == PreferenceFactState.Present)
                {
                    if (toState is PreferenceFactState.Absent or PreferenceFactState.Unknown)
                        changed |= SetFact(facts, from, PreferenceFactState.Conflicting, $"Required companion {to} is not proven.");
                    else if (toState == PreferenceFactState.Conflicting)
                        changed |= SetFact(facts, from, PreferenceFactState.Conflicting, $"Required companion {to} is conflicting.");
                }
                else if (relationship.Kind == PreferenceRelationshipKind.Incompatible
                         && fromState == PreferenceFactState.Present
                         && toState == PreferenceFactState.Present)
                {
                    changed |= SetFact(facts, from, PreferenceFactState.Conflicting, $"Incompatible with {to}.");
                    changed |= SetFact(facts, to, PreferenceFactState.Conflicting, $"Incompatible with {from}.");
                }
            }
        }

        return facts;
    }

    private static bool SetFact(
        IDictionary<string, PreferenceFact> facts,
        string traitId,
        PreferenceFactState state,
        string detail)
    {
        if (facts.TryGetValue(traitId, out var existing))
        {
            if (existing.State == state || existing.State == PreferenceFactState.Conflicting)
                return false;
            facts[traitId] = existing with
            {
                State = state,
                Evidence = existing.Evidence is null
                    ? new PreferenceEvidence("relationship", Detail: detail)
                    : existing.Evidence with { Detail = detail }
            };
            return true;
        }

        facts[traitId] = new PreferenceFact(
            traitId,
            state,
            new PreferenceEvidence("relationship", Detail: detail));
        return true;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static PreferenceFactState StateOf(IReadOnlyDictionary<string, PreferenceFact> facts, string traitId)
        => facts.TryGetValue(traitId.Trim().ToLowerInvariant(), out var fact)
            ? fact.State
            : PreferenceFactState.Unknown;
}
