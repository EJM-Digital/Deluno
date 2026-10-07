using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// A tracker needs something the configured Cloudflare helper cannot do
/// (#1029) -- a POST through Byparr, or a proxy through Byparr, both of
/// which Byparr silently drops rather than refusing. Deluno catches this
/// itself instead, and says in plain words what the person's other helper
/// choice (FlareSolverr) can do that this one cannot.
/// </summary>
public sealed class CloudflareHelperCapabilityException(string message) : TrackerException(message);

/// <summary>
/// The Cloudflare helper itself did not answer (#1027): nothing is listening
/// at its address, or it took too long. This is a setup problem with the
/// helper, not the tracker or its mirrors being down, so the search does not
/// try another mirror through the same helper and Health does not call the
/// indexer unreachable.
/// </summary>
public sealed class CloudflareHelperUnavailableException(string trackerHost, string helperAddress, Exception inner)
    : TrackerException(
        $"{trackerHost} is behind a Cloudflare check, and the Cloudflare helper at {helperAddress} did not answer. Start it, or fix its address in Preferences, General.",
        inner)
{
    public string HelperAddress { get; } = helperAddress;
}

/// <summary>
/// Whether a Cloudflare helper answers at an address: the one check behind
/// the Settings test button and the Health row (#1027).
/// </summary>
public static class CloudflareHelperProbe
{
    public static async Task<(bool Healthy, string Message)> CheckAsync(HttpClient http, Uri address, CancellationToken cancellationToken)
    {
        if (address.Scheme is not ("http" or "https"))
        {
            return (false, "A Cloudflare helper's address must start with http:// or https://.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync(address, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return response.IsSuccessStatusCode
                ? (true, body.Contains("FlareSolverr is ready", StringComparison.OrdinalIgnoreCase) ? $"FlareSolverr answered at {address.Authority}." : $"A helper answered at {address.Authority}.")
                : (false, $"{address.Authority} answered HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (false, $"Nothing answered at {address.Authority}: {ex.Message}");
        }
    }
}

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
    /// Whether a helper is set up at all: the instance-wide one in Preferences, General,
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
    /// instance-wide helper URL in Preferences, General when the indexer has one assigned.
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

/// <summary>The FlareSolverr <c>/v1</c> API, at the address in Preferences, General.</summary>
public sealed class FlareSolverrCloudflareHelper(IHttpClientFactory httpClientFactory, IPlatformSettingsRepository settings) : ICloudflareHelper
{
    public const string HttpClientName = "cloudflare-helper";

    // One helper browser session per site (#628). A fresh one solves the check
    // from scratch each time, which took 49 seconds for 1337x on the rig; a kept
    // one still holds its clearance, and the next page comes back in seconds.
    // Keyed by host, so reconciliation can compare against a plain host list.
    private readonly ConcurrentDictionary<string, bool> sessions = new(StringComparer.OrdinalIgnoreCase);

    // #1029: which helper answered at a given base address, once probed under
    // "auto". Keyed by the helper's own base URL (there can be more than
    // one: the instance-wide address, and any indexer's own #976 address),
    // not by tracker host, since the same helper answers for every tracker
    // routed through it.
    private readonly ConcurrentDictionary<string, string> detectedKinds = new(StringComparer.OrdinalIgnoreCase);

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

        // An address that is not http(s) is no helper (#1064): treated as none set.
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var helperUri) || helperUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            return await SolveAsync(url, formData, proxy, baseUrl, current, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The helper, not the tracker: nothing is listening at its address, or it did not answer in time (#1027).
            throw new CloudflareHelperUnavailableException(url.Host, HelperAddress(baseUrl), exception);
        }
    }

    private static string HelperAddress(string baseUrl)
        => Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : baseUrl.Trim();

    private async Task<CloudflareSolution?> SolveAsync(Uri url, IReadOnlyDictionary<string, string>? formData, CloudflareHelperProxy? proxy, string baseUrl, PlatformSettingsSnapshot current, CancellationToken cancellationToken)
    {
        // #1029: Byparr answers the same /v1 shape as FlareSolverr but its
        // source says it only supports GET -- sessions.create/destroy,
        // postData and a per-request proxy all do nothing, silently. Deluno
        // never sends Byparr a request it cannot perform; it tells the
        // person in plain words instead of letting the request go through
        // with the proxy or the POST body quietly dropped.
        var kind = await ResolveKindAsync(baseUrl, current.CloudflareHelperKind, cancellationToken);
        var isByparr = kind == CloudflareHelperKinds.Byparr;
        if (isByparr && formData is not null)
        {
            throw new CloudflareHelperCapabilityException(
                $"{url.Host} needs a POST to get past its Cloudflare check, which Byparr cannot send (it only supports GET). Use FlareSolverr for this tracker instead.");
        }

        if (isByparr && proxy is not null)
        {
            throw new CloudflareHelperCapabilityException(
                $"{url.Host} is assigned a proxy, but Byparr cannot route a request through one -- it would be sent without it. Use FlareSolverr for this tracker instead, or remove its proxy.");
        }

        // #749: previously a hard-coded 60-second SolveBudget; now the value
        // the person configured (1-180 seconds, clamped the same way on save).
        var solveBudget = TimeSpan.FromSeconds(Math.Clamp(current.CloudflareHelperTimeoutSeconds, 1, 180));
        var endpoint = baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl.TrimEnd('/') : baseUrl.TrimEnd('/') + "/v1";
        var http = httpClientFactory.CreateClient(HttpClientName);
        var host = url.Host;

        // Byparr does nothing with sessions.create/destroy -- it never
        // errors, but there is nothing to gain by sending it, so Deluno
        // skips straight to the request the same way it would for a helper
        // with no session concept at all.
        if (!isByparr && !sessions.ContainsKey(host))
        {
            // An error here usually means the session is already there, which is fine.
            using var created = await http.PostAsJsonAsync(endpoint, new { cmd = "sessions.create", session = SessionName(host) }, cancellationToken);
            sessions[host] = true;
        }

        var answer = await SendAsync(http, endpoint, url, host, formData, proxy, solveBudget, cancellationToken);
        if (!isByparr && answer?.Status is not null && !string.Equals(answer.Status, "ok", StringComparison.OrdinalIgnoreCase)
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

    /// <summary>
    /// The kind of helper at <paramref name="baseUrl"/>: the configured
    /// choice if the person said which one they run, otherwise probed once
    /// and cached for that address (#1029).
    /// </summary>
    private async Task<string> ResolveKindAsync(string baseUrl, string configuredKind, CancellationToken cancellationToken)
    {
        var configured = CloudflareHelperKinds.Normalize(configuredKind);
        if (configured != CloudflareHelperKinds.Auto)
        {
            return configured;
        }

        if (detectedKinds.TryGetValue(baseUrl, out var cached))
        {
            return cached;
        }

        var detected = await DetectKindAsync(baseUrl, cancellationToken);
        detectedKinds[baseUrl] = detected;
        return detected;
    }

    /// <summary>
    /// Probes the helper's own root address for what it says about itself.
    /// FlareSolverr's landing page says "FlareSolverr is ready"; Byparr's
    /// says its own name. Anything else -- unreachable, or neither string
    /// present -- is read as FlareSolverr, today's only behaviour, so an
    /// existing FlareSolverr install (almost every install) is unaffected by
    /// this detection ever being added.
    /// </summary>
    private async Task<string> DetectKindAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync(baseUrl, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (body.Contains("byparr", StringComparison.OrdinalIgnoreCase))
            {
                return CloudflareHelperKinds.Byparr;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Could not probe it; assume FlareSolverr rather than block a
            // helper that might not even be Byparr.
        }

        return CloudflareHelperKinds.FlareSolverr;
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
