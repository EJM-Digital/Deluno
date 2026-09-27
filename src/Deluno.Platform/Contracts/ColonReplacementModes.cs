namespace Deluno.Platform.Contracts;

/// <summary>
/// Radarr/Sonarr's five colon-handling modes (#759), available once
/// <see cref="PlatformSettingsSnapshot.ReplaceIllegalCharacters"/> is on.
/// "Dash" also matches the fixed behaviour every other illegal character
/// keeps, so it is the closest thing to a pre-#759 default.
/// </summary>
public static class ColonReplacementModes
{
    /// <summary>Space-dash-space after a colon+space ("Title: Sub" -> "Title - Sub"); a plain dash otherwise.</summary>
    public const string SmartReplace = "smart";
    public const string Delete = "delete";
    public const string Dash = "dash";
    public const string SpaceDash = "space-dash";
    public const string SpaceDashSpace = "space-dash-space";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SmartReplace, Delete, Dash, SpaceDash, SpaceDashSpace };

    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            Delete => Delete,
            Dash => Dash,
            SpaceDash => SpaceDash,
            SpaceDashSpace => SpaceDashSpace,
            _ => SmartReplace
        };

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value.Trim());
}
