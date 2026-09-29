using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Series.Contracts;

namespace Deluno.Series.Services;

/// <summary>
/// Every place that picks a provider's episode list for a show (the link
/// screen, the scheduled refresh, the episode-group pass) goes through here,
/// so the TheTVDB outage hold and the wrong-id agreement check can never be
/// skipped by one caller.
/// </summary>
public static class SeriesCatalogueChoice
{
    /// <summary>What the show already has, with the titles, air dates and source the checks need.</summary>
    public static IReadOnlyCollection<MetadataEpisodeIdentity> ExistingEpisodes(SeriesInventoryDetail? inventory)
        => (inventory?.Episodes ?? [])
            .Select(episode => new MetadataEpisodeIdentity(
                episode.SeasonNumber,
                episode.EpisodeNumber,
                episode.HasFile,
                episode.Title,
                episode.AirDateUtc,
                episode.CatalogueSource))
            .ToArray();

    public static string HoldMessage(MetadataCatalogueHold hold, string seriesTitle)
        => hold == MetadataCatalogueHold.TvdbUnavailable
            ? $"TheTVDB did not answer for {seriesTitle}, so its episode list was left as it is. Deluno will try again at the next refresh."
            : $"TheTVDB's episode list for {seriesTitle} does not match the episodes Deluno already has, so it was not used and the list was left as it is. Check the TheTVDB link on the title.";

    public static Task RecordHoldAsync(
        IActivityFeedRepository activityFeedRepository,
        MetadataCatalogueHold hold,
        string seriesId,
        string seriesTitle,
        string? jobId,
        CancellationToken cancellationToken)
        => activityFeedRepository.RecordActivityAsync(
            "metadata.series.catalogue.held",
            HoldMessage(hold, seriesTitle),
            null,
            jobId,
            "series",
            seriesId,
            cancellationToken);
}
