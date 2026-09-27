using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Metadata.SceneMapping;

/// <summary>
/// TheXEM's public "map all" endpoint — the same numbering-mismatch source
/// Sonarr documents relying on for anime (see the XEM guide it links from its
/// own FAQ). No account or key: a title's TVDB id is enough to ask for every
/// scene/absolute correction the community has recorded against it.
///
/// <para>Most titles have no entry at all, and TheXEM answers that with
/// <c>"result": "failure"</c> rather than an empty <c>"data"</c> array — which
/// is not a fetch failure, only a title with nothing to correct. Only entries
/// where the scene numbering actually differs from TVDB's are kept: writing an
/// identical scene number back would read as a correction that never
/// happened.</para>
/// </summary>
public sealed class XemSceneMappingProvider(IHttpClientFactory httpClientFactory, ILogger<XemSceneMappingProvider> logger)
    : ISceneMappingProvider
{
    public const string HttpClientName = "scene-mapping";

    private const string BaseUrl = "https://thexem.info";

    public string Key => "thexem";

    public async Task<IReadOnlyList<SceneMappingEntry>> GetMappingAsync(string tvdbId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tvdbId))
        {
            return [];
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{BaseUrl}/map/all?id={Uri.EscapeDataString(tvdbId.Trim())}&origin=tvdb&output=json";

        XemMapAllResponse? response;
        try
        {
            using var httpResponse = await client.GetAsync(url, cancellationToken);
            if (httpResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "TheXEM returned HTTP {StatusCode} for TVDB id {TvdbId}; leaving its scene mapping unchanged.",
                    (int)httpResponse.StatusCode,
                    tvdbId);
                return [];
            }

            response = await httpResponse.Content.ReadFromJsonAsync<XemMapAllResponse>(cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation(
                exception,
                "TheXEM could not be asked for TVDB id {TvdbId}; leaving its scene mapping unchanged.",
                tvdbId);
            return [];
        }

        if (response is null
            || !string.Equals(response.Result, "success", StringComparison.OrdinalIgnoreCase)
            || response.Data is not { Count: > 0 })
        {
            return [];
        }

        var entries = new List<SceneMappingEntry>();
        foreach (var item in response.Data)
        {
            if (item.Tvdb is not { Season: { } providerSeason, Episode: { } providerEpisode } || providerSeason < 0 || providerEpisode <= 0)
            {
                continue;
            }

            var scene = item.Scene;
            var sceneDiffers = scene is { Season: not null, Episode: not null }
                && (scene.Season != providerSeason || scene.Episode != providerEpisode);
            var absolute = item.Tvdb.Absolute ?? scene?.Absolute;

            if (!sceneDiffers && absolute is null)
            {
                // Nothing this title's provider numbering does not already say.
                continue;
            }

            entries.Add(new SceneMappingEntry(
                providerSeason,
                providerEpisode,
                sceneDiffers ? scene!.Season : null,
                sceneDiffers ? scene!.Episode : null,
                absolute));
        }

        return entries;
    }

    private sealed record XemMapAllResponse(
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("data")] IReadOnlyList<XemMapEntry>? Data);

    private sealed record XemMapEntry(
        [property: JsonPropertyName("tvdb")] XemNumber? Tvdb,
        [property: JsonPropertyName("scene")] XemNumber? Scene);

    private sealed record XemNumber(
        [property: JsonPropertyName("season")] int? Season,
        [property: JsonPropertyName("episode")] int? Episode,
        [property: JsonPropertyName("absolute")] int? Absolute);
}
