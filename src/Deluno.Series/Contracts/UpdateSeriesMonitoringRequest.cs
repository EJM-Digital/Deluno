namespace Deluno.Series.Contracts;

public sealed record UpdateSeriesMonitoringRequest(
    IReadOnlyList<string>? SeriesIds,
    bool Monitored);
