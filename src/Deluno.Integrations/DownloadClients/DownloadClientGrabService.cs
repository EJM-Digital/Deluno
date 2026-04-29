using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Resilience;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientGrabService(
    IPlatformSettingsRepository platformRepository,
    IHttpClientFactory httpClientFactory,
    IIntegrationResiliencePolicy resiliencePolicy)
    : IDownloadClientGrabService
{
    public async Task<DownloadClientGrabResult> GrabAsync(
        string clientId,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var client = (await platformRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return Failed(clientId, request, "notFound", "Download client was not found.");
        }

        if (!client.IsEnabled)
        {
            return Failed(client.Id, request, "paused", "Download client is disabled.");
        }

        if (string.IsNullOrWhiteSpace(request.DownloadUrl))
        {
            return Failed(client.Id, request, "planned", "No downloadable URL was available for this release.");
        }

        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                BuildClientResilienceKey(client, "grab"),
                "download-client.grab",
                MaxAttempts: 1,
                FailureThreshold: 3),
            token => GrabCoreAsync(client, request, token),
            value => value.Succeeded
                ? IntegrationResilienceOutcome.Success
                : value.Status == "failed"
                    ? IntegrationResilienceOutcome.RetryableFailure
                    : IntegrationResilienceOutcome.NonRetryableFailure,
            cancellationToken);

        if (result.CircuitOpen)
        {
            return Failed(
                client.Id,
                request,
                "circuitOpen",
                "Deluno paused grabs for this client after repeated failures. Test the client connection before sending another release.");
        }

        return result.Value ?? Failed(client.Id, request, "failed", result.FailureMessage ?? "Download client grab failed.");
    }

    private async Task<DownloadClientGrabResult> GrabCoreAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return client.Protocol switch
            {
                "qbittorrent" => await GrabQbittorrentAsync(client, request, cancellationToken),
                "sabnzbd" => await GrabSabnzbdAsync(client, request, cancellationToken),
                "transmission" => await GrabTransmissionAsync(client, request, cancellationToken),
                "deluge" => await GrabDelugeAsync(client, request, cancellationToken),
                "nzbget" => await GrabNzbGetAsync(client, request, cancellationToken),
                "utorrent" => await GrabUTorrentAsync(client, request, cancellationToken),
                _ => Failed(client.Id, request, "planned", $"{client.Protocol} release grabs are not supported by Deluno.")
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return Failed(client.Id, request, "failed", exception.Message);
        }
    }

    private async Task<DownloadClientGrabResult> GrabQbittorrentAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, request);

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        await LoginQbittorrentAsync(http, client, cancellationToken);
        using var body = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("urls", request.DownloadUrl),
            new KeyValuePair<string, string>("category", ResolveCategory(client, request))
        ]);
        using var response = await http.PostAsync("api/v2/torrents/add", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Success(client, request, "sent", "Release URL sent to qBittorrent.");
    }

    private async Task<DownloadClientGrabResult> GrabSabnzbdAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, request);
        var apiKey = client.Secret ?? client.Username;
        if (string.IsNullOrWhiteSpace(apiKey)) return Failed(client.Id, request, "failed", "SABnzbd API key is missing.");

        var query = new Dictionary<string, string>
        {
            ["mode"] = "addurl",
            ["apikey"] = apiKey,
            ["name"] = request.DownloadUrl,
            ["cat"] = ResolveCategory(client, request),
            ["output"] = "json"
        };
        var uri = new Uri(baseUri, $"api?{BuildQuery(query)}");
        using var response = await httpClientFactory.CreateClient("download-clients").GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Success(client, request, "sent", "Release URL sent to SABnzbd.");
    }

    private async Task<DownloadClientGrabResult> GrabTransmissionAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, request);
        await SendTransmissionAsync(client, baseUri, new TransmissionRequest(
            "torrent-add",
            new Dictionary<string, object> { ["filename"] = request.DownloadUrl }),
            cancellationToken);
        return Success(client, request, "sent", "Release URL sent to Transmission.");
    }

    private async Task<DownloadClientGrabResult> GrabDelugeAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, request);
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(10);
        await PostJsonAsync<DelugeResponse<object>>(
            http,
            new Uri(baseUri, "json"),
            new DelugeRequest("auth.login", [client.Secret ?? string.Empty], 1),
            cancellationToken);
        await PostJsonAsync<DelugeResponse<object>>(
            http,
            new Uri(baseUri, "json"),
            new DelugeRequest("core.add_torrent_url", [request.DownloadUrl, new Dictionary<string, object>()], 2),
            cancellationToken);
        return Success(client, request, "sent", "Release URL sent to Deluge.");
    }

    private async Task<DownloadClientGrabResult> GrabNzbGetAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, request);
        var http = httpClientFactory.CreateClient("download-clients");
        AddBasicAuth(http, client);
        await PostJsonAsync<NzbGetResponse<object>>(
            http,
            new Uri(baseUri, "jsonrpc"),
            new NzbGetRequest("appendurl", [request.ReleaseName, request.DownloadUrl, ResolveCategory(client, request), 0, false, false]),
            cancellationToken);
        return Success(client, request, "sent", "Release URL sent to NZBGet.");
    }

    private async Task<DownloadClientGrabResult> GrabUTorrentAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, request);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), Credentials = BuildCredential(client) };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        var token = await GetUTorrentTokenAsync(http, cancellationToken);
        using var response = await http.GetAsync($"gui/?token={Uri.EscapeDataString(token)}&action=add-url&s={Uri.EscapeDataString(request.DownloadUrl)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return Success(client, request, "sent", "Release URL sent to uTorrent.");
    }

    private static async Task LoginQbittorrentAsync(HttpClient http, DownloadClientItem client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Secret)) return;
        using var body = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
            new KeyValuePair<string, string>("password", client.Secret ?? string.Empty)
        ]);
        using var response = await http.PostAsync("api/v2/auth/login", body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<TransmissionResponse> SendTransmissionAsync(
        DownloadClientItem client,
        Uri baseUri,
        TransmissionRequest payload,
        CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("download-clients");
        AddBasicAuth(http, client);
        var uri = new Uri(baseUri, "transmission/rpc");
        using var first = await http.PostAsJsonAsync(uri, payload, cancellationToken);
        if ((int)first.StatusCode == 409 &&
            first.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
        {
            http.DefaultRequestHeaders.Remove("X-Transmission-Session-Id");
            http.DefaultRequestHeaders.Add("X-Transmission-Session-Id", values.First());
            using var second = await http.PostAsJsonAsync(uri, payload, cancellationToken);
            second.EnsureSuccessStatusCode();
            return await second.Content.ReadFromJsonAsync<TransmissionResponse>(cancellationToken) ?? new TransmissionResponse(null);
        }

        first.EnsureSuccessStatusCode();
        return await first.Content.ReadFromJsonAsync<TransmissionResponse>(cancellationToken) ?? new TransmissionResponse(null);
    }

    private static Uri? ResolveEndpoint(DownloadClientItem client)
    {
        if (!string.IsNullOrWhiteSpace(client.EndpointUrl) &&
            Uri.TryCreate(EnsureTrailingSlash(client.EndpointUrl), UriKind.Absolute, out var endpoint))
        {
            return endpoint;
        }

        if (string.IsNullOrWhiteSpace(client.Host)) return null;
        var scheme = client.Host.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? string.Empty : "http://";
        var port = client.Port is > 0 ? $":{client.Port}" : string.Empty;
        return Uri.TryCreate(EnsureTrailingSlash($"{scheme}{client.Host}{port}"), UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string BuildClientResilienceKey(DownloadClientItem client, string purpose)
    {
        var endpoint = ResolveEndpoint(client);
        var address = endpoint is null
            ? "unconfigured"
            : $"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}{endpoint.AbsolutePath.TrimEnd('/')}";
        return $"download-client:{client.Id}:{client.Protocol}:{purpose}:{address}";
    }

    private static string ResolveCategory(DownloadClientItem client, DownloadClientGrabRequest request)
        => !string.IsNullOrWhiteSpace(request.Category)
            ? request.Category
            : request.MediaType == "tv"
                ? client.TvCategory ?? client.CategoryTemplate ?? "tv"
                : client.MoviesCategory ?? client.CategoryTemplate ?? "movies";

    private static string BuildQuery(Dictionary<string, string> values)
        => string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith('/') ? value : $"{value}/";

    private static void AddBasicAuth(HttpClient http, DownloadClientItem client)
    {
        if (string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Secret)) return;
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client.Username ?? string.Empty}:{client.Secret ?? string.Empty}"));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", raw);
    }

    private static NetworkCredential? BuildCredential(DownloadClientItem client)
        => string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Secret)
            ? null
            : new NetworkCredential(client.Username ?? string.Empty, client.Secret ?? string.Empty);

    private static async Task<string> GetUTorrentTokenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var html = await http.GetStringAsync("gui/token.html", cancellationToken);
        var start = html.IndexOf('>');
        var end = html.LastIndexOf('<');
        return start >= 0 && end > start ? html[(start + 1)..end].Trim() : string.Empty;
    }

    private static async Task<T?> PostJsonAsync<T>(HttpClient http, Uri uri, object payload, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(uri, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private static DownloadClientGrabResult Success(DownloadClientItem client, DownloadClientGrabRequest request, string status, string message)
        => new(client.Id, request.ReleaseName, true, status, message);

    private static DownloadClientGrabResult MissingAddress(DownloadClientItem client, DownloadClientGrabRequest request)
        => Failed(client.Id, request, "failed", "Download client address is missing.");

    private static DownloadClientGrabResult Failed(string clientId, DownloadClientGrabRequest request, string status, string message)
        => new(clientId, request.ReleaseName, false, status, message);

    private sealed record TransmissionRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("arguments")] Dictionary<string, object> Arguments);

    private sealed record TransmissionResponse(
        [property: JsonPropertyName("arguments")] object? Arguments);

    private sealed record DelugeRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Params,
        [property: JsonPropertyName("id")] int Id);

    private sealed record DelugeResponse<T>(
        [property: JsonPropertyName("result")] T? Result);

    private sealed record NzbGetRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Params);

    private sealed record NzbGetResponse<T>(
        [property: JsonPropertyName("result")] T? Result);
}
