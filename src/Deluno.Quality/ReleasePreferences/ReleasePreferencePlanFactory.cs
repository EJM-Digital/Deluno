using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Creates the typed quality policy used by acquisition when a persisted
/// profile has not yet been compiled into a guide-backed plan. Every valid
/// selected custom-format matcher becomes an immutable Boolean decision family;
/// only missing or empty matchers remain in Advanced diagnostics. Reviewed
/// safety mappings are compiled as forbidden hard gates and never behave like
/// a positive preference.
/// </summary>
public static class ReleasePreferencePlanFactory
{
    /// <summary>
    /// Builds the effective immutable plan for one persisted quality profile.
    /// The profile's selected custom-format rows are the only rows considered;
    /// an inventory-wide custom-format list must not silently change a title's
    /// plan.
    /// </summary>
    public static ReleasePreferencePlan CreateQualityPlan(
        QualityProfileItem profile,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? guidePackage = null,
        // #767: the global Proper/Repack acquisition choice (PlatformSettingsSnapshot.ProperRepackAcquisitionMode).
        // Null behaves exactly as before this setting existed: "prefer-and-upgrade".
        string? properRepackAcquisitionMode = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        guidePackage ??= GuidePackageCatalog.Current;
        var normalizedProperRepackMode = ProperRepackAcquisitionModes.Normalize(properRepackAcquisitionMode);
        var selected = SelectProfileFormats(profile, customFormats);
        var inputsVersion = $"profile/{profile.UpdatedUtc.ToUniversalTime():yyyyMMddHHmmssfffffff}/"
            + $"{guidePackage.Version}:{guidePackage.Source.UpstreamRevision}/{normalizedProperRepackMode}";
        var compiled = CreateQualityPlan(
            profile.MediaType,
            profile.CutoffQuality,
            Split(profile.AllowedQualities),
            profile.UpgradeUntilCutoff,
            id: $"quality-profile/{profile.Id}",
            version: inputsVersion,
            customFormats: selected,
            guidePackage: guidePackage,
            formatIntents: profile.FormatIntents,
            languagePreference: profile.LanguagePreference,
            properRepackAcquisitionMode: normalizedProperRepackMode);

        // The profile's saved time and the guide package do not say what the
        // plan was compiled into, and plans are immutable per version. Found on
        // the physical rig after an upgrade: an older build had stored this
        // profile's plan under this same version, the newer build compiled a
        // different definition, and every read of the profile's plan failed
        // with "is immutable and already contains a different definition". A
        // rule edited without re-saving the profile collides the same way.
        //
        // So the definition names itself. An unchanged definition keeps its
        // version across restarts; a changed one gets a new version beside the
        // old, and anything recorded under the old one still resolves to exactly
        // the plan that decided it.
        return compiled with { Version = $"{inputsVersion}/def-{compiled.PlanHash[..16]}" };
    }

    /// <summary>
    /// Returns the typed plan plus explicit review items that cannot be
    /// evaluated safely. A valid local or unreviewed matcher is compiled as a
    /// plan-scoped matched/not-matched family, so its typed owner intent works
    /// without turning its legacy numeric score into decision arithmetic.
    /// </summary>
    public static RuntimeQualityProfileCompilation CompileProfile(
        QualityProfileItem profile,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? guidePackage = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        guidePackage ??= GuidePackageCatalog.Current;
        var selected = SelectProfileFormats(profile, customFormats);
        var selectedIds = Split(profile.CustomFormatIds);
        var availableById = (customFormats ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var advanced = new List<LegacyPreferenceRuleTranslation>();
        var warnings = new List<string>();

        foreach (var id in selectedIds)
        {
            if (!availableById.TryGetValue(id, out var format))
            {
                advanced.Add(new LegacyPreferenceRuleTranslation(
                    id,
                    id,
                    null,
                    MediaPolicyCatalog.NormalizeMediaType(profile.MediaType),
                    0,
                    true,
                    string.Empty,
                    LegacyPreferenceRuleKind.Invalid,
                    null,
                    true,
                    "The quality profile references a custom format that is not present in the current inventory."));
                warnings.Add($"Custom format '{id}' is referenced but missing from the inventory.");
                continue;
            }

            if (!CustomFormatDefinitionMatcher.CanEvaluate(format.Conditions, profile.MediaType))
            {
                var explanation = string.IsNullOrWhiteSpace(format.Conditions)
                    ? "The custom format has no matcher conditions, so Deluno cannot prove whether a release matches it."
                    : "The custom format contains a condition Deluno cannot interpret completely, so it remains review-only rather than changing the rule.";
                advanced.Add(new LegacyPreferenceRuleTranslation(
                    format.Id,
                    format.Name,
                    format.TrashId,
                    MediaPolicyCatalog.NormalizeMediaType(profile.MediaType),
                    format.Score,
                    format.UpgradeAllowed,
                    format.Conditions,
                    LegacyPreferenceRuleKind.UnmappedAdvanced,
                    null,
                    true,
                    explanation));
                warnings.Add($"Custom format '{format.Name}' cannot be evaluated completely and needs review.");
            }
        }

        var plan = CreateQualityPlan(profile, selected, guidePackage);
        return new RuntimeQualityProfileCompilation(
            profile,
            plan,
            advanced,
            warnings,
            advanced.Count > 0);
    }

    /// <summary>
    /// The tiers a quality family has to be able to place, best first.
    ///
    /// <para>Not the same list as "which tiers may be grabbed". The allowed
    /// list is a gate on candidates and <see cref="Deluno.Integrations"/>'s
    /// decision engine enforces it directly; this list decides which installed
    /// files the plan can recognise at all. Building it from the allowed list
    /// alone meant a held file <em>better</em> than every allowed tier matched
    /// no level, read as below goal, and asked to be replaced by something
    /// worse - a profile allowing up to Bluray 1080p would have wanted to
    /// downgrade your Bluray 2160p file. So every catalogue tier at or above
    /// the cutoff is included, which is exactly the set the engine being
    /// replaced calls "cutoff met".</para>
    /// </summary>
    public static IReadOnlyList<string> QualityFamilyTiers(
        IReadOnlyList<string> normalizedAllowed,
        string cutoff)
    {
        if (normalizedAllowed.Count == 0)
        {
            return MediaPolicyCatalog.Current.QualityRanks
                .OrderByDescending(item => item.Value)
                .Select(item => item.Key)
                .ToArray();
        }

        var cutoffRank = MediaPolicyCatalog.Current.GetRank(cutoff);
        return normalizedAllowed
            .Concat([cutoff])
            .Concat(MediaPolicyCatalog.Current.QualityRanks
                .Where(item => item.Value >= cutoffRank)
                .Select(item => item.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(MediaPolicyCatalog.Current.GetRank)
            .ThenBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ReleasePreferencePlan CreateQualityPlan(
        string? mediaType,
        string? targetQuality,
        IReadOnlyList<string>? allowedQualities = null,
        bool upgradeUntilCutoff = true,
        string? id = null,
        string? version = null,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? guidePackage = null,
        IReadOnlyDictionary<string, string>? formatIntents = null,
        // #769: "Preferred language" as a Ranked/Required rule in this same
        // typed system, so a language answer explains itself the same way
        // every other typed preference does.
        ProfileLanguagePreference? languagePreference = null,
        // #767: already normalized by the caller (ProperRepackAcquisitionModes.Normalize).
        string properRepackAcquisitionMode = ProperRepackAcquisitionModes.PreferAndUpgrade)
    {
        guidePackage ??= GuidePackageCatalog.Current;
        var normalizedMediaType = MediaPolicyCatalog.NormalizeMediaType(mediaType);
        var cutoff = MediaPolicyCatalog.Current.NormalizeQuality(targetQuality)
            ?? MediaPolicyCatalog.Current.DefaultCutoffQuality;
        var normalizedAllowed = (allowedQualities ?? [])
            .Select(MediaPolicyCatalog.Current.NormalizeQuality)
            .Where(quality => !string.IsNullOrWhiteSpace(quality))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var qualityNames = QualityFamilyTiers(normalizedAllowed, cutoff);

        var levels = qualityNames
            .Select((quality, index) => new PreferenceFamilyLevel(
                Id: Slug(quality),
                Rank: index,
                TraitIds: [InstalledPreferenceEvaluationFactory.QualityTraitId(quality)]))
            .ToArray();

        var qualityFamily = new PreferenceFamily(
            Id: "quality",
            Dimension: "Quality",
            Order: 1,
            Intent: PreferenceIntent.Ranked,
            Levels: levels,
            TargetLevelId: Slug(cutoff),
            UpgradeDriving: upgradeUntilCutoff,
            Transient: false);

        // #769: a Ranked family orders candidates by the profile's preferred
        // languages whenever more than one is present in a search - a
        // tie-break, never a reason by itself to replace an installed file
        // (UpgradeDriving: false), because the same title rarely changes
        // language across releases. "Require" this language does not become a
        // RequiredTraitIds gate on the preferred trait: most legitimate
        // releases in a person's own language name no language at all, and a
        // hard gate treats that silence as absence and refuses them outright.
        // It becomes a forbidden gate on every *other* known language instead,
        // so a release explicitly tagged as a different language is refused
        // and everything else is left to the ranking.
        var normalizedLanguage = languagePreference?.Normalize();

        var guideFormats = guidePackage.CustomFormats
            .ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);
        var mappedTraits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var forbiddenTraits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var customFormatFamilies = new List<PreferenceFamily>();
        var sources = new List<PreferencePlanProvenance>();
        foreach (var format in customFormats ?? [])
        {
            if (string.IsNullOrWhiteSpace(format.Id)
                || !CustomFormatDefinitionMatcher.CanEvaluate(format.Conditions, normalizedMediaType))
            {
                continue;
            }

            guideFormats.TryGetValue(format.TrashId ?? string.Empty, out var guideFormat);
            var hasReviewedMapping = guideFormat?.MappingStatus == GuideMappingStatus.Reviewed;

            // #767: the guide ships Repack/Proper as ordinary custom formats
            // (see trash-guide-package.json), so the global Proper/Repack
            // choice acts here rather than as a second, competing scoring
            // path. "Do not prefer" drops the format from the plan entirely —
            // no family, no provenance — leaving the outcome to whatever
            // other Custom Format conditions the person has defined.
            var isRevisionFormat = hasReviewedMapping &&
                PreferenceTraitRegistry.Current.CanonicalizeIds(guideFormat!.MappedTraitIds)
                    .Any(traitId => traitId.StartsWith("release.revision.", StringComparison.OrdinalIgnoreCase));
            if (isRevisionFormat && properRepackAcquisitionMode == ProperRepackAcquisitionModes.DoNotPrefer)
            {
                continue;
            }

            // A rule is off until a person turns it on (the owner, 2026-09-19, #550).
            //
            // It used to take the guide's score as its answer when the profile
            // had none: 500 or more became "Strongly prefer", -10000 became
            // "Never", and a guide sync that moved a score changed what every
            // such profile did. On the rig that is how "WEB Tier 02" came to
            // hold every episode for review without anyone having chosen it.
            // Rules the guide files under "unwanted" or "safety" were forced to
            // "Never" even over an answer the person had given. The guide's view
            // is a suggestion the profile screen shows beside the rule
            // (ProfileFormatIntents.SuggestionFor); only a stated answer acts.
            var answer = formatIntents is not null && formatIntents.TryGetValue(format.Id, out var stated)
                ? ProfileFormatIntents.Normalize(stated)
                : ProfileFormatIntents.Off;

            var matchedTrait = CustomFormatPreferenceTraits.Matched(format.Id);
            var notMatchedTrait = CustomFormatPreferenceTraits.NotMatched(format.Id);
            var semanticTraits = hasReviewedMapping
                ? PreferenceTraitRegistry.Current.CanonicalizeIds(guideFormat!.MappedTraitIds)
                : [];

            sources.Add(new PreferencePlanProvenance(
                SourceKind: CustomFormatPreferenceTraits.SourceKind,
                SourceId: format.Id,
                SourceVersion: format.UpdatedUtc.ToUniversalTime().ToString("O"),
                OriginalScore: (hasReviewedMapping ? guideFormat!.OriginalScore : format.Score)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssignedScore: format.Score.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MappingId: hasReviewedMapping ? $"{guidePackage.Id}:{guideFormat!.TrashId}" : null,
                MappingVersion: CustomFormatPreferenceTraits.MappingVersion,
                Layer: "runtime-quality",
                MatcherDefinition: format.Conditions,
                MappedTraitIds: [matchedTrait, notMatchedTrait, .. semanticTraits],
                MatcherAny: !string.IsNullOrWhiteSpace(format.TrashId),
                RuleId: format.Id,
                RuleName: format.Name,
                ProfileIntent: answer));

            if (ProfileFormatIntents.Refuses(answer))
            {
                forbiddenTraits.Add(matchedTrait);
            }
            else
            {
                var preferMatch = answer is ProfileFormatIntents.Prefer or ProfileFormatIntents.StronglyPrefer;
                var ruleLevels = preferMatch
                    ? new[]
                    {
                        new PreferenceFamilyLevel("matched", 0, [matchedTrait]),
                        new PreferenceFamilyLevel("not-matched", 1, [notMatchedTrait])
                    }
                    : new[]
                    {
                        new PreferenceFamilyLevel("not-matched", 0, [notMatchedTrait]),
                        new PreferenceFamilyLevel("matched", 1, [matchedTrait])
                    };
                var drivesUpgrade = ProfileFormatIntents.DrivesUpgrade(answer) && format.UpgradeAllowed
                    // #767: still preferred among releases found in the same
                    // search (Intent stays Ranked below), just never the
                    // reason Deluno replaces an already-installed file on its
                    // own.
                    && !(isRevisionFormat && properRepackAcquisitionMode == ProperRepackAcquisitionModes.DoNotUpgradeAutomatically);
                customFormatFamilies.Add(new PreferenceFamily(
                    Id: CustomFormatPreferenceTraits.Family(format.Id),
                    Dimension: $"Custom rule · {format.Name}",
                    Order: customFormatFamilies.Count + 2,
                    Intent: answer == ProfileFormatIntents.DoNotCare
                        ? PreferenceIntent.Neutral
                        : drivesUpgrade ? PreferenceIntent.Ranked : PreferenceIntent.TieBreak,
                    Levels: ruleLevels,
                    TargetLevelId: drivesUpgrade ? "matched" : null,
                    UpgradeDriving: drivesUpgrade,
                    Transient: false));
            }

            foreach (var traitId in semanticTraits)
            {
                if (PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait)
                    && !trait.Transient)
                {
                    mappedTraits.Add(trait.Id);
                }
            }
        }

        var relationships = PreferenceTraitRegistry.Current.Relationships
            .Where(relationship => mappedTraits.Contains(relationship.FromTraitId)
                && PreferenceTraitRegistry.Current.TryResolve(relationship.ToTraitId, out _))
            .ToArray();
        foreach (var relationship in relationships)
        {
            if (PreferenceTraitRegistry.Current.TryResolve(relationship.ToTraitId, out var target)
                && !target.Transient)
            {
                mappedTraits.Add(target.Id);
            }
        }

        var mappedFamilies = new List<PreferenceFamily>();
        foreach (var group in mappedTraits
                     .Select(traitId => PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait)
                         ? trait
                         : null)
                     .Where(trait => trait is not null)
                     .Cast<PreferenceTraitDefinition>()
                     .GroupBy(trait => trait.Dimension, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var familyLevels = GuidePlanCompiler.OrderTraitLevels(
                group.Key,
                group.Select(trait => trait.Id));
            if (familyLevels.Count == 0) continue;

            mappedFamilies.Add(new PreferenceFamily(
                Id: $"guide.{Slug(group.Key)}",
                Dimension: group.Key,
                Order: mappedFamilies.Count + customFormatFamilies.Count + 2,
                Intent: PreferenceIntent.Neutral,
                Levels: familyLevels,
                TargetLevelId: null,
                UpgradeDriving: false,
                Transient: false));
        }

        // #769: built last, once every trait a selected custom format already
        // claims is known, so a language custom format and a profile-level
        // language preference never fight the validator over the same trait.
        // A trait a custom format already mapped keeps that family as its
        // owner; the preference only orders what is left.
        PreferenceFamily? languageFamily = null;
        if (normalizedLanguage is { IsEmpty: false })
        {
            var languageLevels = (normalizedLanguage.PreferredLanguages ?? [])
                .Select((code, index) => new PreferenceFamilyLevel(
                    Id: Slug(code),
                    Rank: index,
                    TraitIds: [$"audio.language.{code}"]))
                .Where(level => !mappedTraits.Contains(level.TraitIds[0])
                    && PreferenceTraitRegistry.Current.TryResolve(level.TraitIds[0], out _))
                .ToArray();
            if (languageLevels.Length > 0)
            {
                languageFamily = new PreferenceFamily(
                    Id: "language-preference",
                    Dimension: "audio.language",
                    Order: 0,
                    // TieBreak, not Ranked: this orders otherwise-comparable
                    // candidates in one search (CompareForSelection), but
                    // never drives replacing an installed file, because the
                    // same title rarely changes language across releases and
                    // "prefer" must not read as "upgrade required".
                    Intent: PreferenceIntent.TieBreak,
                    Levels: languageLevels,
                    TargetLevelId: null,
                    UpgradeDriving: false,
                    Transient: false);

                // "Require" this language does not become a RequiredTraitIds
                // gate on the preferred trait: most legitimate releases in a
                // person's own language name no language at all, and a hard
                // gate treats that silence as absence and refuses them
                // outright. It becomes a forbidden gate on every *other* known
                // language instead, so a release explicitly tagged as a
                // different language is refused and everything else is left
                // to the ranking above.
                if (normalizedLanguage.RequireLanguage)
                {
                    var mostPreferred = (normalizedLanguage.PreferredLanguages ?? [])[0];
                    foreach (var other in PreferenceTraitRegistry.Current.Traits
                                 .Where(trait => string.Equals(trait.Dimension, "audio.language", StringComparison.OrdinalIgnoreCase)
                                     && !mappedTraits.Contains(trait.Id)
                                     && !string.Equals(trait.Id, $"audio.language.{mostPreferred}", StringComparison.OrdinalIgnoreCase)))
                    {
                        forbiddenTraits.Add(other.Id);
                    }
                }
            }
        }

        var leadingFamilies = languageFamily is null
            ? (IReadOnlyList<PreferenceFamily>)[qualityFamily]
            : [languageFamily, qualityFamily];
        var persistentFamilies = leadingFamilies
            .Concat(customFormatFamilies)
            .Concat(mappedFamilies)
            .ToArray();
        // #646: a season search has no persistent family for "this is a whole
        // season in one file", so a pack and a same-quality single episode
        // scored identically on everything but size. Season-pack-ness is a
        // fact about the release name, not a quality signal, so it belongs
        // beside seeders as a transient tie-break - it decides between
        // otherwise-comparable candidates and never outranks quality or a
        // custom format. TV-only: a movie has no season to pack.
        var transientFamilies = normalizedMediaType == "tv"
            ? new[]
              {
                  CreateSeasonPackAvailabilityFamily(persistentFamilies.Length + 1),
                  CreateSeederAvailabilityFamily(persistentFamilies.Length + 2)
              }
            : [CreateSeederAvailabilityFamily(persistentFamilies.Length + 1)];
        var allFamilies = persistentFamilies
            .Concat(transientFamilies)
            .ToArray();
        var effectiveVersion = version
            ?? (sources.Count > 0
                ? $"{guidePackage.Version}:{guidePackage.Source.UpstreamRevision}"
                : $"{MediaPolicyCatalog.CurrentVersion}/typed-quality/v3");

        return new ReleasePreferencePlan(
            Id: id ?? $"runtime-quality/{normalizedMediaType}/{Slug(cutoff)}",
            Version: effectiveVersion,
            MediaType: normalizedMediaType,
            Families: allFamilies,
            ForbiddenTraitIds: GuidePlanCompiler.ExpandForbiddenTraits(forbiddenTraits),
            Relationships: relationships,
            // Transient families are always evaluated after the persistent
            // dimensions and therefore are not part of the persistent order
            // list. The evaluator appends them as the final tie-break stage.
            DimensionOrder: persistentFamilies.Select(family => family.Id).ToArray(),
            Scenario: "runtime quality policy",
            Provenance: sources.Count > 0 ? "deluno-quality-policy+trash-guides" : "deluno-quality-policy",
            Sources: sources);
    }

    /// <summary>
    /// The default acquisition tie-break is explicit in the typed plan. It is
    /// deliberately bucketed rather than exposing or summing a seeder score.
    /// </summary>
    public static PreferenceFamily CreateSeederAvailabilityFamily(int order)
        => new(
            Id: "transient.seeders",
            Dimension: "Acquisition confidence · seeder availability",
            Order: order,
            Intent: PreferenceIntent.TieBreak,
            Levels:
            [
                new PreferenceFamilyLevel("available", 0, ["transient.seeders.available"]),
                new PreferenceFamilyLevel("none", 1, ["transient.seeders.none"])
            ],
            TargetLevelId: null,
            UpgradeDriving: false,
            Transient: true);

    /// <summary>
    /// #646: prefers a whole-season pack over a same-or-comparable-quality
    /// single episode when both are offered by the same search. Placed ahead
    /// of <see cref="CreateSeederAvailabilityFamily"/> so a search that could
    /// hand one release to a download client instead of several does not lose
    /// that choice to a coin-flip on seed count; a candidate with no seeders
    /// is refused earlier regardless (ReleaseDecisionEngine.ScoreSeeders).
    /// </summary>
    public static PreferenceFamily CreateSeasonPackAvailabilityFamily(int order)
        => new(
            Id: "transient.season-pack",
            Dimension: "Release scope · whole-season pack",
            Order: order,
            Intent: PreferenceIntent.TieBreak,
            Levels:
            [
                new PreferenceFamilyLevel("whole", 0, ["transient.season-pack.whole"]),
                new PreferenceFamilyLevel("episode", 1, ["transient.season-pack.episode"])
            ],
            TargetLevelId: null,
            UpgradeDriving: false,
            Transient: true);

    private static IReadOnlyList<CustomFormatItem> SelectProfileFormats(
        QualityProfileItem profile,
        IReadOnlyList<CustomFormatItem>? customFormats)
    {
        var ids = Split(profile.CustomFormatIds);
        if (ids.Count == 0 || customFormats is null)
        {
            return [];
        }

        return customFormats
            .Where(item => ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<string> Split(string? value)
        => (value ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var slug = string.Join(string.Empty, chars)
            .Replace("--", "-", StringComparison.Ordinal)
            .Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
    }
}

public sealed record RuntimeQualityProfileCompilation(
    QualityProfileItem Profile,
    ReleasePreferencePlan Plan,
    IReadOnlyList<LegacyPreferenceRuleTranslation> AdvancedRules,
    IReadOnlyList<string> Warnings,
    bool RequiresReview);
