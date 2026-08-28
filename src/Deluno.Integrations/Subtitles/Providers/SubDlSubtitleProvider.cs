using System.Text.Json.Serialization;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// SubDL — films and television behind a free API key.
///
/// <para>The key is free and instant, which puts this in a different class from
/// the two OpenSubtitles: "sign up" here costs a minute and no money, and the
/// screen should say so rather than lumping it in with the paid tiers.</para>
/// </summary>
public sealed class SubDlSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string SearchBase = "https://api.subdl.com/api/v1";
    private const string DownloadBase = "https://dl.subdl.com";

    public string Key => "subdl";

    public string DisplayName => "SubDL";

    public string Description => "Films and TV in most languages. Needs a free API key — a minute to get, no payment.";

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
            $"api_key={Uri.EscapeDataString(credentials.ApiKey.Trim())}",
            $"film_name={Uri.EscapeDataString(request.Title.Trim())}",
            $"type={(request.IsEpisode ? "tv" : "movie")}",
            "subs_per_page=10"
        };

        if (request.Languages.Count > 0)
        {
            // SubDL wants them upper-cased, which is its own convention rather
            // than anything about the languages.
            query.Add($"languages={Uri.EscapeDataString(string.Join(',', request.Languages.Select(language => language.ToUpperInvariant())))}");
        }

        if (request.IsEpisode)
        {
            if (request.SeasonNumber is not null) query.Add($"season_number={request.SeasonNumber.Value}");
            if (request.EpisodeNumber is not null) query.Add($"episode_number={request.EpisodeNumber.Value}");
        }

        using var client = SubtitleProviderHttp.Create(httpClientFactory);
        var body = await SubtitleProviderHttp.GetJsonAsync<SubDlSearch>(
            client, $"{SearchBase}/subtitles?{string.Join('&', query)}", Key, cancellationToken);

        if (body?.Status != true)
        {
            return [];
        }

        var found = new List<SubtitleCandidate>();
        foreach (var item in body.Subtitles ?? [])
        {
            var link = item.Url ?? item.DownloadLink;
            if (string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            found.Add(new SubtitleCandidate(
                ProviderKey: Key,
                DownloadToken: link.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? link : $"{DownloadBase}{link}",
                Language: (item.Language ?? item.Lang ?? string.Empty).Trim().ToLowerInvariant(),
                HearingImpaired: item.HearingImpaired ?? false,
                Forced: false,
                ReleaseName: item.ReleaseName,
                Uploader: item.Author,
                DownloadCount: null));
        }

        return found;
    }

    public async Task<byte[]> DownloadAsync(
        SubtitleCandidate candidate,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = SubtitleProviderHttp.Create(httpClientFactory);
        return await SubtitleProviderHttp.GetBytesAsync(client, candidate.DownloadToken, Key, cancellationToken);
    }

    private sealed record SubDlSearch(
        [property: JsonPropertyName("status")] bool? Status,
        [property: JsonPropertyName("subtitles")] IReadOnlyList<SubDlSubtitle>? Subtitles);

    private sealed record SubDlSubtitle(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("download_link")] string? DownloadLink,
        [property: JsonPropertyName("lang")] string? Lang,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("hi")] bool? HearingImpaired,
        [property: JsonPropertyName("release_name")] string? ReleaseName,
        [property: JsonPropertyName("author")] string? Author);
}
