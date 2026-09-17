using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

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

        servers.MapPost(string.Empty, async ([FromBody] SaveSeerrRequest request, [FromServices] ISeerrRepository repository, CancellationToken cancellationToken) =>
        {
            var errors = Validate(request, stored: null);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var saved = await repository.SaveAsync(null, request, cancellationToken);
            return Results.Created($"/api/seerr-servers/{saved!.Id}", saved);
        });

        servers.MapPut("{id}", async (string id, [FromBody] SaveSeerrRequest request, [FromServices] ISeerrRepository repository, CancellationToken cancellationToken) =>
        {
            var stored = await repository.GetAsync(id, cancellationToken);
            if (stored is null)
            {
                return Results.NotFound();
            }

            var errors = Validate(request, stored);
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
            CancellationToken cancellationToken) =>
        {
            var stored = await repository.GetAsync(id, cancellationToken);
            if (stored is null)
            {
                return Results.NotFound();
            }

            var result = await client.TestAsync(request is null ? stored : Draft(request, stored), cancellationToken);
            await repository.RecordHealthAsync(id, result, cancellationToken);
            return Results.Ok(result);
        });

        return endpoints;
    }

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
            now);
    }

    private static Dictionary<string, string[]> Validate(SaveSeerrRequest request, SeerrConnection? stored)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this server a name."];
        }

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
