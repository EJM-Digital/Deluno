using System.Text.Json;
using Deluno.Contracts;
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

    public static string HoldHealthId(string seriesId) => $"notice:catalogue-held:{seriesId}";

    /// <summary>One note for a whole TheTVDB outage, however many shows it holds.</summary>
    public const string OutageHealthId = "notice:catalogue-held:tvdb-outage";

    private sealed record HeldShow(string Id, string Title);

    private sealed record HeldShows(List<HeldShow> Shows);

    private static List<HeldShow> ReadHeldShows(HealthCheckResultItem? note)
    {
        if (note?.DetailsJson is not { Length: > 0 } json)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<HeldShows>(json, JsonOptions)?.Shows ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static HealthCheckOutcome OutageOutcome(IReadOnlyList<HeldShow> shows)
    {
        var names = string.Join(", ", shows.Take(8).Select(show => show.Title))
            + (shows.Count > 8 ? $" and {shows.Count - 8} more" : "");
        return HealthCheckOutcome.Problem(
            OutageHealthId,
            HealthCheckCategories.Notice,
            "TheTVDB did not answer, so some episode lists were left as they are",
            $"TheTVDB did not answer for {shows.Count} show{(shows.Count == 1 ? "" : "s")}, so their episode lists were left as they are: {names}. Deluno tries again at the next refresh.",
            cause: "TheTVDB did not answer, and these shows' episodes are numbered by TheTVDB.",
            fix: "Nothing to do: Deluno tries again at the next refresh, and this note goes away once it goes through. Clear it if you do not need it.",
            detailsJson: JsonSerializer.Serialize(new HeldShows(shows.ToList()), JsonOptions));
    }

    /// <summary>
    /// A held refresh writes one Activity line per hold, not one per refresh:
    /// the first time, it also leaves a Health note the person can clear, and
    /// while that note is there the hold is not written again. The note goes
    /// away by itself once a refresh goes through.
    ///
    /// <para>A TheTVDB outage holds every TheTVDB-numbered show at once, so it
    /// gets one note and one Activity line for the whole outage (the note lists
    /// the shows and how many). A wrong list for one show keeps its own note.</para>
    /// </summary>
    public static async Task RecordHoldAsync(
        IActivityFeedRepository activityFeedRepository,
        IHealthCheckRepository? health,
        MetadataCatalogueHold hold,
        string seriesId,
        string seriesTitle,
        string? jobId,
        CancellationToken cancellationToken)
    {
        if (hold == MetadataCatalogueHold.TvdbUnavailable && health is not null)
        {
            var existing = await health.GetAsync(OutageHealthId, cancellationToken);
            var shows = ReadHeldShows(existing);
            if (shows.Any(show => show.Id == seriesId))
            {
                return;
            }

            shows.Add(new HeldShow(seriesId, seriesTitle));
            if (existing is null)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "metadata.series.catalogue.held",
                    "TheTVDB did not answer, so the episode lists of the shows it numbers were left as they are. Deluno tries again at the next refresh; the Health screen lists the shows.",
                    null,
                    jobId,
                    null,
                    null,
                    cancellationToken);
            }

            await health.UpsertResultsAsync([OutageOutcome(shows)], cancellationToken);
            return;
        }

        var id = HoldHealthId(seriesId);
        if (health is not null && await health.GetAsync(id, cancellationToken) is not null)
        {
            return;
        }

        var message = HoldMessage(hold, seriesTitle);
        await activityFeedRepository.RecordActivityAsync(
            "metadata.series.catalogue.held",
            message,
            null,
            jobId,
            "series",
            seriesId,
            cancellationToken);
        if (health is not null)
        {
            await health.UpsertResultsAsync(
                [HealthCheckOutcome.Problem(
                    id,
                    HealthCheckCategories.Notice,
                    $"{seriesTitle}'s episode list was left as it is",
                    message,
                    cause: hold == MetadataCatalogueHold.TvdbUnavailable
                        ? "TheTVDB did not answer, and this show's episodes are numbered by TheTVDB."
                        : "TheTVDB's episode list does not match the episodes this show already has.",
                    fix: hold == MetadataCatalogueHold.TvdbUnavailable
                        ? "Nothing to do: Deluno tries again at the next refresh. Clear this note if you do not need it."
                        : "Check the TheTVDB link on the title, or choose the right record on the title's metadata screen, then clear this note.")],
                cancellationToken);
        }
    }

    /// <summary>A refresh that went through means the hold is over: its Health note is removed.</summary>
    public static async Task ClearHoldAsync(IHealthCheckRepository? health, string seriesId, CancellationToken cancellationToken)
    {
        if (health is null)
        {
            return;
        }

        var id = HoldHealthId(seriesId);
        if (await health.GetAsync(id, cancellationToken) is not null)
        {
            await health.RemoveAsync(id, cancellationToken);
        }

        var outage = await health.GetAsync(OutageHealthId, cancellationToken);
        if (outage is null)
        {
            return;
        }

        var shows = ReadHeldShows(outage);
        if (shows.RemoveAll(show => show.Id == seriesId) == 0)
        {
            return;
        }

        if (shows.Count == 0)
        {
            await health.RemoveAsync(OutageHealthId, cancellationToken);
        }
        else
        {
            await health.UpsertResultsAsync([OutageOutcome(shows)], cancellationToken);
        }
    }
}
