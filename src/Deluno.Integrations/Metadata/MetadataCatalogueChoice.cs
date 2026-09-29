namespace Deluno.Integrations.Metadata;

/// <summary>Why a catalogue refresh kept a show's episodes exactly as they were.</summary>
public enum MetadataCatalogueHold
{
    /// <summary>Nothing held back: <see cref="MetadataCatalogueChoiceResult.Seasons"/> is the list to sync.</summary>
    None,

    /// <summary>The show's numbering came from TheTVDB and TheTVDB could not answer this time.</summary>
    TvdbUnavailable,

    /// <summary>TheTVDB answered with a list whose episodes do not look like this show's.</summary>
    TvdbDisagrees
}

public sealed record MetadataCatalogueChoiceResult(
    IReadOnlyList<MetadataSeason> Seasons,
    MetadataCatalogueHold Hold = MetadataCatalogueHold.None);

/// <summary>
/// Chooses which provider's episode list a show is synced to. TheTVDB's list
/// is used whenever it lines up with the episodes the show already has; a show
/// whose existing episodes it would leave stranded (TheTVDB numbers an episode
/// differently from TMDb) stays on TMDb's numbering, so nothing already
/// imported is renumbered. Moving such a show to TheTVDB's numbering is a
/// deliberate choice made through the normal numbering and rename preview,
/// never a side effect of a metadata refresh.
///
/// A show whose numbering came from TheTVDB is never re-synced from TMDb's
/// list: if TheTVDB cannot answer, or answers with a list that does not agree
/// with the episodes the show has (same numbers, different titles and air
/// dates: a wrong id), the show's episodes are kept and the refresh is tried
/// again next time.
/// </summary>
public static class MetadataCatalogueChoice
{
    public static async Task<MetadataCatalogueChoiceResult> ChooseAsync(
        IMetadataProvider provider,
        string providerId,
        IReadOnlyCollection<MetadataEpisodeIdentity> existingEpisodes,
        CancellationToken cancellationToken)
    {
        var preferred = await provider.GetSeriesCatalogueAsync(providerId, MetadataCatalogueSource.Preferred, cancellationToken) ?? [];
        var onTvdb = existingEpisodes.Any(episode => string.Equals(episode.CatalogueSource, "tvdb", StringComparison.OrdinalIgnoreCase));
        var preferredHasTvdb = preferred.SelectMany(season => season.Episodes).Any(episode => episode.Source == "tvdb");

        if (existingEpisodes.Count > 0 && onTvdb && !preferredHasTvdb)
        {
            return new MetadataCatalogueChoiceResult([], MetadataCatalogueHold.TvdbUnavailable);
        }

        if (existingEpisodes.Count == 0 || !preferredHasTvdb)
        {
            return new MetadataCatalogueChoiceResult(preferred);
        }

        var proposed = preferred.SelectMany(season => season.Episodes).ToArray();
        var evaluation = MetadataCatalogueSafety.Evaluate(
            existingEpisodes,
            proposed.Select(episode => new MetadataEpisodeIdentity(episode.SeasonNumber, episode.EpisodeNumber)));
        var lineUp = evaluation.PreservesExistingCatalogue && MetadataCatalogueAgreement.Agrees(existingEpisodes, proposed);
        if (lineUp)
        {
            return new MetadataCatalogueChoiceResult(preferred);
        }

        if (onTvdb)
        {
            // Already numbered by TheTVDB: TMDb's list is no answer either.
            return new MetadataCatalogueChoiceResult([], MetadataCatalogueHold.TvdbDisagrees);
        }

        return new MetadataCatalogueChoiceResult(
            await provider.GetSeriesCatalogueAsync(providerId, MetadataCatalogueSource.TmdbOnly, cancellationToken) ?? []);
    }

    /// <summary>The catalogue source name stored beside each synced episode.</summary>
    public static string SourceName(IEnumerable<MetadataSeason> seasons)
        => seasons.SelectMany(season => season.Episodes).Any(episode => episode.Source == "tvdb") ? "tvdb" : "tmdb";
}

/// <summary>
/// Season and episode numbers alone cannot tell two shows apart. This compares
/// what the show already has with a proposed list, episode by episode, on air
/// date (a day either side is fine: providers disagree on time zones) or title.
/// </summary>
public static class MetadataCatalogueAgreement
{
    public static bool Agrees(
        IEnumerable<MetadataEpisodeIdentity> existing,
        IEnumerable<MetadataEpisode> proposed)
    {
        var proposedByKey = proposed
            .GroupBy(episode => (episode.SeasonNumber, episode.EpisodeNumber))
            .ToDictionary(group => group.Key, group => group.First());
        var comparable = 0;
        var agreeing = 0;
        foreach (var episode in existing)
        {
            if (!proposedByKey.TryGetValue((episode.SeasonNumber, episode.EpisodeNumber), out var other))
            {
                continue;
            }

            var haveDates = episode.AirDateUtc is not null && other.AirDateUtc is not null;
            var haveTitles = !string.IsNullOrWhiteSpace(episode.Title) && !string.IsNullOrWhiteSpace(other.Title);
            if (!haveDates && !haveTitles)
            {
                continue;
            }

            comparable++;
            var datesMatch = haveDates &&
                Math.Abs((episode.AirDateUtc!.Value.UtcDateTime.Date - other.AirDateUtc!.Value.UtcDateTime.Date).TotalDays) <= 1;
            var titlesMatch = haveTitles && Normalize(episode.Title!) == Normalize(other.Title!);
            if (datesMatch || titlesMatch)
            {
                agreeing++;
            }
        }

        // Nothing to compare (no dates or titles yet): the numbers are all there is.
        if (comparable == 0)
        {
            return true;
        }

        return comparable < 3 ? agreeing == comparable : agreeing * 10 >= comparable * 6;
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
