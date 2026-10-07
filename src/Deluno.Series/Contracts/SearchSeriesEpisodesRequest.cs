namespace Deluno.Series.Contracts;

/// <param name="Mode">
/// What the search does (#1175), one thing: <c>send</c> (the default) picks the best release and sends it,
/// saying so and listing nothing to choose from; <c>choose</c> sends nothing and returns the releases compared.
/// </param>
public sealed record SearchSeriesEpisodesRequest(
    IReadOnlyList<string> EpisodeIds,
    bool MonitoredOnly = false,
    string? Mode = null)
{
    public bool ChooseOnly => string.Equals(Mode, "choose", StringComparison.OrdinalIgnoreCase);
}
