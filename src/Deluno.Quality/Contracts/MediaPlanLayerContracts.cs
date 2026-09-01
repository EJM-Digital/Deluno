using System.Globalization;

namespace Deluno.Quality.Contracts;

/// <summary>
/// The four ownership levels used when a Media Plan is evaluated. A lower
/// level may replace only the fields it names; it never copies an entire plan
/// and therefore cannot accidentally erase an unrelated local choice.
/// </summary>
public static class MediaPlanLayerKinds
{
    public const string GlobalSafety = "global-safety";
    public const string MediaPlan = "media-plan";
    public const string Library = "library";
    public const string Title = "title";
}

/// <summary>
/// A field-level override for a library or title. Null means that the layer
/// inherits the value above it. The record intentionally contains only fields
/// that are owned by a Media Plan; destination/storage mechanics remain a
/// library concern and title monitoring remains a title concern.
/// </summary>
public sealed record MediaPlanLayerOverride(
    string? QualityProfileId = null,
    string? DestinationRuleId = null,
    string? CustomFormatIds = null,
    int? SearchIntervalOverrideHours = null,
    int? RetryDelayOverrideHours = null,
    bool? UpgradeUntilCutoff = null,
    bool? IsEnabled = null,
    string? Notes = null,
    MediaPlanAutomationIntent? AutomationIntent = null,
    ReleasePreferencePlanReference? ReleasePreferencePlan = null)
{
    public MediaPlanLayerOverride Normalize()
        => this with
        {
            QualityProfileId = NormalizeNullable(QualityProfileId),
            DestinationRuleId = NormalizeNullable(DestinationRuleId),
            CustomFormatIds = NormalizeCsv(CustomFormatIds),
            SearchIntervalOverrideHours = NormalizePositive(SearchIntervalOverrideHours),
            RetryDelayOverrideHours = NormalizePositive(RetryDelayOverrideHours),
            Notes = NormalizeNullable(Notes),
            AutomationIntent = MediaPlanAutomationIntentCodec.Normalize(AutomationIntent),
            ReleasePreferencePlan = ReleasePreferencePlanReference.Normalize(ReleasePreferencePlan)
        };

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? NormalizePositive(int? value) => value is > 0 ? value : null;

    private static string? NormalizeCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join(",", value
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        return normalized.Length == 0 ? null : normalized;
    }
}

/// <summary>
/// The global automation gate is a safety layer, not a plan override. When it
/// is off it can only make a plan less active; neither a library nor a title
/// can turn automation back on through the effective-plan resolver.
/// </summary>
public sealed record MediaPlanGlobalSafety(bool AutomationEnabled = true);

public sealed record MediaPlanFieldResolution(
    string Field,
    string? Value,
    string SourceKind,
    string? SourceId,
    bool IsSafetyLocked = false);

public sealed record MediaPlanEffectiveResolution(
    MediaPlanSnapshot BasePlan,
    MediaPlanSnapshot EffectivePlan,
    IReadOnlyList<MediaPlanFieldResolution> Fields,
    IReadOnlyList<string> Warnings);

public sealed record MediaPlanEffectivePreviewRequest(
    MediaPlanLayerOverride? LibraryOverride = null,
    MediaPlanLayerOverride? TitleOverride = null,
    bool GlobalAutomationEnabled = true,
    string? LibraryId = null,
    string? TitleId = null);

/// <summary>
/// Resolves the one effective plan used by automation and explains the source
/// of every field. This is deliberately pure so the same rule can be used by
/// API previews, runtime adapters, and populated-library acceptance tests.
/// </summary>
public static class MediaPlanInheritanceResolver
{
    private static readonly string[] Fields =
    [
        "qualityProfileId",
        "destinationRuleId",
        "customFormatIds",
        "searchIntervalOverrideHours",
        "retryDelayOverrideHours",
        "upgradeUntilCutoff",
        "isEnabled",
        "notes",
        "automationIntent",
        "releasePreferencePlan"
    ];

    public static MediaPlanEffectiveResolution Resolve(
        MediaPlanSnapshot basePlan,
        MediaPlanLayerOverride? libraryOverride = null,
        MediaPlanLayerOverride? titleOverride = null,
        MediaPlanGlobalSafety? globalSafety = null,
        string? libraryId = null,
        string? titleId = null)
    {
        var effective = basePlan;
        var sources = Fields.ToDictionary(
            field => field,
            field => new MediaPlanFieldResolution(
                field,
                ValueFor(basePlan, field),
                MediaPlanLayerKinds.MediaPlan,
                null),
            StringComparer.Ordinal);
        var warnings = new List<string>();

        ApplyOverride(
            ref effective,
            sources,
            libraryOverride?.Normalize(),
            MediaPlanLayerKinds.Library,
            libraryId);
        ApplyOverride(
            ref effective,
            sources,
            titleOverride?.Normalize(),
            MediaPlanLayerKinds.Title,
            titleId);

        var safety = globalSafety ?? new MediaPlanGlobalSafety();
        if (!safety.AutomationEnabled && effective.IsEnabled)
        {
            effective = effective with { IsEnabled = false };
            sources["isEnabled"] = new MediaPlanFieldResolution(
                "isEnabled",
                bool.FalseString,
                MediaPlanLayerKinds.GlobalSafety,
                null,
                IsSafetyLocked: true);
            warnings.Add("Global automation is disabled, so this plan remains paused regardless of library or title overrides.");
        }

        return new MediaPlanEffectiveResolution(
            basePlan,
            effective,
            Fields.Select(field => sources[field]).ToArray(),
            warnings);
    }

    private static void ApplyOverride(
        ref MediaPlanSnapshot effective,
        IDictionary<string, MediaPlanFieldResolution> sources,
        MediaPlanLayerOverride? layer,
        string sourceKind,
        string? sourceId)
    {
        if (layer is null)
        {
            return;
        }

        if (layer.QualityProfileId is not null)
        {
            effective = effective with { QualityProfileId = layer.QualityProfileId };
            SetSource(sources, effective, "qualityProfileId", sourceKind, sourceId);
        }

        if (layer.DestinationRuleId is not null)
        {
            effective = effective with { DestinationRuleId = layer.DestinationRuleId };
            SetSource(sources, effective, "destinationRuleId", sourceKind, sourceId);
        }

        if (layer.CustomFormatIds is not null)
        {
            effective = effective with { CustomFormatIds = layer.CustomFormatIds };
            SetSource(sources, effective, "customFormatIds", sourceKind, sourceId);
        }

        if (layer.SearchIntervalOverrideHours.HasValue)
        {
            effective = effective with { SearchIntervalOverrideHours = layer.SearchIntervalOverrideHours };
            SetSource(sources, effective, "searchIntervalOverrideHours", sourceKind, sourceId);
        }

        if (layer.RetryDelayOverrideHours.HasValue)
        {
            effective = effective with { RetryDelayOverrideHours = layer.RetryDelayOverrideHours };
            SetSource(sources, effective, "retryDelayOverrideHours", sourceKind, sourceId);
        }

        if (layer.UpgradeUntilCutoff.HasValue)
        {
            effective = effective with { UpgradeUntilCutoff = layer.UpgradeUntilCutoff.Value };
            SetSource(sources, effective, "upgradeUntilCutoff", sourceKind, sourceId);
        }

        if (layer.IsEnabled.HasValue)
        {
            effective = effective with { IsEnabled = layer.IsEnabled.Value };
            SetSource(sources, effective, "isEnabled", sourceKind, sourceId);
        }

        if (layer.Notes is not null)
        {
            effective = effective with { Notes = layer.Notes };
            SetSource(sources, effective, "notes", sourceKind, sourceId);
        }

        if (layer.AutomationIntent is not null)
        {
            effective = effective with { AutomationIntent = layer.AutomationIntent };
            SetSource(sources, effective, "automationIntent", sourceKind, sourceId);
        }

        if (layer.ReleasePreferencePlan is not null)
        {
            effective = effective with { ReleasePreferencePlan = layer.ReleasePreferencePlan };
            SetSource(sources, effective, "releasePreferencePlan", sourceKind, sourceId);
        }
    }

    private static void SetSource(
        IDictionary<string, MediaPlanFieldResolution> sources,
        MediaPlanSnapshot effective,
        string field,
        string sourceKind,
        string? sourceId)
        => sources[field] = new MediaPlanFieldResolution(
            field,
            ValueFor(effective, field),
            sourceKind,
            sourceId);

    private static string? ValueFor(MediaPlanSnapshot plan, string field)
        => field switch
        {
            "qualityProfileId" => plan.QualityProfileId,
            "destinationRuleId" => plan.DestinationRuleId,
            "customFormatIds" => plan.CustomFormatIds,
            "searchIntervalOverrideHours" => plan.SearchIntervalOverrideHours?.ToString(CultureInfo.InvariantCulture),
            "retryDelayOverrideHours" => plan.RetryDelayOverrideHours?.ToString(CultureInfo.InvariantCulture),
            "upgradeUntilCutoff" => plan.UpgradeUntilCutoff.ToString(),
            "isEnabled" => plan.IsEnabled.ToString(),
            "notes" => plan.Notes,
            "automationIntent" => MediaPlanAutomationIntentCodec.Serialize(plan.AutomationIntent),
            "releasePreferencePlan" => ReleasePreferencePlanReferenceCodec.Serialize(plan.ReleasePreferencePlan),
            _ => null
        };
}
