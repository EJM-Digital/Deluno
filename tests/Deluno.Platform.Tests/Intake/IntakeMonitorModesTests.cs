using Deluno.Intake.Contracts;

namespace Deluno.Platform.Tests.Intake;

/// <summary>
/// #991 added three TV monitor modes (First season, Pilot, None) alongside
/// #848's original four. <see cref="IntakeMonitorModes.Normalize"/> is the
/// one place that turns whatever a caller typed into the exact lower-case
/// token <see cref="Deluno.Series.Data.SqliteSeriesCatalogRepository"/>'s
/// switch matches against — a caller storing an unnormalized value (mixed
/// case, or an id the TV/movie split does not recognize) is how a preset
/// silently turns into "every episode is monitored" instead of the one that
/// was picked (found while adding these three modes: the repository's own
/// switch is case-sensitive, and a value that does not survive normalization
/// falls through to its "allow everything" default).
/// </summary>
public sealed class IntakeMonitorModesTests
{
    [Theory]
    [InlineData("firstSeason")]
    [InlineData("FIRSTSEASON")]
    [InlineData("FirstSeason")]
    public void Normalize_lowercases_first_season_for_tv(string input)
    {
        Assert.Equal(IntakeMonitorModes.FirstSeason, IntakeMonitorModes.Normalize("tv", input));
    }

    [Theory]
    [InlineData("pilot")]
    [InlineData("PILOT")]
    public void Normalize_lowercases_pilot_for_tv(string input)
    {
        Assert.Equal(IntakeMonitorModes.Pilot, IntakeMonitorModes.Normalize("tv", input));
    }

    [Fact]
    public void Normalize_accepts_none_for_tv_as_well_as_movies()
    {
        Assert.Equal(IntakeMonitorModes.None, IntakeMonitorModes.Normalize("tv", "None"));
        Assert.Equal(IntakeMonitorModes.None, IntakeMonitorModes.Normalize("movies", "NONE"));
    }

    [Fact]
    public void Normalize_falls_back_to_all_for_an_unknown_tv_value()
    {
        Assert.Equal(IntakeMonitorModes.All, IntakeMonitorModes.Normalize("tv", "not-a-real-mode"));
        Assert.Equal(IntakeMonitorModes.All, IntakeMonitorModes.Normalize("tv", null));
    }

    /// <summary>A movie-only mode never leaks onto a TV source, and vice versa.</summary>
    [Fact]
    public void Normalize_never_crosses_tv_and_movie_modes()
    {
        Assert.Equal(IntakeMonitorModes.All, IntakeMonitorModes.Normalize("tv", IntakeMonitorModes.Movie));
        Assert.Equal(IntakeMonitorModes.Movie, IntakeMonitorModes.Normalize("movies", IntakeMonitorModes.FirstSeason));
    }
}
