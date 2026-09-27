using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.BlockedReleases;

/// <summary>
/// The list of releases Deluno will not use again, and the way to change its
/// mind.
///
/// <para>Decisions 1 and 2 of DESIGN-007 chose permanent refusals — which is
/// only safe because they are visible and reversible. These two routes are
/// that safety, and the Failure and blocklist console is built on them.</para>
/// </summary>
public static class BlockedReleasesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoBlockedReleaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Cleanup sheet J5 (#925): block a release from wherever it is named —
        // a search result never grabbed, a title's search history, or an
        // Activity entry — not only from a proposal Deluno itself raised.
        // Nothing here stops a download; a caller that found the release still
        // sitting in a client queue uses the existing
        // POST /api/download-clients/{clientId}/queue/{queueItemId}/remove
        // with reason "failed" for that (#924), which already blocks, deletes
        // and searches again. This route is the plain "just block it" case.
        endpoints.MapPost("/api/blocked-releases", async (
            HttpContext httpContext,
            [FromBody] BlockReleaseRequest request,
            [FromServices] IBlockedReleaseRepository repository,
            [FromServices] IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var releaseName = request.ReleaseName?.Trim();
            if (string.IsNullOrWhiteSpace(releaseName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["releaseName"] = ["Say which release to block."]
                });
            }

            var indexerName = string.IsNullOrWhiteSpace(request.IndexerName) ? null : request.IndexerName.Trim();
            var title = string.IsNullOrWhiteSpace(request.Title) ? releaseName : request.Title!.Trim();
            var mediaType = string.IsNullOrWhiteSpace(request.MediaType) ? "unknown" : request.MediaType!.Trim();
            var hash = string.IsNullOrWhiteSpace(request.TorrentHashOrItemId) ? null : request.TorrentHashOrItemId!.Trim();
            var entityId = string.IsNullOrWhiteSpace(request.EntityId) ? null : request.EntityId!.Trim();

            var blocked = await repository.BlockAsync(
                new BlockedRelease(
                    Guid.NewGuid().ToString("n"),
                    BlockedReleaseKeys.For(releaseName, indexerName),
                    releaseName,
                    indexerName ?? string.Empty,
                    mediaType,
                    entityId,
                    title,
                    ImportFailurePolicy.ManualFailure,
                    "You blocked this release from Deluno.",
                    hash,
                    null,
                    null,
                    DateTimeOffset.MinValue,
                    Origin: BlockedReleaseOrigins.Manual),
                cancellationToken);

            // Recorded regardless of whether this is a fresh block or the
            // same release blocked again — the person still asked for it, and
            // Activity is where "I did this on purpose" lives (#925).
            await activityFeedRepository.RecordActivityAsync(
                "release.blocked",
                $"You blocked \"{title}\"; every indexer's copy is refused from now on.",
                null,
                null,
                entityId is not null ? mediaType : "blocked_release",
                entityId ?? blocked.Id,
                cancellationToken);

            return Results.Ok(blocked);
        });

        endpoints.MapGet("/api/blocked-releases", async (
            HttpContext httpContext,
            [FromServices] IBlockedReleaseRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.ListAsync(cancellationToken));
        });

        // Lifting a block (#925): offered from the same places blocking itself
        // is offered — search results, the Queue, a title's history, Activity
        // — and on the Blocklist screen (blocked-releases-console.tsx's own
        // "Un-refuse"). Both call this one route.
        endpoints.MapDelete("/api/blocked-releases/{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IBlockedReleaseRepository repository,
            [FromServices] IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // Read before unblocking so the Activity entry can name what was
            // lifted even after the row itself is gone.
            var existing = (await repository.ListAsync(cancellationToken))
                .FirstOrDefault(release => release.Id == id);

            // Unblocking starts nothing. James: "there could be a number of
            // scenarios when the blocklist is being cleared and if an
            // individual title is removed the user is going to manually
            // trigger the search anyway" — so clearing in bulk cannot become a
            // storm of searches. DESIGN-007 decision 8.
            if (!await repository.UnblockAsync(id, cancellationToken))
            {
                return Results.NotFound();
            }

            if (existing is not null)
            {
                var title = existing.Title ?? existing.ReleaseName;
                await activityFeedRepository.RecordActivityAsync(
                    "release.block-lifted",
                    $"You lifted the block on \"{title}\"; Deluno will consider it again. No search was started.",
                    null,
                    null,
                    existing.EntityId is not null ? existing.MediaType : "blocked_release",
                    existing.EntityId ?? existing.Id,
                    cancellationToken);
            }

            return Results.NoContent();
        });

        // Answering a proposal. The other answer is the DELETE above — allowing
        // a proposed release is the same act as un-refusing a refused one, so
        // it is deliberately the same route rather than a third verb that does
        // the same thing.
        endpoints.MapPost("/api/blocked-releases/{id}/refuse", async (
            string id,
            HttpContext httpContext,
            [FromServices] IBlockedReleaseRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return await repository.RefuseAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        // The manual half of the scheduled clear-out — for a refusal that
        // predates the setting, or one whose client was off when the schedule
        // came round. It calls the same service the schedule calls, so it
        // still will not overrule the sharing rule.
        endpoints.MapPost("/api/blocked-releases/{id}/cleanup", async (
            string id,
            HttpContext httpContext,
            [FromServices] IRefusedDownloadCleanupService cleanup,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var outcome = await cleanup.CleanUpOneAsync(id, cancellationToken);
            return outcome == RefusedDownloadCleanupOutcomes.NotFound
                ? Results.NotFound()
                : Results.Ok(new { outcome });
        });

        // The bulk half of the same button (#800): every refused release the
        // schedule would eventually reach anyway, cleared now instead of
        // waited for. Same service, same per-release rules — a copy a
        // sharing hold still needs is still left alone and still counted
        // under waitingOnSharing rather than silently skipped.
        endpoints.MapPost("/api/blocked-releases/cleanup-all", async (
            HttpContext httpContext,
            [FromServices] IRefusedDownloadCleanupService cleanup,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await cleanup.CleanUpEverythingAsync(cancellationToken));
        });

        return endpoints;
    }
}

/// <summary>
/// A release named somewhere Deluno did not itself propose blocking it from —
/// a search result, a title's search history, or an Activity entry (#925).
/// Only <see cref="ReleaseName"/> is required: the rest is whatever the
/// caller already knows about the release, none of it guaranteed.
/// </summary>
public sealed record BlockReleaseRequest(
    string ReleaseName,
    string? IndexerName = null,
    string? TorrentHashOrItemId = null,
    string? MediaType = null,
    string? EntityId = null,
    string? Title = null);
