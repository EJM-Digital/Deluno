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
/// A configured indexer proxy (#748), resolved to what the helper's own
/// <c>proxy</c> request parameter needs (#749): the helper -- not Deluno --
/// makes the request through it, so an HTTP/SOCKS4/SOCKS5 proxy assigned to
/// the indexer travels with the request instead of being silently dropped
/// once a Cloudflare helper is in the loop.
/// </summary>
public sealed record CloudflareHelperProxy(string Url, string? Username, string? Password);

/// <summary>
/// Fetches a page that a tracker put behind a Cloudflare check. Deluno does not
/// solve those checks itself; a helper the person runs does (FlareSolverr, or
/// Byparr, which answers the same API).
/// </summary>
public interface ICloudflareHelper
{
    /// <summary>
    /// Whether a helper is set up at all: the instance-wide one in Settings,
    /// or <paramref name="helperUrl"/> when the indexer has its own
    /// FlareSolverr/Byparr proxy assigned (#976).
    /// </summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken, string? helperUrl = null);

    /// <summary>
    /// The page, or null when there is no helper or it could not get through.
    /// <paramref name="proxy"/> is the indexer's own assigned HTTP/SOCKS proxy
    /// (#748), if it has one, passed to the helper so it makes this request
    /// through it. <paramref name="helperUrl"/> is this indexer's own
    /// FlareSolverr/Byparr proxy (#976), used instead of the single
    /// instance-wide helper URL in Settings when the indexer has one assigned.
    /// </summary>
    Task<CloudflareSolution?> FetchAsync(Uri url, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null);

    /// <summary>
    /// The page fetched with a POST instead of a GET (#749), for a tracker
    /// whose Cloudflare challenge is only passed by posting form data -- the
    /// helper's own <c>request.post</c> command.
    /// </summary>
    Task<CloudflareSolution?> PostAsync(Uri url, IReadOnlyDictionary<string, string> formData, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null);

    /// <summary>Destroys this helper's session for one host, if it has one (#749), rather than leaving it to accumulate.</summary>
    Task DestroySessionAsync(string host, CancellationToken cancellationToken);

    /// <summary>Destroys every session this helper is holding (#749) -- Deluno's own shutdown hook.</summary>
    Task DestroyAllSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Destroys a session for any host not in <paramref name="activeHosts"/> (#749) -- how a removed indexer's session is cleaned up.</summary>
    Task ReconcileSessionsAsync(IReadOnlyCollection<string> activeHosts, CancellationToken cancellationToken);
}

/// <summary>The FlareSolverr <c>/v1</c> API, at the address in Settings.</summary>
public sealed class FlareSolverrCloudflareHelper(IHttpClientFactory httpClientFactory, IPlatformSettingsRepository settings) : ICloudflareHelper
{
    public const string HttpClientName = "cloudflare-helper";

    // One helper browser session per site (#628). A fresh one solves the check
    // from scratch each time, which took 49 seconds for 1337x on the rig; a kept
    // one still holds its clearance, and the next page comes back in seconds.
    // Keyed by host, so reconciliation can compare against a plain host list.
    private readonly ConcurrentDictionary<string, bool> sessions = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken, string? helperUrl = null)
        => !string.IsNullOrWhiteSpace(helperUrl) || !string.IsNullOrWhiteSpace((await settings.GetAsync(cancellationToken)).CloudflareHelperUrl);

    public Task<CloudflareSolution?> FetchAsync(Uri url, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null)
        => RequestAsync(url, formData: null, proxy, helperUrl, cancellationToken);

    public Task<CloudflareSolution?> PostAsync(Uri url, IReadOnlyDictionary<string, string> formData, CancellationToken cancellationToken, CloudflareHelperProxy? proxy = null, string? helperUrl = null)
        => RequestAsync(url, formData, proxy, helperUrl, cancellationToken);

    private async Task<CloudflareSolution?> RequestAsync(Uri url, IReadOnlyDictionary<string, string>? formData, CloudflareHelperProxy? proxy, string? helperUrl, CancellationToken cancellationToken)
    {
        // An indexer's own FlareSolverr/Byparr proxy (#976) replaces the
        // single instance-wide helper URL as that indexer's Cloudflare
        // helper, rather than being ignored because there is no protocol for
        // nesting one helper's solve behind another.
        var current = await settings.GetAsync(cancellationToken);
        var baseUrl = string.IsNullOrWhiteSpace(helperUrl) ? current.CloudflareHelperUrl : helperUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        // #749: previously a hard-coded 60-second SolveBudget; now the value
        // the person configured (1-180 seconds, clamped the same way on save).
        var solveBudget = TimeSpan.FromSeconds(Math.Clamp(current.CloudflareHelperTimeoutSeconds, 1, 180));
        var endpoint = baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl.TrimEnd('/') : baseUrl.TrimEnd('/') + "/v1";
        var http = httpClientFactory.CreateClient(HttpClientName);
        var host = url.Host;
        if (!sessions.ContainsKey(host))
        {
            // An error here usually means the session is already there, which is fine.
            using var created = await http.PostAsJsonAsync(endpoint, new { cmd = "sessions.create", session = SessionName(host) }, cancellationToken);
            sessions[host] = true;
        }

        var answer = await SendAsync(http, endpoint, url, host, formData, proxy, solveBudget, cancellationToken);
        if (answer?.Status is not null && !string.Equals(answer.Status, "ok", StringComparison.OrdinalIgnoreCase)
            && answer.Message?.Contains("session", StringComparison.OrdinalIgnoreCase) == true)
        {
            // The helper restarted and lost its sessions: start this one again.
            sessions.TryRemove(host, out _);
            using var recreated = await http.PostAsJsonAsync(endpoint, new { cmd = "sessions.create", session = SessionName(host) }, cancellationToken);
            sessions[host] = true;
            answer = await SendAsync(http, endpoint, url, host, formData, proxy, solveBudget, cancellationToken);
        }

        return Read(answer, url);
    }

    private static async Task<Answer?> SendAsync(
        HttpClient http, string endpoint, Uri url, string host, IReadOnlyDictionary<string, string>? formData, CloudflareHelperProxy? proxy, TimeSpan solveBudget, CancellationToken cancellationToken)
    {
        var command = new Dictionary<string, object?>
        {
            ["cmd"] = formData is null ? "request.get" : "request.post",
            ["url"] = url.ToString(),
            ["session"] = SessionName(host),
            ["maxTimeout"] = (int)solveBudget.TotalMilliseconds
        };
        if (formData is not null)
        {
            command["postData"] = ToPostDataString(formData);
        }

        // #749: an HTTP/SOCKS4/SOCKS5 proxy assigned to this indexer (#748)
        // travels with the request -- the helper makes the request, so it is
        // the helper's own "proxy" parameter that carries it, not Deluno's
        // own HttpClient.
        if (proxy is not null)
        {
            command["proxy"] = proxy.Username is null
                ? new { url = proxy.Url }
                : new { url = proxy.Url, username = proxy.Username, password = proxy.Password };
        }

        using var response = await http.PostAsJsonAsync(endpoint, command, cancellationToken);
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

    private static string ToPostDataString(IReadOnlyDictionary<string, string> formData)
        => string.Join('&', formData.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    public async Task DestroySessionAsync(string host, CancellationToken cancellationToken)
    {
        if (!sessions.TryRemove(host, out _))
        {
            return;
        }

        var baseUrl = (await settings.GetAsync(cancellationToken)).CloudflareHelperUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        var endpoint = baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl.TrimEnd('/') : baseUrl.TrimEnd('/') + "/v1";
        var http = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await http.PostAsJsonAsync(endpoint, new { cmd = "sessions.destroy", session = SessionName(host) }, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // The helper is not reachable right now; the in-memory record is
            // already gone, and there is nothing more useful to do than try
            // again the next time a session for this host is needed.
        }
    }

    public async Task DestroyAllSessionsAsync(CancellationToken cancellationToken)
    {
        foreach (var host in sessions.Keys.ToArray())
        {
            await DestroySessionAsync(host, cancellationToken);
        }
    }

    public async Task ReconcileSessionsAsync(IReadOnlyCollection<string> activeHosts, CancellationToken cancellationToken)
    {
        var active = new HashSet<string>(activeHosts, StringComparer.OrdinalIgnoreCase);
        foreach (var host in sessions.Keys.ToArray())
        {
            if (!active.Contains(host))
            {
                await DestroySessionAsync(host, cancellationToken);
            }
        }
    }

    private static string SessionName(string host) => "deluno-" + host;

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
