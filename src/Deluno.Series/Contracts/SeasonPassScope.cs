namespace Deluno.Series.Contracts;

/// <summary>
/// One row's worth of bulk-toggle from the Season Pass screen (#791): a
/// whole series (<see cref="SeasonNumber"/> null) or one of its seasons.
/// </summary>
public sealed record SeasonPassScope(string SeriesId, int? SeasonNumber);

public sealed record UpdateSeasonPassMonitoringRequest(
    IReadOnlyList<SeasonPassScope> Scopes,
    bool Monitored);
