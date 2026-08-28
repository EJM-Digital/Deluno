using System.Text.Json.Serialization;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// SubSource — films and television, an API key in a header.
/// </summary>
public sealed class SubSourceSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string BaseUrl = "https://api.subsource.net/api/v1";

    public string Key => "subsource";

    public string DisplayName => "SubSource";

    public string Description => "Films and TV, strong on non-English releases. Needs an API key.";

    public SubtitleProviderScope Scope => SubtitleProviderScope.Both;

    public SubtitleCredentialFields RequiredCredentials => SubtitleCredentialFields.ApiKey;

    public bool CredentialsOptional => false;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            return [];
        }

        var query = new List<string>
        {
            $"query={Uri.EscapeDataString(request.Title.Trim())}",
            $"type={(request.IsEpisode ? "tv" : "movie")}"
        };

        if (request.IsEpisode)
        {
            if (request.SeasonNumber is not null) query.Add($"season={request.SeasonNumber.Value}");
            if (request.EpisodeNumber is not null) query.Add($"episode={request.EpisodeNumber.Value}");
        }

        if (request.Languages.Count > 0)
        {
            query.Add($"langs={Uri.EscapeDataString(string.Join(',', request.Languages))}");
        }

        using var client = SubtitleProviderHttp.Create(httpClientFactory, credentials, apiKeyHeader: "X-API-Key");
        var body = await SubtitleProviderHttp.GetJsonAsync<SubSourceSearch>(
            client, $"{BaseUrl}/subtitles?{string.Join('&', query)}", Key, cancellationToken);

        var items = body?.Subtitles ?? body?.Data ?? body?.Results ?? [];
        var found = new List<SubtitleCandidate>();

        foreach (var item in items)
        {
            var link = item.DownloadUrl ?? item.Url ?? item.DownloadLink;
            if (string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            found.Add(new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: link,
                Language: (item.Language ?? item.Lang ?? string.Empty).Trim().ToLowerInvariant(),
                HearingImpaired: item.HearingImpaired ?? item.Hi ?? false,
                Forced: false,
                ReleaseName: item.ReleaseName,
                Uploader: null,
                DownloadCount: null));
        }

        return found;
    }

    public async Task<byte[]> DownloadAsync(
        SubtitleCandidate candidate,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = SubtitleProviderHttp.Create(httpClientFactory, credentials, apiKeyHeader: "X-API-Key");
        return await SubtitleProviderHttp.GetBytesAsync(client, candidate.DownloadToken, Key, cancellationToken);
    }

    private sealed record SubSourceSearch(
        [property: JsonPropertyName("subtitles")] IReadOnlyList<SubSourceSubtitle>? Subtitles,
        [property: JsonPropertyName("data")] IReadOnlyList<SubSourceSubtitle>? Data,
        [property: JsonPropertyName("results")] IReadOnlyList<SubSourceSubtitle>? Results);

    private sealed record SubSourceSubtitle(
        [property: JsonPropertyName("download_url")] string? DownloadUrl,
        [property: JsonPropertyName("downloadLink")] string? DownloadLink,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("lang")] string? Lang,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("hi")] bool? Hi,
        [property: JsonPropertyName("hearing_impaired")] bool? HearingImpaired,
        [property: JsonPropertyName("release_name")] string? ReleaseName);
}
