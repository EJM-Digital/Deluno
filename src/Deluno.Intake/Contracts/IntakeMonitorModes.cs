namespace Deluno.Intake.Contracts;

/// <summary>
/// The monitor mode a list source can choose for the titles it adds (#848):
/// how much of a newly added title Deluno should actually watch for,
/// mirroring the per-add choice Radarr/Sonarr both offer. Movie sources
/// collapse to Movie/None (episode-grain modes do not apply); TV sources use
/// the full episode-grain set.
///
/// Stored as a stable lower-case identifier on the source, so a provider or
/// UI refresh cannot change the meaning of an existing list's choice. An
/// unset value normalizes to All/Movie — fully monitored — which is exactly
/// how every existing source (and any client that predates #848) behaves
/// today.
/// </summary>
public static class IntakeMonitorModes
{
    public const string All = "all";
    public const string Future = "future";
    public const string Missing = "missing";
    public const string Existing = "existing";
    public const string Recent = "recent";
    public const string Movie = "movie";
    public const string None = "none";

    private static readonly IReadOnlySet<string> TvModes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { All, Future, Missing, Existing, Recent };

    private static readonly IReadOnlySet<string> MovieModes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Movie, None };

    /// <summary>Normalizes against the source's own media type, so a TV mode can never leak onto a movie source or vice versa.</summary>
    public static string Normalize(string? mediaType, string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed is not null && TvModes.Contains(trimmed) ? trimmed.ToLowerInvariant() : All;
        }

        return trimmed is not null && MovieModes.Contains(trimmed) ? trimmed.ToLowerInvariant() : Movie;
    }
}
