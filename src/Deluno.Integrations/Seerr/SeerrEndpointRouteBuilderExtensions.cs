using Deluno.Connections.Versions;
using Deluno.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Seerr;

/// <summary>
/// Jellyseerr and Overseerr servers (#522), beside the media servers and built
/// the same way: the test says which server and version an address reaches
/// before any rule reads from it.
/// </summary>
public static class SeerrEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoSeerr(this IEndpointRouteBuilder endpoints)
    {
        var servers = endpoints.MapGroup("/api/seerr-servers")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        servers.MapGet(string.Empty, async ([FromServices] ISeerrRepository repository, CancellationToken cancellationToken)
            => Results.Ok(await repository.ListAsync(cancellationToken)));

        servers.MapPost(string.Empty, async ([FromBody] SaveSeerrRequest request, [FromServices] ISeerrRepository repository, [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken) =>
        {
            var errors = Validate(request, stored: null);
            await AddDestinationProblemsAsync(connectionFactory, errors, request, cancellationToken);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var saved = await repository.SaveAsync(null, request, cancellationToken);
            return Results.Created($"/api/seerr-servers/{saved!.Id}", saved);
        });

        servers.MapPut("{id}", async (string id, [FromBody] SaveSeerrRequest request, [FromServices] ISeerrRepository repository, [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken) =>
        {
            var stored = await repository.GetAsync(id, cancellationToken);
            if (stored is null)
            {
                return Results.NotFound();
            }

            var errors = Validate(request, stored);
            await AddDestinationProblemsAsync(connectionFactory, errors, request, cancellationToken);
            return errors.Count > 0 ? Results.ValidationProblem(errors) : Results.Ok(await repository.SaveAsync(id, request, cancellationToken));
        });

        servers.MapDelete("{id}", async (string id, [FromServices] ISeerrRepository repository, CancellationToken cancellationToken)
            => await repository.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        // A draft, before it is saved: the key being offered, not a stored one.
        servers.MapPost("test", async ([FromBody] SaveSeerrRequest request, [FromServices] ISeerrClient client, CancellationToken cancellationToken) =>
        {
            var errors = Validate(request, stored: null);
            return errors.Count > 0 ? Results.ValidationProblem(errors) : Results.Ok(await client.TestAsync(Draft(request, stored: null), cancellationToken));
        });

        servers.MapPost("{id}/test", async (
            string id,
            [FromBody] SaveSeerrRequest? request,
            [FromServices] ISeerrRepository repository,
            [FromServices] ISeerrClient client,
            [FromServices] IConnectionCheckRecorder? checks,
            [FromServices] IConnectionVersionStore? versions,
            CancellationToken cancellationToken) =>
        {
            var stored = await repository.GetAsync(id, cancellationToken);
            if (stored is null)
            {
                return Results.NotFound();
            }

            var result = await client.TestAsync(request is null ? stored : Draft(request, stored), cancellationToken);
            await repository.RecordHealthAsync(id, result, cancellationToken);
            await versions.RememberVersionAsync(ConnectionKinds.Requests, stored.Id, ConnectionProjects.Seerr, result.Version, cancellationToken);
            if (checks is not null)
            {
                await checks.RecordAsync(ConnectionKinds.Requests, stored.Id, stored.Name, result.Ok, result.LatencyMs, result.Message, cancellationToken);
            }

            return Results.Ok(result);
        });

        servers.MapGet("{id}/acquisitions", async (
            string id,
            [FromServices] ISeerrAcquisitionsRepository acquisitions,
            CancellationToken cancellationToken)
            => Results.Ok(await acquisitions.ListRecentAsync(id, take: 20, cancellationToken)));

        // #851: generates a fresh secret and hands back the plaintext once so
        // the Seerr card can show/copy it into Overseerr's webhook
        // Authorization header field. Replaces any secret already issued.
        servers.MapPost("{id}/webhook-secret", async (
            string id,
            [FromServices] ISeerrRepository repository,
            CancellationToken cancellationToken) =>
        {
            var secret = await repository.RegenerateWebhookSecretAsync(id, cancellationToken);
            return secret is null ? Results.NotFound() : Results.Ok(new { secret });
        });

        // #851: Overseerr/Jellyseerr call this on their own webhook agent, one
        // per request-status change. No Deluno session exists to authorize —
        // Overseerr sends whatever value was pasted into its webhook's
        // "Authorization Header" field back as the Authorization header, and
        // that value must match the connection's own generated secret,
        // constant-time compared so a wrong guess cannot be timed apart from
        // a right one. A connection that has never had a secret generated for
        // it refuses every call, exactly the state every install starts in.
        endpoints.MapPost("/api/seerr-servers/{id}/webhook", async (
            string id,
            HttpRequest request,
            [FromServices] ISeerrRepository repository,
            [FromServices] ISeerrAcquisitionService acquisitionService,
            [FromServices] ISeerrAcquisitionsRepository acquisitions,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Deluno.Integrations.Seerr.Webhook");
            var connection = await repository.GetAsync(id, cancellationToken);
            if (connection is null || !connection.IsEnabled)
            {
                return Results.NotFound();
            }

            if (!connection.HasWebhookSecret)
            {
                logger.LogWarning("Seerr webhook for connection {ConnectionId} refused: no webhook secret has been generated for it yet.", id);
                return Results.Unauthorized();
            }

            var provided = request.Headers.Authorization.ToString();
            if (!SecretMatches(provided, connection.WebhookSecret!))
            {
                logger.LogWarning("Seerr webhook for connection {ConnectionId} refused: the Authorization header did not match its webhook secret.", id);
                return Results.Unauthorized();
            }

            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                var (message, member) = DelunoBadRequestAnswers.Describe(exception);
                return Results.BadRequest(new { message, member });
            }

            using var _ = document;
            var root = document.RootElement;
            var notificationType = TextOf(root, "notification_type");
            if (notificationType is not ("MEDIA_APPROVED" or "MEDIA_AUTO_APPROVED"))
            {
                // Every other notification type (pending, declined, available,
                // a test ping) is acknowledged but never turned into an add.
                return Results.Ok();
            }

            if (!root.TryGetProperty("media", out var media))
            {
                return Results.Ok();
            }

            var mediaType = TextOf(media, "media_type") == "tv" ? "tv" : "movies";
            var tmdbId = TextOf(media, "tmdbId");
            var tvdbId = TextOf(media, "tvdbId");
            if (!int.TryParse(TextOf(media, "media_id") ?? TextOf(media, "id"), out var seerrMediaId))
            {
                return Results.Ok();
            }

            // Jellyseerr/Overseerr's webhook JSON payload template has no
            // "is4k" field of its own (the request entity's own `is4k` column
            // never reaches the generic webhook template — only the KeyMap
            // placeholders in server/lib/notifications/agents/webhook.ts do).
            // The one place it shows through is the `event` string itself:
            // MediaRequest.sendNotification prefixes it "4K " whenever
            // entity.is4k is true (e.g. "4K Movie Request Approved" versus
            // "Movie Request Approved") — the same source both projects
            // share. A custom payload can also send an explicit boolean
            // "is4k"/"is_4k" field, which takes priority when present.
            var explicitIs4k = BoolOf(root, "is4k") ?? BoolOf(root, "is_4k");
            var eventText = TextOf(root, "event");
            var is4k = explicitIs4k ?? (eventText is not null && eventText.TrimStart().StartsWith("4K", StringComparison.OrdinalIgnoreCase));

            string? requestedBy = null;
            if (root.TryGetProperty("request", out var requestElement))
            {
                requestedBy = TextOf(requestElement, "requestedBy_username")
                    ?? TextOf(requestElement, "requestedBy_email")
                    ?? TextOf(requestElement, "requestedBy_displayName");
            }

            var outcome = await acquisitionService.AcquireAsync(connection, mediaType, tmdbId, tvdbId, seerrMediaId, is4k, requestedBy, cancellationToken);
            return Results.Ok(new { outcome.Ok, outcome.Message });
        })
            .AllowAnonymous()
            .WithMetadata(new DelunoPublicEndpointAttribute());

        return endpoints;
    }

    private static bool SecretMatches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        // Hashed to a fixed length first so FixedTimeEquals cannot leak how
        // long the guess was through how long the comparison took.
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }

    private static string? TextOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    private static bool? BoolOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : null,
                _ => null
            }
            : null;

    private static SeerrConnection Draft(SaveSeerrRequest request, SeerrConnection? stored)
    {
        var now = DateTimeOffset.UtcNow;
        return new SeerrConnection(
            stored?.Id ?? "draft",
            string.IsNullOrWhiteSpace(request.Name) ? stored?.Name ?? "Seerr" : request.Name.Trim(),
            string.IsNullOrWhiteSpace(request.BaseUrl) ? stored?.BaseUrl ?? string.Empty : SqliteSeerrRepository.NormalizeBaseUrl(request.BaseUrl),
            string.IsNullOrWhiteSpace(request.ApiKey) ? stored?.ApiKey : request.ApiKey.Trim(),
            request.IsEnabled,
            stored?.HealthStatus ?? "untested",
            null,
            null,
            null,
            stored?.ServerName,
            stored?.Version,
            stored?.CreatedUtc ?? now,
            now,
            MovieLibraryId: request.MovieLibraryId ?? stored?.MovieLibraryId,
            MovieQualityProfileId: request.MovieQualityProfileId ?? stored?.MovieQualityProfileId,
            SeriesLibraryId: request.SeriesLibraryId ?? stored?.SeriesLibraryId,
            SeriesQualityProfileId: request.SeriesQualityProfileId ?? stored?.SeriesQualityProfileId,
            WebhookSecret: stored?.WebhookSecret,
            Tags: request.Tags ?? stored?.Tags ?? string.Empty,
            Tags4k: request.Tags4k ?? stored?.Tags4k ?? string.Empty);
    }

    private static async Task AddDestinationProblemsAsync(
        Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
        Dictionary<string, string[]> errors,
        SaveSeerrRequest request,
        CancellationToken cancellationToken)
    {
        await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(connectionFactory, errors, "movieLibraryId", Deluno.Infrastructure.Storage.ReferenceKind.Library, request.MovieLibraryId, "movies", cancellationToken);
        await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(connectionFactory, errors, "movieQualityProfileId", Deluno.Infrastructure.Storage.ReferenceKind.QualityProfile, request.MovieQualityProfileId, "movies", cancellationToken);
        await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(connectionFactory, errors, "seriesLibraryId", Deluno.Infrastructure.Storage.ReferenceKind.Library, request.SeriesLibraryId, "tv", cancellationToken);
        await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(connectionFactory, errors, "seriesQualityProfileId", Deluno.Infrastructure.Storage.ReferenceKind.QualityProfile, request.SeriesQualityProfileId, "tv", cancellationToken);
    }

    private static Dictionary<string, string[]> Validate(SaveSeerrRequest request, SeerrConnection? stored)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors["baseUrl"] = ["Use the server's http or https address, for example http://192.168.1.20:5055."];
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey) && stored?.HasApiKey != true)
        {
            errors["apiKey"] = ["Paste the API key from Seerr's Settings → General."];
        }

        return errors;
    }
}
