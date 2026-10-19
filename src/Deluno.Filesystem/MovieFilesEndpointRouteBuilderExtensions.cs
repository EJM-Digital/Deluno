using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Platform;
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
            Deluno.Libraries.Data.ILibrariesRepository librariesRepository,
            ISeriesCatalogRepository seriesRepository,
            ISubtitleInventoryService subtitleInventoryService,
            CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            // Every library the film holds a file in gets its own group (#1065).
            var copies = (await repository.ListLibraryFilesAsync(id, cancellationToken)).ToList();
            if (copies.Count == 0 && movie.FilePath is { Length: > 0 } onlyPath)
            {
                copies.Add(new MovieLibraryFile(movie.LibraryId ?? string.Empty, onlyPath, movie.FileSizeBytes, movie.CurrentQuality, movie.VideoCodec, movie.AudioCodec, movie.AudioChannels));
            }

            // The library that is the film's own primary copy comes first.
            copies = copies
                .OrderBy(copy => string.Equals(copy.FilePath, movie.FilePath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(copy => copy.LibraryId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var labelled = copies.Count > 1;
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var sharing = await LoadSharingAsync(movie, libraries, repository, seriesRepository, cancellationToken);
            var items = new List<MovieFileItem>();
            foreach (var copy in copies)
            {
                if (!File.Exists(copy.FilePath))
                {
                    continue;
                }

                var libraryName = labelled
                    ? libraries.FirstOrDefault(item => string.Equals(item.Id, copy.LibraryId, StringComparison.OrdinalIgnoreCase))?.Name ?? "Removed library"
                    : null;
                items.AddRange(await FilesForCopyAsync(copy, copy.LibraryId, libraryName, sharing.IsOwnFilesOnly(copy.FilePath, copy.LibraryId) ? sharing : null, subtitleInventoryService, cancellationToken));
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
            Deluno.Libraries.Data.ILibrariesRepository librariesRepository,
            ISeriesCatalogRepository seriesRepository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie?.FilePath is not { Length: > 0 } primaryPath)
            {
                return Results.NotFound();
            }

            var sharing = await LoadSharingAsync(movie, await librariesRepository.ListLibrariesAsync(cancellationToken), repository, seriesRepository, cancellationToken);
            if (!TryResolveWithinMovieFolder(primaryPath, movie.LibraryId, sharing, request.Path, out var subtitlePath) || !File.Exists(subtitlePath))
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
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A locked file (Windows) is an IOException; a folder the account may not write to (Linux, or a
                    // denied permission on Windows) is an UnauthorizedAccessException. Either way the file stays put.
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
            Deluno.Libraries.Data.ILibrariesRepository librariesRepository,
            ISeriesCatalogRepository seriesRepository,
            IRecycleBinService recycleBinService,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            ILogger<MovieFileDeleteNotification> logger,
            [FromServices] IOutboundNotificationService? outboundNotificationService,
            [FromServices] INotificationService? notificationService,
            [FromServices] IMediaTagStore? mediaTagStore,
            [FromServices] Deluno.Jobs.Data.IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            // The file belongs to exactly one library's copy (#1065): the one the
            // request names, or else the one whose own folder holds the path. A path is
            // only ever accepted inside that copy's own title folder.
            var copies = (await repository.ListLibraryFilesAsync(id, cancellationToken)).ToList();
            if (copies.Count == 0 && movie.FilePath is { Length: > 0 } soleFile)
            {
                copies.Add(new MovieLibraryFile(movie.LibraryId ?? string.Empty, soleFile, movie.FileSizeBytes, movie.CurrentQuality, movie.VideoCodec, movie.AudioCodec, movie.AudioChannels));
            }

            var sharing = await LoadSharingAsync(movie, await librariesRepository.ListLibrariesAsync(cancellationToken), repository, seriesRepository, cancellationToken);
            MovieLibraryFile? chosen;
            if (!string.IsNullOrWhiteSpace(request.LibraryId))
            {
                chosen = copies.FirstOrDefault(copy => string.Equals(copy.LibraryId, request.LibraryId, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                chosen = copies.FirstOrDefault(copy => TryResolveWithinMovieFolder(copy.FilePath, copy.LibraryId, sharing, request.Path, out _));
            }

            if (chosen is null)
            {
                return copies.Count == 0 || !string.IsNullOrWhiteSpace(request.LibraryId)
                    ? Results.NotFound()
                    : Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["path"] = ["That file could not be found under this title's own folder."]
                    });
            }

            var primaryPath = chosen.FilePath;

            if (!TryResolveWithinMovieFolder(primaryPath, chosen.LibraryId, sharing, request.Path, out var targetPath) || !File.Exists(targetPath))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["path"] = ["That file could not be found under this title's own folder."]
                });
            }

            var fileName = Path.GetFileName(targetPath);

            // #1188: a person's media only ever leaves through Deluno's recycle
            // bin, so it can be restored. Exactly this one file is moved, never
            // the folder around it.
            var wasPrimary = string.Equals(targetPath, primaryPath, StringComparison.OrdinalIgnoreCase);
            var libraryForBin = (await librariesRepository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, chosen.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (libraryForBin is null || await recycleBinService.StoreRemovedFileAsync(
                    libraryForBin,
                    targetPath,
                    new RecycleBinFileOwner("movies", "movie", id, movie.Title, movie.ReleaseYear, null, movie.Monitored),
                    cancellationToken) is null)
            {
                return Results.Problem("The file could not be moved to the recycle bin, so it was left where it is.", statusCode: StatusCodes.Status500InternalServerError);
            }

            MovieFileGoneOutcome goneOutcome = default;
            if (wasPrimary)
            {
                goneOutcome = await repository.ClearFileKeepWantedAsync(id, primaryPath, cancellationToken);
            }
            else
            {
                // A subtitle's scan state would otherwise keep claiming a
                // language is held after the file that held it is gone.
                await mediaSubtitleRepository.ClearScansAsync(MediaKind.Movie, [id], cancellationToken);
            }

            // #1188: a file deleted by hand usually means "get me a better one", so the film stays monitored and wanted.
            var primaryMessage = $"{movie.Title}'s file was moved to the recycle bin. Deluno will look for it again. You can restore it from there.";

            await activityFeedRepository.RecordActivityAsync(
                "library.movie.file-deleted",
                wasPrimary
                    ? primaryMessage
                    : $"A file was moved to the recycle bin from {movie.Title}'s folder, and can be restored there.",
                null,
                null,
                "movie",
                id,
                cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
            await Deluno.Jobs.Data.MediaServerTell.QueueAsync(
                jobScheduler,
                "movies",
                [new MediaServerPathChange(targetPath, MediaServerChangeKind.Deleted)],
                "movie.file-deleted",
                "movie",
                id,
                logger,
                cancellationToken);

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
                $"{fileName} was moved to the recycle bin from {movie.Title}'s folder, and can be restored there.",
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

    private static async Task<List<MovieFileItem>> FilesForCopyAsync(
        MovieLibraryFile copy,
        string libraryId,
        string? libraryName,
        FolderSharing? ownFilesOnly,
        ISubtitleInventoryService subtitleInventoryService,
        CancellationToken cancellationToken)
    {
        var primaryPath = copy.FilePath;
        var folder = Path.GetDirectoryName(primaryPath);
        if (folder is null || !Directory.Exists(folder))
        {
            // Still report the tracked file itself - the folder having gone
            // is not a reason to hide the one row the record already knows.
            return [PrimaryRow(copy, primaryPath, libraryId, libraryName)];
        }

        var inventory = await subtitleInventoryService.InspectAsync(primaryPath, probeContainer: true, cancellationToken);
        var items = new List<MovieFileItem> { PrimaryRow(copy, primaryPath, libraryId, libraryName, inventory.Probed) };

        var accountedFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryPath };
        foreach (var subtitle in inventory.Subtitles)
        {
            if (ownFilesOnly is not null && subtitle.Path is { Length: > 0 } flatSubtitle && !ownFilesOnly.Owns(primaryPath, flatSubtitle))
            {
                continue;
            }

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
                HearingImpaired: subtitle.HearingImpaired,
                LibraryId: libraryId,
                LibraryName: libraryName));
        }

        // Everything else in the folder - extras, samples, nfo files,
        // artwork - that neither the wanted state nor the subtitle
        // inventory already accounts for (#782).
        IEnumerable<string> restOfFolder;
        try
        {
            // A flat film shares its folder with every other title, so only its own
            // named companions are its files; never walk the shared folder.
            restOfFolder = ownFilesOnly is not null
                ? Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => ownFilesOnly.Owns(primaryPath, path))
                : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
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
                HearingImpaired: null,
                LibraryId: libraryId,
                LibraryName: libraryName));
        }

        return items;
    }

    private static MovieFileItem PrimaryRow(MovieLibraryFile copy, string primaryPath, string libraryId, string? libraryName, ProbedMediaFacts? probed = null)
        => new(
            Path: primaryPath,
            SizeBytes: copy.FileSizeBytes,
            Kind: "video",
            IsPrimary: true,
            Quality: copy.CurrentQuality,
            VideoCodec: probed?.VideoCodec ?? copy.VideoCodec,
            AudioCodec: probed?.AudioCodec ?? copy.AudioCodec,
            AudioChannels: probed?.AudioChannels ?? copy.AudioChannels,
            Language: null,
            HearingImpaired: null,
            LibraryId: libraryId,
            LibraryName: libraryName);

    private static bool TryResolveWithinMovieFolder(string primaryFilePath, string? libraryId, FolderSharing sharing, string? requestedPath, out string resolved)
    {
        var folder = Path.GetDirectoryName(primaryFilePath);
        if (folder is null)
        {
            resolved = string.Empty;
            return false;
        }

        if (!MovieFilePathSafety.TryResolveWithinFolder(folder, requestedPath, out resolved))
        {
            return false;
        }

        // A folder shared with anything else (a library's root, a subfolder holding
        // another film or an episode, a library that has since been removed): the
        // folder is everyone's, so only this film's own files count as its files.
        if (sharing.IsOwnFilesOnly(primaryFilePath, libraryId)
            && !sharing.Owns(primaryFilePath, resolved))
        {
            resolved = string.Empty;
            return false;
        }

        return true;
    }

    private static async Task<FolderSharing> LoadSharingAsync(
        MovieListItem movie,
        IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem> libraries,
        IMovieCatalogRepository repository,
        ISeriesCatalogRepository seriesRepository,
        CancellationToken cancellationToken)
    {
        var others = new List<string>();
        others.AddRange(await repository.ListAllPrimaryFilePathsAsync(cancellationToken));
        others.AddRange(await seriesRepository.ListAllEpisodeFilePathsAsync(cancellationToken));

        // A companion without the release's quality tag ("Movie (2020).nfo" beside
        // "Movie (2020) - 1080p.mkv") is still this film's: accept the title-year base too.
        var alternates = new List<string>();
        if (!string.IsNullOrWhiteSpace(movie.Title))
        {
            var titleYear = movie.ReleaseYear is { } year ? $"{movie.Title} ({year})" : movie.Title;
            alternates.Add(titleYear);
            var stripped = string.Concat(titleYear.Where(character => Array.IndexOf(Path.GetInvalidFileNameChars(), character) < 0));
            if (!string.Equals(stripped, titleYear, StringComparison.Ordinal))
            {
                alternates.Add(stripped);
            }
        }

        return new FolderSharing(
            libraries.Select(item => item.RootPath).ToArray(),
            others,
            libraries.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            alternates);
    }

    /// <summary>What decides whether a film's folder is its own: every library root, every other tracked primary file, which libraries still exist.</summary>
    private sealed record FolderSharing(
        IReadOnlyList<string> LibraryRoots,
        IReadOnlyList<string> OtherPrimaryPaths,
        IReadOnlySet<string> LibraryIds,
        IReadOnlyList<string> AlternateBaseNames)
    {
        public bool IsOwnFilesOnly(string primaryFilePath, string? libraryId)
            => MovieFilePathSafety.IsSharedFolder(
                primaryFilePath,
                LibraryRoots,
                OtherPrimaryPaths,
                string.IsNullOrWhiteSpace(libraryId) || LibraryIds.Contains(libraryId));

        public bool Owns(string primaryFilePath, string candidatePath)
            => MovieFilePathSafety.BelongsToFlatFilm(primaryFilePath, candidatePath, AlternateBaseNames);
    }
}


/// <summary>Log-category marker for the movie Files tab's delete endpoint (#856); the endpoint class itself is static and cannot be an ILogger&lt;T&gt; argument.</summary>
public sealed class MovieFileDeleteNotification;


public sealed record UpdateMovieFileQualityRequest(string Quality);

public sealed record UpdateMovieFileLanguageRequest(string Path, string Language, bool HearingImpaired = false);

/// <param name="LibraryId">Which library's copy the file belongs to. Optional: omitted, the library whose own folder holds the path is used (#1065).</param>
public sealed record DeleteMovieFileRequest(string Path, string? LibraryId = null);
