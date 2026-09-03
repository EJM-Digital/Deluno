using Deluno.Quality.Contracts;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// The classification used when an existing score-based rule is moved into a
/// typed plan. It is deliberately public so migration previews can explain
/// every row without pretending that a score carries semantics it never had.
/// </summary>
public enum LegacyPreferenceRuleKind
{
    ExactTyped,
    GuideMapped,
    OrderedFamilyCandidate,
    HardGateCandidate,
    TieBreakCandidate,
    AmbiguousOverlap,
    Conflicting,
    UnmappedAdvanced,
    Invalid
}

public sealed record LegacyPreferenceRuleTranslation(
    string RuleId,
    string Name,
    string? TrashId,
    string MediaType,
    int OriginalScore,
    bool UpgradeAllowed,
    string Conditions,
    LegacyPreferenceRuleKind Kind,
    PreferenceIntent? ProposedIntent,
    bool RequiresReview,
    string Explanation);

public sealed record LegacyReleasePreferenceTranslation(
    string SourceId,
    string SourceVersion,
    ReleasePreferencePlan Plan,
    IReadOnlyList<LegacyPreferenceRuleTranslation> AdvancedRules,
    IReadOnlyList<string> Warnings,
    bool RequiresReview);

/// <summary>
/// Converts the lossless parts of a legacy quality profile into the typed
/// release-preference contract. Quality tiers are the one exact ordered family
/// already owned by Deluno. Custom-format ids and scores are retained as
/// Advanced provenance until a reviewed guide mapping exists; numeric magnitude
/// is never used to invent required, forbidden, or upgrade-driving intent.
/// </summary>
public static class LegacyReleasePreferenceTranslator
{
    public const string MappingVersion = "legacy-score/v1";

    public static LegacyReleasePreferenceTranslation Translate(
        QualityProfileItem profile,
        IReadOnlyList<CustomFormatItem>? customFormats = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var warnings = new List<string>();
        var advanced = new List<LegacyPreferenceRuleTranslation>();
        var mediaType = MediaPolicyCatalog.NormalizeMediaType(profile.MediaType);
        var allowedNames = Split(profile.AllowedQualities)
            .Select(MediaPolicyCatalog.Current.NormalizeQuality)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cutoff = MediaPolicyCatalog.Current.NormalizeQuality(profile.CutoffQuality);
        if (cutoff is null)
        {
            warnings.Add($"The quality profile cutoff '{profile.CutoffQuality}' is not in Deluno's current tier vocabulary.");
        }

        // The same rule the runtime plan uses, not a second copy of it: a
        // migrated plan that could place fewer installed files than the
        // running one would change decisions the moment it activated.
        var allTiers = (cutoff is null
                ? allowedNames
                : ReleasePreferencePlanFactory.QualityFamilyTiers(allowedNames, cutoff))
            .Select(name => new
            {
                Name = name,
                Rank = MediaPolicyCatalog.Current.GetRank(name)
            })
            .Where(item => item.Rank > 0)
            .OrderByDescending(item => item.Rank)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allTiers.Length == 0)
        {
            warnings.Add("No recognized quality tier could be translated; the legacy profile remains review-only.");
        }

        var levels = allTiers
            .Select((tier, index) => new PreferenceFamilyLevel(
                Id: Slug(tier.Name),
                Rank: index,
                TraitIds: [$"quality.{Slug(tier.Name)}"]))
            .ToArray();
        var targetLevelId = cutoff is null ? null : Slug(cutoff);
        var qualityFamily = new PreferenceFamily(
            Id: "quality",
            Dimension: "Quality",
            Order: 1,
            Intent: PreferenceIntent.Ranked,
            Levels: levels,
            TargetLevelId: targetLevelId,
            UpgradeDriving: profile.UpgradeUntilCutoff && targetLevelId is not null,
            Transient: false);

        var sources = new List<PreferencePlanProvenance>
        {
            new(
                SourceKind: "deluno-quality-profile",
                SourceId: profile.Id,
                SourceVersion: profile.UpdatedUtc.ToUniversalTime().ToString("O"),
                Layer: "library-or-title")
        };

        var selectedIds = Split(profile.CustomFormatIds);
        var formatsById = (customFormats ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var formatId in selectedIds)
        {
            if (!formatsById.TryGetValue(formatId, out var format))
            {
                advanced.Add(new LegacyPreferenceRuleTranslation(
                    RuleId: formatId,
                    Name: formatId,
                    TrashId: null,
                    MediaType: mediaType,
                    OriginalScore: 0,
                    UpgradeAllowed: true,
                    Conditions: string.Empty,
                    Kind: LegacyPreferenceRuleKind.Invalid,
                    ProposedIntent: null,
                    RequiresReview: true,
                    Explanation: "The quality profile references a custom format that is not present in the current inventory."));
                warnings.Add($"Custom format '{formatId}' is referenced but missing from the inventory.");
                continue;
            }

            var explanation = format.UpgradeAllowed
                ? "The matcher and original score are preserved as Advanced input; the score does not become a typed upgrade value."
                : "UpgradeAllowed is preserved, but the opaque matcher still needs an owner decision before it can become a required or forbidden fact.";
            advanced.Add(new LegacyPreferenceRuleTranslation(
                RuleId: format.Id,
                Name: format.Name,
                TrashId: format.TrashId,
                MediaType: mediaType,
                OriginalScore: format.Score,
                UpgradeAllowed: format.UpgradeAllowed,
                Conditions: format.Conditions,
                Kind: string.IsNullOrWhiteSpace(format.Conditions)
                    ? LegacyPreferenceRuleKind.Invalid
                    : LegacyPreferenceRuleKind.UnmappedAdvanced,
                ProposedIntent: null,
                RequiresReview: true,
                Explanation: explanation));
            sources.Add(new PreferencePlanProvenance(
                SourceKind: string.IsNullOrWhiteSpace(format.TrashId) ? "deluno-custom-format" : "trash-custom-format",
                SourceId: format.TrashId ?? format.Id,
                SourceVersion: format.UpdatedUtc.ToUniversalTime().ToString("O"),
                OriginalScore: format.Score.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssignedScore: format.Score.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MappingVersion: MappingVersion,
                Layer: "legacy-advanced",
                MatcherDefinition: format.Conditions,
                MatcherAny: !string.IsNullOrWhiteSpace(format.TrashId)));
        }

        if (cutoff is not null && !allowedNames.Contains(cutoff, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"Cutoff '{cutoff}' was not in the legacy allowed tier list; it was retained as the typed Stop when target.");
        }

        var plan = new ReleasePreferencePlan(
            Id: $"legacy-quality-profile/{profile.Id}",
            Version: $"{MappingVersion}/{profile.UpdatedUtc.ToUniversalTime():yyyyMMddHHmmssfffffff}",
            MediaType: mediaType,
            Families: [qualityFamily],
            DimensionOrder: ["quality"],
            Scenario: profile.Name,
            Provenance: "legacy-score-profile",
            Sources: sources);

        ReleasePreferencePlanValidator.ThrowIfInvalid(plan);
        return new LegacyReleasePreferenceTranslation(
            profile.Id,
            profile.UpdatedUtc.ToUniversalTime().ToString("O"),
            plan,
            advanced,
            warnings,
            RequiresReview: advanced.Any(rule => rule.RequiresReview) || cutoff is null);
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
