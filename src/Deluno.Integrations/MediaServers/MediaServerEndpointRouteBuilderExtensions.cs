using Deluno.Connections.Versions;
using Deluno.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Notifications;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Integrations.MediaServers;

/// <summary>
/// Plex, Jellyfin and Emby servers (#493).
///
/// <para>Here rather than in <c>Deluno.Connections</c> for the reason the
/// subtitle providers are: the clients live in Integrations, which already
/// references Connections, so the routes that need both live on this side.</para>
///
/// <para><b>The test is what it says.</b> It asks the server who it is and what
/// it holds, and reports the name the server gave itself - so a person can see
/// which machine an address actually reaches before any rule acts on it.</para>
/// </summary>
public static class MediaServerEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoMediaServers(this IEndpointRouteBuilder endpoints)
    {
        var servers = endpoints.MapGroup("/api/media-servers")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        servers.MapGet(string.Empty, async ([FromServices] IMediaServerRepository repository, CancellationToken cancellationToken)
            => Results.Ok(await repository.ListAsync(cancellationToken)));

        servers.MapPost(string.Empty, async (
            [FromBody] SaveMediaServerRequest request,
            [FromServices] IMediaServerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request, stored: null);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var saved = await repository.SaveAsync(null, request, cancellationToken);
            return Results.Created($"/api/media-servers/{saved!.Id}", saved);
        });

        servers.MapPut("{id}", async (
            string id,
            [FromBody] SaveMediaServerRequest request,
            [FromServices] IMediaServerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var stored = await repository.GetAsync(id, cancellationToken);
            if (stored is null)
            {
                return Results.NotFound();
            }

            var errors = Validate(request, stored);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            return Results.Ok(await repository.SaveAsync(id, request, cancellationToken));
        });

        servers.MapDelete("{id}", async (string id, [FromServices] IMediaServerRepository repository, CancellationToken cancellationToken)
            => await repository.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        // A draft, before it is saved: the token being offered, not a stored one.
        servers.MapPost("test", async (
            [FromBody] SaveMediaServerRequest request,
            [FromServices] IMediaServerClientRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request, stored: null);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var client = registry.Find(MediaServerKinds.Normalize(request.Kind)!)!;
            return Results.Ok(await client.TestAsync(Draft(request, stored: null), cancellationToken));
        });

        servers.MapPost("{id}/test", async (
            string id,
            [FromBody] SaveMediaServerRequest? request,
            [FromServices] IMediaServerRepository repository,
            [FromServices] IMediaServerClientRegistry registry,
            [FromServices] IOutboundNotificationService notificationService,
            [FromServices] INotificationService notificationPreferences,
            [FromServices] IConnectionCheckRecorder? checks,
            [FromServices] IConnectionVersionStore? versions,
            CancellationToken cancellationToken) =>
        {
            var stored = await repository.GetAsync(id, cancellationToken);
            if (stored is null)
            {
                return Results.NotFound();
            }

            var candidate = request is null ? stored : Draft(request, stored);
            var client = registry.Find(candidate.Kind);
            if (client is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Choose Plex, Jellyfin or Emby."] });
            }

            var previousHealthStatus = stored.HealthStatus;
            var result = await client.TestAsync(candidate, cancellationToken);
            await repository.RecordHealthAsync(id, result, cancellationToken);
            await versions.RememberVersionAsync(ConnectionKinds.MediaServer, stored.Id, ConnectionProjects.ForMediaServer(candidate.Kind), result.Version, cancellationToken);
            if (checks is not null)
            {
                await checks.RecordAsync(ConnectionKinds.MediaServer, stored.Id, stored.Name, result.Ok, result.LatencyMs, result.Message, cancellationToken);
            }
            await DispatchHealthChangeAsync(
                notificationService,
                notificationPreferences,
                $"Media server {stored.Name}",
                previousHealthStatus,
                result.Status,
                result.Message,
                cancellationToken);
            return Results.Ok(result);
        });

        servers.MapGet("{id}/path-mappings", async (
            string id,
            [FromServices] IMediaServerRepository repository,
            CancellationToken cancellationToken)
            => Results.Ok(await repository.ListPathMappingsAsync(id, cancellationToken)));

        servers.MapPost("{id}/path-mappings", async (
            string id,
            [FromBody] CreateMediaServerPathMappingRequest request,
            [FromServices] IMediaServerRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.DelunoPath) || string.IsNullOrWhiteSpace(request.ServerPath))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pathMapping"] = ["Both Deluno's path and the server's path are required."]
                });
            }

            // One mapping per Deluno folder per server: say so, instead of letting the
            // database's own refusal come back as a server error.
            static string Bare(string path) => path.Trim().TrimEnd('/', (char)92);
            var alreadyMapped = (await repository.ListPathMappingsAsync(id, cancellationToken))
                .FirstOrDefault(existing => string.Equals(Bare(existing.DelunoPath), Bare(request.DelunoPath), StringComparison.Ordinal));
            if (alreadyMapped is not null)
            {
                return Results.Conflict(new
                {
                    title = "That folder is already mapped",
                    detail = $"{request.DelunoPath.Trim()} already maps to {alreadyMapped.ServerPath} on this server. Change or remove that mapping first."
                });
            }

            var item = await repository.CreatePathMappingAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        servers.MapDelete("{id}/path-mappings/{mappingId}", async (
            string id,
            string mappingId,
            [FromServices] IMediaServerRepository repository,
            CancellationToken cancellationToken)
            => await repository.DeletePathMappingAsync(id, mappingId, cancellationToken) ? Results.NoContent() : Results.NotFound());

        servers.MapPost("{id}/path-mappings/{mappingId}/test", async (
            string id,
            string mappingId,
            [FromServices] IMediaServerRepository repository,
            [FromServices] IMediaServerClientRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var stored = await repository.GetAsync(id, cancellationToken);
            if (stored is null)
            {
                return Results.NotFound();
            }

            var mapping = (await repository.ListPathMappingsAsync(id, cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, mappingId, StringComparison.OrdinalIgnoreCase));
            if (mapping is null)
            {
                return Results.NotFound();
            }

            var client = registry.Find(stored.Kind);
            if (client is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Choose Plex, Jellyfin or Emby."] });
            }

            var answer = await client.TestPathAsync(stored, mapping.ServerPath, cancellationToken);
            return Results.Ok(answer switch
            {
                MediaServerAnswer<MediaServerPathMappingTest>.Reported reported => new { found = reported.Value.Found, message = reported.Value.Message },
                MediaServerAnswer<MediaServerPathMappingTest>.Unreachable unreachable => new { found = false, message = unreachable.Reason },
                MediaServerAnswer<MediaServerPathMappingTest>.NotSupported notSupported => new { found = false, message = notSupported.Reason },
                _ => new { found = false, message = $"{stored.Name} could not be asked." }
            });
        });

        return endpoints;
    }

    private static MediaServerConnection Draft(SaveMediaServerRequest request, MediaServerConnection? stored)
    {
        var now = DateTimeOffset.UtcNow;
        return new MediaServerConnection(
            stored?.Id ?? "draft",
            MediaServerKinds.Normalize(request.Kind) ?? stored?.Kind ?? MediaServerKinds.Jellyfin,
            string.IsNullOrWhiteSpace(request.Name) ? stored?.Name ?? "Media server" : request.Name.Trim(),
            string.IsNullOrWhiteSpace(request.BaseUrl) ? stored?.BaseUrl ?? string.Empty : SqliteMediaServerRepository.NormalizeBaseUrl(request.BaseUrl),
            string.IsNullOrWhiteSpace(request.Token) ? stored?.Token : request.Token.Trim(),
            request.IsEnabled,
            stored?.HealthStatus ?? "untested",
            null,
            null,
            null,
            stored?.ServerName,
            stored?.ServerId,
            stored?.CreatedUtc ?? now,
            now);
    }

    /// <summary>
    /// Media servers were tested and their health recorded, but nothing ever
    /// told the outbound path about it — the "health.degraded"/"health.restored"
    /// pair (#859) existed only for indexers and download clients. This adds
    /// the same pair here, gated by the same single Provider Health Changed
    /// toggle every provider type shares.
    /// </summary>
    private static async Task DispatchHealthChangeAsync(
        IOutboundNotificationService notificationService,
        INotificationService notificationPreferences,
        string providerName,
        string previousHealthStatus,
        string newHealthStatus,
        string? message,
        CancellationToken cancellationToken)
    {
        var isHealthy = string.Equals(newHealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);
        if (!isHealthy)
        {
            // Matches the indexer/download-client pattern: every failing Test
            // reports "health.degraded", not only a genuine transition.
            var preferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
            if (!preferences.ProviderHealthChangedEnabled)
            {
                return;
            }

            await NotificationOutboundDispatch.GuardAsync(async () => await notificationService.DispatchAsync(
                "health.degraded",
                $"{providerName} degraded",
                message ?? $"{providerName} is unhealthy.",
                null,
                cancellationToken), "health.degraded");
            return;
        }

        var wasHealthy = string.Equals(previousHealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);
        if (wasHealthy)
        {
            return;
        }

        var restoredPreferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
        if (!restoredPreferences.ProviderHealthChangedEnabled)
        {
            return;
        }

        await NotificationOutboundDispatch.GuardAsync(async () => await notificationService.DispatchAsync(
            "health.restored",
            $"{providerName} is healthy again",
            message ?? $"{providerName} recovered.",
            null,
            cancellationToken), "health.restored");
    }

    private static Dictionary<string, string[]> Validate(SaveMediaServerRequest request, MediaServerConnection? stored)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (MediaServerKinds.Normalize(request.Kind) is null)
        {
            errors["kind"] = ["Choose Plex, Jellyfin or Emby."];
        }

        if (!Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors["baseUrl"] = ["Use the server's http or https address, for example http://192.168.1.20:8096."];
        }

        if (string.IsNullOrWhiteSpace(request.Token) && stored?.HasToken != true)
        {
            errors["token"] = [MediaServerKinds.Normalize(request.Kind) == MediaServerKinds.Plex
                ? "Paste the server's X-Plex-Token."
                : "Paste an API key created in the server's dashboard."];
        }

        return errors;
    }
}
