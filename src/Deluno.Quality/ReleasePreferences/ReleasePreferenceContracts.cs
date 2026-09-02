using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// The meaning a preference has in a plan. These are deliberately typed: a
/// numeric score cannot distinguish "must have" from "prefer" or explain why
/// an otherwise attractive release was rejected.
/// </summary>
public enum PreferenceIntent
{
    Required,
    Forbidden,
    Ranked,
    TieBreak,
    Neutral
}

public enum PreferenceFactState
{
    Present,
    Absent,
    Unknown,
    Conflicting
}

public enum PreferenceEvaluationStatus
{
    Missing,
    NeedsReview,
    BelowGoal,
    MeetsPlan
}

/// <summary>
/// The typed comparison results named by the normative release-preference
/// contract (#354 section 1). <see cref="Rejected"/> means a hard gate
/// failed, and is the only value that may be presented to the owner as a
/// rule violation. <see cref="CurrentBetter"/> is deliberately separate: a
/// release that passes every gate but is simply worse than the installed
/// file was not rejected by anything, so saying that it was is untrue.
/// </summary>
public enum PreferenceCandidateStatus
{
    Rejected,
    NeedsReview,
    Acceptable,
    BestMatchNow,
    Equivalent,
    CurrentBetter,
    Upgrade
}

public enum PreferenceRelationshipKind
{
    Implies,
    Requires,
    Subsumes,
    CoreOf,
    CarriedBy,
    Incompatible
}

public enum PreferenceEvidenceModel
{
    OpenWorld,
    ClosedWorld
}

public sealed record PreferenceEvidence
{
    [JsonConstructor]
    public PreferenceEvidence(
        string? Source,
        double? Confidence = null,
        string? Detail = null,
        string? DetectionRule = null,
        string? DetectionVersion = null,
        PreferenceEvidenceModel Model = PreferenceEvidenceModel.OpenWorld)
    {
        this.Source = Source?.Trim() ?? string.Empty;
        this.Confidence = Confidence;
        this.Detail = Detail;
        this.DetectionRule = DetectionRule;
        this.DetectionVersion = DetectionVersion;
        this.Model = Model;
    }

    public string Source { get; init; }
    public double? Confidence { get; init; }
    public string? Detail { get; init; }
    public string? DetectionRule { get; init; }
    public string? DetectionVersion { get; init; }
    public PreferenceEvidenceModel Model { get; init; }
}

/// <summary>
/// The immutable provenance attached to a compiled plan. A display name is not
/// enough to audit a preference after a guide or profile changes, so callers
/// can retain the source identity and version alongside the effective plan.
/// </summary>
public sealed record PreferencePlanProvenance(
    string SourceKind,
    string SourceId,
    string SourceVersion,
    string? OriginalScore = null,
    string? AssignedScore = null,
    string? MappingId = null,
    string? MappingVersion = null,
    string? Layer = null,
    /// <summary>
    /// The matcher used when this source was compiled. Keeping it beside the
    /// typed mapping means an immutable plan can re-evaluate an installed
    /// file after the mutable guide/custom-format inventory changes.
    /// </summary>
    string? MatcherDefinition = null,
    IReadOnlyList<string>? MappedTraitIds = null,
    /// <summary>Whether matcher conditions are alternatives rather than an AND.</summary>
    bool MatcherAny = false);

/// <summary>
/// One normalized fact. Missing facts are treated as unknown by the evaluator;
/// callers must not silently turn an unobserved trait into an absent trait.
/// </summary>
public sealed record PreferenceFact
{
    [JsonConstructor]
    public PreferenceFact(
        string? traitId,
        PreferenceFactState state,
        PreferenceEvidence? evidence = null)
    {
        TraitId = traitId?.Trim() ?? string.Empty;
        State = state;
        Evidence = evidence;
    }

    public string TraitId { get; init; }

    public PreferenceFactState State { get; init; }

    public PreferenceEvidence? Evidence { get; init; }

    [JsonIgnore]
    public string NormalizedTraitId => TraitId.Trim().ToLowerInvariant();
}

/// <summary>An equal alternative within one ordered preference family.</summary>
public sealed record PreferenceFamilyLevel(
    string Id,
    int Rank,
    IReadOnlyList<string> TraitIds)
{
    [JsonIgnore]
    public IReadOnlyList<string> NormalizedTraitIds
        => (TraitIds ?? [])
            .Where(trait => !string.IsNullOrWhiteSpace(trait))
            .Select(trait => trait.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(trait => trait, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// An ordered dimension such as quality, HDR, audio format or edition.
/// Levels are ordered best-first inside the family (lower rank is better);
/// families are compared in plan order, not by summing their ranks.
/// </summary>
public sealed record PreferenceFamily(
    string Id,
    string Dimension,
    int Order,
    PreferenceIntent Intent,
    IReadOnlyList<PreferenceFamilyLevel> Levels,
    string? TargetLevelId = null,
    bool UpgradeDriving = true,
    bool Transient = false)
{
    [JsonIgnore]
    public IReadOnlyList<PreferenceFamilyLevel> OrderedLevels
        => (Levels ?? [])
            .Where(level => level is not null)
            .OrderBy(level => level.Rank)
            .ThenBy(level => level.Id, StringComparer.Ordinal)
            .ToArray();

    [JsonIgnore]
    public PreferenceFamilyLevel? TargetLevel
        => TargetLevelId is null
            ? null
            : OrderedLevels.FirstOrDefault(level =>
                string.Equals(level.Id, TargetLevelId, StringComparison.OrdinalIgnoreCase));
}

public sealed record PreferenceRelationship(
    string FromTraitId,
    string ToTraitId,
    PreferenceRelationshipKind Kind);

/// <summary>
/// A compatibility group is an AND-of-OR-of-AND gate. Every group must be
/// satisfied; one alternative must be selected within the group; and every
/// trait in that alternative must be proven present. This preserves the fact
/// that a device may support several equivalent values in one dimension while
/// still requiring all dimensions of one device capability path to match.
/// </summary>
public sealed record PreferenceCompatibilityGroup(
    string Id,
    IReadOnlyList<IReadOnlyList<string>> Alternatives,
    /// <summary>
    /// Optional ordered preference for alternatives in this compatibility
    /// group. A lower value is preferred. When omitted, alternatives are
    /// unordered hard-gate paths. Playback primary/fallback goals use this to
    /// prefer a primary-device path without weakening the compatibility gate.
    /// </summary>
    IReadOnlyList<int>? AlternativeRanks = null);

/// <summary>
/// Versioned, hashable policy input for release comparison. The old quality
/// and custom-format scores can be adapted into this shape, but they are not
/// the source of truth for a typed plan.
/// </summary>
public sealed record ReleasePreferencePlan(
    string Id,
    string Version,
    string MediaType,
    IReadOnlyList<PreferenceFamily> Families,
    IReadOnlyList<string>? RequiredTraitIds = null,
    IReadOnlyList<string>? ForbiddenTraitIds = null,
    IReadOnlyList<PreferenceRelationship>? Relationships = null,
    IReadOnlyList<string>? DimensionOrder = null,
    string? CompatibilityScope = null,
    string? Scenario = null,
    string? Provenance = null,
    IReadOnlyDictionary<string, string>? Overrides = null,
    IReadOnlyList<PreferencePlanProvenance>? Sources = null,
    /// <summary>
    /// Hard-gate groups where at least one trait must be proven present. This
    /// is the typed OR counterpart to <see cref="RequiredTraitIds"/>, which is
    /// deliberately AND-only.
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>>? RequiredAnyTraitGroups = null,
    /// <summary>
    /// Device or source compatibility paths. Every group must pass one of its
    /// alternatives, and every trait in the selected alternative must pass.
    /// This is deliberately separate from <see cref="RequiredAnyTraitGroups"/>
    /// so a release cannot satisfy one device's video gate with another
    /// device's audio gate.
    /// </summary>
    IReadOnlyList<PreferenceCompatibilityGroup>? CompatibilityGroups = null)
{
    [JsonIgnore]
    public string PlanHash => ReleasePreferencePlanHash.Compute(this);

    [JsonIgnore]
    public IReadOnlyList<PreferenceFamily> OrderedFamilies
    {
        get
        {
            var dimensionOrder = (DimensionOrder ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select((id, index) => new { Id = id.Trim(), Index = index })
                .ToDictionary(item => item.Id, item => item.Index, StringComparer.OrdinalIgnoreCase);

            return (Families ?? [])
                .Where(family => family is not null)
                .OrderBy(family => dimensionOrder.TryGetValue(family.Id, out _) ? 0 : 1)
                .ThenBy(family => dimensionOrder.GetValueOrDefault(family.Id, int.MaxValue))
                .ThenBy(family => family.Order)
                .ThenBy(family => family.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }
}

public sealed record PreferenceFamilyEvaluation
{
    [JsonConstructor]
    public PreferenceFamilyEvaluation(
        string? familyId,
        PreferenceIntent intent,
        PreferenceFactState state,
        string? selectedLevelId,
        int selectedRank,
        string? targetLevelId,
        bool targetMet,
        bool upgradeDriving,
        bool transient,
        string? explanation)
    {
        FamilyId = familyId?.Trim() ?? string.Empty;
        Intent = intent;
        State = state;
        SelectedLevelId = selectedLevelId;
        SelectedRank = selectedRank;
        TargetLevelId = targetLevelId;
        TargetMet = targetMet;
        UpgradeDriving = upgradeDriving;
        Transient = transient;
        Explanation = explanation ?? string.Empty;
    }

    public string FamilyId { get; init; }
    public PreferenceIntent Intent { get; init; }
    public PreferenceFactState State { get; init; }
    public string? SelectedLevelId { get; init; }
    public int SelectedRank { get; init; }
    public string? TargetLevelId { get; init; }
    public bool TargetMet { get; init; }
    public bool UpgradeDriving { get; init; }
    public bool Transient { get; init; }
    public string Explanation { get; init; }
}

/// <summary>
/// The compatibility path selected by an evaluation. The rank is only
/// populated for groups that explicitly order their alternatives; ordinary
/// compatibility groups remain unordered hard gates.
/// </summary>
public sealed record PreferenceCompatibilityEvaluation(
    string GroupId,
    PreferenceFactState State,
    int? SelectedAlternativeRank,
    string Explanation);

public sealed record PreferenceEvaluation
{
    [JsonConstructor]
    public PreferenceEvaluation(
        string? planId,
        string? planVersion,
        string? planHash,
        PreferenceEvaluationStatus status,
        bool hardGatesPassed,
        bool targetsMet,
        IReadOnlyList<PreferenceFamilyEvaluation>? families,
        IReadOnlyList<string>? reasons,
        IReadOnlyList<PreferenceCompatibilityEvaluation>? compatibility = null)
    {
        PlanId = planId?.Trim() ?? string.Empty;
        PlanVersion = planVersion?.Trim() ?? string.Empty;
        PlanHash = planHash?.Trim() ?? string.Empty;
        Status = status;
        HardGatesPassed = hardGatesPassed;
        TargetsMet = targetsMet;
        Families = families ?? [];
        Reasons = reasons ?? [];
        Compatibility = compatibility ?? [];
    }

    public string PlanId { get; init; }
    public string PlanVersion { get; init; }
    public string PlanHash { get; init; }
    public PreferenceEvaluationStatus Status { get; init; }
    public bool HardGatesPassed { get; init; }
    public bool TargetsMet { get; init; }
    public IReadOnlyList<PreferenceFamilyEvaluation> Families { get; init; }
    public IReadOnlyList<string> Reasons { get; init; }
    public IReadOnlyList<PreferenceCompatibilityEvaluation> Compatibility { get; init; }
}

/// <summary>
/// The durable evidence used for an installed-file decision. This is kept
/// beside the file identity rather than on a wanted row so a title can retain
/// prior plan versions for audit and rollback. The JSON representation is
/// produced by <see cref="ReleasePreferenceSnapshotCodec"/> and is stable
/// for the same inputs.
/// </summary>
public sealed record PreferenceEvaluationSnapshot(
    string MediaId,
    string? LibraryId,
    string FileIdentity,
    string? FilePath,
    long? FileSizeBytes,
    string PlanId,
    string PlanVersion,
    string PlanHash,
    IReadOnlyList<PreferenceFact> Facts,
    PreferenceEvaluation Evaluation,
    IReadOnlyList<string> MatchedRuleIds,
    DateTimeOffset EvaluatedUtc,
    string? Source = null);

/// <summary>
/// Canonical serialization for persisted release-preference evidence. Facts,
/// reasons and matched rule ids are sorted because database row order must not
/// change an evaluation snapshot or its audit hash after restart.
/// </summary>
public static class ReleasePreferenceSnapshotCodec
{
    private static JsonSerializerOptions JsonOptions => ReleasePreferenceJson.Options;

    public static string Serialize(PreferenceEvaluationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(Canonicalize(snapshot), JsonOptions);
    }

    public static PreferenceEvaluationSnapshot Deserialize(string json)
        => JsonSerializer.Deserialize<PreferenceEvaluationSnapshot>(json, JsonOptions)
            ?? throw new JsonException("The preference evaluation snapshot was empty.");

    private static PreferenceEvaluationSnapshot Canonicalize(PreferenceEvaluationSnapshot snapshot)
    {
        var facts = (snapshot.Facts ?? [])
            .OrderBy(fact => fact.NormalizedTraitId, StringComparer.Ordinal)
            .ThenBy(fact => fact.State)
            .ThenBy(fact => fact.Evidence?.Source ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(fact => fact.Evidence?.Confidence)
            .ThenBy(fact => fact.Evidence?.Detail ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(fact => fact.Evidence?.DetectionRule ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(fact => fact.Evidence?.DetectionVersion ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(fact => fact.Evidence?.Model)
            .ToArray();
        var matchedRuleIds = (snapshot.MatchedRuleIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var evaluation = snapshot.Evaluation with
        {
            Reasons = (snapshot.Evaluation.Reasons ?? [])
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToArray(),
            Families = (snapshot.Evaluation.Families ?? [])
                .OrderBy(family => family.FamilyId, StringComparer.Ordinal)
                .ToArray(),
            Compatibility = (snapshot.Evaluation.Compatibility ?? [])
                .OrderBy(item => item.GroupId, StringComparer.Ordinal)
                .ThenBy(item => item.State)
                .ThenBy(item => item.SelectedAlternativeRank)
                .ThenBy(item => item.Explanation, StringComparer.Ordinal)
                .ToArray()
        };

        return snapshot with
        {
            Facts = facts,
            Evaluation = evaluation,
            MatchedRuleIds = matchedRuleIds
        };
    }
}

public sealed record PreferenceComparison(
    string PlanId,
    string PlanVersion,
    string PlanHash,
    PreferenceCandidateStatus Status,
    bool PersistentImprovement,
    bool Regressed,
    bool Equivalent,
    string? DecisiveFamilyId,
    IReadOnlyList<string> Reasons,
    PreferenceEvaluation Current,
    PreferenceEvaluation Candidate,
    /// <summary>
    /// The first upgrade-driving family whose current value was below its
    /// explicit target and which the candidate improves. This is separate
    /// from <see cref="DecisiveFamilyId"/> because lexicographic comparison
    /// may be decided by an above-target higher-priority family while a
    /// lower-priority family supplies the persistent reason an installed file
    /// is eligible for replacement.
    /// </summary>
    string? PersistentImprovementFamilyId = null);

public static class ReleasePreferencePlanValidator
{
    public static IReadOnlyList<string> Validate(ReleasePreferencePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.Id)) errors.Add("Plan id is required.");
        if (string.IsNullOrWhiteSpace(plan.Version)) errors.Add("Plan version is required.");
        if (string.IsNullOrWhiteSpace(plan.MediaType)) errors.Add("Plan media type is required.");

        var familyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var traitOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var traitLevels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (plan.Families is null)
            errors.Add("Plan families are required.");
        var families = plan.Families ?? [];
        foreach (var family in families)
        {
            if (family is null)
            {
                errors.Add("Every preference family needs a definition.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(family.Id))
            {
                errors.Add("Every preference family needs an id.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(family.Dimension))
                errors.Add($"Family '{family.Id}' needs a dimension.");

            if (!Enum.IsDefined(family.Intent))
                errors.Add($"Family '{family.Id}' has an unknown preference intent.");

            if (family.Levels is null || family.Levels.Count == 0)
            {
                errors.Add($"Family '{family.Id}' must declare at least one level.");
                continue;
            }

            if (!familyIds.Add(family.Id))
                errors.Add($"Preference family '{family.Id}' is declared more than once.");

            var levelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var level in family.Levels)
            {
                if (level is null)
                {
                    errors.Add($"Family '{family.Id}' contains an empty level definition.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(level.Id))
                    errors.Add($"Family '{family.Id}' contains a level without an id.");
                else if (!levelIds.Add(level.Id))
                    errors.Add($"Family '{family.Id}' declares level '{level.Id}' more than once.");

                if (level.NormalizedTraitIds.Count == 0)
                    errors.Add($"Family '{family.Id}' level '{level.Id}' must declare at least one trait.");

                foreach (var traitId in level.NormalizedTraitIds)
                {
                    if (traitOwners.TryGetValue(traitId, out var owner) && !string.Equals(owner, family.Id, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"Trait '{traitId}' is counted by both '{owner}' and '{family.Id}'.");
                    else
                        traitOwners[traitId] = family.Id;

                    if (traitLevels.TryGetValue(traitId, out var previousLevel) && !string.Equals(previousLevel, level.Id, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"Trait '{traitId}' appears in multiple levels of family '{family.Id}'.");
                    else
                        traitLevels[traitId] = level.Id;
                }
            }

            if (family.TargetLevelId is not null && family.TargetLevel is null)
                errors.Add($"Family '{family.Id}' targets unknown level '{family.TargetLevelId}'.");

            if (family.UpgradeDriving && family.Intent == PreferenceIntent.Ranked && family.TargetLevel is null)
                errors.Add($"Upgrade-driving ranked family '{family.Id}' must declare an explicit stop-when target.");
            if (family.UpgradeDriving && family.Intent is PreferenceIntent.TieBreak or PreferenceIntent.Neutral)
                errors.Add($"Family '{family.Id}' cannot use {family.Intent} intent to drive upgrades.");
            if (family.Intent is PreferenceIntent.Required or PreferenceIntent.Forbidden)
                errors.Add($"Family '{family.Id}' must express {family.Intent} as a plan hard gate, not a family.");
            if (family.Transient && family.UpgradeDriving)
                errors.Add($"Transient family '{family.Id}' cannot drive persistent upgrades.");
        }

        var required = (plan.RequiredTraitIds ?? [])
            .Where(trait => !string.IsNullOrWhiteSpace(trait))
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forbidden = (plan.ForbiddenTraitIds ?? [])
            .Where(trait => !string.IsNullOrWhiteSpace(trait))
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var traitId in required.Intersect(forbidden, StringComparer.OrdinalIgnoreCase))
            errors.Add($"Trait '{traitId}' cannot be both required and forbidden.");

        var requiredAnyGroups = plan.RequiredAnyTraitGroups ?? [];
        foreach (var group in requiredAnyGroups)
        {
            if (group is null || group.Count == 0 || group.All(string.IsNullOrWhiteSpace))
            {
                errors.Add("Every required-any compatibility group must contain at least one trait.");
            }
        }

        var compatibilityGroups = plan.CompatibilityGroups ?? [];
        var compatibilityGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in compatibilityGroups)
        {
            if (group is null)
            {
                errors.Add("Every compatibility group needs a definition.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(group.Id))
                errors.Add("Every compatibility group needs an id.");
            else if (!compatibilityGroupIds.Add(group.Id))
                errors.Add($"Compatibility group '{group.Id}' is declared more than once.");

            if (group.Alternatives is null || group.Alternatives.Count == 0)
            {
                errors.Add($"Compatibility group '{group.Id}' must declare at least one alternative.");
                continue;
            }

            if (group.AlternativeRanks is not null)
            {
                if (group.AlternativeRanks.Count != group.Alternatives.Count)
                {
                    errors.Add($"Compatibility group '{group.Id}' must declare one alternative rank per alternative.");
                }
                else if (group.AlternativeRanks.Any(rank => rank < 0))
                {
                    errors.Add($"Compatibility group '{group.Id}' cannot declare a negative alternative rank.");
                }
            }

            foreach (var alternative in group.Alternatives)
            {
                if (alternative is null || alternative.Count == 0 || alternative.All(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"Compatibility group '{group.Id}' contains an empty alternative.");
                }
                else
                {
                    AddIncompatibleTraitErrors(
                        alternative,
                        $"compatibility group '{group.Id}'",
                        errors,
                        plan.Relationships);
                }
            }
        }

        // Hard-gate traits intentionally do not have to be ranked families.
        // Compatibility and safety facts often exist only as gates, and
        // forcing them into an ordered family would make the same trait count
        // as both a gate and a preference.
        var traitIds = traitOwners.Keys
            .Concat(required)
            .Concat(forbidden)
            .Concat(requiredAnyGroups.Where(group => group is not null).SelectMany(group => group))
            .Concat(compatibilityGroups
                .Where(group => group is not null)
                .SelectMany(group => group.Alternatives ?? [])
                .Where(alternative => alternative is not null)
                .SelectMany(alternative => alternative))
            .Where(trait => !string.IsNullOrWhiteSpace(trait))
            .Select(trait => trait.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relationships = plan.Relationships ?? [];
        var relationshipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in relationships)
        {
            if (relationship is null)
            {
                errors.Add("Every preference relationship needs a definition.");
                continue;
            }

            var from = Normalize(relationship.FromTraitId);
            var to = Normalize(relationship.ToTraitId);
            if (!Enum.IsDefined(relationship.Kind))
                errors.Add($"Preference relationship '{from} -> {to}' has an unknown relationship kind.");
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                errors.Add("Preference relationships need non-empty source and target traits.");
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Preference relationship '{from} -> {to}' cannot refer to the same trait.");
            if (!traitIds.Contains(from) || !traitIds.Contains(to))
                errors.Add($"Relationship '{relationship.FromTraitId} -> {relationship.ToTraitId}' refers to an undeclared trait.");

            var relationshipKey = $"{from}|{to}|{relationship.Kind}";
            if (!relationshipKeys.Add(relationshipKey))
                errors.Add($"Preference relationship '{relationship.FromTraitId} -> {relationship.ToTraitId}' ({relationship.Kind}) is declared more than once.");
        }

        foreach (var relationship in relationships.Where(relationship => relationship is not null))
        {
            var from = Normalize(relationship.FromTraitId);
            var to = Normalize(relationship.ToTraitId);
            if (relationship.Kind == PreferenceRelationshipKind.Incompatible
                && required.Contains(from)
                && required.Contains(to))
            {
                errors.Add($"Required traits '{from}' and '{to}' are incompatible.");
            }

            if (relationship.Kind is PreferenceRelationshipKind.Implies
                or PreferenceRelationshipKind.Requires
                or PreferenceRelationshipKind.Subsumes
                or PreferenceRelationshipKind.CoreOf
                or PreferenceRelationshipKind.CarriedBy)
            {
                if (required.Contains(from) && forbidden.Contains(to))
                {
                    errors.Add($"Required trait '{from}' implies or carries forbidden trait '{to}'.");
                }
            }
        }

        var graph = relationships
            .Where(relationship => relationship.Kind is PreferenceRelationshipKind.Implies
                or PreferenceRelationshipKind.Requires
                or PreferenceRelationshipKind.Subsumes
                or PreferenceRelationshipKind.CoreOf
                or PreferenceRelationshipKind.CarriedBy)
            .GroupBy(relationship => Normalize(relationship.FromTraitId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(relationship => Normalize(relationship.ToTraitId)).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        if (HasCycle(graph))
            errors.Add("Implication, requirement, and subsumption relationships must be acyclic.");

        if (plan.DimensionOrder is { } dimensions)
        {
            var knownFamilies = familyIds;
            var seenDimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dimension in dimensions)
            {
                var normalizedDimension = dimension?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(normalizedDimension))
                {
                    errors.Add("Plan dimension order cannot contain an empty family id.");
                    continue;
                }

                if (!seenDimensions.Add(normalizedDimension))
                    errors.Add($"Dimension '{normalizedDimension}' is listed more than once in the plan order.");
                if (!knownFamilies.Contains(normalizedDimension))
                    errors.Add($"Plan dimension order refers to unknown family '{normalizedDimension}'.");
            }

            foreach (var family in families.Where(family => !family.Transient))
            {
                if (!seenDimensions.Contains(family.Id))
                    errors.Add($"Plan dimension order is missing family '{family.Id}'.");
            }

            foreach (var family in families.Where(family => family.Transient))
            {
                if (seenDimensions.Contains(family.Id))
                    errors.Add($"Transient family '{family.Id}' cannot be part of the persistent dimension order.");
            }
        }

        // The registry is the finite vocabulary boundary. Unknown observed
        // facts remain valid evidence, but a plan must never persist an alias
        // or an unreviewed free-form trait as a decision input.
        errors.AddRange(PreferenceTraitRegistry.Current.ValidatePlan(plan));

        return errors;
    }

    private static void AddIncompatibleTraitErrors(
        IEnumerable<string> traits,
        string label,
        ICollection<string> errors,
        IEnumerable<PreferenceRelationship>? planRelationships = null)
    {
        var set = traits
            .Where(trait => !string.IsNullOrWhiteSpace(trait))
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in PreferenceTraitRegistry.Current.Relationships
                     .Concat(planRelationships ?? [])
                     .GroupBy(relationship => $"{Normalize(relationship.FromTraitId)}|{Normalize(relationship.ToTraitId)}|{relationship.Kind}", StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .Where(relationship => relationship.Kind == PreferenceRelationshipKind.Incompatible
                         && set.Contains(relationship.FromTraitId)
                         && set.Contains(relationship.ToTraitId)))
        {
            errors.Add($"The {label} contains incompatible traits '{relationship.FromTraitId}' and '{relationship.ToTraitId}'.");
        }
    }

    public static void ThrowIfInvalid(ReleasePreferencePlan plan)
    {
        var errors = Validate(plan);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(plan));
    }

    private static bool HasCycle(IReadOnlyDictionary<string, string[]> graph)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string node)
        {
            if (!visiting.Add(node)) return true;
            if (visited.Contains(node))
            {
                visiting.Remove(node);
                return false;
            }

            foreach (var child in graph.GetValueOrDefault(node, []))
            {
                if (Visit(child)) return true;
            }

            visiting.Remove(node);
            visited.Add(node);
            return false;
        }

        return graph.Keys.Any(Visit);
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

internal static class ReleasePreferencePlanHash
{
    public static string Compute(ReleasePreferencePlan plan)
    {
        var builder = new StringBuilder();
        Append(builder, plan.Id);
        Append(builder, plan.Version);
        Append(builder, plan.MediaType);
        Append(builder, plan.CompatibilityScope);
        Append(builder, plan.Scenario);
        Append(builder, plan.Provenance);

        foreach (var source in (plan.Sources ?? [])
                     .OrderBy(item => Normalize(item.SourceKind), StringComparer.Ordinal)
                     .ThenBy(item => Normalize(item.SourceId), StringComparer.Ordinal)
                     .ThenBy(item => Normalize(item.SourceVersion), StringComparer.Ordinal)
                     .ThenBy(item => Normalize(item.MappingId ?? string.Empty), StringComparer.Ordinal)
                     .ThenBy(item => Normalize(item.MappingVersion ?? string.Empty), StringComparer.Ordinal)
                     .ThenBy(item => Normalize(item.Layer ?? string.Empty), StringComparer.Ordinal)
                     .ThenBy(item => Normalize(item.MatcherDefinition ?? string.Empty), StringComparer.Ordinal)
                     .ThenBy(item => item.MatcherAny)
                     .ThenBy(item => Normalize(item.OriginalScore ?? string.Empty), StringComparer.Ordinal)
                     .ThenBy(item => Normalize(item.AssignedScore ?? string.Empty), StringComparer.Ordinal)
                     .ThenBy(item => string.Join("|", (item.MappedTraitIds ?? [])
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Select(Normalize)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(id => id, StringComparer.Ordinal))))
        {
            Append(builder, "source:" + Normalize(source.SourceKind));
            Append(builder, Normalize(source.SourceId));
            Append(builder, Normalize(source.SourceVersion));
            Append(builder, Normalize(source.OriginalScore ?? string.Empty));
            Append(builder, Normalize(source.AssignedScore ?? string.Empty));
            Append(builder, Normalize(source.MappingId ?? string.Empty));
            Append(builder, Normalize(source.MappingVersion ?? string.Empty));
            Append(builder, Normalize(source.Layer ?? string.Empty));
            Append(builder, Normalize(source.MatcherDefinition ?? string.Empty));
            Append(builder, source.MatcherAny.ToString());
            foreach (var traitId in (source.MappedTraitIds ?? [])
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Select(Normalize)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                Append(builder, "mapped-trait:" + traitId);
            }
        }

        foreach (var dimension in (plan.DimensionOrder ?? []).Select(Normalize))
            Append(builder, "dimension:" + dimension);

        foreach (var family in plan.OrderedFamilies)
        {
            Append(builder, family.Id);
            Append(builder, family.Dimension);
            Append(builder, family.Order.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, family.Intent.ToString());
            Append(builder, family.TargetLevelId);
            Append(builder, family.UpgradeDriving.ToString());
            Append(builder, family.Transient.ToString());
            foreach (var level in family.OrderedLevels)
            {
                Append(builder, level.Id);
                Append(builder, level.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var traitId in level.NormalizedTraitIds)
                    Append(builder, traitId);
            }
        }

        foreach (var traitId in (plan.RequiredTraitIds ?? [])
                     .Select(Normalize)
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
            Append(builder, "required:" + traitId);
        foreach (var group in (plan.RequiredAnyTraitGroups ?? [])
                     .Where(group => group is { Count: > 0 })
                     .Select(group => group
                         .Where(trait => !string.IsNullOrWhiteSpace(trait))
                         .Select(Normalize)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(trait => trait, StringComparer.Ordinal)
                         .ToArray())
                     .Where(group => group.Length > 0)
                     .OrderBy(group => string.Join("|", group), StringComparer.Ordinal))
        {
            Append(builder, "required-any:" + string.Join("|", group));
        }
        foreach (var group in (plan.CompatibilityGroups ?? [])
                     .Where(group => group is not null && !string.IsNullOrWhiteSpace(group.Id))
                     .OrderBy(group => Normalize(group.Id), StringComparer.Ordinal))
        {
            Append(builder, "compatibility-group:" + Normalize(group.Id));
            var alternatives = (group.Alternatives ?? [])
                .Select((alternative, index) => new
                {
                    Alternative = alternative
                        .Where(trait => !string.IsNullOrWhiteSpace(trait))
                        .Select(Normalize)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(trait => trait, StringComparer.Ordinal)
                        .ToArray(),
                    Rank = group.AlternativeRanks is { Count: > 0 } ranks && index < ranks.Count
                        ? (int?)ranks[index]
                        : null
                })
                .Where(item => item.Alternative.Length > 0)
                .OrderBy(item => item.Rank ?? int.MaxValue)
                .ThenBy(item => string.Join("|", item.Alternative), StringComparer.Ordinal);
            foreach (var item in alternatives)
            {
                if (item.Rank is { } rank)
                    Append(builder, "compatibility-option-rank:" + rank.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Append(builder, "compatibility-option:" + string.Join("|", item.Alternative));
            }
        }
        foreach (var traitId in (plan.ForbiddenTraitIds ?? [])
                     .Select(Normalize)
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
            Append(builder, "forbidden:" + traitId);
        foreach (var relationship in (plan.Relationships ?? [])
                     .Where(relationship => relationship is not null)
                     .OrderBy(item => Normalize(item.FromTraitId), StringComparer.Ordinal)
                     .ThenBy(item => Normalize(item.ToTraitId), StringComparer.Ordinal)
                     .ThenBy(item => item.Kind))
        {
            Append(builder, Normalize(relationship.FromTraitId));
            Append(builder, Normalize(relationship.ToTraitId));
            Append(builder, relationship.Kind.ToString());
        }

        foreach (var overrideValue in (plan.Overrides ?? new Dictionary<string, string>())
                     .OrderBy(item => Normalize(item.Key), StringComparer.Ordinal))
        {
            Append(builder, "override:" + Normalize(overrideValue.Key));
            Append(builder, overrideValue.Value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static void Append(StringBuilder builder, string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        builder.Append(normalized.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(normalized);
        builder.Append('|');
    }
}
