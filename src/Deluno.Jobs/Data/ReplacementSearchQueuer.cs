using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// Queues a one-title replacement search for the title a download dispatch
/// was for, the same way whether the trigger was a person marking a queue
/// item as bad, a health check replacing a release, a processor giving up on
/// a hand-off (#914, #929), or an unmatched import nobody claimed in time
/// (#888). Extracted from <c>DownloadClientTelemetryService</c>'s own
/// (private, single-caller) version so every give-up path points a new
/// search at the same title the same way, instead of each growing its own
/// copy of the season-pack-vs-single-title branch.
/// </summary>
public static class ReplacementSearchQueuer
{
    /// <summary>
    /// Whether <see cref="EnqueueAsync"/> can act on this dispatch's entity
    /// type. A manual grab dispatches with EntityType "movie" or "series"; an
    /// automatic per-episode search (EpisodeSearchJobHandler) dispatches with
    /// "episode". Checking only the first two silently drops the replacement
    /// search for every automatically-searched TV episode (#647).
    /// </summary>
    public static bool Supports(string entityType)
        => entityType is "movie" or "series" or "episode";

    /// <summary>
    /// Enqueues a targeted, one-item search job for the title
    /// <paramref name="dispatch"/> was for. A series dispatch whose notes
    /// record the season-pack episode ids it covered (#875, #829) queues one
    /// search per episode instead of one for the whole show, so a series with
    /// other episodes already in hand is not searched wholesale over one bad
    /// release.
    /// </summary>
    public static async Task EnqueueAsync(
        IJobScheduler jobScheduler,
        DownloadDispatchItem dispatch,
        string libraryName,
        CancellationToken cancellationToken)
    {
        var seasonEpisodeIds = dispatch.EntityType == "series"
            ? ExtractSeasonEpisodeIds(dispatch.NotesJson)
            : [];

        if (seasonEpisodeIds.Count > 0)
        {
            foreach (var episodeId in seasonEpisodeIds)
            {
                await jobScheduler.EnqueueAsync(new EnqueueJobRequest(
                    LibrarySearchJobTypes.For(dispatch.MediaType),
                    "download-health",
                    JsonSerializer.Serialize(new
                    {
                        libraryId = dispatch.LibraryId,
                        libraryName,
                        mediaType = dispatch.MediaType,
                        checkMissing = true,
                        checkUpgrades = true,
                        maxItems = 1,
                        retryDelayHours = 24,
                        triggeredBy = "health-replacement",
                        targetEntityId = episodeId
                    }),
                    "download_dispatch",
                    dispatch.Id,
                    IdempotencyKey: $"download-health-replacement:{dispatch.Id}:{episodeId}",
                    DedupeKey: $"download-health-replacement:{dispatch.Id}:{episodeId}"), cancellationToken);
            }

            return;
        }

        await jobScheduler.EnqueueAsync(new EnqueueJobRequest(
            LibrarySearchJobTypes.For(dispatch.MediaType),
            "download-health",
            JsonSerializer.Serialize(new
            {
                libraryId = dispatch.LibraryId,
                libraryName,
                mediaType = dispatch.MediaType,
                checkMissing = true,
                checkUpgrades = true,
                maxItems = 1,
                retryDelayHours = 24,
                triggeredBy = "health-replacement",
                targetEntityId = dispatch.EntityId
            }),
            "download_dispatch",
            dispatch.Id,
            IdempotencyKey: $"download-health-replacement:{dispatch.Id}",
            DedupeKey: $"download-health-replacement:{dispatch.Id}"), cancellationToken);
    }

    /// <summary>
    /// The episode ids a season-pack dispatch's own notes recorded it covers
    /// (SeasonSearchExecutor's <c>episodeIds</c> field). Absent or unparsable
    /// notes fall back to the series-wide retry rather than silently
    /// retrying nothing.
    /// </summary>
    private static IReadOnlyList<string> ExtractSeasonEpisodeIds(string? notesJson)
    {
        if (string.IsNullOrWhiteSpace(notesJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(notesJson);
            if (!document.RootElement.TryGetProperty("episodeIds", out var episodeIds)
                || episodeIds.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ids = new List<string>();
            foreach (var element in episodeIds.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
