using Deluno.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Quality;
using Deluno.Realtime;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker;

/// <param name="LibraryId">The library to put it in, or to move it to.</param>
public sealed record MediaLibraryMembershipRequest(string? LibraryId);

/// <summary>One library a title is in.</summary>
/// <param name="CanRemove">It holds no file there and is in another library too.</param>
/// <param name="CanMove">It holds no file there, so nothing on disk has to follow.</param>
public sealed record MediaLibraryMembershipItem(string LibraryId, string LibraryName, bool HasFile, string WantedStatus, bool CanRemove, bool CanMove);

/// <summary>
/// The libraries a title is in, chosen by a person (#530).
///
/// <para>A film can be kept in a second library on purpose - an HD and a 4K
/// copy - and taken out of one it holds nothing in. A show is in one library and
/// can be moved: its episodes carry one library each, so two copies of a show is
/// not something Deluno can hold. A library holding a file keeps the title: the
/// file lives in that library's folder, and Remove is how a copy goes.</para>
/// </summary>
public static class MediaLibraryMembershipEndpoints
{
    public static IEndpointRouteBuilder MapDelunoMediaLibraryMembership(this IEndpointRouteBuilder endpoints)
    {
        foreach (var (route, kind, mediaType, noun, entity) in new[]
        {
            ("/api/movies/{id}/libraries", MediaKind.Movie, "movies", "film", "Movie"),
            ("/api/series/{id}/libraries", MediaKind.Series, "tv", "show", "Series")
        })
        {
            var group = endpoints.MapGroup(route).RequireAuthorization(DelunoAuthorizationPolicies.Write);

            group.MapGet(string.Empty, async (string id, [FromServices] IMediaStateRepository media, [FromServices] ILibrariesRepository libraries, CancellationToken cancellationToken) =>
                await media.GetByIdAsync(kind, id, cancellationToken) is null
                    ? Results.NotFound()
                    : Results.Ok(await ListAsync(kind, id, media, libraries, cancellationToken)));

            // Keep it in one more library too: films only.
            group.MapPost(string.Empty, async (
                string id,
                [FromBody] MediaLibraryMembershipRequest request,
                [FromServices] IMediaStateRepository media,
                [FromServices] ILibrariesRepository libraries,
                [FromServices] IMediaDecisionService decisions,
                [FromServices] IRealtimeEventPublisher realtime,
                CancellationToken cancellationToken) =>
            {
                var title = await media.GetByIdAsync(kind, id, cancellationToken);
                if (title is null) return Results.NotFound();
                if (kind == MediaKind.Series)
                {
                    return Results.Conflict(new { message = "A show is in one library. Move it to another library instead." });
                }

                var library = (await libraries.ListLibrariesAsync(cancellationToken))
                    .FirstOrDefault(item => item.MediaType == mediaType && string.Equals(item.Id, request.LibraryId, StringComparison.OrdinalIgnoreCase));
                if (library is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["libraryId"] = [$"Choose one of your {noun} libraries."] });
                }

                // A new copy in that library: it has no file there yet.
                var decision = decisions.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: library.MediaType,
                    HasFile: false,
                    CurrentQuality: null,
                    CutoffQuality: library.CutoffQuality,
                    UpgradeUntilCutoff: library.UpgradeUntilCutoff,
                    UpgradeUnknownItems: library.UpgradeUnknownItems));
                var existing = await media.ListWantedByIdsAsync(kind, [id], cancellationToken);
                if (!existing.Any(row => string.Equals(row.LibraryId, library.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    await media.EnsureWantedStateAsync(kind, id, library.Id, decision.WantedStatus, decision.WantedReason, false, decision.CurrentQuality, decision.TargetQuality, decision.QualityCutoffMet, cancellationToken);
                }

                await realtime.PublishEntityChangedAsync(entity, id, cancellationToken);
                return Results.Ok(await ListAsync(kind, id, media, libraries, cancellationToken));
            });

            group.MapDelete("{libraryId}", async (string id, string libraryId, [FromServices] IMediaStateRepository media, [FromServices] ILibrariesRepository libraries, [FromServices] IRealtimeEventPublisher realtime, CancellationToken cancellationToken) =>
            {
                if (await media.GetByIdAsync(kind, id, cancellationToken) is null) return Results.NotFound();
                var memberships = await ListAsync(kind, id, media, libraries, cancellationToken);
                var membership = memberships.FirstOrDefault(item => string.Equals(item.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase));
                if (membership is null) return Results.NotFound();
                if (!membership.CanRemove)
                {
                    return Results.Conflict(new
                    {
                        message = membership.HasFile
                            ? $"This {noun} has a file in {membership.LibraryName}. Remove that copy, with its file, from the {noun}'s page."
                            : $"{membership.LibraryName} is the only library this {noun} is in. Move it instead, or remove the {noun}."
                    });
                }

                await media.RemoveFromLibraryAsync(kind, id, libraryId, cancellationToken);
                await realtime.PublishEntityChangedAsync(entity, id, cancellationToken);
                return Results.Ok(await ListAsync(kind, id, media, libraries, cancellationToken));
            });

            group.MapPut("{libraryId}", async (
                string id,
                string libraryId,
                [FromBody] MediaLibraryMembershipRequest request,
                [FromServices] IMediaStateRepository media,
                [FromServices] ILibrariesRepository libraries,
                [FromServices] IRealtimeEventPublisher realtime,
                CancellationToken cancellationToken) =>
            {
                if (await media.GetByIdAsync(kind, id, cancellationToken) is null) return Results.NotFound();
                var target = (await libraries.ListLibrariesAsync(cancellationToken))
                    .FirstOrDefault(item => item.MediaType == mediaType && string.Equals(item.Id, request.LibraryId, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["libraryId"] = [$"Choose one of your {noun} libraries."] });
                }

                var memberships = await ListAsync(kind, id, media, libraries, cancellationToken);
                var from = memberships.FirstOrDefault(item => string.Equals(item.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase));
                if (from is null) return Results.NotFound();
                if (memberships.Any(item => string.Equals(item.LibraryId, target.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    return Results.Conflict(new { message = $"This {noun} is already in {target.Name}." });
                }

                if (!from.CanMove)
                {
                    return Results.Conflict(new { message = $"This {noun} has a file in {from.LibraryName}, and the file would not move with it. Remove that copy, or move the file first." });
                }

                await media.MoveToLibraryAsync(kind, id, libraryId, target.Id, cancellationToken);
                await realtime.PublishEntityChangedAsync(entity, id, cancellationToken);
                return Results.Ok(await ListAsync(kind, id, media, libraries, cancellationToken));
            });
        }

        return endpoints;
    }

    private static async Task<IReadOnlyList<MediaLibraryMembershipItem>> ListAsync(MediaKind kind, string id, IMediaStateRepository media, ILibrariesRepository libraries, CancellationToken cancellationToken)
    {
        var all = await libraries.ListLibrariesAsync(cancellationToken);
        var rows = (await media.ListWantedByIdsAsync(kind, [id], cancellationToken))
            .Where(row => row.Id == id && !string.IsNullOrWhiteSpace(row.LibraryId))
            .ToArray();
        return rows
            .Select(row => new MediaLibraryMembershipItem(
                row.LibraryId,
                all.FirstOrDefault(library => string.Equals(library.Id, row.LibraryId, StringComparison.OrdinalIgnoreCase))?.Name ?? "Removed library",
                row.HasFile,
                row.WantedStatus,
                CanRemove: !row.HasFile && rows.Length > 1,
                CanMove: !row.HasFile))
            .OrderBy(item => item.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
