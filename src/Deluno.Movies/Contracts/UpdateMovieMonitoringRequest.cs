namespace Deluno.Movies.Contracts;

public sealed record UpdateMovieMonitoringRequest(
    IReadOnlyList<string>? MovieIds,
    bool Monitored);
