namespace Deluno.Series.Contracts;

public sealed record UpdateEpisodeMonitoringRequest(
    IReadOnlyList<string> EpisodeIds,
    bool Monitored);
