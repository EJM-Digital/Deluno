using System.Collections.Concurrent;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker;

/// <param name="Path">The library file as Deluno holds it, exactly.</param>
/// <param name="Reason">What the tool did, in its words: "removed 2 audio tracks".</param>
/// <param name="Tool">Who changed it: "Weir".</param>
public sealed record ExternalFileChangedRequest(string? Path, string? Reason, string? Tool);

/// <summary>
/// "I changed this library file; re-read it" (#528).
///
/// <para>A tool such as Weir cleans a file already in the library and swaps it
/// in place. Deluno's record of that file is then stale: the languages, codecs
/// and size it shows, and every decision made from them. This takes the size
/// as it is now and owes the file a fresh read by the media probe and the
/// subtitle scan - the same passes that read every file - and nothing else.
/// No search, upgrade, rename or import starts from it; if the fresh read
/// changes whether a title meets its cutoff, that follows Deluno's normal
/// schedule.</para>
///
/// <para>Not <c>/external/trigger-refresh</c>: that asks for library searches,
/// which can download things.</para>
/// </summary>
public static class ExternalFileChangedEndpoints
{
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Recent = new(StringComparer.OrdinalIgnoreCase);

    public static IEndpointRouteBuilder MapDelunoExternalFileChanged(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/integrations/external/file-changed", async (
            HttpContext httpContext,
            [FromBody] ExternalFileChangedRequest request,
            [FromServices] IMediaStateRepository media,
            [FromServices] ILibrariesRepository libraries,
            [FromServices] IJobScheduler jobs,
            [FromServices] IActivityFeedRepository activity,
            [FromServices] TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;

            var path = request.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["path"] = ["Give the full path of the library file that changed."] });
            }

            FileInfo file;
            try
            {
                file = new FileInfo(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["path"] = ["That is not a valid file path."] });
            }

            if (!file.Exists)
            {
                return Results.NotFound(new { message = $"There is no file at {path}. Deluno re-reads a file that exists; a removed file is noticed by the library's own checks." });
            }

            var now = timeProvider.GetUtcNow();
            // The same path told twice in a minute is one change: the first call
            // already owes the file its read.
            if (Recent.TryGetValue(path, out var seen) && now - seen < CoalesceWindow)
            {
                return Results.Accepted(value: new { path, coalesced = true, message = "Already re-reading this file." });
            }

            var changed = new List<(MediaKind Kind, ChangedLibraryFile File)>();
            foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
            {
                foreach (var item in await media.MarkFileChangedAsync(kind, path, file.Length, cancellationToken))
                {
                    changed.Add((kind, item));
                }
            }

            if (changed.Count == 0)
            {
                return Results.NotFound(new { message = $"Deluno does not track a library file at {path}." });
            }

            Recent[path] = now;
            foreach (var old in Recent.Where(entry => now - entry.Value >= CoalesceWindow).Select(entry => entry.Key).ToArray())
            {
                Recent.TryRemove(old, out _);
            }

            foreach (var kind in changed.Select(item => item.Kind).Distinct())
            {
                var entity = kind == MediaKind.Series ? "series" : "movie";
                await jobs.EnqueueAsync(
                    new EnqueueJobRequest(JobType: "library.media.probe", Source: "external", PayloadJson: null, RelatedEntityType: entity, RelatedEntityId: null, DedupeKey: $"media-probe:{entity}"),
                    cancellationToken);
            }

            var known = (await libraries.ListLibrariesAsync(cancellationToken)).ToDictionary(library => library.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var libraryId in changed.Select(item => item.File.LibraryId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!known.TryGetValue(libraryId, out var library)) continue;
                var key = $"library.subtitles.scan:{library.Id}";
                await jobs.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: "library.subtitles.scan",
                        Source: library.MediaType,
                        PayloadJson: JsonSerializer.Serialize(new { libraryId = library.Id, libraryName = library.Name, mediaType = library.MediaType }),
                        RelatedEntityType: "library",
                        RelatedEntityId: library.Id,
                        DedupeKey: key),
                    cancellationToken);
            }

            var tool = string.IsNullOrWhiteSpace(request.Tool) ? "Another tool" : request.Tool.Trim();
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim().TrimEnd('.');
            var first = changed[0];
            // The library's own spelling of the path, not the caller's casing.
            await activity.RecordActivityAsync(
                "external.file-changed",
                $"{tool} changed {Path.GetFileName(first.File.FilePath)}{(reason is null ? string.Empty : $": {reason}")}. Deluno is re-reading it.",
                JsonSerializer.Serialize(new { Path = first.File.FilePath, Tool = tool, Reason = reason, SizeBytes = file.Length }),
                null,
                first.Kind == MediaKind.Series ? "series" : "movie",
                first.File.MediaId,
                cancellationToken);

            return Results.Accepted(value: new
            {
                path,
                coalesced = false,
                titles = changed.Select(item => new { kind = item.Kind == MediaKind.Series ? "series" : "movie", mediaId = item.File.MediaId, libraryId = item.File.LibraryId }).ToArray(),
                message = "Re-reading the file. Nothing is searched, upgraded or imported because of it."
            });
        })
        .RequireAuthorization(DelunoAuthorizationPolicies.Imports);

        return endpoints;
    }
}
