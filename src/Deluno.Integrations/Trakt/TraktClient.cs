using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Deluno.Integrations.Trakt;

/// <summary>The user's own code to enter at <see cref="VerificationUrl"/>, and the opaque device code Deluno polls with.</summary>
public sealed record TraktDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUrl,
    int ExpiresInSeconds,
    int IntervalSeconds);

public enum TraktDevicePollOutcome
{
    /// <summary>The person has not finished entering the code at Trakt yet. Keep polling.</summary>
    Pending,

    /// <summary>Approved — a token pair is ready.</summary>
    Connected,

    /// <summary>The device code expired before it was approved. Start over.</summary>
    Expired,

    /// <summary>The person declined the request at Trakt, or the code was already used.</summary>
    Denied
}

public sealed record TraktDevicePollResult(TraktDevicePollOutcome Outcome, TraktTokens? Tokens);

public sealed record TraktTokens(string AccessToken, string RefreshToken, int ExpiresInSeconds);

/// <summary>
/// Trakt's OAuth device-code flow (#846) — the flow built for a server or CLI
/// app with no browser redirect of its own: the person is handed a short
/// code and a URL, and approves it on any device while Deluno polls. Chosen
/// over the authorization-code flow because Deluno is self-hosted with no
/// fixed public callback URL a Trakt app registration could point at.
/// </summary>
public interface ITraktClient
{
    Task<TraktDeviceCode> StartDeviceAuthorizationAsync(string clientId, CancellationToken cancellationToken);

    Task<TraktDevicePollResult> PollDeviceTokenAsync(string clientId, string clientSecret, string deviceCode, CancellationToken cancellationToken);

    Task<TraktTokens?> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken);

    /// <summary>An authenticated GET against a Trakt API path (e.g. <c>/users/me/watchlist</c>, <c>/movies/trending</c>), returning the raw JSON body.</summary>
    Task<string> GetAsync(string clientId, string accessToken, string pathAndQuery, CancellationToken cancellationToken);
}

public sealed class TraktClient(IHttpClientFactory httpClientFactory) : ITraktClient
{
    private const string ApiBase = "https://api.trakt.tv";
    internal const string HttpClientName = "deluno-trakt";

    public async Task<TraktDeviceCode> StartDeviceAuthorizationAsync(string clientId, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(
            $"{ApiBase}/oauth/device/code",
            new { client_id = clientId },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Trakt did not return a device code.");
        return new TraktDeviceCode(body.device_code, body.user_code, body.verification_url, body.expires_in, body.interval);
    }

    public async Task<TraktDevicePollResult> PollDeviceTokenAsync(string clientId, string clientSecret, string deviceCode, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(
            $"{ApiBase}/oauth/device/token",
            new { code = deviceCode, client_id = clientId, client_secret = clientSecret },
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Trakt did not return an access token.");
            return new TraktDevicePollResult(
                TraktDevicePollOutcome.Connected,
                new TraktTokens(body.access_token, body.refresh_token, body.expires_in));
        }

        // Trakt's device-token polling states: 400 still waiting, 404 unknown
        // code, 409 already used, 410 expired, 418 denied by the person, 429
        // polling too fast (treated as still-pending; the caller's own
        // interval already paces this).
        return (int)response.StatusCode switch
        {
            400 or 429 => new TraktDevicePollResult(TraktDevicePollOutcome.Pending, null),
            404 or 409 or 418 => new TraktDevicePollResult(TraktDevicePollOutcome.Denied, null),
            410 => new TraktDevicePollResult(TraktDevicePollOutcome.Expired, null),
            _ => throw new InvalidOperationException($"Trakt device token poll failed ({(int)response.StatusCode}).")
        };
    }

    public async Task<TraktTokens?> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(
            $"{ApiBase}/oauth/token",
            new
            {
                refresh_token = refreshToken,
                client_id = clientId,
                client_secret = clientSecret,
                grant_type = "refresh_token"
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The refresh token itself has stopped working (revoked, or
            // Trakt's own expiry on it) — the caller surfaces this as
            // "needs reconnect" rather than retrying forever.
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Trakt did not return an access token.");
        return new TraktTokens(body.access_token, body.refresh_token, body.expires_in);
    }

    public async Task<string> GetAsync(string clientId, string accessToken, string pathAndQuery, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{pathAndQuery}");
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private sealed record DeviceCodeResponse(string device_code, string user_code, string verification_url, int expires_in, int interval);

    private sealed record TokenResponse(string access_token, string refresh_token, int expires_in);
}
