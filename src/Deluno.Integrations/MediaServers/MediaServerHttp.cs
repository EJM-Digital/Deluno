using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Deluno.Integrations.MediaServers;

/// <summary>
/// The one way media server requests are made.
///
/// <para>One attempt per request and a named client with a timeout, no retry
/// loop: a server that is down should cost one quiet failure per pass, not a
/// burst of requests and a page of log lines (#493). Anything short of a
/// readable success comes back as <see cref="MediaServerCallResult.Failed"/>
/// with a sentence, so a caller cannot mistake it for an empty answer.</para>
/// </summary>
internal static class MediaServerHttp
{
    public const string ClientName = "media-servers";

    public static async Task<MediaServerCallResult> SendAsync(
        IHttpClientFactory httpClientFactory,
        HttpMethod method,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        string serverName,
        CancellationToken cancellationToken,
        HttpContent? content = null)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(method, uri) { Content = content };
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await httpClientFactory.CreateClient(ClientName).SendAsync(request, cancellationToken);
            var latency = (int)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return MediaServerCallResult.Failed($"{serverName} refused the token (HTTP {(int)response.StatusCode}).", latency, (int)response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return MediaServerCallResult.Failed($"{serverName} answered HTTP {(int)response.StatusCode}.", latency, (int)response.StatusCode);
            }

            return new MediaServerCallResult(true, null, latency, (int)response.StatusCode, body);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException)
        {
            var latency = (int)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return MediaServerCallResult.Failed($"Deluno could not reach {serverName}.", latency, null);
        }
    }

    public static JsonDocument? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? Int(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    public static long? Long(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    public static bool Bool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static double? Double(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    /// <summary>
    /// A list of names: an array of strings, or an array of objects each with a
    /// name (<c>{"Name": …}</c> on Jellyfin and Emby, <c>{"tag": …}</c> on Plex).
    /// </summary>
    public static IReadOnlyList<string> Names(JsonElement element, string name, string key = "Name")
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            var text = entry.ValueKind == JsonValueKind.String ? entry.GetString() : String(entry, key);
            if (!string.IsNullOrWhiteSpace(text) && !names.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(text);
            }
        }

        return names;
    }

    /// <summary>A resolution as a person says it, from the picture's size.</summary>
    public static string? ResolutionLabel(int? width, int? height)
    {
        if (width is null && height is null)
        {
            return null;
        }

        var w = width ?? 0;
        var h = height ?? 0;
        return h >= 1600 || w >= 3200 ? "2160p"
            : h >= 900 || w >= 1800 ? "1080p"
            : h >= 600 || w >= 1200 ? "720p"
            : h >= 400 || w >= 700 ? "480p"
            : "SD";
    }

    public static DateTimeOffset? Date(JsonElement element, string name)
        => String(element, name) is { } text &&
           DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}

internal sealed record MediaServerCallResult(bool Ok, string? Problem, int LatencyMs, int? StatusCode, string? Body = null)
{
    public static MediaServerCallResult Failed(string problem, int latencyMs, int? statusCode) => new(false, problem, latencyMs, statusCode);
}
