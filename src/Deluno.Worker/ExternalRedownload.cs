using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Platform;
using Deluno.Platform.Data;
using Deluno.Security;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker;

/// <param name="Path">The library file as Deluno holds it, exactly.</param>
/// <param name="Reason">Why, in the tool's words: "Keep Japanese audio (removed earlier by Weir)".</param>
/// <param name="Tool">Who is asking: "Weir".</param>
public sealed record ExternalRedownloadRequest(string? Path, string? Reason, string? Tool);

/// <param name="MediaKind"><c>movie</c> or <c>series</c>.</param>
/// <param name="SearchJobId">Null when the file is gone but no search could be queued.</param>
public sealed record ExternalRedownloadResult(
    string TitleId,
    string MediaKind,
    string? SearchJobId,
    bool FileRemoved,
    string Message);

/// <summary>
/// "Download this title again" (#540).
///
/// <para>Weir removes unwanted tracks from files already in the library, and a
/// removal is final. When someone later wants a removed track back - "start
/// keeping Japanese audio" - only a new download brings it. A search on its own
/// does not: the library already holds the title at its quality, so nothing
/// replaces it. The file has to go first, as Sonarr and Radarr require.</para>
///
/// <para>So, in order, and nothing further once a step fails: the file goes to
/// the recycle bin (Deluno's normal delete, undoable while the bin keeps it);
/// the title is marked missing; whatever would stop the fetch is cleared, as a
/// person's Force a re-download clears it - above all the finished download a
/// client still remembers and would quietly refuse; then one search for that
/// title is queued. A new copy comes in through the library's usual workflow,
/// including processing by Weir with its current rules.</para>
/// </summary>
public sealed class ExternalRedownloadService(
    ILibrariesRepository libraries,
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series,
    IRecycleBinService recycleBin,
    AcquisitionOverrideService overrides,
    IDownloadDispatchesRepository dispatches,
    IProcessorRepository processors,
    IUnifiedExclusionRepository exclusions,
    IBlockedReleaseRepository blockedReleases,
    IJobScheduler jobs,
    IActivityFeedRepository activity,
    ILogger<ExternalRedownloadService> logger)
{
    /// <summary>The outcome, or why nothing was done: a missing path is <see cref="NotTracked"/>, a file that would not move is <see cref="NotRemoved"/>.</summary>
    public abstract record Outcome;

    public sealed record Done(ExternalRedownloadResult Result) : Outcome;

    public sealed record NotTracked(string Message) : Outcome;

    public sealed record NotRemoved(string Message) : Outcome;

    public async Task<Outcome> RedownloadAsync(ExternalRedownloadRequest request, CancellationToken cancellationToken)
    {
        var path = request.Path!.Trim();
        var allLibraries = await libraries.ListLibrariesAsync(cancellationToken);
        var tracked = await FindAsync(allLibraries, path, cancellationToken);
        if (tracked is null)
        {
            return new NotTracked($"Deluno does not track a library file at {path}.");
        }

        var library = allLibraries.First(item => string.Equals(item.Id, tracked.LibraryId, StringComparison.OrdinalIgnoreCase));
        var moved = await recycleBin.MoveAsync([new TrackedLibraryFile(tracked.LibraryId, tracked.FilePath)], allLibraries, cancellationToken);
        // A film alone in its folder goes to the bin as the folder: one folder,
        // no files. That is removed too - counting only files answered "not
        // removed" while the file sat in the bin (seen on the rig).
        if (moved.MovedFileCount + moved.MovedFolderCount == 0)
        {
            var why = moved.Warnings.Count > 0 ? string.Join(" ", moved.Warnings) : "The file could not be moved.";
            return new NotRemoved($"Deluno did not remove {Path.GetFileName(tracked.FilePath)}, so nothing was searched: {why}");
        }

        var marked = tracked.Kind == "series"
            ? await series.MarkTrackedFileMissingAsync(tracked.TitleId, tracked.EpisodeId, tracked.LibraryId, tracked.FilePath, cancellationToken)
            : await movies.MarkTrackedFileMissingAsync(tracked.TitleId, tracked.LibraryId, tracked.FilePath, cancellationToken);
        if (!marked)
        {
            logger.LogWarning("Moved {Path} to the recycle bin but could not mark it missing; the library's file check will.", tracked.FilePath);
        }

        var mediaType = tracked.Kind == "series" ? "tv" : "movies";
        try
        {
            await overrides.ForceAsync(
                await AcquisitionBlockerSources.OverrideRequestAsync(
                    mediaType, tracked.TitleId, tracked.Title, tracked.ImdbId, dispatches, processors, exclusions, blockedReleases, cancellationToken),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // What could not be cleared is also cleared by a person's Force later;
            // the search still runs.
            logger.LogWarning(exception, "Could not clear everything in the way of fetching {Title} again.", tracked.Title);
        }

        var tool = string.IsNullOrWhiteSpace(request.Tool) ? "Another tool" : request.Tool.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim().TrimEnd('.');
        string? searchJobId = null;
        try
        {
            var job = await jobs.EnqueueAsync(
                new EnqueueJobRequest(
                    LibrarySearchJobTypes.For(mediaType),
                    "external-redownload",
                    JsonSerializer.Serialize(new
                    {
                        libraryId = library.Id,
                        libraryName = library.Name,
                        mediaType,
                        checkMissing = true,
                        checkUpgrades = true,
                        maxItems = 1,
                        retryDelayHours = 24,
                        triggeredBy = "external-redownload",
                        targetEntityId = tracked.TitleId
                    }),
                    tracked.Kind,
                    tracked.TitleId,
                    DedupeKey: $"external-redownload:{tracked.Kind}:{tracked.TitleId}"),
                cancellationToken);
            searchJobId = job.Id;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Removed {Path} for a re-download but could not queue the search.", tracked.FilePath);
        }

        var name = tracked.DisplayName;
        var message = searchJobId is null
            ? $"{tool} asked Deluno to download {name} again{(reason is null ? string.Empty : $": {reason}")}. The file is in the recycle bin, but the search could not be queued; search for it from its page."
            : $"{tool} asked Deluno to download {name} again{(reason is null ? string.Empty : $": {reason}")}. The file is in the recycle bin and a search has started.";
        await activity.RecordActivityAsync(
            "external.redownload",
            message,
            JsonSerializer.Serialize(new { Path = tracked.FilePath, Tool = tool, Reason = reason, SearchJobId = searchJobId }),
            searchJobId,
            tracked.Kind,
            tracked.TitleId,
            cancellationToken);

        return new Done(new ExternalRedownloadResult(tracked.TitleId, tracked.Kind, searchJobId, FileRemoved: true, message));
    }

    private sealed record Tracked(string Kind, string TitleId, string? EpisodeId, string LibraryId, string FilePath, string Title, string? ImdbId, string DisplayName);

    /// <summary>
    /// The tracked file at exactly this path. Case matters only where the file
    /// system says it does.
    /// </summary>
    private async Task<Tracked?> FindAsync(IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem> allLibraries, string path, CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var library in allLibraries)
        {
            if (string.Equals(library.MediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                await foreach (var file in series.StreamTrackedFilesAsync(library.Id, cancellationToken))
                {
                    if (!string.Equals(file.FilePath, path, comparison)) continue;
                    var show = await series.GetByIdAsync(file.SeriesId, cancellationToken);
                    var display = file.SeasonNumber is { } season && file.EpisodeNumber is { } episode
                        ? $"{file.Title} S{season:00}E{episode:00}"
                        : file.Title;
                    return new Tracked("series", file.SeriesId, file.EpisodeId, file.LibraryId, file.FilePath, file.Title, show?.ImdbId, display);
                }
            }
            else
            {
                await foreach (var file in movies.StreamTrackedFilesAsync(library.Id, cancellationToken))
                {
                    if (!string.Equals(file.FilePath, path, comparison)) continue;
                    var movie = await movies.GetByIdAsync(file.MovieId, cancellationToken);
                    return new Tracked("movie", file.MovieId, null, file.LibraryId, file.FilePath, file.Title, movie?.ImdbId, file.Title);
                }
            }
        }

        return null;
    }
}

public static class ExternalRedownloadEndpoints
{
    public static IEndpointRouteBuilder MapDelunoExternalRedownload(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/integrations/external/redownload", async (
            HttpContext httpContext,
            [FromBody] ExternalRedownloadRequest request,
            [FromServices] ExternalRedownloadService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;

            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["path"] = ["Give the full path of the library file to download again."] });
            }

            return await service.RedownloadAsync(request, cancellationToken) switch
            {
                ExternalRedownloadService.Done done => Results.Accepted(value: new
                {
                    titleId = done.Result.TitleId,
                    mediaKind = done.Result.MediaKind,
                    searchJobId = done.Result.SearchJobId,
                    fileRemoved = done.Result.FileRemoved,
                    message = done.Result.Message
                }),
                ExternalRedownloadService.NotTracked notTracked => Results.NotFound(new { message = notTracked.Message }),
                ExternalRedownloadService.NotRemoved notRemoved => Results.Conflict(new { message = notRemoved.Message }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        })
        .RequireAuthorization(DelunoAuthorizationPolicies.Imports);

        return endpoints;
    }
}
