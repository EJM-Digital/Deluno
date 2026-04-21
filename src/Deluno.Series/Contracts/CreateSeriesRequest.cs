namespace Deluno.Series.Contracts;

public sealed record CreateSeriesRequest(
    string? Title,
    int? StartYear,
    string? ImdbId,
    bool Monitored = true);
