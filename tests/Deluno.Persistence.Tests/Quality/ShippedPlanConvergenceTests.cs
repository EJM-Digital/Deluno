using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #352 line 3: every shipped plan converges.
///
/// <para>The convergence coverage that existed proves the comparator reaches a
/// fixed point on a hand-built two-level audio plan. That is the comparator's
/// property, not the shipped plans' — a profile whose families disagree, or
/// whose target sits above a level nothing can reach, would still oscillate
/// and no test would notice. This runs the harness the issue describes against
/// the actual bundled guide profiles.</para>
///
/// <para>The failure this guards against is the expensive one: a library that
/// grabs, imports, decides it is still not finished, grabs again, and never
/// stops. It cost #345 to find that shape once already.</para>
/// </summary>
public sealed class ShippedPlanConvergenceTests
{
    public static TheoryData<string, string> ShippedProfiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var profile in GuidePackageCatalog.Current.QualityProfiles)
        {
            data.Add(profile.Id, profile.MediaType);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedProfiles))]
    public void A_shipped_plan_reaches_a_fixed_point_and_stays_there(string profileId, string mediaType)
    {
        var plan = GuidePlanCompiler.Compile(profileId, mediaType, GuidePackageCatalog.Current).Plan;

        // The best file this plan can describe: every family at its top level,
        // and every trait the plan does not mention explicitly absent, so
        // nothing is left unknown and asking for review.
        var best = BestCaseFacts(plan);

        // 1. No file yet, and the best candidate arrives.
        var firstGrab = ReleasePreferenceEvaluator.Compare(plan, [], best);
        Assert.NotEqual(PreferenceCandidateStatus.Rejected, firstGrab.Status);

        // 2. It is imported, so it becomes the held file. From here the same
        //    candidate set must produce no work at all.
        var settled = ReleasePreferenceEvaluator.Compare(plan, best, best);
        Assert.Equal(PreferenceCandidateStatus.Equivalent, settled.Status);
        Assert.False(settled.PersistentImprovement, $"'{profileId}' would replace a file with an identical one.");
        Assert.False(settled.Regressed);
        Assert.True(
            settled.Current.TargetsMet,
            $"'{profileId}' cannot be satisfied by the best file it can describe: {string.Join(" | ", settled.Current.Reasons)}");

        // 3. Run the identical cycle again. A plan that queues work on the
        //    second pass is the endless-upgrade shape.
        var again = ReleasePreferenceEvaluator.Compare(plan, best, best);
        Assert.Equal(settled.Status, again.Status);
        Assert.Equal(settled.PersistentImprovement, again.PersistentImprovement);
        Assert.Equal(settled.DecisiveFamilyId, again.DecisiveFamilyId);

        // 4. And a worse candidate arriving later must not reopen it. Seed
        //    counts and indexer order change constantly; the answer must not.
        foreach (var family in plan.OrderedFamilies.Where(item => item.OrderedLevels.Count > 1))
        {
            var worse = DemoteOneFamily(plan, best, family);
            var offered = ReleasePreferenceEvaluator.Compare(plan, best, worse);
            Assert.False(
                offered.PersistentImprovement,
                $"'{profileId}' treated a worse {family.Dimension} as an improvement over the best file it can describe.");
        }
    }

    /// <summary>
    /// Every family at rank 0, everything else explicitly absent. Absent
    /// rather than omitted on purpose: the evaluator treats a missing fact as
    /// unknown, and unknown is a review, so a fixture that omits traits would
    /// prove the plan needs review rather than that it converges.
    /// </summary>
    private static PreferenceFact[] BestCaseFacts(ReleasePreferencePlan plan)
    {
        var chosen = plan.OrderedFamilies
            .SelectMany(family => family.OrderedLevels.OrderBy(level => level.Rank).Take(1))
            .SelectMany(level => level.NormalizedTraitIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var traitId in plan.RequiredTraitIds ?? [])
        {
            chosen.Add(traitId.Trim().ToLowerInvariant());
        }

        var mentioned = plan.OrderedFamilies
            .SelectMany(family => family.OrderedLevels)
            .SelectMany(level => level.NormalizedTraitIds)
            .Concat(plan.RequiredTraitIds?.Select(id => id.Trim().ToLowerInvariant()) ?? [])
            .Concat(plan.ForbiddenTraitIds?.Select(id => id.Trim().ToLowerInvariant()) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return mentioned
            .Select(traitId => new PreferenceFact(
                traitId,
                chosen.Contains(traitId) ? PreferenceFactState.Present : PreferenceFactState.Absent))
            .ToArray();
    }

    /// <summary>The same file with one family moved down a level.</summary>
    private static PreferenceFact[] DemoteOneFamily(
        ReleasePreferencePlan plan,
        IReadOnlyList<PreferenceFact> best,
        PreferenceFamily family)
    {
        var ordered = family.OrderedLevels.OrderBy(level => level.Rank).ToArray();
        var top = ordered[0].NormalizedTraitIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = ordered[1].NormalizedTraitIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return best
            .Select(fact => top.Contains(fact.NormalizedTraitId)
                ? fact with { State = PreferenceFactState.Absent }
                : next.Contains(fact.NormalizedTraitId)
                    ? fact with { State = PreferenceFactState.Present }
                    : fact)
            .ToArray();
    }
}
