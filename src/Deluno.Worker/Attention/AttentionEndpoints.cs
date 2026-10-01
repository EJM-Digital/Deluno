using Deluno.Jobs.Data;
using Deluno.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker.Attention;

/// <summary>What the bell lists that no other screen already lists (#1098).</summary>
public static class AttentionEndpoints
{
    /// <summary>One title with no acceptable release, as the bell draws it.</summary>
    public sealed record NoReleaseRow(string MediaType, string Id, string Title, int? Year, string LibraryId, DateTimeOffset LastSearchUtc, DateTimeOffset? MissingSinceUtc, string? Reason);

    public static IEndpointRouteBuilder MapDelunoAttentionReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Wanted titles whose newest search found nothing Deluno would take, minus the ones the person dismissed.
        endpoints.MapGet("/api/attention/no-release", async (
            INoAcceptableReleaseReader reader,
            AttentionDismissalStore dismissals,
            CancellationToken cancellationToken) =>
        {
            var all = await reader.ListAsync(500, cancellationToken);
            var dismissed = await dismissals.ListAsync(cancellationToken);
            // A dismissal is forgotten once its title stops being this problem, so it can come back as a new one.
            await dismissals.ForgetExceptAsync(
                all.Select(item => AttentionDismissalStore.NoReleaseKey(item.MediaType, item.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                cancellationToken);
            var rows = all
                .Where(item => !dismissed.Contains(AttentionDismissalStore.NoReleaseKey(item.MediaType, item.Id)))
                .Select(item => new NoReleaseRow(item.MediaType, item.Id, item.Title, item.Year, item.LibraryId, item.LastSearchUtc, item.MissingSinceUtc, item.Reason))
                .ToArray();
            return Results.Ok(rows);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapDelunoAttentionWriteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Stop showing a title in the bell. Nothing about the title changes and nothing is deleted.
        endpoints.MapPost("/api/attention/no-release/{mediaType}/{id}/dismiss", async (
            string mediaType,
            string id,
            AttentionDismissalStore dismissals,
            CancellationToken cancellationToken) =>
        {
            if (mediaType is not ("movie" or "series"))
            {
                return Results.NotFound();
            }

            await dismissals.DismissAsync(AttentionDismissalStore.NoReleaseKey(mediaType, id), cancellationToken);
            return Results.NoContent();
        });

        return endpoints;
    }
}
