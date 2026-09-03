namespace Deluno.Quality;

/// <summary>
/// Where files of each tier actually land, in one place.
///
/// <para><b>This is a fact, not a setting.</b> A 2160p Remux genuinely is
/// somewhere around 35–130 GB; that is what the encode is, not what anybody
/// prefers. It is here so a profile's own size answer can be drawn against it —
/// the band behind the slider — rather than typed into a void.</para>
///
/// <para><b>One copy.</b> The same knowledge lived twice: as the quality model's
/// default tier table, and again as a hardcoded ladder inside
/// <c>ReleaseDecisionEngine.ExpectedSizeRangeGb</c> that guessed from substrings
/// of the quality name — "2160 and remux" meant 35–130, "1080" meant 1.5–25. Two
/// copies of a physical fact drift, and the one nobody looks at is the one that
/// decides whether a release is rejected.</para>
/// </summary>
public static class QualityTypicalSizes
{
    /// <summary>
    /// Tier name, rank, film size in GB, episode size in MB, and the ceiling
    /// the ranking uses. Ordered by rank.
    /// </summary>
    public static IReadOnlyList<QualityTierDefinition> Tiers { get; } =
    [
        new("Unknown", 1, 0.1, 2.0, 50, 800, 0),
        new("WORKPRINT", 2, 0.1, 2.0, 50, 800, 0),
        new("CAM", 3, 0.1, 2.0, 50, 800, 0),
        new("TELESYNC", 4, 0.1, 2.5, 50, 900, 0),
        new("TELECINE", 5, 0.2, 3.0, 60, 1000, 0),
        new("REGIONAL", 6, 0.2, 3.0, 60, 1000, 0),
        new("DVDSCR", 7, 0.3, 3.5, 80, 1100, 0),
        new("SDTV", 10, 0.3, 1.8, 120, 900, 0),
        new("DVD", 20, 0.7, 3.5, 180, 1200, 0),
        new("DVD-R", 21, 1.0, 8.5, 220, 2000, 0),
        new("WEB 480p", 22, 0.4, 3.0, 150, 1100, 5),
        new("Bluray 480p", 24, 0.5, 4.0, 170, 1300, 5),
        new("Bluray 576p", 25, 0.6, 5.0, 190, 1500, 5),
        new("HDTV 720p", 30, 0.8, 6.5, 220, 1800, 10),
        new("WEB 720p", 40, 0.8, 8.0, 240, 2200, 20),
        new("Bluray 720p", 50, 1.2, 10.0, 280, 2500, 30),
        new("HDTV 1080p", 60, 1.3, 14.0, 350, 3200, 40),
        new("WEB 1080p", 70, 1.5, 25.0, 420, 3800, 50),
        new("Bluray 1080p", 80, 2.2, 35.0, 480, 4400, 60),
        new("Remux 1080p", 90, 12.0, 60.0, 1500, 8000, 70),
        new("HDTV 2160p", 95, 4.0, 40.0, 900, 9000, 75),
        new("WEB 2160p", 100, 7.0, 60.0, 1600, 12000, 80),
        new("Bluray 2160p", 110, 12.0, 90.0, 2200, 18000, 90),
        new("Remux 2160p", 120, 35.0, 130.0, 6000, 36000, 100),
        new("BR-DISK", 125, 20.0, 130.0, 4000, 36000, 0),
        new("Raw-HD", 126, 4.0, 60.0, 1200, 12000, 0)
    ];

    public static QualityTierDefinition? For(string? quality)
        => string.IsNullOrWhiteSpace(quality)
            ? null
            : Tiers.FirstOrDefault(tier => string.Equals(tier.Name, quality.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The film size band for a tier, in GB.
    ///
    /// <para>The widest sensible band is returned for a tier nobody knows,
    /// rather than a narrow guess. Refusing a release because Deluno could not
    /// identify its tier would be punishing the owner for a gap in the
    /// catalogue.</para>
    /// </summary>
    public static (double MinGb, double MaxGb) FilmSizeGb(string? quality)
        => For(quality) is { } tier ? (tier.MovieMinGb, tier.MovieMaxGb) : (0.1, 130.0);

    /// <summary>The episode size band for a tier, in MB.</summary>
    public static (double MinMb, double MaxMb) EpisodeSizeMb(string? quality)
        => For(quality) is { } tier ? (tier.EpisodeMinMb, tier.EpisodeMaxMb) : (50, 36_000);
}
