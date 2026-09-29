using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

/// <summary>
/// Every file Deluno can see on disk for a movie (#782), not only the one it
/// tracks. Deluno.Movies cannot depend on this project (Deluno.Filesystem
/// already depends on Deluno.Movies), which is why these routes are mapped
/// from here rather than beside the rest of <c>/api/movies</c> - the only
/// project that can see both <see cref="IMovieCatalogRepository"/> and
/// <see cref="ISubtitleInventoryService"/> at once.
/// </summary>
public static class MovieFilesEndpointRouteBuilderExtensions
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".webm"
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".ass", ".ssa", ".vtt"
    };

    public static IEndpointRouteBuilder MapDelunoMovieFilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var movies = endpoints.MapGroup("/api/movies");

        movies.MapGet("/{id}/files", async (
            string id,
            IMovieCatalogRepository repository,
            ISubtitleInventoryService subtitleInventoryService,
            CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            if (movie.FilePath is not { Length: > 0 } primaryPath || !File.Exists(primaryPath))
            {
                return Results.Ok(Array.Empty<MovieFileItem>());
            }

            var folder = Path.GetDirectoryName(primaryPath);
            if (folder is null || !Directory.Exists(folder))
            {
                // Still report the tracked file itself - the folder having gone
                // is not a reason to hide the one row the record already knows.
                return Results.Ok(new[] { PrimaryRow(movie, primaryPath) });
            }

            var inventory = await subtitleInventoryService.InspectAsync(primaryPath, probeContainer: true, cancellationToken);

            var items = new List<MovieFileItem>
            {
                PrimaryRow(movie, primaryPath, inventory.Probed)
            };

            var accountedFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryPath };
            foreach (var subtitle in inventory.Subtitles)
            {
                if (subtitle.Path is not { Length: > 0 } subtitlePath || !accountedFor.Add(subtitlePath))
                {
                    continue;
                }

                long? size = null;
                try { size = new FileInfo(subtitlePath).Length; } catch (IOException) { }

                items.Add(new MovieFileItem(
                    Path: subtitlePath,
                    SizeBytes: size,
                    Kind: "subtitle",
                    IsPrimary: false,
                    Quality: null,
                    VideoCodec: null,
                    AudioCodec: null,
                    AudioChannels: null,
                    Language: subtitle.Language,
                    HearingImpaired: subtitle.HearingImpaired));
            }

            // Everything else in the folder - extras, samples, nfo files,
            // artwork - that neither the wanted state nor the subtitle
            // inventory already accounts for. This is the whole point of
            // #782: a Files tab that shows every file on disk, not only the
            // one Deluno picked as "the" file.
            IEnumerable<string> restOfFolder;
            try
            {
                restOfFolder = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                restOfFolder = [];
            }
            catch (UnauthorizedAccessException)
            {
                restOfFolder = [];
            }

            foreach (var path in restOfFolder.Take(1000))
            {
                if (!accountedFor.Add(path))
                {
                    continue;
                }

                long? size = null;
                try { size = new FileInfo(path).Length; } catch (IOException) { }

                var extension = Path.GetExtension(path);
                var kind = VideoExtensions.Contains(extension) ? "video"
                    : SubtitleExtensions.Contains(extension) ? "subtitle"
                    : "other";

                items.Add(new MovieFileItem(
                    Path: path,
                    SizeBytes: size,
                    Kind: kind,
                    IsPrimary: false,
                    Quality: null,
                    VideoCodec: null,
                    AudioCodec: null,
                    AudioChannels: null,
                    Language: null,
                    HearingImpaired: null));
            }

            return Results.Ok(items);
        });

        movies.MapPut("/{id}/files/quality", async (
            string id,
            [FromBody] UpdateMovieFileQualityRequest request,
            IMovieCatalogRepository repository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Quality))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["quality"] = ["Choose a quality before saving."]
                });
            }

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            var trimmedQuality = request.Quality.Trim();
            var updated = await repository.UpdateCurrentQualityOverrideAsync(id, trimmedQuality, cancellationToken);
            if (!updated)
            {
                return Results.NotFound(new { message = "This movie has no tracked file to correct." });
            }

            await activityFeedRepository.RecordActivityAsync(
                "library.movie.file-quality-edited",
                $"{movie?.Title ?? "A title"}'s file was marked as {trimmedQuality}.",
                null,
                null,
                "movie",
                id,
                cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
            return Results.Ok(new { quality = trimmedQuality });
        });

        movies.MapPut("/{id}/files/language", async (
            string id,
            [FromBody] UpdateMovieFileLanguageRequest request,
            IMovieCatalogRepository repository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie?.FilePath is not { Length: > 0 } primaryPath)
            {
                return Results.NotFound();
            }

            if (!TryResolveWithinMovieFolder(primaryPath, request.Path, out var subtitlePath) || !File.Exists(subtitlePath))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["path"] = ["That file could not be found under this title's own folder."]
                });
            }

            if (string.IsNullOrWhiteSpace(request.Language))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["language"] = ["Choose a language before saving."]
                });
            }

            var newName = SubtitleFileNaming.For(primaryPath, request.Language, request.HearingImpaired);
            var newPath = Path.Combine(Path.GetDirectoryName(subtitlePath)!, newName);
            if (!string.Equals(newPath, subtitlePath, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(newPath))
                {
                    return Results.Conflict(new { message = $"A subtitle named \"{newName}\" already exists." });
                }

                try
                {
                    File.Move(subtitlePath, newPath);
                }
                catch (IOException)
                {
                    return Results.Problem("The subtitle file could not be renamed.", statusCode: StatusCodes.Status500InternalServerError);
                }
            }

            await activityFeedRepository.RecordActivityAsync(
                "library.movie.file-language-edited",
                $"A subtitle for {movie.Title} was marked as {request.Language}.",
                null,
                null,
                "movie",
                id,
                cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
            return Results.Ok(new { path = newPath, language = request.Language });
        });

        movies.MapDelete("/{id}/files", async (
            string id,
            [FromBody] DeleteMovieFileRequest request,
            IMovieCatalogRepository repository,
            IMediaSubtitleRepository mediaSubtitleRepository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            ILogger<MovieFileDeleteNotification> logger,
            [FromServices] IOutboundNotificationService? outboundNotificationService,
            [FromServices] INotificationService? notificationService,
            [FromServices] IMediaTagStore? mediaTagStore,
            CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie?.FilePath is not { Length: > 0 } primaryPath)
            {
                return Results.NotFound();
            }

            if (!TryResolveWithinMovieFolder(primaryPath, request.Path, out var targetPath) || !File.Exists(targetPath))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["path"] = ["That file could not be found under this title's own folder."]
                });
            }

            var fileName = Path.GetFileName(targetPath);

            try
            {
                File.Delete(targetPath);
            }
            catch (IOException)
            {
                return Results.Problem("The file could not be deleted.", statusCode: StatusCodes.Status500InternalServerError);
            }

            var wasPrimary = string.Equals(targetPath, primaryPath, StringComparison.OrdinalIgnoreCase);
            if (wasPrimary)
            {
                await repository.ClearFileAsync(id, primaryPath, cancellationToken);
            }
            else
            {
                // A subtitle's scan state would otherwise keep claiming a
                // language is held after the file that held it is gone.
                await mediaSubtitleRepository.ClearScansAsync(MediaKind.Movie, [id], cancellationToken);
            }

            await activityFeedRepository.RecordActivityAsync(
                "library.movie.file-deleted",
                wasPrimary
                    ? $"{movie.Title}'s file was deleted from disk, so Deluno stopped wanting it."
                    : $"A file was deleted from {movie.Title}'s folder.",
                null,
                null,
                "movie",
                id,
                cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);

            // Same "file delete" concept the import pipeline fires for a
            // replaced file (#856) - a person deleting a file by hand from
            // the Files tab is just as much a file being removed.
            var movieTags = mediaTagStore is null
                ? null
                : (await mediaTagStore.ListAsync(MediaKind.Movie, id, cancellationToken)).Select(assignment => assignment.Name).ToArray();
            await NotificationOutboundDispatch.DispatchIfEnabledAsync(
                outboundNotificationService,
                notificationService,
                "file.deleted",
                $"File removed: {fileName}",
                $"{fileName} was deleted from {movie.Title}'s folder.",
                JsonSerializer.Serialize(new { fileName, movieId = id, wasPrimary }),
                prefs => prefs.FileDeletedEnabled,
                "movie-files-delete",
                logger,
                cancellationToken,
                movieTags);

            return Results.Ok(new { deleted = true, wasPrimary });
        });

        return endpoints;
    }

    private static MovieFileItem PrimaryRow(MovieListItem movie, string primaryPath, ProbedMediaFacts? probed = null)
        => new(
            Path: primaryPath,
            SizeBytes: movie.FileSizeBytes,
            Kind: "video",
            IsPrimary: true,
            Quality: movie.CurrentQuality,
            VideoCodec: probed?.VideoCodec ?? movie.VideoCodec,
            AudioCodec: probed?.AudioCodec ?? movie.AudioCodec,
            AudioChannels: probed?.AudioChannels ?? movie.AudioChannels,
            Language: null,
            HearingImpaired: null);

    private static bool TryResolveWithinMovieFolder(string primaryFilePath, string? requestedPath, out string resolved)
    {
        var folder = Path.GetDirectoryName(primaryFilePath);
        if (folder is null)
        {
            resolved = string.Empty;
            return false;
        }

        return MovieFilePathSafety.TryResolveWithinFolder(folder, requestedPath, out resolved);
    }
}

/// <summary>
/// A requested path is only ever accepted when it resolves to somewhere
/// under the movie's own folder - never an arbitrary absolute path the
/// client happened to send, which is what would let one title's edit surface
/// delete or rename a file anywhere else on the host. Its own class, and
/// public, so this rule is tested directly rather than only through the
/// endpoints that lean on it (#782).
/// </summary>
/// <summary>Log-category marker for the movie Files tab's delete endpoint (#856); the endpoint class itself is static and cannot be an ILogger&lt;T&gt; argument.</summary>
public sealed class MovieFileDeleteNotification;

public static class MovieFilePathSafety
{
    public static bool TryResolveWithinFolder(string folder, string? requestedPath, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        string fullFolder;
        string fullRequested;
        try
        {
            fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            fullRequested = Path.GetFullPath(requestedPath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullRequested.StartsWith(fullFolder + Path.DirectorySeparatorChar, comparer)
            && !string.Equals(fullRequested, fullFolder, comparer))
        {
            return false;
        }

        resolved = fullRequested;
        return true;
    }
}

public sealed record UpdateMovieFileQualityRequest(string Quality);

public sealed record UpdateMovieFileLanguageRequest(string Path, string Language, bool HearingImpaired = false);

public sealed record DeleteMovieFileRequest(string Path);
