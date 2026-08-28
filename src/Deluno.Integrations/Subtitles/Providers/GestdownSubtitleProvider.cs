using System.Text.Json.Serialization;

namespace Deluno.Integrations.Subtitles.Providers;

/// <summary>
/// Gestdown — Addic7ed's catalogue behind a plain JSON API, and TV only.
///
/// <para>The first provider Deluno ships because it is the one that asks for
/// nothing: no account, no key, no captcha. A person turning subtitles on can
/// see the whole loop work before deciding whether any of the paid sources are
/// worth signing up for.</para>
///
/// <para>Two requests: the show by name, then that show's subtitles for the
/// season and episode. Ported from Weir's client, which learnt the shape the
/// hard way — the search endpoint answers with <c>shows</c> or <c>results</c>
/// depending on the route, and the download link arrives under any of three
/// names.</para>
/// </summary>
public sealed class GestdownSubtitleProvider(IHttpClientFactory httpClientFactory) : ISubtitleProvider
{
    private const string BaseUrl = "https://api.gestdown.info";

    public string Key => "gestdown";

    public string DisplayName => "Gestdown";

    public string Description => "Addic7ed's TV subtitles, no account needed. Strong on English and current shows.";

    public SubtitleProviderScope Scope => SubtitleProviderScope.TvOnly;

    public SubtitleCredentialFields RequiredCredentials => SubtitleCredentialFields.None;

    public bool CredentialsOptional => false;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (!request.IsEpisode || request.SeasonNumber is null || request.EpisodeNumber is null)
        {
            // A show without a season and an episode is not a question this API
            // can answer, and asking anyway returns the whole series.
            return [];
        }

        using var client = SubtitleProviderHttp.Create(httpClientFactory);

        var shows = await SubtitleProviderHttp.GetJsonAsync<GestdownShowSearch>(
            client,
            $"{BaseUrl}/shows/search/{Uri.EscapeDataString(request.Title.Trim())}",
            Key,
            cancellationToken);

        var showId = shows?.Shows?.FirstOrDefault(show => !string.IsNullOrWhiteSpace(show.Id))?.Id;
        if (string.IsNullOrWhiteSpace(showId))
        {
            return [];
        }

        // One request per language: the API takes a single code, and the
        // alternative is asking only for the first and quietly never fetching
        // the second language somebody configured.
        var found = new List<SubtitleCandidate>();
        foreach (var language in request.Languages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var code = ShortCode(language);
            if (code.Length == 0)
            {
                continue;
            }

            var page = await SubtitleProviderHttp.GetJsonAsync<GestdownSubtitleList>(
                client,
                $"{BaseUrl}/subtitles/get/{Uri.EscapeDataString(showId)}/{request.SeasonNumber.Value}/{request.EpisodeNumber.Value}/{Uri.EscapeDataString(code)}",
                Key,
                cancellationToken);

            foreach (var subtitle in page?.Subtitles ?? [])
            {
                var link = subtitle.DownloadUri ?? subtitle.Download ?? subtitle.Url;
                if (string.IsNullOrWhiteSpace(link))
                {
                    continue;
                }

                found.Add(new SubtitleCandidate(
                    ProviderKey: Key,
                    DownloadToken: link,
                    Language: language,
                    HearingImpaired: subtitle.HearingImpaired ?? false,
                    Forced: false,
                    ReleaseName: subtitle.Version,
                    Uploader: null,
                    DownloadCount: subtitle.Downloads));
            }
        }

        return found;
    }

    public async Task<byte[]> DownloadAsync(
        SubtitleCandidate candidate,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = SubtitleProviderHttp.Create(httpClientFactory);
        var url = candidate.DownloadToken.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? candidate.DownloadToken
            : $"{BaseUrl}{candidate.DownloadToken}";

        return await SubtitleProviderHttp.GetBytesAsync(client, url, Key, cancellationToken);
    }

    /// <summary>
    /// The two-letter code Gestdown addresses a language by.
    ///
    /// <para>Deluno stores <c>en</c>; ffprobe emits <c>eng</c>; a file is named
    /// any of three. <c>SubtitleLanguages.Normalize</c> is the one place that
    /// reconciles them, and this only has to shorten what it produced.</para>
    /// </summary>
    private static string ShortCode(string language)
    {
        var trimmed = language.Trim().ToLowerInvariant();
        return trimmed.Length >= 2 ? trimmed[..2] : string.Empty;
    }

    private sealed record GestdownShowSearch(
        [property: JsonPropertyName("shows")] IReadOnlyList<GestdownShow>? Shows);

    private sealed record GestdownShow(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record GestdownSubtitleList(
        [property: JsonPropertyName("subtitles")] IReadOnlyList<GestdownSubtitle>? Subtitles);

    private sealed record GestdownSubtitle(
        [property: JsonPropertyName("downloadUri")] string? DownloadUri,
        [property: JsonPropertyName("download")] string? Download,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("hearingImpaired")] bool? HearingImpaired,
        [property: JsonPropertyName("downloadCount")] int? Downloads);
}
