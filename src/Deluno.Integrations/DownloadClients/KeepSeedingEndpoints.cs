using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Platform.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// The Keep seeding screen's API (#1240): what a person has chosen for keeping a struck private torrent seeding, which
/// of their sites and clients it applies to, whether a category is ready in a client, and the moves that did not work.
/// </summary>
public static class KeepSeedingEndpoints
{
    internal const int MaxNameLength = 100;

    public static IEndpointRouteBuilder MapDelunoKeepSeedingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/download-health/keep-seeding", async (
            HttpContext httpContext,
            IKeepSeedingStore store,
            IConnectionsRepository connections,
            ITrackerDefinitionNames definitionNames,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await BuildViewAsync(store, connections, definitionNames, cancellationToken));
        });

        endpoints.MapPut("/api/download-health/keep-seeding", async (
            KeepSeedingSettings request,
            HttpContext httpContext,
            IKeepSeedingStore store,
            IConnectionsRepository connections,
            ITrackerDefinitionNames definitionNames,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var clients = await connections.ListDownloadClientsAsync(cancellationToken);
            var error = Validate(request.Normalized(), clients);
            if (error is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["keepSeeding"] = [error] });
            }

            await store.SaveAsync(request, cancellationToken);
            return Results.Ok(await BuildViewAsync(store, connections, definitionNames, cancellationToken));
        });

        // Is this category ready in the client? Never creates it: a person makes the category in the client first.
        endpoints.MapPost("/api/download-health/keep-seeding/check", async (
            KeepSeedingCheckRequest request,
            HttpContext httpContext,
            IConnectionsRepository connections,
            IDownloadClientRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var category = request.Category?.Trim();
            if (string.IsNullOrWhiteSpace(category))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["Enter the category you want downloads moved to."] });
            }

            var client = (await connections.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, request.ClientId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                return Results.NotFound();
            }

            return registry.TryGet(client.Protocol, out var implementation)
                ? Results.Ok(await implementation.CheckKeepSeedingCategoryAsync(client, category, cancellationToken))
                : Results.Ok(new DownloadClientCategoryCheckResult(
                    client.Id, client.Name, category, DownloadClientCategoryStatuses.Unsupported,
                    $"Deluno cannot move downloads on {client.Protocol}.", Supported: false, Found: false));
        });

        // Clear a problem: Deluno stops showing it. If the torrent is struck again it is raised afresh.
        endpoints.MapPost("/api/download-health/keep-seeding/problems/{key}/clear", async (
            string key,
            HttpContext httpContext,
            IKeepSeedingStore store,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            return await store.ClearProblemAsync(key, cancellationToken)
                ? Results.Ok(new { key, cleared = true })
                : Results.NotFound();
        });

        return endpoints;
    }

    /// <summary>Null when the choices can be kept; otherwise the plain reason they cannot.</summary>
    internal static string? Validate(KeepSeedingSettings settings, IReadOnlyList<DownloadClientItem> clients)
    {
        static string? BadName(string name, string what)
            => name.Length > MaxNameLength || name.Any(char.IsControl)
                ? $"{what} must be a plain name of up to {MaxNameLength} characters."
                : null;

        foreach (var (clientId, category) in settings.ClientCategoryMap)
        {
            var client = clients.FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
            if (client is null) continue;
            if (BadName(category, "A category") is { } bad) return bad;
            if (DownloadClientHelpers.IsUsenetProtocol(client.Protocol))
            {
                return $"{client.Name} is a Usenet client, and only a torrent can keep seeding.";
            }

            var own = new[] { client.MoviesCategory, client.TvCategory }.Where(item => !string.IsNullOrWhiteSpace(item));
            if (own.Any(item => string.Equals(item!.Trim(), category, StringComparison.OrdinalIgnoreCase)))
            {
                return $"{category} is the category Deluno uses for downloads on {client.Name}. Choose a different one, so a kept download is never mixed up with the ones Deluno is still handling.";
            }
        }

        foreach (var (_, site) in settings.IndexerMap)
        {
            if (site.Category is { } category && BadName(category, "A category") is { } bad) return bad;
            foreach (var tag in site.TagList)
            {
                if (BadName(tag, "A tag") is { } badTag) return badTag;
            }
        }

        foreach (var (tag, category) in settings.TagCategoryMap)
        {
            if (BadName(tag, "A tag") is { } badTag) return badTag;
            if (BadName(category, "A category") is { } bad) return bad;
        }

        return null;
    }

    private static async Task<KeepSeedingView> BuildViewAsync(
        IKeepSeedingStore store,
        IConnectionsRepository connections,
        ITrackerDefinitionNames definitionNames,
        CancellationToken cancellationToken)
    {
        var settings = await store.GetAsync(cancellationToken);
        var clients = (await connections.ListDownloadClientsAsync(cancellationToken))
            .OrderBy(item => item.Priority).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var usenet = DownloadClientHelpers.IsUsenetProtocol(item.Protocol);
                return new KeepSeedingClientView(
                    item.Id, item.Name, item.Protocol, !usenet,
                    usenet ? $"{item.Name} is a Usenet client, and only a torrent can keep seeding." : null,
                    settings.ClientCategory(item.Id));
            })
            .ToArray();
        var indexers = (await connections.ListIndexersAsync(cancellationToken))
            .Where(item => IndexerProtocols.Normalize(item.Protocol) != IndexerProtocols.Newznab)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var stored = IndexerPrivacy.Normalize(item.Privacy);
                var fromDefinition = stored == IndexerPrivacy.Unknown && !string.IsNullOrWhiteSpace(item.DefinitionId)
                    ? IndexerPrivacy.Normalize(definitionNames.Privacy(item.DefinitionId))
                    : IndexerPrivacy.Unknown;
                var known = stored != IndexerPrivacy.Unknown ? stored : fromDefinition;
                var mine = settings.Indexer(item.Id);
                var isPrivate = mine?.PrivateOverride ?? IndexerPrivacy.ExpectsSharing(known);
                return new KeepSeedingIndexerView(
                    item.Id, item.Name, known, mine?.PrivateOverride, isPrivate, mine?.TagList ?? [], mine?.Category);
            })
            .ToArray();
        var tagCategories = settings.TagCategoryMap
            .Select(pair => new KeepSeedingTagView(pair.Key, pair.Value))
            .OrderBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var problems = (await store.ListProblemsAsync(cancellationToken)).OrderByDescending(item => item.LastTriedUtc).ToArray();
        return new KeepSeedingView(clients, indexers, tagCategories, problems);
    }
}

public sealed record KeepSeedingCheckRequest(string? ClientId, string? Category);

public sealed record KeepSeedingClientView(string ClientId, string Name, string Protocol, bool CanKeepSeeding, string? Reason, string? Category);

/// <param name="Privacy">What is known about the site: private, semi-private, public or unknown (not set).</param>
/// <param name="PrivateOverride">The person's own answer; null follows <paramref name="Privacy"/>.</param>
/// <param name="IsPrivate">What Deluno treats the site as, after the person's override.</param>
public sealed record KeepSeedingIndexerView(
    string IndexerId,
    string Name,
    string Privacy,
    bool? PrivateOverride,
    bool IsPrivate,
    IReadOnlyList<string> Tags,
    string? Category);

public sealed record KeepSeedingTagView(string Tag, string Category);

public sealed record KeepSeedingView(
    IReadOnlyList<KeepSeedingClientView> Clients,
    IReadOnlyList<KeepSeedingIndexerView> Indexers,
    IReadOnlyList<KeepSeedingTagView> TagCategories,
    IReadOnlyList<KeepSeedingProblem> Problems);
