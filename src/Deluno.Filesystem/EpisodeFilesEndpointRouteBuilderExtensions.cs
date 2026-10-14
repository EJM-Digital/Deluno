using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

/// <summary>
/// Removing one episode's file from the series page (#1188). The file goes to
/// Deluno's recycle bin, never off the disk for good: the same path a film's
/// Files tab uses, so a restore puts the file back and the episode has it
/// again. Mapped from here for the same reason the film routes are: only this
/// project sees the series catalogue and the recycle bin together.
/// </summary>
public static class EpisodeFilesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoEpisodeFilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var series = endpoints.MapGroup("/api/series");

        series.MapDelete("/episodes/{episodeId}/file", async (
            string episodeId,
            ISeriesCatalogRepository repository,
            Deluno.Libraries.Data.ILibrariesRepository librariesRepository,
            IRecycleBinService recycleBinService,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            ILogger<MovieFileDeleteNotification> logger,
            [FromServices] IOutboundNotificationService? outboundNotificationService,
            [FromServices] INotificationService? notificationService,
            [FromServices] IMediaTagStore? mediaTagStore,
            CancellationToken cancellationToken) =>
        {
            var seriesId = await repository.GetEpisodeSeriesIdAsync(episodeId, cancellationToken);
            var filePath = await repository.GetEpisodeFilePathAsync(episodeId, cancellationToken);
            if (seriesId is null || string.IsNullOrWhiteSpace(filePath))
            {
                return Results.NotFound();
            }

            var inventory = await repository.GetInventoryDetailAsync(seriesId, cancellationToken);
            var episode = inventory?.Episodes.FirstOrDefault(item => string.Equals(item.EpisodeId, episodeId, StringComparison.OrdinalIgnoreCase));
            if (inventory is null || episode is null)
            {
                return Results.NotFound();
            }

            // The file belongs to the library whose own folder holds it; a path
            // outside every library is never touched.
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.NotFound();
            }

            var library = (await librariesRepository.ListLibrariesAsync(cancellationToken))
                .Where(item => !string.IsNullOrWhiteSpace(item.RootPath))
                .FirstOrDefault(item => IsInside(fullPath, item.RootPath!));
            if (library is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = ["That file is not inside one of your libraries, so Deluno will not move it."]
                });
            }

            var code = $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}";
            var fileName = Path.GetFileName(fullPath);
            if (File.Exists(fullPath))
            {
                var stored = await recycleBinService.StoreRemovedFileAsync(
                    library,
                    fullPath,
                    new RecycleBinFileOwner("tv", "series", seriesId, inventory.Title, inventory.StartYear, episodeId, episode.Monitored),
                    cancellationToken);
                if (stored is null)
                {
                    return Results.Problem("The file could not be moved to the recycle bin, so it was left where it is.", statusCode: StatusCodes.Status500InternalServerError);
                }
            }

            // The file
            // fields cleared, the episode Missing but still monitored and wanted, and the
            // show's own file notes corrected, in one step.
            await repository.ClearMislabelledEpisodeFileAsync(
                episodeId, filePath, cancellationToken, "The file was moved to the recycle bin, so Deluno will look for it again.");

            await activityFeedRepository.RecordActivityAsync(
                "library.episode.file-deleted",
                $"{inventory.Title} {code}'s file was moved to the recycle bin. Deluno will look for it again. You can restore it from there.",
                null,
                null,
                "episode",
                episodeId,
                cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesId, cancellationToken);

            var tags = mediaTagStore is null
                ? null
                : (await mediaTagStore.ListAsync(MediaKind.Series, seriesId, cancellationToken)).Select(assignment => assignment.Name).ToArray();
            await NotificationOutboundDispatch.DispatchIfEnabledAsync(
                outboundNotificationService,
                notificationService,
                "file.deleted",
                $"File removed: {fileName}",
                $"{fileName} ({inventory.Title} {code}) was moved to the recycle bin and can be restored there.",
                JsonSerializer.Serialize(new { fileName, seriesId, episodeId }),
                prefs => prefs.FileDeletedEnabled,
                "episode-file-delete",
                logger,
                cancellationToken,
                tags);

            return Results.Ok(new { deleted = true, episodeId });
        });

        return endpoints;
    }

    private static bool IsInside(string path, string root)
    {
        try
        {
            return Deluno.Contracts.SafePath.IsInside(Path.GetFullPath(root), path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
