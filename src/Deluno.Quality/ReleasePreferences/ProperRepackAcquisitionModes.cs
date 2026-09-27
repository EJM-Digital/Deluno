namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// #767: how a Proper/Repack revision affects acquisition, instead of the
/// guide's Repack/Proper custom formats always scoring the same way a person
/// can never turn off. Mirrors PlatformSettingsSnapshot.ProperRepackAcquisitionMode.
/// </summary>
public static class ProperRepackAcquisitionModes
{
    /// <summary>Today's behaviour: a Proper/Repack is preferred and can drive an automatic upgrade.</summary>
    public const string PreferAndUpgrade = "prefer-and-upgrade";

    /// <summary>Still preferred among releases found in the same search, but never the reason Deluno replaces an already-installed file on its own.</summary>
    public const string DoNotUpgradeAutomatically = "do-not-upgrade-automatically";

    /// <summary>Turns the built-in scoring off entirely, leaving the outcome to whatever other Custom Format conditions the person has defined.</summary>
    public const string DoNotPrefer = "do-not-prefer";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            DoNotUpgradeAutomatically => DoNotUpgradeAutomatically,
            DoNotPrefer => DoNotPrefer,
            _ => PreferAndUpgrade
        };
}
