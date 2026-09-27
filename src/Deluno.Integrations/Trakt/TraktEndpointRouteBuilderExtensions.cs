using Deluno.Connections.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Integrations.Trakt;

public sealed record TraktClientCredentialsRequest(string ClientId, string ClientSecret);

public sealed record TraktConnectResponse(string DeviceCode, string UserCode, string VerificationUrl, int ExpiresInSeconds, int IntervalSeconds);

public sealed record TraktConnectPollRequest(string DeviceCode);

public sealed record TraktConnectPollResponse(string Status);

/// <summary>
/// Trakt OAuth (#846) — a "Connect Trakt" action on the Lists screen that
/// runs the device-code flow: Deluno hands back a short code and a URL, the
/// person approves it on any device, and Deluno's poll picks up the token
/// pair once it lands. Every write here requires the same authenticated
/// session every other settings write does; the Lists screen is where the
/// UI for this lives.
/// </summary>
public static class TraktEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoTrakt(this IEndpointRouteBuilder endpoints)
    {
        var trakt = endpoints.MapGroup("/api/trakt")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        trakt.MapGet("/status", async (
            [FromServices] ITraktConnectionRepository repository,
            CancellationToken cancellationToken) => Results.Ok(await repository.GetStatusAsync(cancellationToken)));

        trakt.MapPost("/credentials", async (
            [FromBody] TraktClientCredentialsRequest request,
            [FromServices] ITraktConnectionRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["clientId"] = ["Enter the Client ID and Client Secret from your Trakt app."]
                });
            }

            await repository.SetClientCredentialsAsync(request.ClientId, request.ClientSecret, cancellationToken);
            return Results.Ok(await repository.GetStatusAsync(cancellationToken));
        });

        trakt.MapPost("/connect", async (
            [FromServices] ITraktConnectionRepository repository,
            [FromServices] ITraktClient client,
            CancellationToken cancellationToken) =>
        {
            var credentials = await repository.GetClientCredentialsAsync(cancellationToken);
            if (credentials is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["clientId"] = ["Enter your Trakt app's Client ID and Client Secret first."]
                });
            }

            var deviceCode = await client.StartDeviceAuthorizationAsync(credentials.ClientId, cancellationToken);
            return Results.Ok(new TraktConnectResponse(
                deviceCode.DeviceCode,
                deviceCode.UserCode,
                deviceCode.VerificationUrl,
                deviceCode.ExpiresInSeconds,
                deviceCode.IntervalSeconds));
        });

        trakt.MapPost("/connect/poll", async (
            [FromBody] TraktConnectPollRequest request,
            [FromServices] ITraktConnectionRepository repository,
            [FromServices] ITraktClient client,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var credentials = await repository.GetClientCredentialsAsync(cancellationToken);
            if (credentials is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["clientId"] = ["Enter your Trakt app's Client ID and Client Secret first."]
                });
            }

            var poll = await client.PollDeviceTokenAsync(credentials.ClientId, credentials.ClientSecret, request.DeviceCode, cancellationToken);
            switch (poll.Outcome)
            {
                case TraktDevicePollOutcome.Connected:
                    var expiresUtc = timeProvider.GetUtcNow().AddSeconds(poll.Tokens!.ExpiresInSeconds);
                    await repository.SaveTokensAsync(poll.Tokens.AccessToken, poll.Tokens.RefreshToken, expiresUtc, cancellationToken);
                    return Results.Ok(new TraktConnectPollResponse("connected"));
                case TraktDevicePollOutcome.Expired:
                    return Results.Ok(new TraktConnectPollResponse("expired"));
                case TraktDevicePollOutcome.Denied:
                    return Results.Ok(new TraktConnectPollResponse("denied"));
                default:
                    return Results.Ok(new TraktConnectPollResponse("pending"));
            }
        });

        trakt.MapPost("/disconnect", async (
            [FromServices] ITraktConnectionRepository repository,
            CancellationToken cancellationToken) =>
        {
            await repository.DisconnectAsync(cancellationToken);
            return Results.Ok(await repository.GetStatusAsync(cancellationToken));
        });

        return endpoints;
    }
}
