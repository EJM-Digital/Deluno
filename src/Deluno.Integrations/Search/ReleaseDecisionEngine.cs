using System.Globalization;
using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Search;

public static partial class ReleaseDecisionEngine
{
    /// <summary>
    /// The shared quality model is no longer an input.
    ///
    /// <para>#394: the two things it was consulted for - how big a file of this
    /// tier should be, and when to stop upgrading - are now the profile's own
    /// answers and arrive on <see cref="ReleaseDecisionInput"/>. A parameter
    /// that is still accepted and no longer read is worse than no parameter:
    /// every caller keeps passing it, and the next person to change the model
    /// has no way to know it stopped mattering.</para>
    /// </summary>
    public static ReleaseDecision Decide(ReleaseDecisionInput input)
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
        // The first gate that refused the release, so its summary names what
        // actually stopped it rather than whichever risk was noted first (#611).
        string? rejectionGate = null;
        var refusals = new List<string>();
        void Refuse(string reason)
        {
            hardReject = true;
            risks.Add(reason);
            refusals.Add(reason);
            rejectionGate ??= reason;
        }
        var delayed = false;
        var profileRules = ReleaseProfileRuleEvaluator.Combine(
            input.ReleaseProfiles,
            input.IndexerProtocol ?? "torznab",
            input.IndexerId);

        if (string.IsNullOrWhiteSpace(input.DownloadUrl))
        {
            risks.Add("No downloadable URL was returned by the indexer.");
        }

        if (!string.IsNullOrWhiteSpace(input.TitleMismatch))
        {
            Refuse(input.TitleMismatch);
        }

        if (LooksLikeSample(input.ReleaseName))
        {
            Refuse("Release name looks like a sample, trailer, proof, or extras file.");
        }

        if (ContainsBlockedToken(input.ReleaseName))
        {
            Refuse("Release name contains a blocked token such as CAM, Telesync, workprint, or screener.");
        }

        foreach (var term in profileRules.MustContain)
        {
            if (!ReleaseProfileRuleEvaluator.ContainsTerm(input.ReleaseName, term))
            {
                Refuse($"Release name is missing required term '{term}'.");
            }
        }

        foreach (var term in profileRules.MustNotContain)
        {
            if (ReleaseProfileRuleEvaluator.ContainsTerm(input.ReleaseName, term))
            {
                Refuse($"Release name contains excluded term '{term}'.");
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
            // #992: Radarr's delay profile ('Bypass if Highest Quality')
            // skips its wait when the release is already the top quality the
            // profile allows, over the protocol the profile prefers. Checked
            // in Radarr's own source (DelaySpecification): the custom-format
            // score plays no part in that check - it only feeds a second,
            // independent 'Bypass if Above Custom Format Score' toggle, which
            // is not what James asked for here, so it is not implemented.
            // Every applicable profile must opt in (the same conservative,
            // all-must-agree rule as the delay itself); a new profile and
            // every profile that existed before this toggle defaults to off,
            // so an existing install keeps waiting out its delay exactly as
            // it does today (#936).
            var topAllowedQualityRank = input.AllowedQualities is { Count: > 0 } allowedForBypass
                ? allowedForBypass.Select(QualityRank).DefaultIfEmpty(0).Max()
                : 0;
            var bypassesDelay = profileRules.BypassIfHighestQuality
                && topAllowedQualityRank > 0
                && candidateRank >= topAllowedQualityRank
                && (profileRules.PreferredProtocol == "any"
                    || string.Equals(
                        profileRules.PreferredProtocol,
                        DownloadProtocols.SourceKind(input.IndexerProtocol),
                        StringComparison.OrdinalIgnoreCase));

            if (bypassesDelay)
            {
                reasons.Add($"Acquisition delay skipped: {normalizedCandidate} is already the best quality this profile allows, so Deluno grabbed it right away.");
            }
            else if (input.ReleaseAgeHours is null)
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
                Refuse($"Release age is unavailable, so the {input.RetentionDays}-day retention limit cannot be verified.");
            }
            else if (input.ReleaseAgeHours.Value > input.RetentionDays.Value * 24d)
            {
                Refuse($"Release is older than this indexer's {input.RetentionDays}-day retention window.");
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
            Refuse($"Release size exceeds this indexer's {input.MaximumSizeMb} MB maximum.");
        }

        var preferredFlagScore = input.PreferencePlan is null
            ? ScorePreferredIndexerFlags(input.IndexerFlags, input.PreferIndexerFlags, reasons)
            : 0;

        // #744: a per-indexer keyword gate, separate from PreferIndexerFlags
        // (which only nudges ranking and never rejects). Required words are
        // an all-must-match list; ignored words reject on any match.
        if (!string.IsNullOrWhiteSpace(input.RequiredWords))
        {
            var missingRequired = ReleaseProfileRuleEvaluator.SplitTerms(input.RequiredWords)
                .FirstOrDefault(term => !ReleaseProfileRuleEvaluator.ContainsTerm(input.ReleaseName, term));
            if (!string.IsNullOrWhiteSpace(missingRequired))
            {
                Refuse($"Release name is missing this indexer's required word '{missingRequired}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(input.IgnoredWords))
        {
            var matchedIgnored = ReleaseProfileRuleEvaluator.SplitTerms(input.IgnoredWords)
                .FirstOrDefault(term => ReleaseProfileRuleEvaluator.ContainsTerm(input.ReleaseName, term));
            if (!string.IsNullOrWhiteSpace(matchedIgnored))
            {
                Refuse($"Release name contains this indexer's ignored word '{matchedIgnored}'.");
            }
        }

        // #743: a per-indexer TV search restriction. Only ever applies to TV;
        // a movie search against the same indexer is unaffected.
        if (string.Equals(input.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            && input.SeasonPackSearchMode is { } seasonPackSearchMode
            && !string.Equals(seasonPackSearchMode, "both", StringComparison.OrdinalIgnoreCase))
        {
            var isSeasonPackCandidate = SeasonPackDetector.Classify(input.ReleaseName).IsSeason;
            if (string.Equals(seasonPackSearchMode, "season-packs-only", StringComparison.OrdinalIgnoreCase) && !isSeasonPackCandidate)
            {
                Refuse("This indexer is configured for season-pack searches only, and this release is a single episode.");
            }
            else if (string.Equals(seasonPackSearchMode, "episodes-only", StringComparison.OrdinalIgnoreCase) && isSeasonPackCandidate)
            {
                Refuse("This indexer is configured for episode searches only, and this release is a season pack.");
            }
        }

        var matchedNeverGrab = MatchNeverGrabPattern(input.ReleaseName, input.NeverGrabPatterns);
        if (!string.IsNullOrWhiteSpace(matchedNeverGrab))
        {
            Refuse($"Release name matched the never-grab pattern '{matchedNeverGrab}'.");
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
            Refuse(AllowedQualityGate.Refusal(allowed, normalizedCandidate));
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

        // #394: when to stop is this profile's answer, not one policy for every
        // shelf. A library you want left alone once it is good enough and one
        // you want chased forever are the same question answered two ways, and
        // a single setting could only ever be right for one of them.
        var upgradeStop = input.UpgradeStop;
        if (input.PreferencePlan is null &&
            !hardReject &&
            upgradeStop?.StopWhenCutoffMet == true &&
            currentRank >= targetRank &&
            qualityDelta <= 0)
        {
            var requiresGain = upgradeStop.RequireCustomFormatGainForSameQuality;
            var hasCurrentFormatEvaluation = input.CurrentCustomFormatScore is not null;
            var currentScore = input.CurrentCustomFormatScore.GetValueOrDefault();
            if (!requiresGain || (hasCurrentFormatEvaluation && input.CustomFormatScore <= currentScore))
            {
                Refuse(hasCurrentFormatEvaluation
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
            Refuse("Equivalent replacement blocked: the installed file already meets cutoff and this candidate does not improve its upgrade-driving custom formats.");
        }

        // #778: the three TRaSH-style guardrails behind "Never grab copies
        // rated bad", "Stop upgrading once a copy is..." and "Only replace
        // when...". These sit beside the legacy and typed-plan paths rather
        // than inside either: a profile that picked a plain choice gets it
        // honoured whichever comparator otherwise decided the release, and
        // every refusal names the plain choice, never the number behind it.
        var scoreLimits = input.ScoreLimits;
        if (scoreLimits is not null && !hardReject)
        {
            if (scoreLimits.MinFormatScore is { } minScore && input.CustomFormatScore < minScore)
            {
                Refuse("Not grabbed: TRaSH rates this copy as bad, and you asked Deluno to never grab those.");
            }

            if (!hardReject
                && scoreLimits.CutoffFormatScore is { } cutoffScore
                && currentRank >= targetRank
                && qualityDelta <= 0
                && input.CurrentCustomFormatScore is { } currentScoreForCutoff
                && currentScoreForCutoff >= cutoffScore)
            {
                var cutoffChoice = PlainQualityChoiceMapper.NearestCutoffChoice(
                    cutoffScore,
                    scoreLimits.MinFormatScore ?? PlainQualityChoiceMapper.GenericMinFormatScore,
                    cutoffScore);
                Refuse($"Stopped upgrading: your copy is already '{PlainQualityChoiceMapper.CutoffLabel(cutoffChoice)}'.");
            }

            if (!hardReject
                && scoreLimits.MinUpgradeFormatScore is { } minGain
                && currentRank >= targetRank
                && qualityDelta <= 0
                && input.CurrentCustomFormatScore is { } currentScoreForGain
                && input.CustomFormatScore > currentScoreForGain
                && input.CustomFormatScore - currentScoreForGain < minGain)
            {
                var gainChoice = PlainQualityChoiceMapper.NearestUpgradeGainChoice(
                    minGain,
                    PlainQualityChoiceMapper.GenericMinUpgradeFormatScore);
                Refuse($"Not grabbed: not {PlainQualityChoiceMapper.UpgradeGainLabel(gainChoice)} than the copy you have, which is what you asked for.");
            }
        }

        var seederScore = ScoreSeeders(input.Seeders, risks, reasons);
        // A torrent nobody seeds never downloads (#611). It stays in a manual
        // search with this reason, where a person can still force it.
        if (input.Seeders == 0 && DownloadProtocols.SourceKind(input.IndexerProtocol) == DownloadProtocols.Torrent)
        {
            // It leads the reasons like any refusal, so a person never reads the quality line as why it was refused (#1159).
            hardReject = true;
            refusals.Add("No one is seeding this torrent, so it cannot download.");
            rejectionGate ??= "No one is seeding this torrent, so it cannot download.";
        }

        // #738: a per-indexer seeder floor, stricter than the always-on
        // zero-seeder gate above. Usenet has no seeder count, so this only
        // ever applies to torrents.
        if (input.MinimumSeeders is > 0
            && DownloadProtocols.SourceKind(input.IndexerProtocol) == DownloadProtocols.Torrent
            && input.Seeders is { } seederCount
            && seederCount < input.MinimumSeeders.Value)
        {
            Refuse($"Release has {seederCount} seeder(s), below this indexer's configured minimum of {input.MinimumSeeders.Value}.");
        }

        var sizeScore = ScoreSize(input.SizeBytes, normalizedCandidate, input.SizeRules, IsSingleEpisode(input.MediaType, input.ReleaseName), input.RuntimeMinutes, risks, reasons, out var estimatedBitrate, out var sizeOutOfRange);
        if (sizeOutOfRange)
        {
            hardReject = true;
            refusals.Add(risks[^1]);
            rejectionGate ??= risks[^1];
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
            // #646: a fresh season search has no installed episode for
            // SeasonPackReplacementDecision to compare against, so this is
            // the only place season-pack-ness reaches typed selection - a
            // transient tie-break, never upgrade-driving, so it cannot outrank
            // a better quality or custom-format family.
            var isSeasonPack = string.Equals(input.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                ? SeasonPackDetector.Classify(input.ReleaseName).IsSeason
                : (bool?)null;
            var candidateFacts = ReleasePreferenceFactFactory.WithTransientSignals(
                preferencePlan,
                ReleasePreferenceFactFactory.FromReleaseName(
                    preferencePlan,
                    input.ReleaseName,
                    normalizedCandidate,
                    indexerFlags: input.IndexerFlags,
                    multiLanguages: input.MultiLanguages,
                    releaseSizeBytes: input.SizeBytes),
                input.Seeders,
                isSeasonPack);
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
                // The facts are re-evaluated here, and a file below the size
                // floor is not something they can show (#597).
                preferenceComparison = ReleasePreferenceEvaluator.Compare(
                    preferencePlan,
                    currentFacts,
                    candidateFacts,
                    input.CurrentPreferenceEvaluation!.Evaluation.InstalledFileViolation);
            }

            var installedFilePresent = input.CurrentFilePresent
                || input.CurrentPreferenceEvaluation is not null
                || !string.IsNullOrWhiteSpace(input.CurrentReleaseName)
                || !string.IsNullOrWhiteSpace(input.CurrentQuality);
            // #1169/#1157: the installed file's quality is a recorded fact
            // (its file record), not a guess from a name. A move to a higher
            // quality tier needs nothing else, so a missing probe snapshot
            // must not hold it. Only the quality family is built from this;
            // every other trait stays unknown, so same-quality, custom-format
            // and preference upgrades still wait for real probe evidence.
            var sentOnQualityAlone = false;
            if (installedFilePresent && currentFacts is null && currentRank > 0 && qualityDelta > 0 && !hardReject)
            {
                var qualityOnlyComparison = ReleasePreferenceEvaluator.Compare(
                    preferencePlan,
                    ReleasePreferenceFactFactory.FromReleaseName(preferencePlan, null, normalizedCurrent, evidenceSource: "installed-file-record"),
                    candidateFacts);
                if (qualityOnlyComparison.Status == PreferenceCandidateStatus.Upgrade)
                {
                    preferenceComparison = qualityOnlyComparison;
                    sentOnQualityAlone = true;
                    reasons.Add($"The installed file is {normalizedCurrent}; this is a higher quality tier, so it is sent on quality alone.");
                }
            }

            if (installedFilePresent && currentFacts is null && !sentOnQualityAlone)
            {
                // A title that already has a file must not be treated like a
                // missing title just because its old snapshot disappeared or
                // the caller could not provide a release name. Without a
                // same-plan baseline Deluno cannot prove a persistent
                // improvement, so automatic replacement waits for a probe or
                // an explicit re-evaluation. Manual force can still override
                // this held result at the caller's discretion.
                requiresInstalledBaselineReview = true;
                risks.Add("Deluno has not checked the file you have yet, so it cannot tell whether this one is better.");
                reasons.Add("Your file has not been checked yet; check it before this can replace it.");
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
                // Nothing is on disk, so there is nothing to compare against.
                // Only the quality cutoff can hold a release for a title you
                // have none of (James, 2026-09-19, #550): a preference it
                // misses still ranks it below a candidate that has it, and can
                // still justify an upgrade once there is a file. Holding here
                // for any missed target meant a release from outside a
                // strongly-preferred group was never downloaded at all.
                var takenAtCutoff = preferenceEvaluation.Status == PreferenceEvaluationStatus.BelowGoal && meetsCutoff;
                status = preferenceEvaluation.Status switch
                {
                    PreferenceEvaluationStatus.MeetsPlan => "preferred",
                    PreferenceEvaluationStatus.BelowGoal => takenAtCutoff ? "preferred" : "eligible",
                    PreferenceEvaluationStatus.NeedsReview => "held",
                    _ => "rejected"
                };
                reasons.AddRange(preferenceEvaluation.Reasons);
                if (takenAtCutoff)
                {
                    reasons.Add("Nothing is on disk yet and this meets the quality cutoff, so it is taken; the preferences it misses can still justify an upgrade later.");
                }
            }

            // The legacy total is retained only for compatibility with old
            // persisted history. Typed candidates are ordered by their
            // evaluation/comparison, never by this value.
            score = 0;
        }

        var summary = input.PreferencePlan is null
            ? BuildSummary(status, normalizedCandidate, normalizedTarget, input.CustomFormatScore, input.Seeders, risks.Count, risks)
            : BuildTypedSummary(status, preferenceComparison, preferenceEvaluation, rejectionGate);
        return new ReleaseDecision(
            effectivePolicyVersion,
            status,
            score,
            meetsCutoff,
            summary,
            // What refused it leads: "Quality X is below cutoff Y" read as the reason when it was not (#1159).
            LeadWithWhyItWasRefused(status, refusals, reasons, preferenceComparison, preferenceEvaluation),
            risks,
            qualityDelta,
            effectiveCustomFormatScore,
            seederScore,
            sizeScore,
            releaseGroup,
            estimatedBitrate,
            preferenceEvaluation,
            preferenceComparison,
            rejectionGate);
    }

    /// <summary>
    /// A refused release's first reason is the one that refused it. A refusal by a gate leads; a refusal by
    /// the preference plan leads with that plan's own reasons, never "Quality X meets cutoff" (#1159).
    /// </summary>
    private static List<string> LeadWithWhyItWasRefused(
        string status,
        List<string> refusals,
        List<string> reasons,
        PreferenceComparison? comparison,
        PreferenceEvaluation? evaluation)
    {
        if (refusals.Count > 0)
        {
            return refusals.Concat(reasons).ToList();
        }

        if (!string.Equals(status, ReleaseDecisionStatuses.Rejected, StringComparison.Ordinal))
        {
            return reasons;
        }

        var why = (comparison?.Reasons ?? evaluation?.Reasons ?? [])
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .ToList();
        return why.Count == 0 ? reasons : why.Concat(reasons.Where(reason => !why.Contains(reason))).ToList();
    }

    private static string BuildTypedSummary(
        string status,
        PreferenceComparison? comparison,
        PreferenceEvaluation? evaluation,
        string? rejectionGate)
    {
        // Section 10: a rejection names the gate that failed. The typed
        // comparison's reasons describe the preference outcome, which for a
        // rejected release is the wrong sentence - it explained a release
        // the profile's allowed tiers had refused as "your file is better",
        // naming a comparison instead of the rule that actually stopped it.
        // It is the gate that refused, not the first risk noted (#611): a
        // release under the size floor with one seeder was said to have been
        // refused for its seed count.
        if (string.Equals(status, ReleaseDecisionStatuses.Rejected, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(rejectionGate))
        {
            return $"Not taken: {rejectionGate}";
        }

        var reasons = comparison?.Reasons ?? evaluation?.Reasons ?? [];
        var explanation = reasons.FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
        // Plain words only: the internal names for these outcomes ("typed release plan", "preference
        // evaluation") stay in the stored status codes and logs (#1159).
        var headline = status switch
        {
            "preferred" => "Would be taken.",
            "held" => "Held for your review.",
            "equivalent" => "Same as the file you have.",
            ReleaseDecisionStatuses.CurrentBetter => "Your installed file is better than this release.",
            ReleaseDecisionStatuses.Rejected => "Not taken.",
            _ => "Usable, but not taken on its own."
        };
        return explanation is null ? headline : $"{headline} {explanation}";
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
        reasons.Add($"{PluralWords.Count(seeders.Value, "seeder")} reported.");
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

    /// <summary>
    /// Whether a release is one TV episode, sized in MB against the episode
    /// band (#600). Season packs and films keep the film band.
    /// </summary>
    public static bool IsSingleEpisode(string? mediaType, string releaseName)
        => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
            && SeasonPackDetector.Classify(releaseName).IsEpisode;

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
    ///
    /// <para>#600: the band is in bytes. A single episode reads the profile's
    /// episode band (MB), anything else the film band (GB). With
    /// <paramref name="typicalWhenUnset"/> false, a tier the profile says
    /// nothing about has no band at all.</para>
    /// </summary>
    public static (double MinBytes, double MaxBytes, bool Episode)? SizeBand(
        string? quality,
        IReadOnlyList<ProfileSizeRule>? sizeRules,
        bool episode,
        bool typicalWhenUnset)
    {
        const double Gb = 1_073_741_824d;
        const double Mb = 1_048_576d;
        if (ProfileSizeRulesCodec.For(sizeRules, quality) is { } rule)
        {
            return episode ? (rule.MinMb * Mb, rule.MaxMb * Mb, true) : (rule.MinGb * Gb, rule.MaxGb * Gb, false);
        }

        if (!typicalWhenUnset || string.IsNullOrWhiteSpace(quality))
        {
            return null;
        }

        if (episode)
        {
            var (minMb, maxMb) = QualityTypicalSizes.EpisodeSizeMb(quality);
            return (minMb * Mb, maxMb * Mb, true);
        }

        var (minGb, maxGb) = QualityTypicalSizes.FilmSizeGb(quality);
        return (minGb * Gb, maxGb * Gb, false);
    }

    /// <summary>A size spoken of in the unit its band uses: MB for an episode, GB otherwise.</summary>
    public static string DescribeSize(double bytes, bool episode)
        => episode ? $"{bytes / 1_048_576d:0} MB" : $"{bytes / 1_073_741_824d:0.0} GB";

    private static string DescribeLimit(double bytes, bool episode)
        => episode ? $"{bytes / 1_048_576d:0} MB" : $"{bytes / 1_073_741_824d:0.#} GB";

    /// <summary>
    /// The runtime Size Rules assumes for a title when none is known: about 45
    /// minutes for an episode, two hours for a film (#600). #630: this is now
    /// only the reference runtime the configured band is scaled from - the
    /// fallback used when a title's real runtime is unavailable, not the
    /// runtime every title is judged against.
    /// </summary>
    private const double EpisodeReferenceRuntimeMinutes = 45.0;

    private const double FilmReferenceRuntimeMinutes = 120.0;

    /// <summary>A runtime named the way a person would say it - whole hours in
    /// hours, everything else in minutes - so a rejection can say "for a
    /// 12-minute runtime" or "for a 2-hour runtime" and a person can tell
    /// which threshold applied (#630).</summary>
    private static string DescribeRuntimeSpan(double minutes)
    {
        var rounded = Math.Max(1, (int)Math.Round(minutes, MidpointRounding.AwayFromZero));
        return rounded % 60 == 0
            ? $"{rounded / 60}-hour"
            : $"{rounded}-minute";
    }

    private static int ScoreSize(
        long? sizeBytes,
        string quality,
        IReadOnlyList<ProfileSizeRule>? sizeRules,
        bool episode,
        int? runtimeMinutes,
        ICollection<string> risks,
        ICollection<string> reasons,
        out double? estimatedBitrate,
        out bool outOfRange)
    {
        const double Gb = 1_073_741_824d;
        const double Mb = 1_048_576d;
        estimatedBitrate = null;
        outOfRange = false;
        if (sizeBytes is null or <= 0)
        {
            // An unreported size is not a size violation. Rejecting here would
            // block every indexer that omits the field.
            risks.Add("Indexer did not report release size.");
            return -50;
        }

        // #630: Tears of Steel (2012), a 12-minute short, had every real
        // 1080p release refused as "below the 1.5 GB minimum configured for
        // WEB 1080p" - a floor sized for a film ten times its length, because
        // the band was judged against a fixed runtime for every title. Fixed
        // it the way Radarr/Sonarr do: the configured band is a rate against
        // the reference runtime above, scaled against the title's real
        // runtime when metadata supplies one. With no runtime known there is
        // nothing to scale by, so the reference runtime remains the
        // assumption exactly as before.
        var referenceRuntimeMinutes = episode ? EpisodeReferenceRuntimeMinutes : FilmReferenceRuntimeMinutes;
        var actualRuntimeMinutes = runtimeMinutes is > 0 ? (double)runtimeMinutes.Value : (double?)null;
        var effectiveRuntimeMinutes = actualRuntimeMinutes ?? referenceRuntimeMinutes;
        estimatedBitrate = Math.Round(sizeBytes.Value * 8d / (effectiveRuntimeMinutes * 60) / 1_000_000, 1);
        var (bandMin, bandMax, _) = SizeBand(quality, sizeRules, episode, typicalWhenUnset: true)!.Value;
        var runtimeScale = actualRuntimeMinutes.HasValue ? actualRuntimeMinutes.Value / referenceRuntimeMinutes : 1.0;
        var min = bandMin * runtimeScale;
        var max = bandMax * runtimeScale;
        var size = DescribeSize(sizeBytes.Value, episode);
        var runtimeDescription = DescribeRuntimeSpan(effectiveRuntimeMinutes);

        // Size Rules is described in the UI as "the final check that rejects a
        // release as implausibly small or large", and a minimum in GB reads to a
        // user as a floor. It used to be a score penalty only: one risk flag was
        // not enough to reach "risky" (which needs three) let alone "rejected",
        // so a 0.06 GB file passed a 7 GB floor and was grabbed. The penalty also
        // could not discriminate when every candidate was equally wrong.
        //
        // 0 and 0 remains the documented "no limit" convention, handled by
        // SizeBand returning a zero bound - scaling a zero bound by any runtime
        // still leaves it zero, so "no limit" still means no limit.
        if (min > 0 && sizeBytes.Value < min)
        {
            outOfRange = true;
            risks.Add($"Size {size} is below the {DescribeLimit(min, episode)} minimum configured for {quality} for a {runtimeDescription} runtime.");
            return -180;
        }

        // max <= 0 is the "unlimited" convention from the quality model.
        if (max > 0 && sizeBytes.Value > max)
        {
            outOfRange = true;
            risks.Add($"Size {size} is above the {DescribeLimit(max, episode)} maximum configured for {quality} for a {runtimeDescription} runtime.");
            return -80;
        }

        // #771: within the accepted band, a release closer to the tier's
        // Preferred size (Settings > Quality) beats one further from it — the
        // same idea as Radarr's Preferred column, just expressed as a score
        // bonus rather than a second rejection line, since being off-Preferred
        // is a taste, not a fault. The bonus caps at 20 so it can never turn an
        // out-of-range release into an in-range one; it only orders same-tier
        // in-range candidates against each other.
        var preferredRaw = episode
            ? QualityTypicalSizes.For(quality)?.EpisodePreferredMb * Mb
            : QualityTypicalSizes.For(quality)?.MoviePreferredGb * Gb;
        if (preferredRaw is { } preferredUnscaled)
        {
            var preferred = preferredUnscaled * runtimeScale;
            var bandRadius = Math.Max(preferred - min, max > 0 ? max - preferred : preferred - min);
            if (bandRadius > 0)
            {
                var closeness = Math.Clamp(1 - Math.Abs(sizeBytes.Value - preferred) / bandRadius, 0, 1);
                var preferenceBonus = (int)Math.Round(closeness * 20);
                reasons.Add(closeness >= 0.85
                    ? $"Size {size} is close to the preferred size for {quality}."
                    : $"Size {size} is within the expected range for {quality}.");
                return 80 + preferenceBonus;
            }
        }

        reasons.Add($"Size {size} is within the expected range for {quality}.");
        return 80;
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
        if (seeders is not null) pieces.Add($"{PluralWords.Count(seeders.Value, "seeder")}.");
        if (riskCount > 0 && downgradeBlock is null) pieces.Add($"{riskCount} risk flag{(riskCount == 1 ? "" : "s")}.");
        return string.Join(" ", pieces);
    }

    [GeneratedRegex(@"(^|[.\s_-])(sample|trailer|extras?|proof)([.\s_-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SampleTokenRegex();

    [GeneratedRegex(@"(^|[.\s_-])(cam|hdcam|hd-cam|camrip|ts|hdts|hd-ts|telesync|tc|telecine|wp|workprint|scr|screener|dvdscr|bdscr|r5|vhsrip)([.\s_-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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
    /// <summary>
    /// When the profile making this decision stops looking for something
    /// better. Null means it never stops on cutoff alone.
    /// </summary>
    QualityUpgradeStopPolicy? UpgradeStop = null,
    IReadOnlyList<ReleaseProfileItem>? ReleaseProfiles = null,
    string? IndexerProtocol = null,
    string? IndexerId = null,
    double? ReleaseAgeHours = null,
    int? MinimumAgeMinutes = null,
    int? RetentionDays = null,
    int? MaximumSizeMb = null,
    /// <summary>
    /// The seeder floor a torrent from this indexer must clear to be grabbed
    /// (#738). Null means unrestricted; ignored for usenet/RSS sources.
    /// </summary>
    int? MinimumSeeders = null,
    string? IndexerFlags = null,
    string? PreferIndexerFlags = null,
    /// <summary>
    /// Comma-separated, case-insensitive keyword lists that require or
    /// reject a release from this indexer before it can be grabbed (#744).
    /// Separate from <see cref="PreferIndexerFlags"/>, which only ranks.
    /// </summary>
    string? RequiredWords = null,
    string? IgnoredWords = null,
    /// <summary>
    /// What this indexer's own MULTI tag means (#746), e.g. "Original,
    /// French". Null keeps Deluno's fixed "Multilingual" default.
    /// </summary>
    string? MultiLanguages = null,
    /// <summary>
    /// Restricts a TV search against this indexer to season packs only,
    /// episodes only, or both (#743). Null or "both" means no restriction;
    /// ignored for a movie search.
    /// </summary>
    string? SeasonPackSearchMode = null,
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
    bool CurrentFilePresent = false,
    /// <summary>
    /// "movies" or "tv". A single TV episode is sized against the episode
    /// band, not the film one (#600). Null keeps the film band.
    /// </summary>
    string? MediaType = null,
    /// <summary>
    /// Why the release's name is not for what was searched, or null when it is
    /// (#613). A search by words can answer with anything sharing a word.
    /// </summary>
    string? TitleMismatch = null,
    /// <summary>
    /// The title's real runtime in minutes, when metadata supplies one. Size
    /// Rules is configured against a reference runtime - two hours for a
    /// film, 45 minutes for an episode (#600) - and is scaled against this
    /// value so a release is not judged against a floor sized for a much
    /// longer or shorter title (#630). Null or non-positive keeps the fixed
    /// reference runtime as the assumption, exactly as before #630.
    /// </summary>
    int? RuntimeMinutes = null,
    /// <summary>
    /// The profile's own minimum, cutoff and minimum-upgrade-gain score
    /// guardrails behind "Stop upgrading once a copy is", "Only replace when
    /// clearly better" and "Never grab copies rated bad" (#778). Null means
    /// this profile has no score opinion, which is the behaviour every profile
    /// had before it could hold one.
    /// </summary>
    ProfileScoreLimits? ScoreLimits = null);
