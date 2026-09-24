using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Platform.Data;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// A page fetched through a Cloudflare helper (#628): what it returned, and
/// what later requests need to be let through without the helper. The
/// clearance cookie only holds with the User-Agent that earned it.
/// </summary>
public sealed record CloudflareSolution(Uri Url, HttpStatusCode Status, string Html, string? UserAgent, IReadOnlyList<Cookie> Cookies);

/// <summary>
/// Fetches a page that a tracker put behind a Cloudflare check. Deluno does not
/// solve those checks itself; a helper the person runs does (FlareSolverr, or
/// Byparr, which answers the same API).
/// </summary>
public interface ICloudflareHelper
{
    /// <summary>Whether a helper is set up at all.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);

    /// <summary>The page, or null when there is no helper or it could not get through.</summary>
    Task<CloudflareSolution?> FetchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>The FlareSolverr <c>/v1</c> API, at the address in Settings.</summary>
public sealed class FlareSolverrCloudflareHelper(IHttpClientFactory httpClientFactory, IPlatformSettingsRepository settings) : ICloudflareHelper
{
    public const string HttpClientName = "cloudflare-helper";

    // A challenge can take FlareSolverr a while; the tracker request that
    // needed it has already waited for the first answer.
    private static readonly TimeSpan SolveBudget = TimeSpan.FromSeconds(60);

    // One helper browser session per site (#628). A fresh one solves the check
    // from scratch each time, which took 49 seconds for 1337x on the rig; a kept
    // one still holds its clearance, and the next page comes back in seconds.
    private readonly ConcurrentDictionary<string, bool> sessions = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken)
        => !string.IsNullOrWhiteSpace((await settings.GetAsync(cancellationToken)).CloudflareHelperUrl);

    public async Task<CloudflareSolution?> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        var baseUrl = (await settings.GetAsync(cancellationToken)).CloudflareHelperUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var endpoint = baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl.TrimEnd('/') : baseUrl.TrimEnd('/') + "/v1";
        var http = httpClientFactory.CreateClient(HttpClientName);
        var session = "deluno-" + url.Host;
        if (!sessions.ContainsKey(session))
        {
            // An error here usually means the session is already there, which is fine.
            using var created = await http.PostAsJsonAsync(endpoint, new { cmd = "sessions.create", session }, cancellationToken);
            sessions[session] = true;
        }

        var answer = await GetAsync(http, endpoint, url, session, cancellationToken);
        if (answer?.Status is not null && !string.Equals(answer.Status, "ok", StringComparison.OrdinalIgnoreCase)
            && answer.Message?.Contains("session", StringComparison.OrdinalIgnoreCase) == true)
        {
            // The helper restarted and lost its sessions: start this one again.
            sessions.TryRemove(session, out _);
            using var recreated = await http.PostAsJsonAsync(endpoint, new { cmd = "sessions.create", session }, cancellationToken);
            sessions[session] = true;
            answer = await GetAsync(http, endpoint, url, session, cancellationToken);
        }

        return Read(answer, url);
    }

    private static async Task<Answer?> GetAsync(HttpClient http, string endpoint, Uri url, string session, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            endpoint,
            new { cmd = "request.get", url = url.ToString(), session, maxTimeout = (int)SolveBudget.TotalMilliseconds },
            cancellationToken);
        try
        {
            return await response.Content.ReadFromJsonAsync<Answer>(cancellationToken);
        }
        catch (JsonException)
        {
            // Not the helper's API at all, or an error page: nothing to use.
            return null;
        }
    }

    /// <summary>A helper's answer, read; null unless it says it got the page.</summary>
    public static CloudflareSolution? Read(Answer? answer, Uri requested)
    {
        if (answer is null || !string.Equals(answer.Status, "ok", StringComparison.OrdinalIgnoreCase) || answer.Solution is not { } solution)
        {
            return null;
        }

        var url = Uri.TryCreate(solution.Url, UriKind.Absolute, out var final) ? final : requested;
        var cookies = (solution.Cookies ?? [])
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name))
            .Select(cookie => new Cookie(
                cookie.Name!,
                cookie.Value ?? string.Empty,
                string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                string.IsNullOrWhiteSpace(cookie.Domain) ? url.Host : cookie.Domain))
            .ToArray();
        return new CloudflareSolution(url, (HttpStatusCode)(solution.Status ?? 200), solution.Response ?? string.Empty, solution.UserAgent, cookies);
    }

    public sealed record Answer(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("solution")] Solution? Solution);

    public sealed record Solution(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("status")] int? Status,
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("userAgent")] string? UserAgent,
        [property: JsonPropertyName("cookies")] IReadOnlyList<SolvedCookie>? Cookies);

    public sealed record SolvedCookie(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("domain")] string? Domain,
        [property: JsonPropertyName("path")] string? Path);
}
