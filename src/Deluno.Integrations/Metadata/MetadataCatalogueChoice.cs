namespace Deluno.Integrations.Metadata;

/// <summary>
/// Chooses which provider's episode list a show is synced to. TheTVDB's list
/// is used whenever it lines up with the episodes the show already has; a show
/// whose existing episodes it would leave stranded (TheTVDB numbers an episode
/// differently from TMDb) stays on TMDb's numbering, so nothing already
/// imported is renumbered. Moving such a show to TheTVDB's numbering is a
/// deliberate choice made through the normal numbering and rename preview,
/// never a side effect of a metadata refresh.
/// </summary>
public static class MetadataCatalogueChoice
{
    public static async Task<IReadOnlyList<MetadataSeason>> GetAsync(
        IMetadataProvider provider,
        string providerId,
        IReadOnlyCollection<MetadataEpisodeIdentity> existingEpisodes,
        CancellationToken cancellationToken)
    {
        var preferred = await provider.GetSeriesCatalogueAsync(providerId, MetadataCatalogueSource.Preferred, cancellationToken) ?? [];
        if (existingEpisodes.Count == 0 ||
            !preferred.SelectMany(season => season.Episodes).Any(episode => episode.Source == "tvdb"))
        {
            return preferred;
        }

        var evaluation = MetadataCatalogueSafety.Evaluate(
            existingEpisodes,
            preferred.SelectMany(season => season.Episodes)
                .Select(episode => new MetadataEpisodeIdentity(episode.SeasonNumber, episode.EpisodeNumber)));
        return evaluation.PreservesExistingCatalogue
            ? preferred
            : await provider.GetSeriesCatalogueAsync(providerId, MetadataCatalogueSource.TmdbOnly, cancellationToken);
    }

    /// <summary>The catalogue source name stored beside each synced episode.</summary>
    public static string SourceName(IEnumerable<MetadataSeason> seasons)
        => seasons.SelectMany(season => season.Episodes).Any(episode => episode.Source == "tvdb") ? "tvdb" : "tmdb";
}
