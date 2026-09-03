using System.Globalization;
using System.Text.RegularExpressions;
using Deluno.Platform.Contracts;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Search;

public static partial class ReleaseDecisionEngine
{
    public static ReleaseDecision Decide(ReleaseDecisionInput input, QualityModelSnapshot? qualityModel = null)
    {
        var normalizedCurrent = LibraryQualityDecider.NormalizeQuality(input.CurrentQuality);
        var normalizedTarget = LibraryQualityDecider.NormalizeQuality(input.TargetQuality) ?? "WEB 1080p";
        var normalizedCandidate = LibraryQualityDecider.NormalizeQuality(input.Quality) ?? input.Quality;
        var candidateRank = QualityRank(normalizedCandidate);
        var currentRank = QualityRank(normalizedCurrent);
        var targetRank = QualityRank(normalizedTarget);
        var qualityDelta = currentRank == 0 ? candidateRank : candidateRank - currentRank;
        var meetsCutoff = candidateRank >= targetRank;
        var reasons = new List<string>();
        var risks = new List<string>();
        var hardReject = false;
        var delayed = false;
        var profileRules = ReleaseProfileRuleEvaluator.Combine(
            input.ReleaseProfiles,
            input.IndexerProtocol ?? "torznab");

        if (string.IsNullOrWhiteSpace(input.DownloadUrl))
        {
            risks.Add("No downloadable URL was returned by the indexer.");
        }

        if (LooksLikeSample(input.ReleaseName))
        {
            hardReject = true;
            risks.Add("Release name looks like a sample, trailer, proof, or extras file.");
        }

        if (ContainsBlockedToken(input.ReleaseName))
        {
            hardReject = true;
            risks.Add("Release name contains a blocked token such as CAM, Telesync, workprint, or screener.");
        }

        foreach (var term in profileRules.MustContain)
        {
            if (!ReleaseProfileRuleEvaluator.ContainsTerm(input.ReleaseName, term))
            {
                hardReject = true;
                risks.Add($"Release name is missing required term '{term}'.");
            }
        }

        foreach (var term in profileRules.MustNotContain)
        {
            if (ReleaseProfileRuleEvaluator.ContainsTerm(input.ReleaseName, term))
            {
                hardReject = true;
                risks.Add($"Release name contains excluded term '{term}'.");
            }
        }

        // Release profiles predate the typed plan and retain numeric term and
        // protocol weights for legacy callers. They must not leak into a typed
        // explanation (or appear to influence a typed decision); hard
        // contain/not-contain rules above remain valid safety gates.
        var preferredTermScore = 0;
        if (input.PreferencePlan is null)
        {
            foreach (var term in profileRules.PreferredTerms)
            {
                if (!ReleaseProfileRuleEvaluator.ContainsTerm(input.ReleaseName, term.Term))
                {
                    continue;
                }

                preferredTermScore += term.Score;
                // Say what the rule did, not what it weighed. "adds 250
                // points" is the arithmetic behind the decision, and #353
                // asks that an explanation completes "Deluno chose this
                // because..." in language that does not require knowing the
                // scale those points are on.
                reasons.Add(term.Score >= 0
                    ? $"It has '{term.Term}', which you told Deluno to prefer."
                    : $"It has '{term.Term}', which you told Deluno to avoid.");
            }
        }

        if (input.PreferencePlan is null && profileRules.PreferredProtocolScore > 0)
        {
            reasons.Add($"It came over {profileRules.PreferredProtocol}, which is the protocol you prefer here.");
        }

        var effectiveMinimumAgeMinutes = Math.Max(input.MinimumAgeMinutes ?? 0, profileRules.DelayMinutes);
        if (effectiveMinimumAgeMinutes > 0)
        {
            if (input.ReleaseAgeHours is null)
            {
                delayed = true;
                risks.Add($"Release age is unavailable, so the {effectiveMinimumAgeMinutes}-minute acquisition delay cannot be verified.");
            }
            else if (input.ReleaseAgeHours.Value * 60d < effectiveMinimumAgeMinutes)
            {
                delayed = true;
                var remainingMinutes = Math.Ceiling(effectiveMinimumAgeMinutes - input.ReleaseAgeHours.Value * 60d);
                risks.Add($"Acquisition delay is active; wait about {remainingMinutes:0} more minute(s) before grabbing this release.");
            }
            else
            {
                reasons.Add($"Release has cleared the {effectiveMinimumAgeMinutes}-minute acquisition delay.");
            }
        }

        if (input.RetentionDays is > 0)
        {
            if (input.ReleaseAgeHours is null)
            {
                hardReject = true;
                risks.Add($"Release age is unavailable, so the {input.RetentionDays}-day retention limit cannot be verified.");
            }
            else if (input.ReleaseAgeHours.Value > input.RetentionDays.Value * 24d)
            {
                hardReject = true;
                risks.Add($"Release is older than this indexer's {input.RetentionDays}-day retention window.");
            }
        }

        if (input.AvailabilityDelayDays is > 0)
        {
            if (input.AvailableUtc is null)
            {
                delayed = true;
                risks.Add($"Availability date is unavailable, so the {input.AvailabilityDelayDays}-day availability delay cannot be verified.");
            }
            else
            {
                var availableAfter = input.AvailableUtc.Value.AddDays(input.AvailabilityDelayDays.Value);
                if (DateTimeOffset.UtcNow < availableAfter)
                {
                    delayed = true;
                    risks.Add($"Availability delay holds this release until {availableAfter:yyyy-MM-dd}.");
                }
                else
                {
                    reasons.Add($"Availability delay of {input.AvailabilityDelayDays} day(s) has cleared.");
                }
            }
        }

        if (input.MaximumSizeMb is > 0 && input.SizeBytes is > 0 &&
            input.SizeBytes.Value > input.MaximumSizeMb.Value * 1_000_000L)
        {
            hardReject = true;
            risks.Add($"Release size exceeds this indexer's {input.MaximumSizeMb} MB maximum.");
        }

        var preferredFlagScore = input.PreferencePlan is null
            ? ScorePreferredIndexerFlags(input.IndexerFlags, input.PreferIndexerFlags, reasons)
            : 0;

        var matchedNeverGrab = MatchNeverGrabPattern(input.ReleaseName, input.NeverGrabPatterns);
        if (!string.IsNullOrWhiteSpace(matchedNeverGrab))
        {
            hardReject = true;
            risks.Add($"Release name matched the never-grab pattern '{matchedNeverGrab}'.");
        }

        // The profile's allowed tiers are a gate, not a preference. Without this
        // the only quality signal was the cutoff, and anything at or above it
        // scored as "preferred" — so a profile allowing up to Bluray 1080p would
        // happily grab WEB 2160p, which is the exact outcome an allowed list
        // exists to prevent. An empty list means the profile does not constrain
        // tiers, so it is not treated as "nothing is allowed".
        if (input.AllowedQualities is { Count: > 0 } allowed
            && !AllowedQualityGate.Accepts(allowed, normalizedCandidate))
        {
            hardReject = true;
            risks.Add(AllowedQualityGate.Refusal(allowed, normalizedCandidate));
        }

        reasons.Add(meetsCutoff
            ? $"Quality {normalizedCandidate} meets or exceeds cutoff {normalizedTarget}."
            : $"Quality {normalizedCandidate} is below cutoff {normalizedTarget}.");

        if (currentRank > 0)
        {
            if (qualityDelta > 0)
            {
                reasons.Add(qualityDelta == 1
                    ? "It is one quality tier better than the file you have."
                    : $"It is {qualityDelta} quality tiers better than the file you have.");
            }

            if (qualityDelta == 0)
            {
                reasons.Add("Quality rank matches the current file, so custom formats and risk decide whether it is worthwhile.");
            }

            if (qualityDelta < 0)
            {
                var currentMeetsCutoff = currentRank >= targetRank;
                if (currentMeetsCutoff)
                {
                    // The other legacy rank rules below are already guarded on
                    // there being no typed plan; this one was not, so a typed
                    // plan's own "your file is better" comparison was being
                    // overwritten by a rank rule that could not see it. Keep
                    // the warning either way - it is the useful half - but let
                    // the typed comparator name the outcome when it owns the
                    // decision. Neither path can dispatch automatically.
                    //
                    // Only ever raise the flag here. Assigning it would clear a
                    // hard gate an earlier rule had already raised - a release
                    // outside the profile's allowed tiers is usually also a
                    // downgrade, so that mistake would quietly reopen the exact
                    // gate the allowed list exists to close.
                    if (input.PreferencePlan is null)
                    {
                        hardReject = true;
                    }

                    risks.Add($"Downgrade blocked: current file ({normalizedCurrent}) already meets the quality target ({normalizedTarget}). Grab this manually if you want to downgrade.");
                }
                else
                {
                    risks.Add($"Quality rank is {Math.Abs(qualityDelta)} step(s) below the current file ({normalizedCurrent} -> {normalizedCandidate}).");
                }
            }
        }

        if (input.PreferencePlan is null &&
            !hardReject &&
            qualityModel?.UpgradeStop.StopWhenCutoffMet == true &&
            currentRank >= targetRank &&
            qualityDelta <= 0)
        {
            var requiresGain = qualityModel.UpgradeStop.RequireCustomFormatGainForSameQuality;
            var hasCurrentFormatEvaluation = input.CurrentCustomFormatScore is not null;
            var currentScore = input.CurrentCustomFormatScore.GetValueOrDefault();
            if (!requiresGain || (hasCurrentFormatEvaluation && input.CustomFormatScore <= currentScore))
            {
                hardReject = true;
                risks.Add(hasCurrentFormatEvaluation
                    ? "Upgrade stop policy blocked this release because the current file already meets cutoff and the candidate does not improve the custom-format score."
                    : "Upgrade stop policy could not compare custom formats because the installed file evaluation is unknown.");
            }
        }

        if (input.PreferencePlan is null &&
            input.CurrentCustomFormatScore is not null &&
            input.CustomFormatScore < input.CurrentCustomFormatScore.Value)
        {
            risks.Add($"Custom format score ({input.CustomFormatScore}) is lower than the current file's score ({input.CurrentCustomFormatScore.Value}).");
        }

        // A same-quality replacement is not an upgrade merely because the
        // candidate arrived later or scored well on transient availability
        // signals. When the installed evaluation is present, equal or lower
        // upgrade-driving format value is an equivalent/non-improving
        // candidate and must not be dispatched automatically.
        if (input.PreferencePlan is null &&
            !hardReject &&
            input.CurrentCustomFormatScore is not null &&
            currentRank >= targetRank &&
            qualityDelta <= 0 &&
            input.CustomFormatScore <= input.CurrentCustomFormatScore.Value)
        {
            hardReject = true;
            risks.Add("Equivalent replacement blocked: the installed file already meets cutoff and this candidate does not improve its upgrade-driving custom formats.");
        }

        var seederScore = ScoreSeeders(input.Seeders, risks, reasons);
        var sizeScore = ScoreSize(input.SizeBytes, normalizedCandidate, input.SizeRules, risks, reasons, out var estimatedBitrate, out var sizeOutOfRange);
        if (sizeOutOfRange)
        {
            hardReject = true;
        }
        var releaseGroup = InferReleaseGroup(input.ReleaseName);
        if (!string.IsNullOrWhiteSpace(releaseGroup))
        {
            reasons.Add($"Release group detected: {releaseGroup}.");
        }

        var codecScore = ScoreCodecAndHdr(input.ReleaseName, reasons, risks);
        var score = 1000
            + input.SourcePriorityScore
            + profileRules.PreferredProtocolScore
            + preferredTermScore
            + preferredFlagScore
            + candidateRank * 90
            + Math.Max(-300, qualityDelta * 80)
            + input.CustomFormatScore
            + seederScore
            + sizeScore
            + codecScore;

        if (!meetsCutoff)
        {
            score -= 250;
        }

        if (risks.Count > 0)
        {
            score -= Math.Min(400, risks.Count * 85);
        }

        var status = hardReject
            ? "rejected"
            : delayed
                ? "delayed"
            : risks.Count >= 3
                ? "risky"
                : meetsCutoff
                    ? "preferred"
                    : "eligible";

        if (hardReject)
        {
            score = Math.Min(score, -10000);
        }

        PreferenceEvaluation? preferenceEvaluation = null;
        PreferenceComparison? preferenceComparison = null;
        var requiresInstalledBaselineReview = false;
        var effectivePolicyVersion = input.PreferencePlan?.Version ?? MediaPolicyCatalog.CurrentVersion;
        var effectiveCustomFormatScore = input.PreferencePlan is null ? input.CustomFormatScore : 0;
        if (input.PreferencePlan is { } preferencePlan)
        {
            var candidateFacts = ReleasePreferenceFactFactory.WithTransientSignals(
                preferencePlan,
                ReleasePreferenceFactFactory.FromReleaseName(
                    preferencePlan,
                    input.ReleaseName,
                    normalizedCandidate),
                input.Seeders);
            preferenceEvaluation = ReleasePreferenceEvaluator.Evaluate(
                preferencePlan,
                candidateFacts);

            // A persisted snapshot is the authoritative installed-file
            // baseline only for the exact plan that produced it. Reusing
            // facts from an older plan would make an old preference vector
            // look comparable to the new one. When a real installed file is
            // present, a stale or missing snapshot is not repaired by parsing
            // its path: the path is not proof of the container's contents.
            // An installed-file baseline is trusted only when every part of
            // the durable snapshot names this exact immutable plan.  In
            // particular, never rebuild installed facts from a release name
            // or a quality label: those values describe a search row, not the
            // container that is currently held.  A missing or malformed
            // snapshot therefore holds replacement automation until a probe
            // records fresh evidence for the file.
            var currentFacts = input.CurrentPreferenceEvaluation is { } snapshot
                && string.Equals(snapshot.PlanId, preferencePlan.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.PlanVersion, preferencePlan.Version, StringComparison.Ordinal)
                && string.Equals(snapshot.PlanHash, preferencePlan.PlanHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.Evaluation.PlanId, snapshot.PlanId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.Evaluation.PlanVersion, snapshot.PlanVersion, StringComparison.Ordinal)
                && string.Equals(snapshot.Evaluation.PlanHash, snapshot.PlanHash, StringComparison.OrdinalIgnoreCase)
                ? snapshot.Facts
                : null;
            if (currentFacts is not null)
            {
                preferenceComparison = ReleasePreferenceEvaluator.Compare(
                    preferencePlan,
                    currentFacts,
                    candidateFacts);
            }

            var installedFilePresent = input.CurrentFilePresent
                || input.CurrentPreferenceEvaluation is not null
                || !string.IsNullOrWhiteSpace(input.CurrentReleaseName)
                || !string.IsNullOrWhiteSpace(input.CurrentQuality);
            if (installedFilePresent && currentFacts is null)
            {
                // A title that already has a file must not be treated like a
                // missing title just because its old snapshot disappeared or
                // the caller could not provide a release name. Without a
                // same-plan baseline Deluno cannot prove a persistent
                // improvement, so automatic replacement waits for a probe or
                // an explicit re-evaluation. Manual force can still override
                // this held result at the caller's discretion.
                requiresInstalledBaselineReview = true;
                risks.Add("Installed-file preference evidence is missing, so Deluno cannot prove that this candidate is a persistent improvement.");
                reasons.Add("Installed-file preference evidence is missing; re-evaluate the held file before automatic replacement.");
                // Keep a typed comparison in the response so callers can
                // render a normal NeedsReview result and its candidate
                // evaluation, while the empty current fact set remains
                // explicitly unknown and can never authorize a replacement.
                preferenceComparison = ReleasePreferenceEvaluator.Compare(
                    preferencePlan,
                    [],
                    candidateFacts);
            }

            if (hardReject)
            {
                status = "rejected";
                reasons.Add("Typed preference evaluation was not allowed to override an earlier hard safety or acquisition gate.");
            }
            else if (requiresInstalledBaselineReview)
            {
                status = "held";
            }
            else if (preferenceComparison is { } comparison)
            {
                status = comparison.Status switch
                {
                    PreferenceCandidateStatus.Upgrade => "preferred",
                    PreferenceCandidateStatus.Rejected => ReleaseDecisionStatuses.Rejected,
                    PreferenceCandidateStatus.NeedsReview => "held",
                    PreferenceCandidateStatus.Equivalent => "equivalent",
                    PreferenceCandidateStatus.CurrentBetter => ReleaseDecisionStatuses.CurrentBetter,
                    _ => meetsCutoff ? "acceptable" : "eligible"
                };
                reasons.AddRange(comparison.Reasons);
            }
            else
            {
                status = preferenceEvaluation.Status switch
                {
                    PreferenceEvaluationStatus.MeetsPlan => "preferred",
                    PreferenceEvaluationStatus.BelowGoal => "eligible",
                    PreferenceEvaluationStatus.NeedsReview => "held",
                    _ => "rejected"
                };
                reasons.AddRange(preferenceEvaluation.Reasons);
            }

            // The legacy total is retained only for compatibility with old
            // persisted history. Typed candidates are ordered by their
            // evaluation/comparison, never by this value.
            score = 0;
        }

        var summary = input.PreferencePlan is null
            ? BuildSummary(status, normalizedCandidate, normalizedTarget, input.CustomFormatScore, input.Seeders, risks.Count, risks)
            : BuildTypedSummary(status, preferenceComparison, preferenceEvaluation, risks);
        return new ReleaseDecision(
            effectivePolicyVersion,
            status,
            score,
            meetsCutoff,
            summary,
            reasons,
            risks,
            qualityDelta,
            effectiveCustomFormatScore,
            seederScore,
            sizeScore,
            releaseGroup,
            estimatedBitrate,
            preferenceEvaluation,
            preferenceComparison);
    }

    private static string BuildTypedSummary(
        string status,
        PreferenceComparison? comparison,
        PreferenceEvaluation? evaluation,
        IReadOnlyList<string> risks)
    {
        // Section 10: a rejection names the gate that failed. The typed
        // comparison's reasons describe the preference outcome, which for a
        // rejected release is the wrong sentence - it explained a release
        // the profile's allowed tiers had refused as "your file is better",
        // naming a comparison instead of the rule that actually stopped it.
        // The gate's own wording lives in the risk flags.
        if (string.Equals(status, ReleaseDecisionStatuses.Rejected, StringComparison.Ordinal)
            && risks.FirstOrDefault(risk => !string.IsNullOrWhiteSpace(risk)) is { } gate)
        {
            return $"Rejected by the typed release plan. {gate}";
        }

        var reasons = comparison?.Reasons ?? evaluation?.Reasons ?? [];
        var explanation = reasons.FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
            ?? "Typed preference evaluation completed.";
        return $"{status switch
        {
            "preferred" => "Preferred by the typed release plan.",
            "held" => "Held for review by the typed release plan.",
            "equivalent" => "Equivalent to the installed file under the typed release plan.",
            ReleaseDecisionStatuses.CurrentBetter => "Your installed file is better than this release.",
            ReleaseDecisionStatuses.Rejected => "Rejected by the typed release plan.",
            _ => "Eligible under the typed release plan."
        }} {explanation}";
    }

    public static int QualityRank(string? quality)
        => LibraryQualityDecider.GetRank(quality);

    private static int ScoreSeeders(int? seeders, ICollection<string> risks, ICollection<string> reasons)
    {
        if (seeders is null)
        {
            risks.Add("Indexer did not report seeders, so availability confidence is unknown.");
            return -40;
        }

        if (seeders <= 0)
        {
            risks.Add("No seeders were reported.");
            return -160;
        }

        if (seeders < 3)
        {
            risks.Add("Very low seed count may stall or fail.");
            return -70;
        }

        var score = Math.Min(220, seeders.Value * 6);
        reasons.Add($"{seeders.Value.ToString(CultureInfo.InvariantCulture)} seeders reported.");
        return score;
    }

    private static int ScorePreferredIndexerFlags(
        string? indexerFlags,
        string? preferredFlags,
        ICollection<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(indexerFlags) || string.IsNullOrWhiteSpace(preferredFlags))
        {
            return 0;
        }

        var matched = ReleaseProfileRuleEvaluator.SplitTerms(preferredFlags)
            .Where(flag => ReleaseProfileRuleEvaluator.ContainsTerm(indexerFlags, flag))
            .ToArray();
        if (matched.Length == 0)
        {
            return 0;
        }

        var score = matched.Length * 50;
        reasons.Add(matched.Length == 1
            ? $"The indexer flagged it {matched[0]}, which you asked Deluno to prefer."
            : $"The indexer flagged it {string.Join(" and ", matched)}, which you asked Deluno to prefer.");
        return score;
    }

    private static int ScoreSize(
        long? sizeBytes,
        string quality,
        IReadOnlyList<ProfileSizeRule>? sizeRules,
        ICollection<string> risks,
        ICollection<string> reasons,
        out double? estimatedBitrate,
        out bool outOfRange)
    {
        estimatedBitrate = null;
        outOfRange = false;
        if (sizeBytes is null or <= 0)
        {
            // An unreported size is not a size violation. Rejecting here would
            // block every indexer that omits the field.
            risks.Add("Indexer did not report release size.");
            return -50;
        }

        var sizeGb = sizeBytes.Value / 1_073_741_824d;
        estimatedBitrate = Math.Round(sizeBytes.Value * 8d / (2.0 * 60 * 60) / 1_000_000, 1);
        var (min, max) = ExpectedSizeRangeGb(quality, sizeRules);

        // Size Rules is described in the UI as "the final check that rejects a
        // release as implausibly small or large", and a minimum in GB reads to a
        // user as a floor. It used to be a score penalty only: one risk flag was
        // not enough to reach "risky" (which needs three) let alone "rejected",
        // so a 0.06 GB file passed a 7 GB floor and was grabbed. The penalty also
        // could not discriminate when every candidate was equally wrong.
        //
        // 0 and 0 remains the documented "no limit" convention, handled by
        // ExpectedSizeRangeGb returning a zero bound.
        if (min > 0 && sizeGb < min)
        {
            outOfRange = true;
            risks.Add($"Size {sizeGb:0.0} GB is below the {min:0.#} GB minimum configured for {quality}.");
            return -180;
        }

        // max <= 0 is the "unlimited" convention from the quality model.
        if (max > 0 && sizeGb > max)
        {
            outOfRange = true;
            risks.Add($"Size {sizeGb:0.0} GB is above the {max:0.#} GB maximum configured for {quality}.");
            return -80;
        }

        reasons.Add($"Size {sizeGb:0.0} GB is within the expected range for {quality}.");
        return 80;
    }

    /// <summary>
    /// The size band this decision judges against.
    ///
    /// <para><b>The profile's own answer first, and nothing else if it has
    /// one.</b> #394: size belongs to the profile, because anime at 1080p and a
    /// film at 1080p are not the same number of gigabytes. A profile that has
    /// said nothing about this tier has no size opinion, and
    /// <see cref="QualityTypicalSizes"/> is what a release is judged against
    /// then - the physical fact about the encode, which is also the band drawn
    /// behind the slider where the answer is given.</para>
    ///
    /// <para>What used to be here was a ladder of substring guesses - "2160 and
    /// remux" meant 35-130, "1080" meant 1.5-25 - a second copy of the same
    /// physical fact the quality model already held. Two copies drift, and the
    /// one nobody looks at is the one that decides whether a release is
    /// refused.</para>
    /// </summary>
    private static (double Min, double Max) ExpectedSizeRangeGb(
        string quality,
        IReadOnlyList<ProfileSizeRule>? sizeRules)
    {
        if (ProfileSizeRulesCodec.For(sizeRules, quality) is { } rule)
        {
            return (rule.MinGb, rule.MaxGb);
        }

        return QualityTypicalSizes.FilmSizeGb(quality);
    }

    private static int ScoreCodecAndHdr(string releaseName, ICollection<string> reasons, ICollection<string> risks)
    {
        var normalized = releaseName.ToLowerInvariant();
        var score = 0;
        if (normalized.Contains("x265") || normalized.Contains("h265") || normalized.Contains("hevc"))
        {
            score += 25;
            reasons.Add("Modern HEVC/x265 video codec detected.");
        }

        if (normalized.Contains("av1"))
        {
            score += 15;
            reasons.Add("AV1 video codec detected.");
        }

        if (normalized.Contains("dv") || normalized.Contains("dolby.vision") || normalized.Contains("hdr10"))
        {
            score += 20;
            reasons.Add("HDR/Dolby Vision signal detected.");
        }

        if (normalized.Contains("hc") && normalized.Contains("sub"))
        {
            risks.Add("Hardcoded subtitles may not match user language preferences.");
            score -= 80;
        }

        return score;
    }

    private static bool LooksLikeSample(string releaseName)
        => SampleTokenRegex().IsMatch(releaseName);

    private static bool ContainsBlockedToken(string releaseName)
        => BlockedTokenRegex().IsMatch(releaseName);

    private static string? MatchNeverGrabPattern(string releaseName, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return null;
        }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (releaseName.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return pattern.Trim();
            }
        }

        return null;
    }

    private static string? InferReleaseGroup(string releaseName)
    {
        var match = ReleaseGroupRegex().Match(releaseName);
        return match.Success ? match.Groups["group"].Value : null;
    }

    private static string BuildSummary(string status, string quality, string target, int customFormatScore, int? seeders, int riskCount, IReadOnlyList<string> risks)
    {
        var downgradeBlock = risks.FirstOrDefault(r => r.StartsWith("Downgrade blocked:", StringComparison.Ordinal));
        var pieces = new List<string>
        {
            downgradeBlock is not null
                ? "Downgrade blocked."
                : status switch
                {
                    "rejected" => "Rejected by hard safety rules.",
                    "delayed" => "Held until the acquisition timing rule clears.",
                    "risky" => "Usable only with caution.",
                    "preferred" => "Preferred candidate.",
                    _ => "Eligible candidate."
                },
            $"{quality} vs cutoff {target}."
        };

        if (customFormatScore != 0) pieces.Add($"Custom formats {customFormatScore:+#;-#;0}.");
        if (seeders is not null) pieces.Add($"{seeders.Value} seeders.");
        if (riskCount > 0 && downgradeBlock is null) pieces.Add($"{riskCount} risk flag{(riskCount == 1 ? "" : "s")}.");
        return string.Join(" ", pieces);
    }

    [GeneratedRegex(@"(^|[.\s_-])(sample|trailer|extras?|proof)([.\s_-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SampleTokenRegex();

    [GeneratedRegex(@"(^|[.\s_-])(cam|camrip|ts|telesync|tc|telecine|wp|workprint|scr|screener)([.\s_-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockedTokenRegex();

    [GeneratedRegex(@"-(?<group>[A-Za-z0-9]{2,20})$")]
    private static partial Regex ReleaseGroupRegex();
}

public sealed record ReleaseDecisionInput(
    string ReleaseName,
    string Quality,
    string? CurrentQuality,
    string? TargetQuality,
    long? SizeBytes,
    int? Seeders,
    string? DownloadUrl,
    int SourcePriorityScore,
    int CustomFormatScore,
    IReadOnlyList<string>? NeverGrabPatterns = null,
    int? CurrentCustomFormatScore = null,
    /// <summary>
    /// The quality tiers the governing profile permits. Null or empty means the
    /// profile does not constrain tiers; a non-empty list rejects anything
    /// outside it, whatever the cutoff says.
    /// </summary>
    IReadOnlyList<string>? AllowedQualities = null,
    /// <summary>
    /// How big a file of each tier should be, according to the profile making
    /// this decision. Null or empty means the profile has no size opinion, and
    /// size is not used to reject anything.
    /// </summary>
    IReadOnlyList<ProfileSizeRule>? SizeRules = null,
    IReadOnlyList<ReleaseProfileItem>? ReleaseProfiles = null,
    string? IndexerProtocol = null,
    double? ReleaseAgeHours = null,
    int? MinimumAgeMinutes = null,
    int? RetentionDays = null,
    int? MaximumSizeMb = null,
    string? IndexerFlags = null,
    string? PreferIndexerFlags = null,
    DateTimeOffset? AvailableUtc = null,
    int? AvailabilityDelayDays = null,
    ReleasePreferencePlan? PreferencePlan = null,
    string? CurrentReleaseName = null,
    /// <summary>
    /// Durable facts for the installed file. They are trusted only when the
    /// persisted plan hash is the exact plan being used for this comparison.
    /// </summary>
    PreferenceEvaluationSnapshot? CurrentPreferenceEvaluation = null,
    /// <summary>
    /// True when the title/episode is known to have an installed file even if
    /// its path, quality, and previous preference snapshot are unavailable.
    /// This prevents an upgrade sweep from treating an unbaselined file as a
    /// missing title.
    /// </summary>
    bool CurrentFilePresent = false);
