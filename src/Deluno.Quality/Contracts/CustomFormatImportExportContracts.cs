using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Contracts;

/// <summary>
/// A custom format as JSON import/export represents it (#777): Deluno's own,
/// re-importable condition shape, never the raw stored string (which can
/// still be an imported Radarr/Sonarr array, a plain string array, or the
/// legacy newline shape).
/// </summary>
public sealed record CustomFormatExportItem(
    string Name,
    string MediaType,
    int Score,
    bool UpgradeAllowed,
    IReadOnlyList<CustomFormatImportCondition> Conditions);

/// <summary>
/// One imported format's outcome. <see cref="DroppedConditions"/> is never
/// silent (#774, #777): a condition this matcher could not interpret is
/// named here even when the rest of the format imported successfully.
/// </summary>
public sealed record CustomFormatImportResult(
    string? Name,
    bool Imported,
    string? Id,
    IReadOnlyList<string> DroppedConditions,
    string? Error);
