namespace Deluno.Series.Contracts;

/// <summary>
/// One episode as the metadata provider describes it, on its way into the
/// catalogue. Deliberately separate from <see cref="ImportedEpisodeItem"/>:
/// that one says "a file for this arrived", this one says "this exists".
/// </summary>
public sealed record CatalogueEpisodeItem(
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    string? Overview,
    DateTimeOffset? AirDateUtc);

/// <summary>What a catalogue sync did, so the caller can say so out loud.</summary>
public sealed record SeriesCatalogueSyncResult(
    int SeasonCount,
    int EpisodeCount,
    int AddedCount,
    int UpdatedCount)
{
    public static readonly SeriesCatalogueSyncResult None = new(0, 0, 0, 0);
}
