using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Quality.Contracts;

/// <summary>
/// The persisted, dependency-safe projection of a media plan. References are
/// stored by id rather than copied by name, so a version continues to point at
/// the exact quality, destination and custom-format inputs it was created with.
/// </summary>
public sealed record MediaPlanSnapshot(
    string Name,
    string MediaType,
    string? QualityProfileId,
    string? DestinationRuleId,
    string CustomFormatIds,
    int? SearchIntervalOverrideHours,
    int? RetryDelayOverrideHours,
    bool UpgradeUntilCutoff,
    bool IsEnabled,
    string? Notes,
    MediaPlanAutomationIntent? AutomationIntent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReleasePreferencePlanReference? ReleasePreferencePlan = null)
{
    public static MediaPlanSnapshot From(PolicySetItem item)
        => new(
            item.Name.Trim(),
            NormalizeMediaType(item.MediaType),
            NormalizeNullable(item.QualityProfileId),
            NormalizeNullable(item.DestinationRuleId),
            NormalizeCsv(item.CustomFormatIds),
            NormalizePositive(item.SearchIntervalOverrideHours),
            NormalizePositive(item.RetryDelayOverrideHours),
            item.UpgradeUntilCutoff,
            item.IsEnabled,
            NormalizeNullable(item.Notes),
            MediaPlanAutomationIntentCodec.Normalize(item.AutomationIntent),
            ReleasePreferencePlanReference.Normalize(item.ReleasePreferencePlan));

    public PolicySetItem ToPolicySet(
        string id,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        string? qualityProfileName = null,
        string? destinationRuleName = null)
        => new(
            id,
            Name,
            MediaType,
            QualityProfileId,
            qualityProfileName,
            DestinationRuleId,
            destinationRuleName,
            CustomFormatIds,
            SearchIntervalOverrideHours,
            RetryDelayOverrideHours,
            UpgradeUntilCutoff,
            IsEnabled,
            Notes,
            createdUtc,
            updatedUtc,
            AutomationIntent,
            ReleasePreferencePlan);

    private static string NormalizeMediaType(string? value)
        => string.Equals(value?.Trim(), "tv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tv shows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "tvshows", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? NormalizePositive(int? value) => value is > 0 ? value : null;

    private static string NormalizeCsv(string? value)
        => string.Join(",", (value ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
}

public sealed record MediaPlanVersionItem(
    string PlanId,
    int Version,
    string PlanHash,
    string ChangeKind,
    MediaPlanSnapshot Snapshot,
    DateTimeOffset CreatedUtc);

public sealed record MediaPlanDiffItem(
    string Field,
    string? CurrentValue,
    string? ProposedValue);

public sealed record MediaPlanPreview(
    string PlanId,
    int? CurrentVersion,
    MediaPlanSnapshot Current,
    MediaPlanSnapshot Proposed,
    IReadOnlyList<MediaPlanDiffItem> Changes,
    bool HasChanges,
    string? BasePlanHash);

public sealed record RollbackMediaPlanRequest(int Version);

/// <summary>
/// Raised when a reviewed Media Plan is no longer the same immutable content
/// at apply time. Callers must fetch a fresh preview rather than overwriting a
/// change that happened after the owner reviewed it.
/// </summary>
public sealed class MediaPlanVersionConflictException(
    string planId,
    string expectedPlanHash,
    string actualPlanHash)
    : InvalidOperationException($"Media Plan '{planId}' changed after it was previewed.")
{
    public string PlanId { get; } = planId;

    public string ExpectedPlanHash { get; } = expectedPlanHash;

    public string ActualPlanHash { get; } = actualPlanHash;
}

/// <summary>
/// Canonical serialization and diffing for media-plan history. A version hash
/// is content-addressed and intentionally independent of its sequence number:
/// rolling back creates a new audit version while retaining the old content
/// identity.
/// </summary>
public static class MediaPlanVersionCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string Serialize(MediaPlanSnapshot snapshot)
        => JsonSerializer.Serialize(
            snapshot with
            {
                AutomationIntent = MediaPlanAutomationIntentCodec.Normalize(snapshot.AutomationIntent)
            },
            JsonOptions);

    public static MediaPlanSnapshot Deserialize(string json)
        => NormalizeSnapshot(JsonSerializer.Deserialize<MediaPlanSnapshot>(json, JsonOptions)
            ?? throw new JsonException("The media-plan snapshot was empty."));

    public static string ComputeHash(MediaPlanSnapshot snapshot)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(snapshot))));

    public static IReadOnlyList<MediaPlanDiffItem> Diff(
        MediaPlanSnapshot current,
        MediaPlanSnapshot proposed)
    {
        var changes = new List<MediaPlanDiffItem>();
        Add(changes, "name", current.Name, proposed.Name);
        Add(changes, "mediaType", current.MediaType, proposed.MediaType);
        Add(changes, "qualityProfileId", current.QualityProfileId, proposed.QualityProfileId);
        Add(changes, "destinationRuleId", current.DestinationRuleId, proposed.DestinationRuleId);
        Add(changes, "customFormatIds", current.CustomFormatIds, proposed.CustomFormatIds);
        Add(changes, "searchIntervalOverrideHours", current.SearchIntervalOverrideHours?.ToString(), proposed.SearchIntervalOverrideHours?.ToString());
        Add(changes, "retryDelayOverrideHours", current.RetryDelayOverrideHours?.ToString(), proposed.RetryDelayOverrideHours?.ToString());
        Add(changes, "upgradeUntilCutoff", current.UpgradeUntilCutoff.ToString(), proposed.UpgradeUntilCutoff.ToString());
        Add(changes, "isEnabled", current.IsEnabled.ToString(), proposed.IsEnabled.ToString());
        Add(changes, "notes", current.Notes, proposed.Notes);
        Add(
            changes,
            "automationIntent",
            MediaPlanAutomationIntentCodec.Serialize(current.AutomationIntent),
            MediaPlanAutomationIntentCodec.Serialize(proposed.AutomationIntent));
        Add(
            changes,
            "releasePreferencePlan",
            ReleasePreferencePlanReferenceCodec.Serialize(current.ReleasePreferencePlan),
            ReleasePreferencePlanReferenceCodec.Serialize(proposed.ReleasePreferencePlan));
        return changes;
    }

    private static void Add(List<MediaPlanDiffItem> changes, string field, string? current, string? proposed)
    {
        if (!string.Equals(current, proposed, StringComparison.Ordinal))
        {
            changes.Add(new MediaPlanDiffItem(field, current, proposed));
        }
    }

    private static MediaPlanSnapshot NormalizeSnapshot(MediaPlanSnapshot snapshot)
        => snapshot with
        {
            AutomationIntent = MediaPlanAutomationIntentCodec.Normalize(snapshot.AutomationIntent)
        };
}
